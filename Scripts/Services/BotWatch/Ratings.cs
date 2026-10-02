using System;
using System.Collections.Generic;
using System.Linq;

namespace Server.Services.BotWatch
{
    public enum RatingCategory
    {
        PvM,
        PvP,
        Crafting,
        Gathering,
        SkillGain,
        Trade,
        Social,
        Exploration,
        Interaction
    }

    /// <summary>One part of the Watch or Session score, with the value it contributed.</summary>
    public class ScoreComponent
    {
        public string Name;
        public double Weight;
        public double? Value; // 0..1, null when there is not enough data
        public string Detail;
    }

    public class DayStats
    {
        public DateTime Day;
        public double OnlineHours;
        public double OutdoorHours;
        public int Encounters;
        public int IdleEncounters;
        public int MeaningfulActions;
    }

    /// <summary>A character's computed profile over the rating window.</summary>
    public class BotProfile
    {
        public CharacterRecord Record;
        public HourBucket Window;
        public double OnlineHours;
        public bool InPopulation;

        private static readonly int CategoryCount = Enum.GetValues(typeof(RatingCategory)).Length;

        public readonly double[] Raw = new double[CategoryCount];
        public readonly int[] Ratings = new int[CategoryCount];

        public double? Watch;
        public readonly List<ScoreComponent> WatchParts = new List<ScoreComponent>();
        public double? Session;
        public readonly List<ScoreComponent> SessionParts = new List<ScoreComponent>();

        public int SessionCount;
        public readonly List<DayStats> Days = new List<DayStats>();
        public readonly List<string> Notes = new List<string>();

        public bool ScoutPattern;

        public int this[RatingCategory c] => Ratings[(int)c];

        /// <summary>Highest rating among the activities a scout cannot fake cheaply.</summary>
        public int CoreActivity => new[]
        {
            this[RatingCategory.PvM], this[RatingCategory.PvP], this[RatingCategory.Crafting],
            this[RatingCategory.Gathering], this[RatingCategory.Trade]
        }.Max();
    }

    /// <summary>
    /// Turns recorded data into per-character ratings. Activity ratings are activity per
    /// hour online, ranked 0-100 against every character active in the rating window.
    /// Watch and Session are scores from 0 (normal) to 100 (scout-like) built from shares
    /// and rates, so they do not depend on how busy the shard or the spot is.
    /// </summary>
    public static class Ratings
    {
        public static readonly int CategoryCount = Enum.GetValues(typeof(RatingCategory)).Length;

        public static TimeSpan Window { get; private set; }
        public static double MinOnlineHours { get; private set; }
        public static int MinEncounters { get; private set; }
        public static int MinSessions { get; private set; }
        public static int ScoutWatchThreshold { get; private set; }
        public static int ScoutActivityCeiling { get; private set; }
        public static int SessionThreshold { get; private set; }
        public static TimeSpan RelogGap { get; private set; }
        public static TimeSpan ShortSession { get; private set; }
        public static int FreshDays { get; private set; }
        public static int LowSkills { get; private set; }

        public static void Configure()
        {
            Window = Config.Get("BotWatch.RatingWindow", TimeSpan.FromDays(7));
            MinOnlineHours = Config.Get("BotWatch.MinOnlineHours", 1.0);
            MinEncounters = Config.Get("BotWatch.MinEncounters", 5);
            MinSessions = Config.Get("BotWatch.MinSessions", 3);
            ScoutWatchThreshold = Config.Get("BotWatch.ScoutWatchThreshold", 60);
            ScoutActivityCeiling = Config.Get("BotWatch.ScoutActivityCeiling", 25);
            SessionThreshold = Config.Get("BotWatch.SessionThreshold", 60);
            RelogGap = Config.Get("BotWatch.RelogGap", TimeSpan.FromMinutes(5));
            ShortSession = Config.Get("BotWatch.ShortSession", TimeSpan.FromMinutes(15));
            FreshDays = Config.Get("BotWatch.FreshDays", 7);
            LowSkills = Config.Get("BotWatch.LowSkills", 200);
        }

