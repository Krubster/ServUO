using Server.Gumps;
using Server.Network;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Server.Services.BotWatch
{
    /// <summary>
    /// Every character active in the rating window, sortable, with scout patterns marked.
    /// </summary>
    public class ProfilesGump : Gump
    {
        public enum SortBy
        {
            Watch,
            Session,
            Online,
            Name
        }

        private const int Width = 760;
        private const int PerPage = 15;

        private const int ButtonPrev = 1;
        private const int ButtonNext = 2;
        private const int ButtonSortBase = 10;
        private const int ButtonProfileBase = 100;

        private readonly SortBy m_Sort;
        private readonly int m_Page;
        private readonly List<Serial> m_Rows = new List<Serial>();

        public ProfilesGump(Mobile staff, SortBy sort, int page)
            : base(40, 40)
        {
            m_Sort = sort;

            List<BotProfile> profiles = Sort(Ratings.ComputeAll().Values, sort).ToList();

            int pages = Math.Max(1, (profiles.Count + PerPage - 1) / PerPage);
            m_Page = Math.Max(0, Math.Min(page, pages - 1));

            List<BotProfile> rows = profiles.Skip(m_Page * PerPage).Take(PerPage).ToList();
            m_Rows.AddRange(rows.Select(p => p.Record.Serial));

            int height = 130 + PerPage * 24;

            AddPage(0);
            AddBackground(0, 0, Width, height, 9270);
            AddAlphaRegion(10, 10, Width - 20, height - 20);

            AddHtml(20, 18, Width - 40, 20, ProfileGump.Text(String.Format(
                "<BIG>BotWatch profiles</BIG>   {0} characters active in the last {1:F0} days, {2} with the scout pattern",
                profiles.Count, Ratings.Window.TotalDays, profiles.Count(p => p.ScoutPattern)), "#FFD080"), false, false);

            int[] cols = { 50, 190, 300, 365, 425, 490, 570, 675 };

            AddSortHeader(cols[0], "Name", SortBy.Name);
            AddHtml(cols[1], 48, 110, 20, ProfileGump.Text("Account", "#FFD080"), false, false);
            AddSortHeader(cols[2], "Online", SortBy.Online);
            AddSortHeader(cols[3], "Watch", SortBy.Watch);
            AddSortHeader(cols[4], "Session", SortBy.Session);
            AddHtml(cols[5], 48, 80, 20, ProfileGump.Text("Reports", "#FFD080"), false, false);
            AddHtml(cols[6], 48, 110, 20, ProfileGump.Text("Top activity", "#FFD080"), false, false);
            AddHtml(cols[7], 48, 70, 20, ProfileGump.Text("Pattern", "#FFD080"), false, false);

            int y = 74;

            for (int i = 0; i < rows.Count; i++)
            {
                BotProfile p = rows[i];
                CharacterRecord rec = p.Record;

                RatingCategory top = Enum.GetValues(typeof(RatingCategory)).Cast<RatingCategory>().OrderByDescending(c => p[c]).First();
                string topText = p[top] > 0 ? String.Format("{0} {1}", top, p[top]) : "none";

                string name = rec.Name + (rec.IsDeleted ? " (deleted)" : BotWatch.IsOnline(rec.Serial) ? " *" : String.Empty);

                AddButton(20, y, 4011, 4012, ButtonProfileBase + i, GumpButtonType.Reply, 0);
                AddHtml(cols[0], y + 2, 135, 20, ProfileGump.Text(ProfileGump.Escape(name)), false, false);
                AddHtml(cols[1], y + 2, 105, 20, ProfileGump.Text(ProfileGump.Escape(rec.Account ?? "?"), "#C0C0C0"), false, false);
                AddHtml(cols[2], y + 2, 60, 20, ProfileGump.Text(String.Format("{0:F1}h", p.OnlineHours) + (p.InPopulation ? String.Empty : "!"), "#C0C0C0"), false, false);
                AddHtml(cols[3], y + 2, 55, 20, ProfileGump.Text(ProfileGump.Score(p.Watch), ProfileGump.ScoreColor(p.Watch, Ratings.ScoutWatchThreshold)), false, false);
                AddHtml(cols[4], y + 2, 60, 20, ProfileGump.Text(ProfileGump.Score(p.Session), ProfileGump.ScoreColor(p.Session, Ratings.SessionThreshold)), false, false);
                AddHtml(cols[5], y + 2, 75, 20, ProfileGump.Text(p.Reporters > 0 ? String.Format("{0} ({1})", p.Reports, p.Reporters) : "-", p.Reporters > 0 ? "#FFB040" : "#C0C0C0"), false, false);
                AddHtml(cols[6], y + 2, 105, 20, ProfileGump.Text(topText, "#C0C0C0"), false, false);

                if (p.ScoutPattern)
                    AddHtml(cols[7], y + 2, 70, 20, ProfileGump.Text("SCOUT", "#FF5050"), false, false);

                y += 24;
            }

            int by = height - 40;

            AddHtml(20, by + 2, 420, 20, ProfileGump.Text("* online   ! too little time online for reliable ratings", "#A0A0A0"), false, false);
            AddHtml(Width - 220, by + 2, 80, 20, ProfileGump.Text(String.Format("Page {0}/{1}", m_Page + 1, pages)), false, false);

            if (m_Page > 0)
                AddButton(Width - 130, by, 4014, 4016, ButtonPrev, GumpButtonType.Reply, 0);

            if (m_Page < pages - 1)
                AddButton(Width - 90, by, 4005, 4007, ButtonNext, GumpButtonType.Reply, 0);
        }

        private static IEnumerable<BotProfile> Sort(IEnumerable<BotProfile> profiles, SortBy sort)
        {
            switch (sort)
            {
                case SortBy.Session: return profiles.OrderByDescending(p => p.Session ?? -1);
                case SortBy.Online: return profiles.OrderByDescending(p => p.OnlineHours);
                case SortBy.Name: return profiles.OrderBy(p => p.Record.Name);
                default: return profiles.OrderByDescending(p => p.ScoutPattern).ThenByDescending(p => p.Reporters).ThenByDescending(p => p.Watch ?? -1);
            }
        }

        private void AddSortHeader(int x, string label, SortBy sort)
        {
            AddButton(x, 50, 2117, 2118, ButtonSortBase + (int)sort, GumpButtonType.Reply, 0);
            AddHtml(x + 18, 48, 100, 20, ProfileGump.Text(label, m_Sort == sort ? "#FFFFFF" : "#FFD080"), false, false);
        }

        public override void OnResponse(NetState sender, RelayInfo info)
        {
            Mobile from = sender.Mobile;

            if (from == null || from.AccessLevel < AccessLevel.Counselor)
                return;

            int id = info.ButtonID;

            if (id == ButtonPrev)
            {
                from.SendGump(new ProfilesGump(from, m_Sort, m_Page - 1));
            }
            else if (id == ButtonNext)
            {
                from.SendGump(new ProfilesGump(from, m_Sort, m_Page + 1));
            }
            else if (id >= ButtonSortBase && id < ButtonSortBase + 4)
            {
                from.SendGump(new ProfilesGump(from, (SortBy)(id - ButtonSortBase), 0));
            }
            else if (id >= ButtonProfileBase && id - ButtonProfileBase < m_Rows.Count)
            {
                from.SendGump(new ProfileGump(from, m_Rows[id - ButtonProfileBase], ProfileGump.View.Overview, true));
            }
        }
    }
}
