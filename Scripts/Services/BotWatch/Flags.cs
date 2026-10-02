using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Server.Services.BotWatch
{
    public enum FlagStatus
    {
        Open,
        Legit,
        Confirmed,
        Actioned,
        Dismissed
    }

    /// <summary>The evidence for a flag at one moment, as readable text.</summary>
    public class Snapshot
    {
        public DateTime Time;
        public double? Watch;
        public double? Session;
        public readonly List<string> Lines = new List<string>();

        public void Serialize(GenericWriter writer)
        {
            writer.Write(0); // version

            writer.Write(Time);
            writer.Write(Watch.HasValue);
            writer.Write(Watch ?? 0);
            writer.Write(Session.HasValue);
            writer.Write(Session ?? 0);

            writer.Write(Lines.Count);

            foreach (string line in Lines)
                writer.Write(line);
        }

        public void Deserialize(GenericReader reader)
        {
            reader.ReadInt(); // version

            Time = reader.ReadDateTime();

            bool hasWatch = reader.ReadBool();
            double watch = reader.ReadDouble();
            Watch = hasWatch ? watch : (double?)null;

            bool hasSession = reader.ReadBool();
            double session = reader.ReadDouble();
            Session = hasSession ? session : (double?)null;

            int count = reader.ReadInt();

            for (int i = 0; i < count; i++)
                Lines.Add(reader.ReadString());
        }
    }

    public class FlagRecord
    {
        public int Id;
        public Serial Character;
        public string Name;
        public string Account;
        public DateTime Created;
        public DateTime Updated;
        public FlagStatus Status;
        public readonly List<string> Reasons = new List<string>();
        public readonly List<Snapshot> Snapshots = new List<Snapshot>();
        public string StaffNote = String.Empty;
        public string ResolvedBy = String.Empty;
        public DateTime ResolvedAt = DateTime.MinValue;

        public Snapshot Latest => Snapshots.Count > 0 ? Snapshots[Snapshots.Count - 1] : null;

        public void Serialize(GenericWriter writer)
        {
            writer.Write(0); // version

            writer.Write(Id);
            writer.Write(Character.Value);
            writer.Write(Name);
            writer.Write(Account);
            writer.Write(Created);
            writer.Write(Updated);
            writer.Write((int)Status);

            writer.Write(Reasons.Count);

            foreach (string r in Reasons)
                writer.Write(r);

            writer.Write(Snapshots.Count);

            foreach (Snapshot s in Snapshots)
                s.Serialize(writer);

            writer.Write(StaffNote);
            writer.Write(ResolvedBy);
            writer.Write(ResolvedAt);
        }

        public void Deserialize(GenericReader reader)
        {
            reader.ReadInt(); // version

            Id = reader.ReadInt();
            Character = (Serial)reader.ReadInt();
            Name = reader.ReadString();
            Account = reader.ReadString();
            Created = reader.ReadDateTime();
            Updated = reader.ReadDateTime();
            Status = (FlagStatus)reader.ReadInt();

            int count = reader.ReadInt();

            for (int i = 0; i < count; i++)
                Reasons.Add(reader.ReadString());

            count = reader.ReadInt();

            for (int i = 0; i < count; i++)
            {
                Snapshot s = new Snapshot();
                s.Deserialize(reader);
                Snapshots.Add(s);
            }

            StaffNote = reader.ReadString();
            ResolvedBy = reader.ReadString();
            ResolvedAt = reader.ReadDateTime();
        }
    }

    /// <summary>
    /// Flags characters for staff review when they show the scout pattern or are reported
    /// by enough players, with evidence snapshots. Staff decide; nothing is done to players.
    /// Confirmed flags are the list to act on in one go ("ban wave").
    /// </summary>
    public static class Flags
    {
        private const int MaxSnapshots = 5;

        public static readonly string EvidencePath = Path.Combine("Logs", "BotWatch", "Evidence");

        public static readonly List<FlagRecord> All = new List<FlagRecord>();
        public static int NextId = 1;

        public static TimeSpan SnapshotInterval { get; private set; }
        public static TimeSpan LegitQuiet { get; private set; }

        public static void Configure()
        {
            SnapshotInterval = Config.Get("BotWatch.SnapshotInterval", TimeSpan.FromDays(1));
            LegitQuiet = Config.Get("BotWatch.LegitQuietPeriod", TimeSpan.FromDays(14));
        }

        public static FlagRecord Find(int id)
        {
            return All.FirstOrDefault(f => f.Id == id);
        }

        public static FlagRecord LatestFor(Serial character)
        {
            return All.Where(f => f.Character == character).OrderByDescending(f => f.Created).FirstOrDefault();
        }

        public static void Check(IEnumerable<Serial> only)
        {
            Check(Ratings.ComputeAll(), only);
        }

        /// <summary>
        /// Opens flags for characters that newly qualify and refreshes the evidence of open ones.
        /// </summary>
        public static void Check(Dictionary<Serial, BotProfile> profiles, IEnumerable<Serial> only)
        {
            DateTime now = DateTime.UtcNow;
            NetworkGraph g = null;
            HashSet<Serial> filter = only != null ? new HashSet<Serial>(only) : null;

            foreach (BotProfile p in profiles.Values)
            {
                CharacterRecord rec = p.Record;

                if (filter != null && !filter.Contains(rec.Serial))
                    continue;

                List<string> reasons = new List<string>();

                if (p.ScoutPattern)
                    reasons.Add(String.Format("Scout pattern (Watch {0}, core activity {1})", ProfileGump.Score(p.Watch), p.CoreActivity));

                if (p.Reporters >= Reports.FlagThreshold)
                    reasons.Add(String.Format("Reported by {0} players", p.Reporters));

                GuildResponse guild = p.GuildResponses.FirstOrDefault(r => r.Significant);

                if (guild != null)
                    reasons.Add(String.Format("Guild response ({0} after {1} of {2} idle encounters, {3:F1}x usual)", guild.Name, guild.Responses, guild.Checks, guild.Lift));

                if (reasons.Count == 0)
                    continue;

                FlagRecord flag = LatestFor(rec.Serial);

                if (flag != null && flag.Status == FlagStatus.Open)
                {
                    foreach (string r in reasons.Where(r => !flag.Reasons.Any(x => Kind(x) == Kind(r))))
                        flag.Reasons.Add(r);

                    if (flag.Latest == null || now - flag.Latest.Time >= SnapshotInterval)
                    {
                        g = g ?? Networks.Build();
                        AddSnapshot(flag, p, g);
                    }

                    continue;
                }

                // Staff already decided: legit characters stay quiet for a while, confirmed
                // and actioned ones need no new flag.
                if (flag != null && (flag.Status == FlagStatus.Confirmed || flag.Status == FlagStatus.Actioned))
                    continue;

                if (flag != null && flag.Status == FlagStatus.Legit && now - flag.ResolvedAt < LegitQuiet)
                    continue;

                g = g ?? Networks.Build();

                flag = new FlagRecord
                {
                    Id = NextId++,
                    Character = rec.Serial,
                    Name = rec.Name,
                    Account = rec.Account,
                    Created = now,
                    Status = FlagStatus.Open
                };

                flag.Reasons.AddRange(reasons);
                All.Add(flag);

                AddSnapshot(flag, p, g);

                Alerts.Raise(AlertKind.Flagged, rec.Account, rec.Serial, rec.Name, String.Format(
                    "Flag #{0}: {1} (account {2}): {3}. Review with [Flags.", flag.Id, rec.Name, rec.Account, String.Join("; ", reasons)));
            }
        }

        private static string Kind(string reason)
        {
            int i = reason.IndexOf(' ');
            return i > 0 ? reason.Substring(0, i) : reason;
        }

        public static void AddSnapshot(FlagRecord flag, BotProfile p, NetworkGraph g)
        {
            Snapshot s = Evidence.Build(p, g);

            flag.Snapshots.Add(s);
            flag.Updated = s.Time;

            // Keep the first snapshot and the most recent ones.
            while (flag.Snapshots.Count > MaxSnapshots)
                flag.Snapshots.RemoveAt(1);

            WriteFile(flag, s);
        }

        public static void SetStatus(FlagRecord flag, FlagStatus status, Mobile staff, string note)
        {
            flag.Status = status;
            flag.Updated = DateTime.UtcNow;

            if (note != null)
                flag.StaffNote = note;

            if (status == FlagStatus.Open)
            {
                flag.ResolvedBy = String.Empty;
                flag.ResolvedAt = DateTime.MinValue;
            }
            else
            {
                flag.ResolvedBy = staff?.RawName ?? String.Empty;
                flag.ResolvedAt = DateTime.UtcNow;
            }
        }

        public static void Prune(DateTime cutoff)
        {
            // Open and confirmed flags are kept until staff close them.
            All.RemoveAll(f => (f.Status == FlagStatus.Legit || f.Status == FlagStatus.Dismissed || f.Status == FlagStatus.Actioned) && f.Updated < cutoff);
        }

        public static string WriteFile(FlagRecord flag, Snapshot s)
        {
            try
            {
                Directory.CreateDirectory(EvidencePath);

                string name = new string((flag.Name ?? "unknown").Where(c => Char.IsLetterOrDigit(c) || c == '_').ToArray());
                string path = Path.Combine(EvidencePath, String.Format("flag{0}_{1}_{2:yyyyMMdd-HHmm}.txt", flag.Id, name, s.Time));

                List<string> lines = new List<string>
                {
                    String.Format("Flag #{0}: {1} (account {2}), status {3}", flag.Id, flag.Name, flag.Account, flag.Status),
                    "Reasons: " + String.Join("; ", flag.Reasons),
                    String.Empty
                };

                lines.AddRange(s.Lines);
                File.WriteAllLines(path, lines);

                return path;
            }
            catch (Exception e)
            {
                Diagnostics.ExceptionLogging.LogException(e);
                return null;
            }
        }
    }

    /// <summary>Builds the readable evidence for a character.</summary>
    public static class Evidence
    {
        private const int Encounters = 25;
        private const int SessionCount = 15;

        public static Snapshot Build(BotProfile p, NetworkGraph g)
        {
            DateTime now = DateTime.UtcNow;
            CharacterRecord rec = p.Record;
            HourBucket b = p.Window;

            Snapshot s = new Snapshot { Time = now, Watch = p.Watch, Session = p.Session };
            List<string> l = s.Lines;

            l.Add(String.Format("== Summary ({0:yyyy-MM-dd HH:mm} UTC, last {1:F0} days)", now, Ratings.Window.TotalDays));
            l.Add(String.Format("Character {0} (serial {1}), account {2}, character created {3}, account created {4}{5}",
                rec.Name, rec.Serial, rec.Account, Date(rec.CharacterCreated), Date(rec.AccountCreated), rec.IsDeleted ? ", DELETED " + Date(rec.Deleted) : ""));
            l.Add(String.Format("Watch {0}, Session {1}, scout pattern: {2}", ProfileGump.Score(p.Watch), ProfileGump.Score(p.Session), p.ScoutPattern ? "yes" : "no"));
            l.Add(String.Format("Online {0:F1}h, outside towns {1:F1}h, {2} sessions, skills {3:F1}, bank items {4}",
                p.OnlineHours, b.OutdoorSeconds / 3600.0, p.SessionCount, rec.SkillsTotal / 10.0, rec.BankItems));
            l.Add("Activity ratings: " + String.Join(", ", Enum.GetValues(typeof(RatingCategory)).Cast<RatingCategory>().Select(c => String.Format("{0} {1}", c, p[c]))));

            l.Add(String.Empty);
            l.Add("== Watch parts");
            AddParts(l, p.WatchParts);

            l.Add(String.Empty);
            l.Add("== Session parts");
            AddParts(l, p.SessionParts);

            l.Add(String.Empty);
            l.Add(String.Format("== Last {0} encounters (UTC)", Encounters));

            foreach (EncounterNote e in rec.RecentEncounters.AsEnumerable().Reverse().Take(Encounters))
            {
                l.Add(String.Format("{0:MM-dd HH:mm} {1} {2},{3}: saw {4}{5}{6}{7}",
                    e.Time, e.Map, e.Location.X, e.Location.Y, e.OtherName,
                    e.Idle ? ", idle" : ", active",
                    e.Reacted ? ", reacted" : ", no reaction",
                    e.NearTeleporter ? ", near teleporter" : ""));
            }

            if (rec.RecentEncounters.Count == 0)
                l.Add("none recorded");

            l.Add(String.Empty);
            l.Add(String.Format("== Last {0} sessions (UTC)", SessionCount));

            foreach (SessionRecord ses in BotWatch.GetSessions(rec.Serial).OrderByDescending(x => x.Start).Take(SessionCount))
            {
                l.Add(String.Format("{0:MM-dd HH:mm} {1,4:F0} min from {2}: in at {3} {4},{5}{6}, out at {7}, {8} meaningful actions{9}",
                    ses.Start, ses.Duration.TotalMinutes, ses.Address,
                    ses.StartMap, ses.StartLocation.X, ses.StartLocation.Y, ses.StartInTown ? " (town)" : "",
                    ses.Open ? "online" : String.Format("{0} {1},{2}{3}", ses.EndMap, ses.EndLocation.X, ses.EndLocation.Y, ses.EndInTown ? " (town)" : ""),
                    ses.MeaningfulActions, ses.IsParked(BotWatch.ReactionMoveTiles) ? ", PARKED" : ""));
            }

            l.Add(String.Empty);
            l.Add("== Network");

            AccountNetwork n = g.NetworkOf(rec.Account);

            if (n == null || n.Accounts.Count < 2)
            {
                l.Add("no linked accounts");
            }
            else
            {
                foreach (AccountLink link in g.LinksOf(rec.Account))
                    l.Add(String.Format("linked to {0}: {1}", link.Other(rec.Account), link.Describe()));

                foreach (string account in n.Accounts.Where(a => a != rec.Account))
                {
                    if (g.Accounts.TryGetValue(account, out AccountInfo info))
                        l.Add(String.Format("{0}{1}: {2}", account, info.IsFresh ? " (new)" : "", String.Join(", ", info.Characters.Select(c => c.Name))));
                }
            }

            l.Add(String.Empty);
            l.Add("== Guild responses (guilds arriving after idle encounters, against their usual rate at those spots)");

            foreach (GuildResponse r in p.GuildResponses.Take(5))
                l.Add((r.Significant ? "SIGNIFICANT: " : "") + r.Describe());

            if (p.GuildResponses.Count == 0)
                l.Add(rec.GuildChecks.Count > 0 ? "no guild arrived after its idle encounters" : "no idle encounters checked yet");

            l.Add(String.Empty);
            l.Add("== Player reports");

            List<ReportRecord> reports = Reports.For(rec.Serial, now - Ratings.Window).OrderByDescending(r => r.Time).ToList();

            foreach (ReportRecord r in reports)
            {
                l.Add(String.Format("{0:MM-dd HH:mm} by {1} (account {2}) at {3} {4},{5}{6}",
                    r.Time, r.ReporterName, r.ReporterAccount, r.Map, r.Location.X, r.Location.Y,
                    String.IsNullOrEmpty(r.Note) ? "" : ": \"" + r.Note + "\""));
            }

            if (reports.Count == 0)
                l.Add("none");

            l.Add(String.Empty);
            l.Add("== Raw counters");
            l.Add(String.Join(", ", Enum.GetValues(typeof(Activity)).Cast<Activity>()
                .Where(a => b[a] > 0)
                .Select(a => String.Format("{0} {1}", a, b[a]))));

            return s;
        }

        private static void AddParts(List<string> l, List<ScoreComponent> parts)
        {
            foreach (ScoreComponent c in parts)
            {
                l.Add(String.Format("{0} (weight {1}): {2} - {3}", c.Name, c.Weight,
                    c.Value.HasValue ? String.Format("{0:F0}%", c.Value.Value * 100) : "n/a", c.Detail));
            }
        }

        private static string Date(DateTime d)
        {
            return d == DateTime.MinValue ? "?" : d.ToString("yyyy-MM-dd");
        }
    }
}