        /// <summary>Computes profiles for every character with activity in the window.</summary>
        public static Dictionary<Serial, BotProfile> ComputeAll()
        {
            DateTime now = DateTime.UtcNow;
            DateTime from = now - Window;

            Dictionary<Serial, List<SessionRecord>> sessions = BotWatch.Sessions
                .Where(s => s.EndOrLastSeen >= from)
                .GroupBy(s => s.Character)
                .ToDictionary(g => g.Key, g => g.OrderBy(s => s.Start).ToList());

            Dictionary<Serial, BotProfile> profiles = new Dictionary<Serial, BotProfile>();

            foreach (CharacterRecord rec in BotWatch.Characters.Values)
            {
                if (rec.LastActive < from)
                    continue;

                sessions.TryGetValue(rec.Serial, out List<SessionRecord> list);

                profiles[rec.Serial] = Compute(rec, list ?? new List<SessionRecord>(), now, from);
            }

            RankActivity(profiles.Values.ToList());

            foreach (BotProfile p in profiles.Values)
            {
                p.ScoutPattern = p.Watch >= ScoutWatchThreshold && p.CoreActivity <= ScoutActivityCeiling;
            }

            return profiles;
        }

        public static BotProfile ComputeOne(Serial serial)
        {
            return ComputeAll().TryGetValue(serial, out BotProfile p) ? p : null;
        }

        private static BotProfile Compute(CharacterRecord rec, List<SessionRecord> sessions, DateTime now, DateTime from)
        {
            HourBucket b = rec.Sum(from);
            BotProfile p = new BotProfile
            {
                Record = rec,
                Window = b,
                OnlineHours = b.OnlineSeconds / 3600.0,
                SessionCount = sessions.Count
            };

            p.InPopulation = p.OnlineHours >= MinOnlineHours;

            ComputeRaw(p, b);
            ComputeWatch(p, b);
            ComputeSession(p, rec, sessions, now);
            ComputeDays(p, rec, now);

            if (!p.InPopulation)
                p.Notes.Add(String.Format("Less than {0:F1}h online in the window: ratings are unreliable.", MinOnlineHours));

            if (b.Encounters < MinEncounters)
                p.Notes.Add(String.Format("Only {0} encounters (need {1}): Watch is based on presence only.", b.Encounters, MinEncounters));

            if (rec.IsDeleted)
                p.Notes.Add(String.Format("Character deleted on {0:yyyy-MM-dd}.", rec.Deleted));

            if (b[Activity.FastWalk] > 0)
                p.Notes.Add(String.Format("{0} speed-hack attempts.", b[Activity.FastWalk]));

            return p;
        }

        private static void ComputeRaw(BotProfile p, HourBucket b)
        {
            double hours = Math.Max(p.OnlineHours, 0.25);

            double[] raw =
            {
                // PvM
                b[Activity.PvMAttack] + 5 * b[Activity.PvMKill] + 0.5 * b[Activity.LootPvM] + b.DamageDealtPvM / 50.0 + 3 * b[Activity.Tame],
                // PvP
                b[Activity.PvPAttack] + 10 * b[Activity.PvPKill] + 2 * b[Activity.PvPDeath] + b[Activity.LootPvP] +
                b.DamageDealtPvP / 25.0 + 0.5 * b[Activity.TargetPlayer] + 3 * b[Activity.RevealedOther],
                // Crafting
                b[Activity.Craft] + 3 * b[Activity.BODTurnedIn],
                // Gathering
                b[Activity.Gather],
                // Skill gain
                b[Activity.SkillGain],
                // Trade
                b[Activity.Trade] + 3 * b[Activity.PlayerTrade] + 2 * b[Activity.BODTaken] + (b.GoldGained + b.GoldLost) / 1000.0,
                // Social
                b[Activity.Speech] + b[Activity.Whisper] + b[Activity.Yell] + b[Activity.Emote] + b[Activity.GuildChat] +
                2 * (b[Activity.HealOther] + b[Activity.BuffOther] + b[Activity.AssistOther]) +
                5 * (b[Activity.PartyJoin] + b[Activity.GuildJoin]),
                // Exploration
                b.Cells + 3 * b[Activity.DungeonEnter] + b[Activity.TravelJump] + b[Activity.TravelSpell] + 5 * b[Activity.QuestComplete],
                // Interaction
                b[Activity.InterfaceUse] + b[Activity.Consume] + 0.5 * b[Activity.ItemObtained] +
                0.5 * b[Activity.HealthBarRequest] + 0.2 * b[Activity.ItemUse] + 0.2 * b[Activity.Spell]
            };

            for (int i = 0; i < CategoryCount; i++)
                p.Raw[i] = raw[i] / hours;
        }

