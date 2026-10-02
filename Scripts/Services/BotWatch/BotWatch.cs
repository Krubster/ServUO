using Server.Accounting;
using Server.Engines.PartySystem;
using Server.Guilds;
using Server.Items;
using Server.Mobiles;
using Server.Regions;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Server.Services.BotWatch
{
    /// <summary>
    /// Records what player characters do, whom they see and when they play, so staff can
    /// tell real players from scout bots. Observe-only. Configured in Config/BotWatch.cfg.
    /// Event subscriptions and hook handlers are in BotWatchEvents.cs.
    /// </summary>
    public static partial class BotWatch
    {
        public static readonly string SavePath = Path.Combine("Saves", "BotWatch", "BotWatch.bin");

        private const int StepWindow = 100;

        public static bool Enabled { get; private set; }
        public static TimeSpan ScanInterval { get; private set; }
        public static string[] TownRegions { get; private set; }
        public static TimeSpan EncounterCooldown { get; private set; }
        public static TimeSpan IdleWindow { get; private set; }
        public static TimeSpan ReactionWindow { get; private set; }
        public static int ReactionMoveTiles { get; private set; }
        public static int TeleporterRange { get; private set; }
        public static TimeSpan TeleporterGrace { get; private set; }
        public static TimeSpan ActivityRetention { get; private set; }
        public static TimeSpan SessionRetention { get; private set; }

        public static readonly Dictionary<Serial, CharacterRecord> Characters = new Dictionary<Serial, CharacterRecord>();
        public static readonly List<SessionRecord> Sessions = new List<SessionRecord>();
        public static readonly Dictionary<string, AddressRecord> Addresses = new Dictionary<string, AddressRecord>();

        private static readonly Dictionary<Mobile, LiveState> m_Live = new Dictionary<Mobile, LiveState>();
        private static DateTime m_LastScan = DateTime.UtcNow;

        /// <summary>When BotWatch started collecting on this shard.</summary>
        public static DateTime DataSince { get; private set; } = DateTime.UtcNow;

        public static int LiveCount => m_Live.Count;
        public static int PendingEncounterCount => m_Live.Values.Sum(l => l.Pending.Count);

        /// <summary>
        /// Runtime state of a character that is online, or offline with encounters or
        /// attacks still waiting to be resolved.
        /// </summary>
        private class LiveState
        {
            public CharacterRecord Record;
            public SessionRecord Session;
            public bool Online;
            public Map LastMap;

            public readonly List<DateTime> Meaningful = new List<DateTime>();
            public DateTime LastMeaningful = DateTime.MinValue;
            public DateTime LastResponse = DateTime.MinValue;

            public HashSet<Mobile> InView = new HashSet<Mobile>();
            public readonly Dictionary<Serial, DateTime> LastEncounter = new Dictionary<Serial, DateTime>();
            public readonly List<PendingEncounter> Pending = new List<PendingEncounter>();

            public readonly Dictionary<Serial, DateTime> LastAttackedBy = new Dictionary<Serial, DateTime>();
            public readonly List<PendingAttack> Attacks = new List<PendingAttack>();

            public DateTime TeleporterSince = DateTime.MinValue;
            public int SessionOutdoorSeconds;

            public long CellsHour;
            public readonly HashSet<int> CellsSeen = new HashSet<int>();

            public Map StepMap;
            public readonly List<int> StepTiles = new List<int>(StepWindow);
        }

        private class PendingEncounter
        {
            public DateTime Time;
            public Serial Other;
            public Map Map;
            public Point3D Location;
            public bool NearTeleporter;
            public bool Reacted;
        }

        private class PendingAttack
        {
            public DateTime Time;
            public Map Map;
            public Point3D Location;
            public bool Moved;
        }

        public static void Configure()
        {
            Enabled = Config.Get("BotWatch.Enabled", true);
            ScanInterval = Config.Get("BotWatch.ScanInterval", TimeSpan.FromSeconds(2));
            TownRegions = Config.Get("BotWatch.TownRegions", String.Empty)
                .Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => s.Length > 0)
                .ToArray();
            EncounterCooldown = Config.Get("BotWatch.EncounterCooldown", TimeSpan.FromMinutes(10));
            IdleWindow = Config.Get("BotWatch.IdleWindow", TimeSpan.FromMinutes(5));
            ReactionWindow = Config.Get("BotWatch.ReactionWindow", TimeSpan.FromSeconds(30));
            ReactionMoveTiles = Config.Get("BotWatch.ReactionMoveTiles", 8);
            TeleporterRange = Config.Get("BotWatch.TeleporterRange", 12);
            TeleporterGrace = Config.Get("BotWatch.TeleporterGrace", TimeSpan.FromMinutes(2));
            ActivityRetention = Config.Get("BotWatch.ActivityRetention", TimeSpan.FromDays(30));
            SessionRetention = Config.Get("BotWatch.SessionRetention", TimeSpan.FromDays(90));

            Ratings.Configure();
            Networks.Configure();
            Alerts.Configure();
            Reports.Configure();
            Flags.Configure();
            GuildReactions.Configure();

            EventSink.WorldSave += e => Save();
            EventSink.WorldLoad += Load;
        }

        public static void Initialize()
        {
            if (!Enabled)
                return;

            SubscribeEvents();

            TeleporterIndex.Initialize();
            Alerts.Initialize();
            Reports.Initialize();

            Timer.DelayCall(ScanInterval, ScanInterval, Scan);

            BotWatchCommands.Initialize();
        }

        #region Helpers
        public static bool IsTracked(Mobile m)
        {
            return m is PlayerMobile && !m.Deleted && m.AccessLevel == AccessLevel.Player;
        }

        /// <summary>
        /// Activities that count against idleness. Self-heals, self-buffs, skill use, spells,
        /// item use, travel and speech are left out because a bot can repeat them at its post.
        /// </summary>
        private static readonly HashSet<Activity> m_Meaningful = new HashSet<Activity>
        {
            Activity.PvMAttack, Activity.PvMKill, Activity.PvPAttack, Activity.PvPKill,
            Activity.Craft, Activity.Gather, Activity.Trade,
            Activity.HealOther, Activity.BuffOther, Activity.PlayerTrade,
            Activity.LootPvM, Activity.LootPvP,
            Activity.QuestComplete, Activity.BODTaken, Activity.BODTurnedIn, Activity.Tame,
            Activity.TargetPlayer, Activity.AssistOther, Activity.RevealedOther
        };

        /// <summary>
        /// Things that happen to a character rather than things it does. Everything else
        /// counts as a response when the character is attacked.
        /// </summary>
        private static readonly HashSet<Activity> m_Passive = new HashSet<Activity>
        {
            Activity.SkillGain, Activity.PvPDeath, Activity.PvMDeath, Activity.ItemObtained,
            Activity.AttackedByPlayer, Activity.AttackedNoResponse, Activity.RevealedByOther,
            Activity.FastWalk, Activity.DungeonEnter, Activity.GuildJoin, Activity.PartyJoin
        };

        public static bool IsMeaningful(Activity a)
        {
            return m_Meaningful.Contains(a);
        }

        public static bool IsTown(Region r)
        {
            if (TownRegions.Length > 0)
                return TownRegions.Any(r.IsPartOf);

            GuardedRegion g = (GuardedRegion)r.GetRegion(typeof(GuardedRegion));

            return g != null && !g.IsDisabled();
        }

        /// <summary>Towns and houses: nothing that happens there counts as watching.</summary>
        public static bool IsSafe(Mobile m)
        {
            return m.Map == null || m.Map == Map.Internal || IsTown(m.Region) || m.Region.IsPartOf<HouseRegion>();
        }

        public static CharacterRecord GetRecord(Mobile m)
        {
            if (!Characters.TryGetValue(m.Serial, out CharacterRecord rec))
            {
                Characters[m.Serial] = rec = new CharacterRecord { Serial = m.Serial };
            }

            rec.Name = m.RawName;
            rec.Account = m.Account != null ? m.Account.Username : rec.Account;
            rec.CharacterCreated = m.CreationTime;

            if (m.Account is Account a)
                rec.AccountCreated = a.Created;

            return rec;
        }

        public static CharacterRecord FindRecord(string name)
        {
            return Characters.Values
                .Where(r => String.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(r => r.LastActive)
                .FirstOrDefault();
        }

        public static bool IsOnline(Serial s)
        {
            Mobile m = World.FindMobile(s);

            return m != null && m_Live.TryGetValue(m, out LiveState live) && live.Online;
        }

        private static LiveState GetLive(Mobile m)
        {
            if (!m_Live.TryGetValue(m, out LiveState live))
            {
                m_Live[m] = live = new LiveState { Record = GetRecord(m) };
            }

            return live;
        }

        /// <summary>Pets and summons act on behalf of their master.</summary>
        private static Mobile Owner(Mobile m)
        {
            if (m is BaseCreature bc)
                return bc.GetMaster();

            return m;
        }
        #endregion

        #region Recording
        public static void Record(Mobile m, Activity activity)
        {
            Record(m, activity, 1);
        }

        public static void Record(Mobile m, Activity activity, int amount)
        {
            if (!IsTracked(m))
                return;

            DateTime now = DateTime.UtcNow;
            LiveState live = GetLive(m);

            live.Record.GetHour(now).Counts[(int)activity] += amount;

            if (IsMeaningful(activity))
            {
                live.Meaningful.Add(now);
                live.LastMeaningful = now;

                if (live.Session != null && live.Session.Open)
                    live.Session.MeaningfulActions++;
            }

            if (!m_Passive.Contains(activity))
                live.LastResponse = now;
        }

        /// <summary>
        /// Doing something to a player shortly after encountering them is a reaction to
        /// that encounter.
        /// </summary>
        private static void MarkReaction(Mobile m, Mobile other)
        {
            if (!IsTracked(m) || other == null || !m_Live.TryGetValue(m, out LiveState live))
                return;

            DateTime now = DateTime.UtcNow;

            foreach (PendingEncounter p in live.Pending)
            {
                if (p.Other == other.Serial && now - p.Time <= ReactionWindow)
                    p.Reacted = true;
            }
        }

        /// <summary>
        /// A player attacked this character. Whether it responds is resolved after the
        /// reaction window; one attack per attacker is counted every two minutes.
        /// </summary>
        private static void RecordAttacked(Mobile victim, Mobile attacker)
        {
            if (!IsTracked(victim) || !IsTracked(attacker))
                return;

            DateTime now = DateTime.UtcNow;
            LiveState live = GetLive(victim);

            if (live.LastAttackedBy.TryGetValue(attacker.Serial, out DateTime last) && now - last < TimeSpan.FromMinutes(2))
                return;

            live.LastAttackedBy[attacker.Serial] = now;

            Record(victim, Activity.AttackedByPlayer);

            live.Attacks.Add(new PendingAttack { Time = now, Map = victim.Map, Location = victim.Location });
        }

        /// <summary>
        /// One step outside towns. Steps are grouped in windows of 100 and each window
        /// records how many distinct tiles it covered.
        /// </summary>
        private static void RecordStep(Mobile m)
        {
            if (!IsTracked(m) || !m_Live.TryGetValue(m, out LiveState live) || !live.Online || IsSafe(m))
                return;

            if (live.StepMap != m.Map)
            {
                live.StepMap = m.Map;
                live.StepTiles.Clear();
            }

            HourBucket bucket = live.Record.GetHour(DateTime.UtcNow);

            bucket.Steps++;
            live.StepTiles.Add((m.X << 16) | m.Y);

            if (live.StepTiles.Count >= StepWindow)
            {
                bucket.StepWindows++;
                bucket.StepWindowDistinct += live.StepTiles.Distinct().Count();
                live.StepTiles.Clear();
            }
        }
        #endregion

        #region Sessions
        private static void StartSession(Mobile m)
        {
            if (!IsTracked(m))
                return;

            DateTime now = DateTime.UtcNow;
            LiveState live = GetLive(m);

            if (live.Session != null && live.Session.Open)
                CloseSession(m, live, now);

            string address = m.NetState?.Address?.ToString() ?? "?";

            // An account BotWatch has never seen. Only meaningful once BotWatch has been
            // collecting long enough to have seen the shard's regular accounts.
            bool firstSeen = now - DataSince >= TimeSpan.FromDays(Ratings.FreshDays) &&
                             !Addresses.Values.Any(a => a.Account == live.Record.Account);

            live.Online = true;
            live.SessionOutdoorSeconds = 0;
            live.LastMap = m.Map;
            live.InView.Clear();
            live.StepTiles.Clear();
            live.Session = new SessionRecord
            {
                Account = live.Record.Account,
                Character = m.Serial,
                CharacterName = m.RawName,
                Address = address,
                Start = now,
                End = DateTime.MinValue,
                LastSeen = now,
                StartMap = m.Map,
                StartLocation = m.Location,
                StartInTown = IsSafe(m)
            };

            Sessions.Add(live.Session);

            string key = live.Record.Account + "|" + address;

            if (!Addresses.TryGetValue(key, out AddressRecord ar))
            {
                Addresses[key] = ar = new AddressRecord { Account = live.Record.Account, Address = address, FirstSeen = now };
            }

            ar.LastSeen = now;
            ar.Sessions++;

            Alerts.CheckNewAccount(m, live.Record, address, firstSeen);
        }

        private static void EndSession(Mobile m)
        {
            if (m != null && m_Live.TryGetValue(m, out LiveState live))
                CloseSession(m, live, DateTime.UtcNow);
        }

        private static void CloseSession(Mobile m, LiveState live, DateTime now)
        {
            live.Online = false;
            live.InView.Clear();
            live.TeleporterSince = DateTime.MinValue;

            SessionRecord s = live.Session;

            if (s != null && s.Open)
            {
                s.End = now;
                s.LastSeen = now;
                s.EndMap = m.Map;
                s.EndLocation = m.Location;
                s.EndInTown = IsSafe(m);
            }

            CharacterRecord rec = live.Record;

            rec.SkillsTotal = m.SkillsTotal;
            rec.BackpackItems = m.Backpack != null ? m.Backpack.TotalItems : 0;

            BankBox bank = m.FindBankNoCreate();
            rec.BankItems = bank != null ? bank.TotalItems : 0;
        }

        public static IEnumerable<SessionRecord> GetSessions(Serial character)
        {
            return Sessions.Where(s => s.Character == character);
        }
        #endregion

        #region Scan
        private static void Scan()
        {
            DateTime now = DateTime.UtcNow;
            int seconds = (int)Math.Round(Math.Min((now - m_LastScan).TotalSeconds, ScanInterval.TotalSeconds * 5));
            m_LastScan = now;

            foreach (KeyValuePair<Mobile, LiveState> kv in m_Live.ToList())
            {
                Mobile m = kv.Key;
                LiveState live = kv.Value;

                if (live.Online && m.NetState != null && !m.Deleted && m.Map != null && m.Map != Map.Internal)
                    ScanOnline(m, live, now, seconds);

                ResolveEncounters(m, live, now);
                ResolveAttacks(m, live, now);

                live.Meaningful.RemoveAll(t => now - t > IdleWindow + IdleWindow);

                foreach (Serial s in live.LastEncounter.Where(p => now - p.Value > EncounterCooldown).Select(p => p.Key).ToList())
                    live.LastEncounter.Remove(s);

                foreach (Serial s in live.LastAttackedBy.Where(p => now - p.Value > TimeSpan.FromMinutes(2)).Select(p => p.Key).ToList())
                    live.LastAttackedBy.Remove(s);

                if (!live.Online && live.Pending.Count == 0 && live.Attacks.Count == 0)
                    m_Live.Remove(m);
            }

            GuildReactions.Process(now);
        }

        private static void ScanOnline(Mobile m, LiveState live, DateTime now, int seconds)
        {
            HourBucket bucket = live.Record.GetHour(now);
            bool outdoor = !IsSafe(m);

            if (live.Session != null)
                live.Session.LastSeen = now;

            bucket.OnlineSeconds += seconds;

            if (outdoor)
            {
                bucket.OutdoorSeconds += seconds;
                live.SessionOutdoorSeconds += seconds;

                Alerts.CheckFreshIdle(m, live.Record, live.Session, live.SessionOutdoorSeconds);
            }

            // Moving to another facet raises no teleport event, so it is caught here.
            if (live.LastMap != null && live.LastMap != m.Map)
                Record(m, Activity.TravelJump);

            live.LastMap = m.Map;

            // Exploration: distinct 16x16 areas visited this hour.
            long hour = CharacterRecord.HourOf(now);

            if (live.CellsHour != hour)
            {
                live.CellsHour = hour;
                live.CellsSeen.Clear();
            }

            if (live.CellsSeen.Add((m.Map.MapID << 24) | ((m.X >> 4) << 12) | (m.Y >> 4)))
                bucket.Cells++;

            if (outdoor && !m.Alive)
                bucket.GhostOutdoorSeconds += seconds;

            if (outdoor && m.Hidden)
                bucket.HiddenOutdoorSeconds += seconds;

            // Idle presence near a teleporter. Walking around it does not reset this, only
            // leaving its range or doing something meaningful does.
            if (outdoor && TeleporterIndex.IsNear(m.Map, m.Location, TeleporterRange))
            {
                if (live.TeleporterSince == DateTime.MinValue)
                    live.TeleporterSince = now;

                if (now - live.TeleporterSince >= TeleporterGrace && now - live.LastMeaningful >= TeleporterGrace)
                    bucket.TeleporterIdleSeconds += seconds;
            }
            else
            {
                live.TeleporterSince = DateTime.MinValue;
            }

            GuildReactions.Sample(m, now);

            ScanEncounters(m, live, now, outdoor);
        }

        private static void ScanEncounters(Mobile m, LiveState live, DateTime now, bool outdoor)
        {
            HashSet<Mobile> inView = new HashSet<Mobile>();

            if (outdoor)
            {
                IPooledEnumerable<Mobile> eable = m.Map.GetMobilesInRange(m.Location, Core.GlobalUpdateRange);

                foreach (Mobile o in eable)
                {
                    if (IsEncounterCandidate(m, o))
                        inView.Add(o);
                }

                eable.Free();
            }

            foreach (Mobile o in inView)
            {
                if (live.InView.Contains(o))
                    continue;

                if (live.LastEncounter.TryGetValue(o.Serial, out DateTime last) && now - last < EncounterCooldown)
                    continue;

                live.LastEncounter[o.Serial] = now;
                live.Pending.Add(new PendingEncounter
                {
                    Time = now,
                    Other = o.Serial,
                    Map = m.Map,
                    Location = m.Location,
                    NearTeleporter = TeleporterIndex.IsNear(m.Map, m.Location, TeleporterRange)
                });
            }

            live.InView = inView;
        }

        /// <summary>
        /// Another online player this character can see, outside towns and houses, and not
        /// a guildmate, ally, party member or a character from the same account or address.
        /// </summary>
        private static bool IsEncounterCandidate(Mobile m, Mobile o)
        {
            if (o == m || !IsTracked(o) || o.NetState == null || !m.CanSee(o) || IsSafe(o))
                return false;

            if (o.Account == m.Account)
                return false;

            if (m.NetState != null && m.NetState.Address != null && m.NetState.Address.Equals(o.NetState.Address))
                return false;

            Party party = Party.Get(m);

            if (party != null && party == Party.Get(o))
                return false;

            if (m.Guild is Guild g && o.Guild is Guild og && (g == og || g.IsAlly(og)))
                return false;

            return true;
        }

        private static bool MovedAway(Mobile m, LiveState live, Map map, Point3D location)
        {
            return live.Online && (m.Map != map || !Utility.InRange(m.Location, location, ReactionMoveTiles));
        }

        private static void ResolveEncounters(Mobile m, LiveState live, DateTime now)
        {
            for (int i = live.Pending.Count - 1; i >= 0; i--)
            {
                PendingEncounter p = live.Pending[i];

                // Leaving the spot (walking away, recalling, gating) right after seeing
                // someone counts as reacting to them.
                if (!p.Reacted && now - p.Time <= ReactionWindow && MovedAway(m, live, p.Map, p.Location))
                    p.Reacted = true;

                if (now - p.Time < IdleWindow)
                    continue;

                bool idle = !live.Meaningful.Any(t => t >= p.Time - IdleWindow && t <= p.Time + IdleWindow);
                HourBucket bucket = live.Record.GetHour(p.Time);

                bucket.Encounters++;

                if (idle)
                    bucket.IdleEncounters++;

                if (p.Reacted)
                    bucket.ReactedEncounters++;

                if (p.NearTeleporter)
                {
                    bucket.TeleporterEncounters++;

                    if (idle)
                        bucket.TeleporterIdleEncounters++;
                }

                Mobile other = World.FindMobile(p.Other);

                EncounterNote note = new EncounterNote
                {
                    Time = p.Time,
                    Map = p.Map,
                    Location = p.Location,
                    Other = p.Other,
                    OtherName = other != null ? other.RawName : "?",
                    Idle = idle,
                    Reacted = p.Reacted,
                    NearTeleporter = p.NearTeleporter
                };

                live.Record.AddEncounter(note);

                if (idle)
                    GuildReactions.OnIdleEncounter(m, live.Record, note);

                live.Pending.RemoveAt(i);
            }
        }

        private static void ResolveAttacks(Mobile m, LiveState live, DateTime now)
        {
            for (int i = live.Attacks.Count - 1; i >= 0; i--)
            {
                PendingAttack p = live.Attacks[i];

                if (!p.Moved && MovedAway(m, live, p.Map, p.Location))
                    p.Moved = true;

                if (now - p.Time < ReactionWindow)
                    continue;

                if (!p.Moved && live.LastResponse < p.Time)
                    live.Record.GetHour(p.Time).Counts[(int)Activity.AttackedNoResponse]++;

                live.Attacks.RemoveAt(i);
            }
        }
        #endregion

        #region Persistence
        private static void Save()
        {
            DateTime now = DateTime.UtcNow;

            foreach (CharacterRecord rec in Characters.Values)
                rec.Prune(now - ActivityRetention);

            foreach (Serial s in Characters.Where(kv => kv.Value.Hours.Count == 0 && World.FindMobile(kv.Key) == null).Select(kv => kv.Key).ToList())
                Characters.Remove(s);

            Sessions.RemoveAll(s => !s.Open && now - s.End > SessionRetention);

            foreach (string key in Addresses.Where(kv => now - kv.Value.LastSeen > SessionRetention).Select(kv => kv.Key).ToList())
                Addresses.Remove(key);

            Alerts.Prune(now - SessionRetention);
            Reports.Prune(now - SessionRetention);
            Flags.Prune(now - SessionRetention);

            Persistence.Serialize(SavePath, writer =>
            {
                writer.Write(2); // version

                writer.Write(Characters.Count);

                foreach (CharacterRecord rec in Characters.Values)
                    rec.Serialize(writer);

                writer.Write(Sessions.Count);

                foreach (SessionRecord s in Sessions)
                    s.Serialize(writer);

                writer.Write(Addresses.Count);

                foreach (AddressRecord a in Addresses.Values)
                    a.Serialize(writer);

                // version 1
                writer.Write(DataSince);
                writer.Write(Alerts.Recent.Count);

                foreach (AlertRecord a in Alerts.Recent)
                    a.Serialize(writer);

                // version 2
                writer.Write(Reports.All.Count);

                foreach (ReportRecord r in Reports.All)
                    r.Serialize(writer);

                writer.Write(Reports.Blocked.Count);

                foreach (string account in Reports.Blocked)
                    writer.Write(account);

                writer.Write(Flags.NextId);
                writer.Write(Flags.All.Count);

                foreach (FlagRecord f in Flags.All)
                    f.Serialize(writer);
            });
        }

        private static void Load()
        {
            Persistence.Deserialize(SavePath, reader =>
            {
                int version = reader.ReadInt();

                int count = reader.ReadInt();

                for (int i = 0; i < count; i++)
                {
                    CharacterRecord rec = new CharacterRecord();
                    rec.Deserialize(reader);
                    Characters[rec.Serial] = rec;
                }

                count = reader.ReadInt();

                for (int i = 0; i < count; i++)
                {
                    SessionRecord s = new SessionRecord();
                    s.Deserialize(reader);

                    // Sessions still open were cut by a crash or shutdown.
                    if (s.Open)
                        s.End = s.LastSeen;

                    Sessions.Add(s);
                }

                count = reader.ReadInt();

                for (int i = 0; i < count; i++)
                {
                    AddressRecord a = new AddressRecord();
                    a.Deserialize(reader);
                    Addresses[a.Account + "|" + a.Address] = a;
                }

                if (version >= 1)
                {
                    DataSince = reader.ReadDateTime();

                    count = reader.ReadInt();

                    for (int i = 0; i < count; i++)
                    {
                        AlertRecord a = new AlertRecord();
                        a.Deserialize(reader);
                        Alerts.Recent.Add(a);
                    }
                }
                else
                {
                    // Saved before DataSince existed: the earliest record is the best guess.
                    DateTime earliest = Addresses.Values.Select(a => a.FirstSeen).DefaultIfEmpty(DateTime.UtcNow).Min();

                    if (earliest < DataSince)
                        DataSince = earliest;
                }

                if (version >= 2)
                {
                    count = reader.ReadInt();

                    for (int i = 0; i < count; i++)
                    {
                        ReportRecord r = new ReportRecord();
                        r.Deserialize(reader);
                        Reports.All.Add(r);
                    }

                    count = reader.ReadInt();

                    for (int i = 0; i < count; i++)
                        Reports.Blocked.Add(reader.ReadString());

                    Flags.NextId = reader.ReadInt();
                    count = reader.ReadInt();

                    for (int i = 0; i < count; i++)
                    {
                        FlagRecord f = new FlagRecord();
                        f.Deserialize(reader);
                        Flags.All.Add(f);
                    }
                }
            });
        }
        #endregion
    }
}
