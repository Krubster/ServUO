using Server.Commands;
using Server.Targeting;
using System;
using System.Linq;

namespace Server.Services.BotWatch
{
    public static class BotWatchCommands
    {
        public static void Initialize()
        {
            CommandSystem.Register("Profile", AccessLevel.Counselor, Profile_OnCommand);
            CommandSystem.Register("Profiles", AccessLevel.Counselor, Profiles_OnCommand);
            CommandSystem.Register("Network", AccessLevel.Counselor, Network_OnCommand);
            CommandSystem.Register("Networks", AccessLevel.Counselor, Networks_OnCommand);
            CommandSystem.Register("BWAlerts", AccessLevel.Counselor, Alerts_OnCommand);
            CommandSystem.Register("BWStatus", AccessLevel.GameMaster, Status_OnCommand);
            CommandSystem.Register("BWActivity", AccessLevel.Counselor, Activity_OnCommand);
        }

        [Usage("Profile [name]")]
        [Description("Opens a character's BotWatch profile. Targets if no name is given; works for offline and deleted characters by name.")]
        private static void Profile_OnCommand(CommandEventArgs e)
        {
            WithRecord(e, (from, rec) => from.SendGump(new ProfileGump(from, rec.Serial, ProfileGump.View.Overview, false)));
        }

        [Usage("Profiles")]
        [Description("Lists every character active in the rating window, scout patterns first.")]
        private static void Profiles_OnCommand(CommandEventArgs e)
        {
            e.Mobile.SendGump(new ProfilesGump(e.Mobile, ProfilesGump.SortBy.Watch, 0));
        }

        [Usage("Network [character or account name]")]
        [Description("Shows the accounts linked to a player's account. Targets if no name is given.")]
        private static void Network_OnCommand(CommandEventArgs e)
        {
            if (e.Length > 0)
            {
                string name = e.ArgString.Trim();
                CharacterRecord rec = BotWatch.FindRecord(name);
                string account = rec != null ? rec.Account : null;

                if (account == null)
                {
                    account = BotWatch.Characters.Values.Select(r => r.Account)
                        .FirstOrDefault(a => String.Equals(a, name, StringComparison.OrdinalIgnoreCase));
                }

                if (account == null)
                    e.Mobile.SendMessage("No BotWatch record for a character or account named '{0}'.", name);
                else
                    e.Mobile.SendGump(new NetworkGump(e.Mobile, account, 0));

                return;
            }

            WithRecord(e, (from, rec) => from.SendGump(new NetworkGump(from, rec.Account, 0)));
        }

        [Usage("Networks")]
        [Description("Lists every network of two or more linked accounts, most suspicious first.")]
        private static void Networks_OnCommand(CommandEventArgs e)
        {
            e.Mobile.SendGump(new NetworksGump(e.Mobile, 0));
        }

        [Usage("BWAlerts [count]")]
        [Description("Shows the most recent BotWatch alerts, newest first (default 15).")]
        private static void Alerts_OnCommand(CommandEventArgs e)
        {
            int count = e.Length > 0 ? Math.Max(1, e.GetInt32(0)) : 15;
            var alerts = Alerts.Recent.OrderByDescending(a => a.Time).Take(count).ToList();

            if (alerts.Count == 0)
            {
                e.Mobile.SendMessage("No BotWatch alerts.");
                return;
            }

            foreach (AlertRecord a in alerts.AsEnumerable().Reverse())
                e.Mobile.SendMessage(0x35, "{0:MM-dd HH:mm} UTC: {1}", a.Time, a.Text);
        }

        [Usage("BWStatus")]
        [Description("Shows what BotWatch is currently tracking.")]
        private static void Status_OnCommand(CommandEventArgs e)
        {
            Mobile from = e.Mobile;

            from.SendMessage("BotWatch: {0} characters on record, {1} live, {2} encounters pending.",
                BotWatch.Characters.Count, BotWatch.LiveCount, BotWatch.PendingEncounterCount);
            from.SendMessage("{0} sessions and {1} account/address pairs on record, {2} teleporter points indexed.",
                BotWatch.Sessions.Count, BotWatch.Addresses.Count, TeleporterIndex.Count);
        }

        [Usage("BWActivity [name]")]
        [Description("Dumps every raw counter for a character over the rating window. Targets if no name is given.")]
        private static void Activity_OnCommand(CommandEventArgs e)
        {
            WithRecord(e, (from, rec) =>
            {
                HourBucket b = rec.Sum(DateTime.UtcNow - Ratings.Window);

                from.SendMessage("{0} (account {1}), last {2:F0} days:", rec.Name, rec.Account, Ratings.Window.TotalDays);
                from.SendMessage("Online {0:F1}h, outside towns {1:F1}h, ghost {2:F1}h, hidden {3:F1}h, idle near teleporters {4:F1}h, areas {5}",
                    b.OnlineSeconds / 3600.0, b.OutdoorSeconds / 3600.0, b.GhostOutdoorSeconds / 3600.0,
                    b.HiddenOutdoorSeconds / 3600.0, b.TeleporterIdleSeconds / 3600.0, b.Cells);
                from.SendMessage("Encounters {0}: idle {1}, reacted {2}, near teleporters {3} ({4} idle)",
                    b.Encounters, b.IdleEncounters, b.ReactedEncounters, b.TeleporterEncounters, b.TeleporterIdleEncounters);
                from.SendMessage("Steps {0}, step windows {1} ({2:F0} distinct tiles each), damage dealt PvM/PvP {3}/{4}, taken {5}, gold +{6}/-{7}",
                    b.Steps, b.StepWindows, b.StepWindows > 0 ? (double)b.StepWindowDistinct / b.StepWindows : 0,
                    b.DamageDealtPvM, b.DamageDealtPvP, b.DamageTaken, b.GoldGained, b.GoldLost);

                string counts = String.Join(", ", Enum.GetValues(typeof(Activity)).Cast<Activity>()
                    .Where(a => b[a] > 0)
                    .Select(a => String.Format("{0} {1}", a, b[a])));

                from.SendMessage(counts.Length > 0 ? counts : "No activity recorded.");
            });
        }

        private static void WithRecord(CommandEventArgs e, Action<Mobile, CharacterRecord> action)
        {
            if (e.Length > 0)
            {
                CharacterRecord rec = BotWatch.FindRecord(e.ArgString.Trim());

                if (rec == null)
                    e.Mobile.SendMessage("No BotWatch record for a character named '{0}'.", e.ArgString.Trim());
                else
                    action(e.Mobile, rec);

                return;
            }

            e.Mobile.SendMessage("Target a player.");
            e.Mobile.BeginTarget(-1, false, TargetFlags.None, (from, targeted) =>
            {
                if (targeted is Mobile m && BotWatch.Characters.TryGetValue(m.Serial, out CharacterRecord rec))
                    action(from, rec);
                else
                    from.SendMessage("No BotWatch record for that.");
            });
        }
    }
}
