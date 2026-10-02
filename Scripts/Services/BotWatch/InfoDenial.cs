using Server.Engines.PartySystem;
using Server.Guilds;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Server.Services.BotWatch
{
    /// <summary>
    /// Optional, off by default. Characters showing the scout pattern stop receiving other
    /// players while they stand idle outside towns, so the scout has nothing to report. Any
    /// meaningful action restores sight at once, so a real player notices nothing. Guildmates,
    /// allies, party members and the character's own account stay visible.
    /// Hooked in through PlayerMobile.CanSee.
    /// </summary>
    public static class InfoDenial
    {
        public static bool Enabled { get; private set; }
        public static int WatchThreshold { get; private set; }
        public static TimeSpan IdleBefore { get; private set; }

        /// <summary>Characters that qualify, refreshed with the periodic profile check.</summary>
        private static readonly HashSet<Serial> m_Candidates = new HashSet<Serial>();

        /// <summary>Characters currently not receiving other players.</summary>
        private static readonly HashSet<Mobile> m_Denied = new HashSet<Mobile>();

        /// <summary>Set while BotWatch itself checks visibility, so encounters are still counted.</summary>
        [ThreadStatic]
        public static bool Bypass;

        public static int CandidateCount => m_Candidates.Count;
        public static int DeniedCount => m_Denied.Count;

        public static void Configure()
        {
            Enabled = Config.Get("BotWatch.InfoDenial", false);
            WatchThreshold = Config.Get("BotWatch.InfoDenialWatch", 70);
            IdleBefore = Config.Get("BotWatch.InfoDenialIdle", TimeSpan.FromMinutes(5));
        }

        public static bool IsCandidate(Serial s)
        {
            return m_Candidates.Contains(s);
        }

        public static bool IsDenied(Mobile m)
        {
            return m_Denied.Contains(m);
        }

        /// <summary>Called by PlayerMobile.CanSee. Must stay cheap: it runs for every visibility check.</summary>
        public static bool Hides(Mobile observer, Mobile target)
        {
            if (m_Denied.Count == 0 || Bypass || !m_Denied.Contains(observer))
                return false;

            try
            {
                return HidesFrom(observer, target);
            }
            catch (Exception e)
            {
                BotWatch.LogError(e);
                return false;
            }
        }

        private static bool HidesFrom(Mobile observer, Mobile target)
        {
            if (target == observer || !BotWatch.IsTracked(target) || target.Account == observer.Account)
                return false;

            Party party = Party.Get(observer);

            if (party != null && party == Party.Get(target))
                return false;

            if (observer.Guild is Guild g && target.Guild is Guild og && (g == og || g.IsAlly(og)))
                return false;

            return true;
        }

        /// <summary>Refreshes the candidates from freshly computed profiles.</summary>
        public static void Update(Dictionary<Serial, BotProfile> profiles)
        {
            m_Candidates.Clear();

            if (!Enabled)
                return;

            foreach (BotProfile p in profiles.Values)
            {
                if (!p.ScoutPattern || !(p.Watch >= WatchThreshold))
                    continue;

                FlagRecord flag = Flags.LatestFor(p.Record.Serial);

                if (flag != null && flag.Status == FlagStatus.Legit)
                    continue;

                m_Candidates.Add(p.Record.Serial);
            }
        }

        /// <summary>Called from the scan for every online character.</summary>
        public static void Tick(Mobile m, bool outdoor, DateTime lastMeaningful, DateTime now)
        {
            bool deny = Enabled && outdoor && m_Candidates.Contains(m.Serial) && now - lastMeaningful >= IdleBefore;

            if (deny && !m_Denied.Contains(m))
                Deny(m);
            else if (!deny && m_Denied.Contains(m))
                Restore(m);
        }

        public static void OnMeaningful(Mobile m)
        {
            if (m_Denied.Contains(m))
                Restore(m);
        }

        public static void OnDisconnect(Mobile m)
        {
            m_Denied.Remove(m);
        }

        private static void Deny(Mobile m)
        {
            m_Denied.Add(m);

            // Remove everything from the client, then send back what it may still see.
            m.ClearScreen();
            m.SendEverything();

            Alerts.Raise(AlertKind.InfoDenied, m.Account?.Username, m.Serial, m.RawName, String.Format(
                "Information denial started for {0} (account {1}) at {2} {3}.", m.RawName, m.Account?.Username, m.Map, m.Location));
        }

        private static void Restore(Mobile m)
        {
            m_Denied.Remove(m);

            if (m.NetState != null)
                m.SendEverything();
        }

        public static IEnumerable<Mobile> Denied => m_Denied.ToList();
    }
}
