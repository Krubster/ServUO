using Server.Gumps;
using Server.Network;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Server.Services.BotWatch
{
    /// <summary>Flags for review, filtered by status.</summary>
    public class FlagsGump : Gump
    {
        private const int Width = 760;
        private const int PerPage = 15;

        private const int ButtonPrev = 1;
        private const int ButtonNext = 2;
        private const int ButtonExport = 3;
        private const int ButtonFilterAll = 9;
        private const int ButtonFilterBase = 10;
        private const int ButtonFlagBase = 100;

        private readonly FlagStatus? m_Filter;
        private readonly int m_Page;
        private readonly List<int> m_Rows = new List<int>();

        public FlagsGump(Mobile staff, FlagStatus? filter, int page)
            : base(40, 40)
        {
            m_Filter = filter;

            List<FlagRecord> flags = Flags.All
                .Where(f => filter == null || f.Status == filter)
                .OrderByDescending(f => f.Status == FlagStatus.Open)
                .ThenByDescending(f => f.Updated)
                .ToList();

            int pages = Math.Max(1, (flags.Count + PerPage - 1) / PerPage);
            m_Page = Math.Max(0, Math.Min(page, pages - 1));

            int height = 160 + PerPage * 24;

            AddPage(0);
            AddBackground(0, 0, Width, height, 9270);
            AddAlphaRegion(10, 10, Width - 20, height - 20);

            AddHtml(20, 18, Width - 40, 20, ProfileGump.Text(String.Format("<BIG>BotWatch flags</BIG>   {0} open, {1} confirmed and waiting for action",
                Flags.All.Count(f => f.Status == FlagStatus.Open), Flags.All.Count(f => f.Status == FlagStatus.Confirmed)), "#FFD080"), false, false);

            int x = 20;

            foreach (FlagStatus status in Enum.GetValues(typeof(FlagStatus)))
            {
                AddFilter(x, ButtonFilterBase + (int)status, status.ToString(), filter == status);
                x += 110;
            }

            AddFilter(x, ButtonFilterAll, "All", filter == null);

            int[] cols = { 50, 90, 185, 310, 420, 610, 670 };
            string[] heads = { "#", "Flagged", "Character", "Account", "Reasons", "Watch", "Status" };

            for (int i = 0; i < cols.Length; i++)
                AddHtml(cols[i], 72, 120, 20, ProfileGump.Text(heads[i], "#FFD080"), false, false);

            int y = 96;

            foreach (FlagRecord f in flags.Skip(m_Page * PerPage).Take(PerPage))
            {
                int index = m_Rows.Count;
                m_Rows.Add(f.Id);

                double? watch = f.Latest?.Watch;

                AddButton(20, y, 4011, 4012, ButtonFlagBase + index, GumpButtonType.Reply, 0);
                AddHtml(cols[0], y + 2, 40, 20, ProfileGump.Text(f.Id.ToString()), false, false);
                AddHtml(cols[1], y + 2, 95, 20, ProfileGump.Text(f.Created.ToString("MM-dd HH:mm"), "#C0C0C0"), false, false);
                AddHtml(cols[2], y + 2, 120, 20, ProfileGump.Text(FlagGump.Escape(f.Name)), false, false);
                AddHtml(cols[3], y + 2, 105, 20, ProfileGump.Text(FlagGump.Escape(f.Account), "#C0C0C0"), false, false);
                AddHtml(cols[4], y + 2, 185, 20, ProfileGump.Text(FlagGump.Escape(String.Join("; ", f.Reasons.Select(Short))), "#C0C0C0"), false, false);
                AddHtml(cols[5], y + 2, 55, 20, ProfileGump.Text(ProfileGump.Score(watch), ProfileGump.ScoreColor(watch, Ratings.ScoutWatchThreshold)), false, false);
                AddHtml(cols[6], y + 2, 80, 20, ProfileGump.Text(f.Status.ToString(), FlagGump.StatusColor(f.Status)), false, false);

                y += 24;
            }

            if (flags.Count == 0)
                AddHtml(50, y, 400, 20, ProfileGump.Text("No flags."), false, false);

            int by = height - 40;

            if (filter == FlagStatus.Confirmed && flags.Count > 0)
            {
                AddButton(20, by, 4005, 4007, ButtonExport, GumpButtonType.Reply, 0);
                AddHtml(55, by + 2, 420, 20, ProfileGump.Text("Write the confirmed list to a file for the ban wave"), false, false);
            }

            AddHtml(Width - 220, by + 2, 80, 20, ProfileGump.Text(String.Format("Page {0}/{1}", m_Page + 1, pages)), false, false);

            if (m_Page > 0)
                AddButton(Width - 130, by, 4014, 4016, ButtonPrev, GumpButtonType.Reply, 0);

            if (m_Page < pages - 1)
                AddButton(Width - 90, by, 4005, 4007, ButtonNext, GumpButtonType.Reply, 0);
        }

        private static string Short(string reason)
        {
            int i = reason.IndexOf(" (");
            return i > 0 ? reason.Substring(0, i) : reason;
        }

        private void AddFilter(int x, int id, string label, bool selected)
        {
            AddButton(x, 46, 4005, 4007, id, GumpButtonType.Reply, 0);
            AddHtml(x + 35, 48, 75, 20, ProfileGump.Text(label, selected ? "#FFD080" : "#FFFFFF"), false, false);
        }

        /// <summary>Confirmed flags as a plain list of accounts and characters to act on.</summary>
        private static string ExportConfirmed()
        {
            try
            {
                Directory.CreateDirectory(Flags.EvidencePath);

                string path = Path.Combine(Flags.EvidencePath, String.Format("confirmed_{0:yyyyMMdd-HHmm}.txt", DateTime.UtcNow));

                File.WriteAllLines(path, Flags.All
                    .Where(f => f.Status == FlagStatus.Confirmed)
                    .OrderBy(f => f.Account)
                    .Select(f => String.Format("account {0}\tcharacter {1}\tflag #{2}\tconfirmed by {3} on {4:yyyy-MM-dd}\t{5}",
                        f.Account, f.Name, f.Id, f.ResolvedBy, f.ResolvedAt, f.StaffNote)));

                return path;
            }
            catch (Exception e)
            {
                Diagnostics.ExceptionLogging.LogException(e);
                return null;
            }
        }

        public override void OnResponse(NetState sender, RelayInfo info)
        {
            Mobile from = sender.Mobile;

            if (from == null || from.AccessLevel < AccessLevel.Counselor)
                return;

            int id = info.ButtonID;

            if (id == ButtonPrev)
            {
                from.SendGump(new FlagsGump(from, m_Filter, m_Page - 1));
            }
            else if (id == ButtonNext)
            {
                from.SendGump(new FlagsGump(from, m_Filter, m_Page + 1));
            }
            else if (id == ButtonExport)
            {
                string path = ExportConfirmed();

                from.SendMessage(path != null ? "Confirmed list written to " + path : "Could not write the file; see the console.");
                from.SendGump(new FlagsGump(from, m_Filter, m_Page));
            }
            else if (id == ButtonFilterAll)
            {
                from.SendGump(new FlagsGump(from, null, 0));
            }
            else if (id >= ButtonFilterBase && id < ButtonFilterBase + Enum.GetValues(typeof(FlagStatus)).Length)
            {
                from.SendGump(new FlagsGump(from, (FlagStatus)(id - ButtonFilterBase), 0));
            }
            else if (id >= ButtonFlagBase && id - ButtonFlagBase < m_Rows.Count)
            {
                from.SendGump(new FlagGump(from, m_Rows[id - ButtonFlagBase], -1, m_Filter));
            }
        }
    }

    /// <summary>One flag: reasons, verdict buttons, staff note and evidence snapshots.</summary>
    public class FlagGump : Gump
    {
        private const int Width = 760;
        private const int Height = 650;

        private const int ButtonLegit = 1;
        private const int ButtonConfirm = 2;
        private const int ButtonActioned = 3;
        private const int ButtonDismiss = 4;
        private const int ButtonReopen = 5;
        private const int ButtonSaveNote = 6;
        private const int ButtonSnapshotNow = 7;
        private const int ButtonProfile = 8;
        private const int ButtonNetwork = 9;
        private const int ButtonList = 10;
        private const int ButtonGoTo = 11;
        private const int ButtonJail = 12;
        private const int ButtonRelease = 13;
        private const int ButtonSnapshotBase = 20;

        private const int NoteEntry = 0;

        private readonly int m_Id;
        private readonly int m_Snapshot;
        private readonly FlagStatus? m_ListFilter;

        public static string Escape(string text)
        {
            return ProfileGump.Escape(text);
        }

        public static string StatusColor(FlagStatus status)
        {
            switch (status)
            {
                case FlagStatus.Open: return "#FFB040";
                case FlagStatus.Confirmed: return "#FF5050";
                case FlagStatus.Legit: return "#70E070";
                default: return "#A0A0A0";
            }
        }

        public FlagGump(Mobile staff, int id, int snapshot, FlagStatus? listFilter)
            : base(40, 40)
        {
            m_Id = id;
            m_ListFilter = listFilter;

            FlagRecord flag = Flags.Find(id);

            AddPage(0);
            AddBackground(0, 0, Width, Height, 9270);
            AddAlphaRegion(10, 10, Width - 20, Height - 20);

            if (flag == null)
            {
                AddHtml(20, 20, Width - 40, 20, ProfileGump.Text("This flag no longer exists."), false, false);
                return;
            }

            m_Snapshot = snapshot < 0 || snapshot >= flag.Snapshots.Count ? flag.Snapshots.Count - 1 : snapshot;

            AddHtml(20, 18, Width - 40, 20, ProfileGump.Text(String.Format("<BIG>Flag #{0}: {1}</BIG>   account {2}   {3}",
                flag.Id, Escape(flag.Name), Escape(flag.Account), ProfileGump.Text(flag.Status.ToString(), StatusColor(flag.Status))), "#FFD080"), false, false);

            string resolved = flag.Status != FlagStatus.Open && flag.ResolvedAt != DateTime.MinValue
                ? String.Format(", set to {0} by {1} on {2:yyyy-MM-dd HH:mm}", flag.Status, flag.ResolvedBy, flag.ResolvedAt)
                : String.Empty;

            AddHtml(20, 42, Width - 40, 20, ProfileGump.Text(String.Format("Flagged {0:yyyy-MM-dd HH:mm} UTC, updated {1:yyyy-MM-dd HH:mm}{2}",
                flag.Created, flag.Updated, resolved), "#C0C0C0"), false, false);

            AddHtml(20, 64, Width - 40, 20, ProfileGump.Text("Reasons: " + Escape(String.Join("; ", flag.Reasons)), "#FF8080"), false, false);

            // Staff note
            AddHtml(20, 90, 80, 20, ProfileGump.Text("Staff note:"), false, false);
            AddImageTiled(100, 88, 540, 22, 2624);
            AddTextEntry(104, 90, 532, 20, 0x481, NoteEntry, flag.StaffNote ?? String.Empty, 200);
            AddButton(650, 88, 4005, 4007, ButtonSaveNote, GumpButtonType.Reply, 0);
            AddHtml(685, 90, 60, 20, ProfileGump.Text("Save"), false, false);

            // Snapshot selector
            int x = 20;

            AddHtml(x, 120, 90, 20, ProfileGump.Text("Evidence:", "#FFD080"), false, false);
            x += 85;

            for (int i = 0; i < flag.Snapshots.Count; i++)
            {
                AddButton(x, 120, i == m_Snapshot ? 4006 : 4005, 4007, ButtonSnapshotBase + i, GumpButtonType.Reply, 0);
                AddHtml(x + 33, 122, 90, 20, ProfileGump.Text(flag.Snapshots[i].Time.ToString("MM-dd HH:mm"), i == m_Snapshot ? "#FFD080" : "#FFFFFF"), false, false);
                x += 125;
            }

            Snapshot s = m_Snapshot >= 0 ? flag.Snapshots[m_Snapshot] : null;

            AddHtml(20, 150, Width - 40, Height - 270, s != null ? Render(s) : ProfileGump.Text("No evidence recorded."), true, true);

            // Actions
            int y = Height - 112;

            if (flag.Status == FlagStatus.Open)
            {
                AddAction(20, y, ButtonLegit, "Legit player");
                AddAction(150, y, ButtonConfirm, "Confirmed bot");
                AddAction(290, y, ButtonDismiss, "Dismiss");
            }
            else
            {
                AddAction(20, y, ButtonReopen, "Reopen");

                if (flag.Status == FlagStatus.Confirmed)
                    AddAction(150, y, ButtonActioned, "Actioned (banned)");
            }

            AddAction(470, y, ButtonSnapshotNow, "New snapshot");

            y += 34;

            AddAction(20, y, ButtonProfile, "Profile");
            AddAction(150, y, ButtonNetwork, "Network");
            AddAction(290, y, ButtonList, "Back to flags");

            ProfileGump.AddStaffActions(this, staff, flag.Character, 20, y + 34, ButtonGoTo, ButtonJail, ButtonRelease);
        }

        private void AddAction(int x, int y, int id, string label)
        {
            AddButton(x, y, 4005, 4007, id, GumpButtonType.Reply, 0);
            AddHtml(x + 35, y + 2, 140, 20, ProfileGump.Text(label), false, false);
        }

        private static string Render(Snapshot s)
        {
            return String.Join("<BR>", s.Lines.Select(line => line.StartsWith("==")
                ? "<BASEFONT COLOR=#000080><B>" + Escape(line.Trim('=', ' ')) + "</B></BASEFONT>"
                : "<BASEFONT COLOR=#000000>" + Escape(line) + "</BASEFONT>"));
        }

        public override void OnResponse(NetState sender, RelayInfo info)
        {
            Mobile from = sender.Mobile;
            FlagRecord flag = Flags.Find(m_Id);

            if (from == null || from.AccessLevel < AccessLevel.Counselor || flag == null)
                return;

            string note = info.GetTextEntry(NoteEntry)?.Text?.Trim();
            int id = info.ButtonID;

            switch (id)
            {
                case 0:
                    return;
                case ButtonLegit:
                    Flags.SetStatus(flag, FlagStatus.Legit, from, note);
                    break;
                case ButtonConfirm:
                    Flags.SetStatus(flag, FlagStatus.Confirmed, from, note);
                    break;
                case ButtonActioned:
                    Flags.SetStatus(flag, FlagStatus.Actioned, from, note);
                    break;
                case ButtonDismiss:
                    Flags.SetStatus(flag, FlagStatus.Dismissed, from, note);
                    break;
                case ButtonReopen:
                    Flags.SetStatus(flag, FlagStatus.Open, from, note);
                    break;
                case ButtonSaveNote:
                    flag.StaffNote = note ?? String.Empty;
                    flag.Updated = DateTime.UtcNow;
                    break;
                case ButtonSnapshotNow:
                    {
                        BotProfile p = Ratings.ComputeOne(flag.Character);

                        if (p == null)
                        {
                            from.SendMessage("The character has no activity in the rating window.");
                        }
                        else
                        {
                            Flags.AddSnapshot(flag, p, Networks.Build());
                        }

                        from.SendGump(new FlagGump(from, m_Id, -1, m_ListFilter));
                        return;
                    }
                case ButtonProfile:
                    from.SendGump(new ProfileGump(from, flag.Character, ProfileGump.View.Overview, false));
                    return;
                case ButtonNetwork:
                    if (flag.Account != null)
                        from.SendGump(new NetworkGump(from, flag.Account, 0));
                    return;
                case ButtonList:
                    from.SendGump(new FlagsGump(from, m_ListFilter, 0));
                    return;
                case ButtonGoTo:
                    StaffActions.GoTo(from, flag.Character);
                    break;
                case ButtonJail:
                    {
                        int flagId = m_Id;
                        int snapshot = m_Snapshot;
                        FlagStatus? filter = m_ListFilter;

                        from.SendGump(new JailConfirmGump(flag.Character, flag.Name, staff => staff.SendGump(new FlagGump(staff, flagId, snapshot, filter))));
                        return;
                    }
                case ButtonRelease:
                    StaffActions.Release(from, flag.Character);
                    break;
                default:
                    if (id >= ButtonSnapshotBase && id - ButtonSnapshotBase < flag.Snapshots.Count)
                        from.SendGump(new FlagGump(from, m_Id, id - ButtonSnapshotBase, m_ListFilter));
                    return;
            }

            from.SendGump(new FlagGump(from, m_Id, m_Snapshot, m_ListFilter));
        }
    }
}