        /// <summary>
        /// Percentile rank of each activity rate among characters with enough time online.
        /// No activity at all is always 0.
        /// </summary>
        private static void RankActivity(List<BotProfile> profiles)
        {
            List<BotProfile> population = profiles.Where(p => p.InPopulation).ToList();

            for (int c = 0; c < CategoryCount; c++)
            {
                double[] values = population.Select(p => p.Raw[c]).ToArray();

                foreach (BotProfile p in profiles)
                {
                    double v = p.Raw[c];

                    if (v <= 0 || values.Length == 0)
                    {
                        p.Ratings[c] = 0;
                        continue;
                    }

                    int below = values.Count(x => x < v);
                    int equal = values.Count(x => x == v);

                    p.Ratings[c] = (int)Math.Round(100.0 * (below + 0.5 * Math.Max(equal, 1)) / Math.Max(values.Length, 1));
                    p.Ratings[c] = Math.Max(1, Math.Min(100, p.Ratings[c]));
                }
            }
        }

        private static ScoreComponent Part(string name, double weight, double? value, string detail)
        {
            return new ScoreComponent
            {
                Name = name,
                Weight = weight,
                Value = value.HasValue ? Math.Max(0, Math.Min(1, value.Value)) : (double?)null,
                Detail = detail
            };
        }

        private static double? Share(double part, double whole, double minWhole)
        {
            return whole >= minWhole && whole > 0 ? part / whole : (double?)null;
        }

        private static double? Combine(List<ScoreComponent> parts)
        {
            double weight = parts.Where(c => c.Value.HasValue).Sum(c => c.Weight);

            if (weight <= 0)
                return null;

            return 100.0 * parts.Where(c => c.Value.HasValue).Sum(c => c.Weight * c.Value.Value) / weight;
        }

        private static void ComputeWatch(BotProfile p, HourBucket b)
        {
            double outdoor = b.OutdoorSeconds;
            double avgDistinct = b.StepWindows > 0 ? (double)b.StepWindowDistinct / b.StepWindows : 0;

            p.WatchParts.Add(Part("Idle encounters", 35, Share(b.IdleEncounters, b.Encounters, MinEncounters),
                String.Format("{0} of {1} encounters with no meaningful action around them", b.IdleEncounters, b.Encounters)));

            p.WatchParts.Add(Part("No reaction", 25,
                b.Encounters >= MinEncounters ? 1.0 - (double)b.ReactedEncounters / b.Encounters : (double?)null,
                String.Format("reacted to {0} of {1} encounters", b.ReactedEncounters, b.Encounters)));

            p.WatchParts.Add(Part("Idle near teleporters", 15, Share(b.TeleporterIdleSeconds, outdoor, 600),
                String.Format("{0:F1}h of {1:F1}h outside towns", b.TeleporterIdleSeconds / 3600.0, outdoor / 3600.0)));

            p.WatchParts.Add(Part("Looping path", 10, b.StepWindows >= 3 ? (60 - avgDistinct) / 50 : (double?)null,
                String.Format("{0:F0} distinct tiles per 100 steps ({1} windows)", avgDistinct, b.StepWindows)));

            p.WatchParts.Add(Part("Ghost outside", 5, Share(b.GhostOutdoorSeconds, outdoor, 600),
                String.Format("{0:F1}h as a ghost outside towns", b.GhostOutdoorSeconds / 3600.0)));

            p.WatchParts.Add(Part("Hidden outside", 5, Share(b.HiddenOutdoorSeconds, outdoor, 600),
                String.Format("{0:F1}h hidden outside towns", b.HiddenOutdoorSeconds / 3600.0)));

            p.WatchParts.Add(Part("No response to attacks", 5, Share(b[Activity.AttackedNoResponse], b[Activity.AttackedByPlayer], 2),
                String.Format("ignored {0} of {1} attacks by players", b[Activity.AttackedNoResponse], b[Activity.AttackedByPlayer])));

            p.Watch = Combine(p.WatchParts);
        }

