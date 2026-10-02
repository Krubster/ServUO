using Server.Guilds;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Server.Services.BotWatch
{
    /// <summary>A check of which guilds converged on a spot, for evidence and statistics.</summary>
    public class GuildCheck
    {
        public DateTime Time;
        public int[] Guilds;

        public void Serialize(GenericWriter writer)
        {
            writer.Write(Time);
            writer.Write(Guilds.Length);

            foreach (int g in Guilds)
                writer.Write(g);
        }

        public static GuildCheck Deserialize(GenericReader reader)
        {
            GuildCheck c = new GuildCheck { Time = reader.ReadDateTime() };

            c.Guilds = new int[reader.ReadInt()];

            for (int i = 0; i < c.Guilds.Length; i++)
                c.Guilds[i] = reader.ReadInt();

            return c;
        }
    }

    public class GuildResponse
    {
        public int GuildId;
        public string Name;
        public int Responses;
        public int Checks;
        public double BaselineRate;
        public double Lift;
        public bool Significant;

        public string Describe()
        {
            return String.Format("{0} arrived after {1} of {2} idle encounters ({3:F0}%, {4:F1}x its usual rate at those spots)",
                Name, Responses, Checks, 100.0 * Responses / Math.Max(1, Checks), Lift);
        }
    }

    /// <summary>
    /// Ties scouts to the guild they work for. After each idle encounter, checks which guilds
    /// had a member far away at that moment who arrived at the spot shortly after, and
    /// compares that with how often the same guilds arrive there at random moments.
    /// Position trails are kept in memory for a few hours only.
    /// </summary>
    public static class GuildReactions
    {
        private struct TrailPoint
        {
            public DateTime Time;
            public Map Map;
            public int X, Y;
            public int Guild;
        }

        private class Trail
        {
            public Serial Serial;
            public string Account;
            public string Address;
            public DateTime LastSample;
            public readonly List<TrailPoint> Points = new List<TrailPoint>();
        }

        private class PendingCheck
        {
            public Serial Observer;
            public string Account;
            public string Address;
            public Map Map;
            public int X, Y;
            public DateTime Time;
            public Serial Seen;
            public int SeenGuild;
        }

        private const int MaxChecks = 200;
        private const int MaxBaseline = 600;

        public static bool Enabled { get; private set; }
        public static TimeSpan SampleInterval { get; private set; }
        public static TimeSpan TrailLength { get; private set; }
        public static TimeSpan ResponseWindow { get; private set; }
        public static int NearRange { get; private set; }
        public static int FarRange { get; private set; }
        public static int BaselineSamples { get; private set; }
        public static int MinResponses { get; private set; }
        public static double MinLift { get; private set; }

        private static readonly Dictionary<Serial, Trail> m_Trails = new Dictionary<Serial, Trail>();
        private static readonly List<PendingCheck> m_Pending = new List<PendingCheck>();

        public static void Configure()
        {
            Enabled = Config.Get("BotWatch.GuildReactions", true);
            SampleInterval = Config.Get("BotWatch.TrailSampleInterval", TimeSpan.FromSeconds(30));
            TrailLength = Config.Get("BotWatch.TrailLength", TimeSpan.FromHours(3));
            ResponseWindow = Config.Get("BotWatch.GuildResponseWindow", TimeSpan.FromMinutes(10));
            NearRange = Config.Get("BotWatch.GuildNearRange", 24);
            FarRange = Config.Get("BotWatch.GuildFarRange", 60);
            BaselineSamples = Config.Get("BotWatch.GuildBaselineSamples", 3);
            MinResponses = Config.Get("BotWatch.GuildMinResponses", 5);
            MinLift = Config.Get("BotWatch.GuildMinLift", 3.0);
        }

        /// <summary>Called from the scan for every online character.</summary>
        public static void Sample(Mobile m, DateTime now)
        {
            if (!Enabled)
                return;

            if (!m_Trails.TryGetValue(m.Serial, out Trail trail))
                m_Trails[m.Serial] = trail = new Trail { Serial = m.Serial };

            if (now - trail.LastSample < SampleInterval)
                return;

            trail.LastSample = now;
            trail.Account = m.Account?.Username;
            trail.Address = m.NetState?.Address?.ToString();
            trail.Points.Add(new TrailPoint { Time = now, Map = m.Map, X = m.X, Y = m.Y, Guild = m.Guild != null ? m.Guild.Id : 0 });
        }

        /// <summary>An idle encounter was resolved; check for responses once the window has passed.</summary>
        public static void OnIdleEncounter(Mobile observer, CharacterRecord rec, EncounterNote note)
        {
            if (!Enabled || note.Map == null)
                return;

            Mobile seen = World.FindMobile(note.Other);

            m_Pending.Add(new PendingCheck
            {
                Observer = rec.Serial,
                Account = rec.Account,
                Address = observer?.NetState?.Address?.ToString(),
                Map = note.Map,
                X = note.Location.X,
                Y = note.Location.Y,
                Time = note.Time,
                Seen = note.Other,
                SeenGuild = seen?.Guild != null ? seen.Guild.Id : 0
            });
        }

        /// <summary>Called from the scan: evaluates due checks and trims old trail points.</summary>
        public static void Process(DateTime now)
        {
            if (!Enabled)
                return;

            for (int i = m_Pending.Count - 1; i >= 0; i--)
            {
                PendingCheck c = m_Pending[i];

                if (now - c.Time < ResponseWindow)
                    continue;

                m_Pending.RemoveAt(i);

                if (!BotWatch.Characters.TryGetValue(c.Observer, out CharacterRecord rec))
                    continue;

                Add(rec.GuildChecks, new GuildCheck { Time = c.Time, Guilds = Converged(c, c.Time) }, MaxChecks);

                // Baseline: the same spot at random moments away from this character's encounters.
                DateTime earliest = now - TrailLength;
                DateTime latest = now - ResponseWindow;

                for (int s = 0; s < BaselineSamples && latest > earliest; s++)
                {
                    DateTime t = earliest + TimeSpan.FromTicks((long)(Utility.RandomDouble() * (latest - earliest).Ticks));

                    if (rec.RecentEncounters.Any(e => (e.Time - t).Duration() < ResponseWindow))
                        continue;

                    Add(rec.GuildBaseline, new GuildCheck { Time = t, Guilds = Converged(c, t) }, MaxBaseline);
                }
            }

            DateTime cutoff = now - TrailLength;

            foreach (Trail trail in m_Trails.Values.ToList())
            {
                trail.Points.RemoveAll(p => p.Time < cutoff);

                if (trail.Points.Count == 0 && now - trail.LastSample > TrailLength)
                    m_Trails.Remove(trail.Serial);
            }
        }

        private static void Add(List<GuildCheck> list, GuildCheck check, int max)
        {
            list.Add(check);

            if (list.Count > max)
                list.RemoveRange(0, list.Count - max);
        }

        /// <summary>
        /// Guilds with a member who was far from the spot at time t (or offline) and came
        /// within NearRange of it during the following response window.
        /// </summary>
        private static int[] Converged(PendingCheck c, DateTime t)
        {
            HashSet<int> guilds = new HashSet<int>();
            DateTime end = t + ResponseWindow;

            foreach (Trail trail in m_Trails.Values)
            {
                if (trail.Serial == c.Observer || trail.Serial == c.Seen)
                    continue;

                if (trail.Account != null && trail.Account == c.Account)
                    continue;

                if (trail.Address != null && trail.Address == c.Address)
                    continue;

                TrailPoint? before = null;
                TrailPoint? arrival = null;

                foreach (TrailPoint p in trail.Points)
                {
                    if (p.Time <= t)
                        before = p;
                    else if (p.Time <= end && arrival == null && p.Map == c.Map && Math.Abs(p.X - c.X) <= NearRange && Math.Abs(p.Y - c.Y) <= NearRange)
                        arrival = p;
                }

                if (arrival == null || arrival.Value.Guild == 0 || arrival.Value.Guild == c.SeenGuild)
                    continue;

                bool wasFar = before == null || t - before.Value.Time > SampleInterval + SampleInterval ||
                              before.Value.Map != c.Map ||
                              Math.Abs(before.Value.X - c.X) > FarRange || Math.Abs(before.Value.Y - c.Y) > FarRange;

                if (wasFar)
                    guilds.Add(arrival.Value.Guild);
            }

            return guilds.ToArray();
        }

        public static string GuildName(int id)
        {
            BaseGuild g = BaseGuild.Find(id);

            return g != null ? String.Format("[{0}] {1}", g.Abbreviation, g.Name) : String.Format("guild #{0}", id);
        }

        /// <summary>Per-guild response statistics for a character since the given time.</summary>
        public static List<GuildResponse> Stats(CharacterRecord rec, DateTime from)
        {
            List<GuildCheck> checks = rec.GuildChecks.Where(c => c.Time >= from).ToList();
            List<GuildCheck> baseline = rec.GuildBaseline.Where(c => c.Time >= from).ToList();

            List<GuildResponse> result = new List<GuildResponse>();

            if (checks.Count == 0)
                return result;

            foreach (int guild in checks.SelectMany(c => c.Guilds).Distinct())
            {
                int responses = checks.Count(c => c.Guilds.Contains(guild));
                int hits = baseline.Count(c => c.Guilds.Contains(guild));

                // Smoothed so a guild never seen in the baseline does not get an infinite lift.
                double baseRate = (hits + 0.5) / (baseline.Count + 1.0);
                double rate = (double)responses / checks.Count;

                GuildResponse r = new GuildResponse
                {
                    GuildId = guild,
                    Name = GuildName(guild),
                    Responses = responses,
                    Checks = checks.Count,
                    BaselineRate = (double)hits / Math.Max(1, baseline.Count),
                    Lift = rate / baseRate
                };

                r.Significant = r.Responses >= MinResponses && r.Lift >= MinLift;
                result.Add(r);
            }

            return result.OrderByDescending(r => r.Significant).ThenByDescending(r => r.Responses).ToList();
        }
    }
}
