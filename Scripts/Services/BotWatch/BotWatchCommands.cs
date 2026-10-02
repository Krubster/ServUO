using Server.Commands;
using Server.Targeting;
using System;
using System.Linq;

namespace Server.Services.BotWatch
{
    /// <summary>
    /// Text commands for checking that collection works. The profile gumps come in the
    /// next step and build on the same data.
    /// </summary>
    public static class BotWatchCommands
    {
        public static void Initialize()
        {
            CommandSystem.Register("BWStatus", AccessLevel.GameMaster, Status_OnCommand);
            CommandSystem.Register("BWActivity", AccessLevel.Counselor, Activity_OnCommand);
            CommandSystem.Register("BWSessions", AccessLevel.Counselor, Sessions_OnCommand);
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
        [Description("Shows a character's recorded activity over the last 7 days. Targets if no name is given.")]
        private static void Activity_OnCommand(CommandEventArgs e)
        {
            WithRecord(e, (from, rec) =>
            {
                HourBucket b = rec.Sum(DateTime.UtcNow - TimeSpan.FromDays(7));
                int[] c = b.Counts;

                from.SendMessage("{0} (account {1}), last 7 days:", rec.Name, rec.Account);
                from.SendMessage("Online {0:F1}h, outside towns {1:F1}h, ghost outside {2:F1}h, idle near teleporters {3:F1}h, areas visited {4}",
                    b.OnlineSeconds / 3600.0, b.OutdoorSeconds / 3600.0, b.GhostOutdoorSeconds / 3600.0, b.TeleporterIdleSeconds / 3600.0, b.Cells);
                from.SendMessage("PvM attacks {0}, kills {1} | PvP attacks {2}, kills {3}, deaths {4}",
                    c[(int)Activity.PvMAttack], c[(int)Activity.PvMKill], c[(int)Activity.PvPAttack], c[(int)Activity.PvPKill], c[(int)Activity.PvPDeath]);
                from.SendMessage("Crafts {0}, harvests {1}, trades {2} | skill gains {3}, skill uses {4}, spells {5}, item uses {6}, speech {7}",
                    c[(int)Activity.Craft], c[(int)Activity.Gather], c[(int)Activity.Trade], c[(int)Activity.SkillGain],
                    c[(int)Activity.SkillUse], c[(int)Activity.Spell], c[(int)Activity.ItemUse], c[(int)Activity.Speech]);
                from.SendMessage("Heals self/others {0}/{1}, buffs self/others {2}/{3}, player trades {4}, looted PvM/PvP {5}/{6}",
                    c[(int)Activity.HealSelf], c[(int)Activity.HealOther], c[(int)Activity.BuffSelf], c[(int)Activity.BuffOther],
                    c[(int)Activity.PlayerTrade], c[(int)Activity.LootPvM], c[(int)Activity.LootPvP]);
                from.SendMessage("Encounters {0}: idle {1}, reacted {2}, near teleporters {3} ({4} idle)",
                    b.Encounters, b.IdleEncounters, b.ReactedEncounters, b.TeleporterEncounters, b.TeleporterIdleEncounters);
                from.SendMessage("Character created {0:yyyy-MM-dd}, account created {1:yyyy-MM-dd}, skills {2:F1}, backpack items {3}, bank items {4}",
                    rec.CharacterCreated, rec.AccountCreated, rec.SkillsTotal / 10.0, rec.BackpackItems, rec.BankItems);
            });
        }

        [Usage("BWSessions [name]")]
        [Description("Lists a character's last 15 sessions. Targets if no name is given.")]
        private static void Sessions_OnCommand(CommandEventArgs e)
        {
            WithRecord(e, (from, rec) =>
            {
                var sessions = BotWatch.GetSessions(rec.Serial).OrderByDescending(s => s.Start).Take(15).ToList();

                from.SendMessage("{0}: {1} recent sessions (UTC)", rec.Name, sessions.Count);

                foreach (SessionRecord s in sessions)
                {
                    from.SendMessage("{0:MM-dd HH:mm} {1,6:F0}min {2} | in: {3} {4}{5} | out: {6}",
                        s.Start,
                        s.Duration.TotalMinutes,
                        s.Address,
                        s.StartMap,
                        s.StartLocation,
                        s.StartInTown ? " (town)" : String.Empty,
                        s.Open ? "online" : String.Format("{0} {1}{2}", s.EndMap, s.EndLocation, s.EndInTown ? " (town)" : String.Empty));
                }
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
