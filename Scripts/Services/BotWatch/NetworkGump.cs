using Server.Gumps;
using Server.Network;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Server.Services.BotWatch
{
    /// <summary>
    /// All accounts linked to one account, how they are linked, and their characters'
    /// scores.
    /// </summary>
    public class NetworkGump : Gump
    {
        private const int Width = 760;
        private const int Height = 600;
        private const int AccountsPerPage = 3;
        private const int MaxLinks = 2;
        private const int MaxCharacters = 4;

        private const int ButtonPrev = 1;
        private const int ButtonNext = 2;
        private const int ButtonList = 3;
        private const int ButtonCharacterBase = 100;

        private readonly string m_Account;
        private readonly int m_Page;
        private readonly List<Serial> m_Characters = new List<Serial>();

        public NetworkGump(Mobile staff, string account, int page)
            : base(40, 40)
        {
            m_Account = account;

            NetworkGraph g = Networks.Build();
            Dictionary<Serial, BotProfile> profiles = Ratings.ComputeAll();
            AccountNetwork network = g.NetworkOf(account);

            List<string> accounts = network != null ? network.Accounts.ToList() : new List<string> { account };

            // The account asked about first, then fresh accounts, then by last activity.
            accounts = accounts
                .OrderByDescending(a => a == account)
                .ThenByDescending(a => g.Accounts.TryGetValue(a, out AccountInfo i) && i.IsFresh)
                .ThenByDescending(a => g.Accounts.TryGetValue(a, out AccountInfo i) ? i.LastSeen : DateTime.MinValue)
                .ToList();

            int pages = Math.Max(1, (accounts.Count + AccountsPerPage - 1) / AccountsPerPage);
            m_Page = Math.Max(0, Math.Min(page, pages - 1));

            AddPage(0);
            AddBackground(0, 0, Width, Height, 9270);
            AddAlphaRegion(10, 10, Width - 20, Height - 20);

            int characters = accounts.Sum(a => g.Accounts.TryGetValue(a, out AccountInfo i) ? i.Characters.Count : 0);

            AddHtml(20, 18, Width - 40, 20, ProfileGump.Text(String.Format("<BIG>Network of {0}</BIG>   {1} account{2}, {3} characters",
                ProfileGump.Escape(account), accounts.Count, accounts.Count == 1 ? "" : "s", characters), "#FFD080"), false, false);

            AddHtml(20, 42, Width - 40, 20, ProfileGump.Text(accounts.Count == 1
                ? "No linked accounts: no shared IP and no sessions that start and end together."
                : "Linked by shared IPs, concurrent sessions from one IP, or sessions that repeatedly start and end together.", "#C0C0C0"), false, false);

            int y = 72;

            foreach (string a in accounts.Skip(m_Page * AccountsPerPage).Take(AccountsPerPage))
            {
                y = AddAccount(g, profiles, a, y);
                y += 10;
            }

            int by = Height - 40;

            AddButton(20, by, 4005, 4007, ButtonList, GumpButtonType.Reply, 0);
            AddHtml(55, by + 2, 120, 20, ProfileGump.Text("All networks"), false, false);

            AddHtml(Width - 220, by + 2, 80, 20, ProfileGump.Text(String.Format("Page {0}/{1}", m_Page + 1, pages)), false, false);

            if (m_Page > 0)
                AddButton(Width - 130, by, 4014, 4016, ButtonPrev, GumpButtonType.Reply, 0);

            if (m_Page < pages - 1)
                AddButton(Width - 90, by, 4005, 4007, ButtonNext, GumpButtonType.Reply, 0);
        }

        private int AddAccount(NetworkGraph g, Dictionary<Serial, BotProfile> profiles, string account, int y)
        {
            g.Accounts.TryGetValue(account, out AccountInfo info);

            string created = info != null && info.Created != DateTime.MinValue ? "created " + Networks.Ago(info.Created) : "created ?";
            string fresh = info != null && info.IsFresh ? "  NEW" : String.Empty;
            string seen = info != null && info.LastSeen != DateTime.MinValue
                ? String.Format("first seen {0}, last seen {1}, {2} IP{3}", Networks.Ago(info.FirstSeen), Networks.Ago(info.LastSeen), info.Addresses.Count, info.Addresses.Count == 1 ? "" : "s")
                : "no sessions on record";

            AddHtml(20, y + 4, Width - 40, 20, ProfileGump.Text(String.Format("<BIG>{0}</BIG>{1}   {2} | {3}", ProfileGump.Escape(account), fresh, created, seen),
                info != null && info.IsFresh ? "#FFB040" : "#FFFFFF"), false, false);

            y += 28;

            foreach (AccountLink link in g.LinksOf(account).OrderByDescending(l => l.LastShared).Take(MaxLinks))
            {
                AddHtml(35, y, Width - 55, 18, ProfileGump.Text(ProfileGump.Escape(String.Format("linked to {0}: {1}", link.Other(account), link.Describe())), "#A0A0A0"), false, false);
                y += 18;
            }

            int more = g.LinksOf(account).Count() - MaxLinks;

            if (more > 0)
            {
                AddHtml(35, y, Width - 55, 18, ProfileGump.Text(String.Format("and {0} more links", more), "#A0A0A0"), false, false);
                y += 18;
            }

            if (info == null || info.Characters.Count == 0)
                return y;

            foreach (CharacterRecord rec in info.Characters.OrderByDescending(c => c.LastActive).Take(MaxCharacters))
            {
                profiles.TryGetValue(rec.Serial, out BotProfile p);

                int index = m_Characters.Count;
                m_Characters.Add(rec.Serial);

                string state = rec.IsDeleted ? " (deleted)" : BotWatch.IsOnline(rec.Serial) ? " *" : String.Empty;

                AddButton(35, y + 2, 4011, 4012, ButtonCharacterBase + index, GumpButtonType.Reply, 0);
                AddHtml(70, y + 4, 170, 20, ProfileGump.Text(ProfileGump.Escape(rec.Name) + state), false, false);

                if (p != null)
                {
                    RatingCategory top = Enum.GetValues(typeof(RatingCategory)).Cast<RatingCategory>().OrderByDescending(c => p[c]).First();

                    AddHtml(245, y + 4, 90, 20, ProfileGump.Text("Watch " + ProfileGump.Score(p.Watch), ProfileGump.ScoreColor(p.Watch, Ratings.ScoutWatchThreshold)), false, false);
                    AddHtml(340, y + 4, 100, 20, ProfileGump.Text("Session " + ProfileGump.Score(p.Session), ProfileGump.ScoreColor(p.Session, Ratings.SessionThreshold)), false, false);
                    AddHtml(445, y + 4, 180, 20, ProfileGump.Text(p[top] > 0 ? String.Format("top: {0} {1}, {2:F1}h online", top, p[top], p.OnlineHours) : String.Format("no activity, {0:F1}h online", p.OnlineHours), "#C0C0C0"), false, false);

                    if (p.ScoutPattern)
                        AddHtml(640, y + 4, 80, 20, ProfileGump.Text("SCOUT", "#FF5050"), false, false);
                }
                else
                {
                    AddHtml(245, y + 4, 300, 20, ProfileGump.Text("not active in the rating window, last active " + Networks.Ago(rec.LastActive), "#A0A0A0"), false, false);
                }

                y += 24;
            }

            if (info.Characters.Count > MaxCharacters)
            {
                AddHtml(70, y, 300, 18, ProfileGump.Text(String.Format("and {0} more characters", info.Characters.Count - MaxCharacters), "#A0A0A0"), false, false);
                y += 18;
            }

            return y;
        }

        public override void OnResponse(NetState sender, RelayInfo info)
        {
            Mobile from = sender.Mobile;

            if (from == null || from.AccessLevel < AccessLevel.Counselor)
                return;

            int id = info.ButtonID;

            if (id == ButtonPrev)
                from.SendGump(new NetworkGump(from, m_Account, m_Page - 1));
            else if (id == ButtonNext)
                from.SendGump(new NetworkGump(from, m_Account, m_Page + 1));
            else if (id == ButtonList)
                from.SendGump(new NetworksGump(from, 0));
            else if (id >= ButtonCharacterBase && id - ButtonCharacterBase < m_Characters.Count)
                from.SendGump(new ProfileGump(from, m_Characters[id - ButtonCharacterBase], ProfileGump.View.Overview, false));
        }
    }

    /// <summary>Every network of two or more accounts, most suspicious first.</summary>
    public class NetworksGump : Gump
    {
        private const int Width = 760;
        private const int PerPage = 15;

        private const int ButtonPrev = 1;
        private const int ButtonNext = 2;
        private const int ButtonNetworkBase = 100;

        private readonly int m_Page;
        private readonly List<string> m_Rows = new List<string>();

        private class Row
        {
            public AccountNetwork Network;
            public int Characters;
            public int Fresh;
            public int Scouts;
            public double MaxWatch = -1;
            public double MaxSession = -1;
        }

        public NetworksGump(Mobile staff, int page)
            : base(40, 40)
        {
            NetworkGraph g = Networks.Build();
            Dictionary<Serial, BotProfile> profiles = Ratings.ComputeAll();

            List<Row> rows = new List<Row>();

            foreach (AccountNetwork n in g.Networks.Where(n => n.Accounts.Count > 1))
            {
                Row row = new Row { Network = n };

                foreach (string a in n.Accounts)
                {
                    if (!g.Accounts.TryGetValue(a, out AccountInfo info))
                        continue;

                    row.Characters += info.Characters.Count;

                    if (info.IsFresh)
                        row.Fresh++;

                    foreach (CharacterRecord rec in info.Characters)
                    {
                        if (!profiles.TryGetValue(rec.Serial, out BotProfile p))
                            continue;

                        if (p.ScoutPattern)
                            row.Scouts++;

                        row.MaxWatch = Math.Max(row.MaxWatch, p.Watch ?? -1);
                        row.MaxSession = Math.Max(row.MaxSession, p.Session ?? -1);
                    }
                }

                rows.Add(row);
            }

            rows = rows.OrderByDescending(r => r.Scouts).ThenByDescending(r => r.Fresh).ThenByDescending(r => r.MaxWatch).ToList();

            int pages = Math.Max(1, (rows.Count + PerPage - 1) / PerPage);
            m_Page = Math.Max(0, Math.Min(page, pages - 1));

            int height = 130 + PerPage * 24;

            AddPage(0);
            AddBackground(0, 0, Width, height, 9270);
            AddAlphaRegion(10, 10, Width - 20, height - 20);

            AddHtml(20, 18, Width - 40, 20, ProfileGump.Text(String.Format("<BIG>Account networks</BIG>   {0} networks of two or more accounts", rows.Count), "#FFD080"), false, false);

            int[] cols = { 50, 360, 440, 500, 570, 650 };
            string[] heads = { "Accounts", "Characters", "New", "Scouts", "Max Watch", "Max Session" };

            for (int i = 0; i < cols.Length; i++)
                AddHtml(cols[i], 48, 110, 20, ProfileGump.Text(heads[i], "#FFD080"), false, false);

            int y = 74;

            foreach (Row row in rows.Skip(m_Page * PerPage).Take(PerPage))
            {
                int index = m_Rows.Count;
                m_Rows.Add(row.Network.Accounts[0]);

                string names = String.Join(", ", row.Network.Accounts.Take(4)) + (row.Network.Accounts.Count > 4 ? String.Format(" +{0}", row.Network.Accounts.Count - 4) : String.Empty);
                double? maxWatch = row.MaxWatch >= 0 ? row.MaxWatch : (double?)null;
                double? maxSession = row.MaxSession >= 0 ? row.MaxSession : (double?)null;

                AddButton(20, y, 4011, 4012, ButtonNetworkBase + index, GumpButtonType.Reply, 0);
                AddHtml(cols[0], y + 2, 300, 20, ProfileGump.Text(ProfileGump.Escape(names)), false, false);
                AddHtml(cols[1], y + 2, 70, 20, ProfileGump.Text(row.Characters.ToString(), "#C0C0C0"), false, false);
                AddHtml(cols[2], y + 2, 50, 20, ProfileGump.Text(row.Fresh.ToString(), row.Fresh > 0 ? "#FFB040" : "#C0C0C0"), false, false);
                AddHtml(cols[3], y + 2, 60, 20, ProfileGump.Text(row.Scouts.ToString(), row.Scouts > 0 ? "#FF5050" : "#C0C0C0"), false, false);
                AddHtml(cols[4], y + 2, 70, 20, ProfileGump.Text(ProfileGump.Score(maxWatch), ProfileGump.ScoreColor(maxWatch, Ratings.ScoutWatchThreshold)), false, false);
                AddHtml(cols[5], y + 2, 80, 20, ProfileGump.Text(ProfileGump.Score(maxSession), ProfileGump.ScoreColor(maxSession, Ratings.SessionThreshold)), false, false);

                y += 24;
            }

            if (rows.Count == 0)
                AddHtml(50, y, 500, 20, ProfileGump.Text("No accounts are linked yet."), false, false);

            int by = height - 40;

            AddHtml(Width - 220, by + 2, 80, 20, ProfileGump.Text(String.Format("Page {0}/{1}", m_Page + 1, pages)), false, false);

            if (m_Page > 0)
                AddButton(Width - 130, by, 4014, 4016, ButtonPrev, GumpButtonType.Reply, 0);

            if (m_Page < pages - 1)
                AddButton(Width - 90, by, 4005, 4007, ButtonNext, GumpButtonType.Reply, 0);
        }

        public override void OnResponse(NetState sender, RelayInfo info)
        {
            Mobile from = sender.Mobile;

            if (from == null || from.AccessLevel < AccessLevel.Counselor)
                return;

            int id = info.ButtonID;

            if (id == ButtonPrev)
                from.SendGump(new NetworksGump(from, m_Page - 1));
            else if (id == ButtonNext)
                from.SendGump(new NetworksGump(from, m_Page + 1));
            else if (id >= ButtonNetworkBase && id - ButtonNetworkBase < m_Rows.Count)
                from.SendGump(new NetworkGump(from, m_Rows[id - ButtonNetworkBase], 0));
        }
    }
}