        private static void ComputeSession(BotProfile p, CharacterRecord rec, List<SessionRecord> sessions, DateTime now)
        {
            List<SessionRecord> closed = sessions.Where(s => !s.Open).ToList();

            int parked = closed.Count(s => s.IsParked(BotWatch.ReactionMoveTiles));

            int relogs = 0;

            for (int i = 1; i < sessions.Count; i++)
            {
                if (sessions[i].Start - sessions[i - 1].EndOrLastSeen <= RelogGap)
                    relogs++;
            }

            int activeDays = sessions.Select(s => s.Start.Date).Distinct().Count();
            double relogsPerDay = activeDays > 0 ? (double)relogs / activeDays : 0;

            int shortIdle = closed.Count(s => s.Duration <= ShortSession && s.MeaningfulActions == 0 && !s.StartInTown);

            p.SessionParts.Add(Part("Parked sessions", 30, Share(parked, closed.Count, MinSessions),
                String.Format("{0} of {1} sessions started and ended at the same spot outside towns", parked, closed.Count)));

            p.SessionParts.Add(Part("Relogs", 20, sessions.Count >= MinSessions ? relogsPerDay / 5 : (double?)null,
                String.Format("{0} relogs within {1:F0} min ({2:F1} per active day)", relogs, RelogGap.TotalMinutes, relogsPerDay)));

            p.SessionParts.Add(Part("Short idle sessions", 25, Share(shortIdle, closed.Count, MinSessions),
                String.Format("{0} sessions under {1:F0} min outside towns with no meaningful action", shortIdle, ShortSession.TotalMinutes)));

            int flags = 0;
            List<string> reasons = new List<string>();

            if (rec.AccountCreated != DateTime.MinValue && now - rec.AccountCreated < TimeSpan.FromDays(FreshDays))
            {
                flags++;
                reasons.Add("new account");
            }

            if (rec.CharacterCreated != DateTime.MinValue && now - rec.CharacterCreated < TimeSpan.FromDays(FreshDays))
            {
                flags++;
                reasons.Add("new character");
            }

            if (rec.SkillsTotal < LowSkills * 10)
            {
                flags++;
                reasons.Add("low skills");
            }

            if (rec.BankItems == 0)
            {
                flags++;
                reasons.Add("empty bank");
            }

            p.SessionParts.Add(Part("Disposable character", 25, flags / 4.0,
                reasons.Count > 0 ? String.Join(", ", reasons) : "established character"));

            p.Session = Combine(p.SessionParts);
        }

        private static void ComputeDays(BotProfile p, CharacterRecord rec, DateTime now)
        {
            DateTime today = now.Date;

            for (int d = 6; d >= 0; d--)
            {
                DateTime day = today.AddDays(-d);
                HourBucket b = rec.Sum(day, day.AddDays(1));

                p.Days.Add(new DayStats
                {
                    Day = day,
                    OnlineHours = b.OnlineSeconds / 3600.0,
                    OutdoorHours = b.OutdoorSeconds / 3600.0,
                    Encounters = b.Encounters,
                    IdleEncounters = b.IdleEncounters,
                    MeaningfulActions = Enum.GetValues(typeof(Activity)).Cast<Activity>()
                        .Where(BotWatch.IsMeaningful)
                        .Sum(a => b[a])
                });
            }
        }
    }
}
