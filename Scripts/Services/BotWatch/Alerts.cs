using Server.Network;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Server.Services.BotWatch
{
    public enum AlertKind
    {
        NewAccountInNetwork,
        FreshCharacterIdle,
        FreshWatchPattern,
        Report,
        Flagged,
        InfoDenied,
        StaffAction
    }

    public class AlertRecord
    {
        public DateTime Time;
        public AlertKind Kind;
        public string Account;
        public Serial Character;
        public string CharacterName;
        public string Text;

        public void Serialize(GenericWriter writer)
        {
            writer.Write(0); // version

            writer.Write(Time);
            writer.Write((int)Kind);
            writer.Write(Account);
            writer.Write(Character.Value);
            writer.Write(CharacterName);
            writer.Write(Text);
        }

        public void Deserialize(GenericReader reader)
        {
            reader.ReadInt(); // version

            Time = reader.ReadDateTime();
            Kind = (AlertKind)reader.ReadInt();
            Account = reader.ReadString();
            Character = (Serial)reader.ReadInt();
            CharacterName = reader.ReadString();
            Text = reader.ReadString();
        }
    }

    /// <summary>
    /// Warnings for staff about fresh accounts and characters. Sent to online staff, written
    /// to Logs/BotWatch.log and kept in a list for [BWAlerts, so nothing is lost while no
    /// staff member is online. Warnings only; nothing is done to the player.
    /// </summary>
    public static class Alerts
    {
        private static readonly string LogPath = Path.Combine("Logs", "BotWatch.log");

        public static readonly List<AlertRecord> Recent = new List<AlertRecord>();

        public static TimeSpan FreshIdleAlert { get; private set; }
        public static TimeSpan CheckInterval { get; private set; }
        public static TimeSpan Repeat { get; private set; }

        public static void Configure()
        {
            FreshIdleAlert = Config.Get("BotWatch.FreshIdleAlert", TimeSpan.FromMinutes(20));
            CheckInterval = Config.Get("BotWatch.AlertCheckInterval", TimeSpan.FromMinutes(30));
            Repeat = Config.Get("BotWatch.AlertRepeat", TimeSpan.FromDays(1));
        }

        public static void Initialize()
        {
            // First check shortly after startup, so flags and information denial are current.
            Timer.DelayCall(TimeSpan.FromMinutes(2), CheckInterval, () =>
            {
                try
                {
                    CheckWatchPatterns();
                }
                catch (Exception e)
                {
                    BotWatch.LogError(e);
                }
            });
        }

        /// <summary>
        /// Raises an alert unless the same kind was raised for the same character (or account)
        /// within the repeat period.
        /// </summary>
        public static void Raise(AlertKind kind, string account, Serial character, string name, string text)
        {
            Raise(kind, account, character, name, text, false);
        }

        public static void Raise(AlertKind kind, string account, Serial character, string name, string text, bool always)
        {
            DateTime now = DateTime.UtcNow;

            bool repeated = !always && Recent.Any(a => a.Kind == kind && now - a.Time < Repeat &&
                (character != Serial.MinusOne ? a.Character == character : a.Account == account));

            if (repeated)
                return;

            AlertRecord alert = new AlertRecord
            {
                Time = now,
                Kind = kind,
                Account = account,
                Character = character,
                CharacterName = name,
                Text = text
            };

            Recent.Add(alert);

            string line = String.Format("[BotWatch] {0}", text);

            Utility.WriteConsoleColor(ConsoleColor.Yellow, line);

            foreach (NetState ns in NetState.Instances)
            {
                if (ns.Mobile != null && ns.Mobile.AccessLevel >= AccessLevel.Counselor)
                    ns.Mobile.SendMessage(0x35, line);
            }

            try
            {
                Directory.CreateDirectory("Logs");
                File.AppendAllText(LogPath, String.Format("{0:u} {1}: {2}{3}", now, kind, text, Environment.NewLine));
            }
            catch (Exception e)
            {
                Diagnostics.ExceptionLogging.LogException(e);
            }
        }

        public static void Prune(DateTime cutoff)
        {
            Recent.RemoveAll(a => a.Time < cutoff);
        }

        private static string Characters(AccountInfo info)
        {
            return String.Join(", ", info.Characters.Select(c => c.Name).Take(5));
        }

        /// <summary>
        /// A new or never-seen account logged in from an address other accounts used.
        /// </summary>
        public static void CheckNewAccount(Mobile m, CharacterRecord rec, string address, bool firstSeen)
        {
            bool fresh = rec.AccountCreated != DateTime.MinValue && DateTime.UtcNow - rec.AccountCreated < TimeSpan.FromDays(Ratings.FreshDays);

            if (!fresh && !firstSeen)
                return;

            List<string> others = BotWatch.Addresses.Values
                .Where(a => a.Address == address && a.Account != rec.Account && address != "?")
                .Select(a => a.Account)
                .Distinct()
                .ToList();

            if (others.Count == 0)
                return;

            NetworkGraph g = Networks.Build();

            string known = String.Join("; ", others.Take(5).Select(a =>
                g.Accounts.TryGetValue(a, out AccountInfo info) ? String.Format("{0} ({1})", a, Characters(info)) : a));

            Raise(AlertKind.NewAccountInNetwork, rec.Account, Serial.MinusOne, rec.Name, String.Format(
                "New account {0} (character {1}, account {2}) logged in from an address used by {3}",
                rec.Account, rec.Name, firstSeen ? "seen for the first time" : "created " + Networks.Ago(rec.AccountCreated), known));
        }

        /// <summary>
        /// A fresh character has spent a while outside towns this session without any
        /// meaningful action.
        /// </summary>
        public static void CheckFreshIdle(Mobile m, CharacterRecord rec, SessionRecord session, int sessionOutdoorSeconds)
        {
            if (session == null || session.MeaningfulActions > 0 || sessionOutdoorSeconds < FreshIdleAlert.TotalSeconds)
                return;

            TimeSpan fresh = TimeSpan.FromDays(Ratings.FreshDays);
            DateTime now = DateTime.UtcNow;

            bool freshCharacter = rec.CharacterCreated != DateTime.MinValue && now - rec.CharacterCreated < fresh;
            bool freshAccount = rec.AccountCreated != DateTime.MinValue && now - rec.AccountCreated < fresh;

            if (!freshCharacter && !freshAccount)
                return;

            Raise(AlertKind.FreshCharacterIdle, rec.Account, rec.Serial, rec.Name, String.Format(
                "Fresh {0} {1} (account {2}) has spent {3:F0} min outside towns this session without a meaningful action, now at {4} {5}",
                freshAccount ? "account's character" : "character", rec.Name, rec.Account,
                sessionOutdoorSeconds / 60.0, m.Map, m.Location));
        }

        /// <summary>
        /// Periodic check: fresh characters that already show the Watch pattern, with the
        /// accounts they are linked to.
        /// </summary>
        private static void CheckWatchPatterns()
        {
            if (!BotWatch.Enabled)
                return;

            Dictionary<Serial, BotProfile> profiles = Ratings.ComputeAll();
            NetworkGraph g = null;
            DateTime now = DateTime.UtcNow;
            TimeSpan fresh = TimeSpan.FromDays(Ratings.FreshDays);

            foreach (BotProfile p in profiles.Values)
            {
                CharacterRecord rec = p.Record;

                bool isFresh = (rec.CharacterCreated != DateTime.MinValue && now - rec.CharacterCreated < fresh) ||
                               (rec.AccountCreated != DateTime.MinValue && now - rec.AccountCreated < fresh);

                if (!isFresh || !(p.ScoutPattern || p.Watch >= Ratings.ScoutWatchThreshold))
                    continue;

                g = g ?? Networks.Build();

                AccountNetwork n = g.NetworkOf(rec.Account);
                string linked = n != null && n.Accounts.Count > 1
                    ? "linked to " + String.Join(", ", n.Accounts.Where(a => a != rec.Account).Take(5))
                    : "no linked accounts";

                Raise(AlertKind.FreshWatchPattern, rec.Account, rec.Serial, rec.Name, String.Format(
                    "Fresh character {0} (account {1}) shows the {2}: Watch {3}, Session {4}; {5}",
                    rec.Name, rec.Account, p.ScoutPattern ? "scout pattern" : "Watch pattern",
                    ProfileGump.Score(p.Watch), ProfileGump.Score(p.Session), linked));
            }

            Flags.Check(profiles, null);
            InfoDenial.Update(profiles);
        }
    }
}
