using Server.Accounting;
using Server.Commands;
using Server.Targeting;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Server.Services.BotWatch
{
    public class ReportRecord
    {
        public DateTime Time;
        public string ReporterAccount;
        public string ReporterName;
        public Serial Target;
        public string TargetName;
        public string TargetAccount;
        public Map Map;
        public Point3D Location;
        public string Note;

        public void Serialize(GenericWriter writer)
        {
            writer.Write(0); // version

            writer.Write(Time);
            writer.Write(ReporterAccount);
            writer.Write(ReporterName);
            writer.Write(Target.Value);
            writer.Write(TargetName);
            writer.Write(TargetAccount);
            writer.Write(Map);
            writer.Write(Location);
            writer.Write(Note);
        }

        public void Deserialize(GenericReader reader)
        {
            reader.ReadInt(); // version

            Time = reader.ReadDateTime();
            ReporterAccount = reader.ReadString();
            ReporterName = reader.ReadString();
            Target = (Serial)reader.ReadInt();
            TargetName = reader.ReadString();
            TargetAccount = reader.ReadString();
            Map = reader.ReadMap();
            Location = reader.ReadPoint3D();
            Note = reader.ReadString();
        }
    }

    /// <summary>
    /// [ReportScout: players point out suspected scouts. Reports show on the profile, raise
    /// the character's place in [Profiles and flag it once enough different players report it.
    /// </summary>
    public static class Reports
    {
        public static readonly List<ReportRecord> All = new List<ReportRecord>();

        /// <summary>Accounts staff have barred from reporting.</summary>
        public static readonly HashSet<string> Blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public static int PerDay { get; private set; }
        public static TimeSpan TargetCooldown { get; private set; }
        public static TimeSpan MinAccountAge { get; private set; }
        public static int FlagThreshold { get; private set; }

        public static void Configure()
        {
            PerDay = Config.Get("BotWatch.ReportsPerDay", 3);
            TargetCooldown = Config.Get("BotWatch.ReportTargetCooldown", TimeSpan.FromDays(1));
            MinAccountAge = Config.Get("BotWatch.ReportMinAccountAge", TimeSpan.FromDays(3));
            FlagThreshold = Config.Get("BotWatch.ReportFlagThreshold", 3);
        }

        public static void Initialize()
        {
            CommandSystem.Register("ReportScout", AccessLevel.Player, ReportScout_OnCommand);
            CommandSystem.Register("ReportBlock", AccessLevel.GameMaster, ReportBlock_OnCommand);
        }

        public static IEnumerable<ReportRecord> For(Serial target, DateTime since)
        {
            return All.Where(r => r.Target == target && r.Time >= since);
        }

        public static int DistinctReporters(Serial target, DateTime since)
        {
            return For(target, since).Select(r => r.ReporterAccount).Distinct().Count();
        }

        public static void Prune(DateTime cutoff)
        {
            All.RemoveAll(r => r.Time < cutoff);
        }

        [Usage("ReportScout [note]")]
        [Description("Report a character you believe is a scout bot. Target the character; an optional note is passed to staff.")]
        private static void ReportScout_OnCommand(CommandEventArgs e)
        {
            Mobile from = e.Mobile;
            string note = e.ArgString?.Trim() ?? String.Empty;

            if (note.Length > 200)
                note = note.Substring(0, 200);

            string reason = CanReport(from);

            if (reason != null)
            {
                from.SendMessage(reason);
                return;
            }

            from.SendMessage("Target the character you want to report as a scout.");
            from.BeginTarget(18, false, TargetFlags.None, (m, targeted) => OnTarget(m, targeted, note));
        }

        private static string CanReport(Mobile from)
        {
            Account a = from.Account as Account;

            if (a == null)
                return "You cannot report anyone.";

            if (Blocked.Contains(a.Username))
                return "You can no longer send reports.";

            if (a.Created != DateTime.MinValue && DateTime.UtcNow - a.Created < MinAccountAge)
                return "Your account is too new to send reports.";

            if (All.Count(r => r.ReporterAccount == a.Username && DateTime.UtcNow - r.Time < TimeSpan.FromDays(1)) >= PerDay)
                return "You have already sent the maximum number of reports today.";

            return null;
        }

        private static void OnTarget(Mobile from, object targeted, string note)
        {
            Mobile target = targeted as Mobile;

            if (target == null || !target.Player || target == from)
            {
                from.SendMessage("You can only report another player's character.");
                return;
            }

            if (!BotWatch.IsTracked(target))
            {
                from.SendMessage("That character cannot be reported.");
                return;
            }

            if (target.Account == from.Account)
            {
                from.SendMessage("You cannot report your own characters.");
                return;
            }

            string reason = CanReport(from);

            if (reason != null)
            {
                from.SendMessage(reason);
                return;
            }

            string account = from.Account.Username;

            if (All.Any(r => r.ReporterAccount == account && r.Target == target.Serial && DateTime.UtcNow - r.Time < TargetCooldown))
            {
                from.SendMessage("You have already reported that character recently.");
                return;
            }

            ReportRecord report = new ReportRecord
            {
                Time = DateTime.UtcNow,
                ReporterAccount = account,
                ReporterName = from.RawName,
                Target = target.Serial,
                TargetName = target.RawName,
                TargetAccount = target.Account?.Username,
                Map = target.Map,
                Location = target.Location,
                Note = note
            };

            All.Add(report);
            BotWatch.GetRecord(target);

            from.SendMessage(0x3F, "Thank you. Your report was recorded and will be reviewed by staff.");

            int reporters = DistinctReporters(target.Serial, DateTime.UtcNow - Ratings.Window);

            Alerts.Raise(AlertKind.Report, report.TargetAccount, target.Serial, target.RawName, String.Format(
                "{0} reported {1} (account {2}) at {3} {4}{5}. {6} player{7} reported this character in the last {8:F0} days.",
                from.RawName, target.RawName, report.TargetAccount, target.Map, target.Location,
                note.Length > 0 ? ": \"" + note + "\"" : String.Empty, reporters, reporters == 1 ? "" : "s", Ratings.Window.TotalDays), true);

            if (reporters >= FlagThreshold)
                Flags.Check(new[] { target.Serial });
        }

        [Usage("ReportBlock <account>")]
        [Description("Bars an account from using [ReportScout, or allows it again if it is already barred.")]
        private static void ReportBlock_OnCommand(CommandEventArgs e)
        {
            if (e.Length < 1)
            {
                e.Mobile.SendMessage("Usage: ReportBlock <account>");
                return;
            }

            string account = e.GetString(0);

            if (Blocked.Remove(account))
            {
                e.Mobile.SendMessage("{0} may send reports again.", account);
            }
            else
            {
                Blocked.Add(account);
                e.Mobile.SendMessage("{0} can no longer send reports. Use the command again to undo.", account);
            }
        }
    }
}
