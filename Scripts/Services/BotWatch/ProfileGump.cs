using Server.Gumps;
using Server.Network;
using System;
using System.Linq;

namespace Server.Services.BotWatch
{
    /// <summary>
    /// One character's BotWatch profile: activity ratings, Watch and Session scores with
    /// their parts, a 7-day overview and the recent session log.
    /// </summary>
    public class ProfileGump : Gump
    {
        public enum View
        {
            Overview,
            Daily,
            Sessions
        }

        private const int Width = 760;
        private const int Height = 560;

        private const int ButtonOverview = 1;
        private const int ButtonDaily = 2;
        private const int ButtonSessions = 3;
        private const int ButtonRefresh = 4;
        private const int ButtonGoTo = 5;
        private const int ButtonList = 6;
        private const int ButtonNetwork = 7;

        private readonly Serial m_Serial;
        private readonly View m_View;
        private readonly bool m_FromList;

        public ProfileGump(Mobile staff, Serial serial, View view, bool fromList)
            : base(40, 40)
        {
            m_Serial = serial;
            m_View = view;
            m_FromList = fromList;

            AddPage(0);
            AddBackground(0, 0, Width, Height, 9270);
            AddAlphaRegion(10, 10, Width - 20, Height - 20);

            BotProfile p = Ratings.ComputeOne(serial);
            BotWatch.Characters.TryGetValue(serial, out CharacterRecord rec);

            if (rec == null)
            {
                AddHtml(20, 20, Width - 40, 20, Text("No BotWatch record for this character."), false, false);
                return;
            }

            AddHeader(rec, p);

            if (p == null)
            {
                AddHtml(20, 90, Width - 40, 20, Text(String.Format("No activity in the last {0:F0} days.", Ratings.Window.TotalDays)), false, false);
            }
            else
            {
                switch (view)
                {
                    case View.Overview: AddOverview(p); break;
                    case View.Daily: AddDaily(p); break;
                    case View.Sessions: AddSessions(rec); break;
                }
            }

            AddButtons(staff, rec);
        }

        #region Formatting
        public static string Text(string text)
        {
            return Text(text, "#FFFFFF");
        }

        public static string Text(string text, string color)
        {
            return String.Format("<BASEFONT COLOR={0}>{1}</BASEFONT>", color, text);
        }

        public static string ScoreColor(double? score, int threshold)
        {
            if (!score.HasValue)
                return "#A0A0A0";

            if (score.Value >= threshold)
                return "#FF5050";

            if (score.Value >= threshold * 2 / 3.0)
                return "#FFB040";

            return "#70E070";
        }

        public static string Score(double? score)
        {
            return score.HasValue ? ((int)Math.Round(score.Value)).ToString() : "n/a";
        }

        private static string Age(DateTime created)
        {
            if (created == DateTime.MinValue)
                return "?";

            return String.Format("{0:F0}d", (DateTime.UtcNow - created).TotalDays);
        }

        private static string Place(Map map, Point3D loc, bool town)
        {
            return String.Format("{0} {1},{2}{3}", map, loc.X, loc.Y, town ? " (town)" : String.Empty);
        }
        #endregion

        private void AddHeader(CharacterRecord rec, BotProfile p)
        {
            string status = rec.IsDeleted ? "deleted" : BotWatch.IsOnline(rec.Serial) ? "online" : "offline";

            AddHtml(20, 18, Width - 40, 20, Text(String.Format("<BIG>{0}</BIG>   account {1}   ({2})", rec.Name, rec.Account, status), "#FFD080"), false, false);

            string line = String.Format("Character age {0}, account age {1}, skills {2:F1}, bank items {3}",
                Age(rec.CharacterCreated), Age(rec.AccountCreated), rec.SkillsTotal / 10.0, rec.BankItems);

            if (p != null)
            {
                line += String.Format(" | last {0:F0} days: {1:F1}h online, {2:F1}h outside towns, {3} sessions",
                    Ratings.Window.TotalDays, p.OnlineHours, p.Window.OutdoorSeconds / 3600.0, p.SessionCount);
            }

            AddHtml(20, 42, Width - 40, 20, Text(line, "#C0C0C0"), false, false);

            if (p == null)
                return;

            string banner = p.ScoutPattern
                ? Text(String.Format("SCOUT PATTERN: Watch {0} with core activity (PvM, PvP, crafting, gathering, trade) at most {1}",
                    Score(p.Watch), p.CoreActivity), "#FF5050")
                : Text("No scout pattern", "#70E070");

            FlagRecord flag = Flags.LatestFor(rec.Serial);

            if (flag != null)
                banner += Text(String.Format("   Flag #{0}: {1}", flag.Id, flag.Status), FlagGump.StatusColor(flag.Status));

            if (p.Reporters > 0)
                banner += Text(String.Format("   Reported by {0} player{1}", p.Reporters, p.Reporters == 1 ? "" : "s"), "#FFB040");

            Mobile m = World.FindMobile(rec.Serial);

            if (m != null && InfoDenial.IsDenied(m))
                banner += Text("   Information denial active", "#FF5050");
            else if (InfoDenial.IsCandidate(rec.Serial))
                banner += Text("   Information denial when idle", "#FFB040");

            AddHtml(20, 64, Width - 40, 20, banner, false, false);
        }

        private void AddOverview(BotProfile p)
        {
            // Activity ratings
            AddHtml(20, 92, 270, 20, Text("Activity (rank among active players)", "#FFD080"), false, false);

            int y = 116;

            foreach (RatingCategory c in Enum.GetValues(typeof(RatingCategory)))
            {
                int value = p[c];

                AddHtml(20, y - 3, 110, 20, Text(c.ToString()), false, false);
                AddImage(130, y, 0x805);

                if (value > 0)
                    AddBackground(130, y, Math.Max(1, 109 * value / 100), 11, 0x806);

                AddHtml(248, y - 3, 40, 20, Text(value.ToString()), false, false);

                y += 22;
            }

            // Notes
            y += 10;
            AddHtml(20, y, 270, 20, Text("Notes", "#FFD080"), false, false);
            y += 22;

            string notes = p.Notes.Count > 0 ? String.Join("<BR>", p.Notes) : "None";
            AddHtml(20, y, 270, Height - 70 - y, Text(notes, "#C0C0C0"), false, true);

            // Watch and Session scores
            int x = 305;
            int w = Width - x - 20;

            y = 92;
            y = AddScore("Watch", p.Watch, Ratings.ScoutWatchThreshold, p.WatchParts, x, y, w);
            y += 12;
            AddScore("Session", p.Session, Ratings.SessionThreshold, p.SessionParts, x, y, w);
        }

        private int AddScore(string title, double? score, int threshold, System.Collections.Generic.List<ScoreComponent> parts, int x, int y, int w)
        {
            AddHtml(x, y, w, 20, Text(String.Format("{0}: <BIG>{1}</BIG>", title, Score(score)), ScoreColor(score, threshold)), false, false);
            y += 24;

            foreach (ScoreComponent part in parts)
            {
                string value = part.Value.HasValue ? String.Format("{0:F0}%", part.Value.Value * 100) : "n/a";
                string color = part.Value.HasValue ? ScoreColor(part.Value * 100, 60) : "#A0A0A0";

                AddHtml(x, y, w, 18, Text(String.Format("{0} ({1}): {2}", part.Name, part.Weight, Text(value, color))), false, false);
                AddHtml(x + 15, y + 16, w - 15, 18, Text(part.Detail, "#A0A0A0"), false, false);

                y += 34;
            }

            return y;
        }

        private void AddDaily(BotProfile p)
        {
            int[] cols = { 20, 140, 240, 350, 470, 580 };
            string[] heads = { "Day (UTC)", "Online", "Outside towns", "Encounters", "Idle encounters", "Meaningful actions" };

            for (int i = 0; i < cols.Length; i++)
                AddHtml(cols[i], 92, 130, 20, Text(heads[i], "#FFD080"), false, false);

            int y = 118;

            foreach (DayStats d in p.Days.AsEnumerable().Reverse())
            {
                string idle = d.Encounters > 0 ? String.Format("{0} ({1:F0}%)", d.IdleEncounters, 100.0 * d.IdleEncounters / d.Encounters) : "-";

                AddHtml(cols[0], y, 120, 20, Text(d.Day.ToString("ddd yyyy-MM-dd")), false, false);
                AddHtml(cols[1], y, 100, 20, Text(String.Format("{0:F1}h", d.OnlineHours)), false, false);
                AddHtml(cols[2], y, 100, 20, Text(String.Format("{0:F1}h", d.OutdoorHours)), false, false);
                AddHtml(cols[3], y, 100, 20, Text(d.Encounters.ToString()), false, false);
                AddHtml(cols[4], y, 100, 20, Text(idle), false, false);
                AddHtml(cols[5], y, 120, 20, Text(d.MeaningfulActions.ToString()), false, false);

                y += 24;
            }
        }

        private void AddSessions(CharacterRecord rec)
        {
            var sessions = BotWatch.GetSessions(rec.Serial).OrderByDescending(s => s.Start).Take(16).ToList();

            int[] cols = { 20, 125, 185, 300, 450, 610, 680 };
            string[] heads = { "Start (UTC)", "Length", "Address", "Logged in at", "Logged out at", "Actions", "" };

            for (int i = 0; i < cols.Length; i++)
                AddHtml(cols[i], 92, 150, 20, Text(heads[i], "#FFD080"), false, false);

            int y = 116;

            foreach (SessionRecord s in sessions)
            {
                bool parked = s.IsParked(BotWatch.ReactionMoveTiles);

                AddHtml(cols[0], y, 105, 20, Text(s.Start.ToString("MM-dd HH:mm")), false, false);
                AddHtml(cols[1], y, 60, 20, Text(String.Format("{0:F0} min", s.Duration.TotalMinutes)), false, false);
                AddHtml(cols[2], y, 115, 20, Text(s.Address), false, false);
                AddHtml(cols[3], y, 150, 20, Text(Place(s.StartMap, s.StartLocation, s.StartInTown)), false, false);
                AddHtml(cols[4], y, 160, 20, Text(s.Open ? "online now" : Place(s.EndMap, s.EndLocation, s.EndInTown)), false, false);
                AddHtml(cols[5], y, 70, 20, Text(s.MeaningfulActions.ToString()), false, false);

                if (parked)
                    AddHtml(cols[6], y, 70, 20, Text("parked", "#FF5050"), false, false);

                y += 24;
            }

            if (sessions.Count == 0)
                AddHtml(20, y, 300, 20, Text("No sessions on record."), false, false);
        }

        private void AddButtons(Mobile staff, CharacterRecord rec)
        {
            int y = Height - 40;

            AddButtonLabel(15, y, ButtonOverview, "Overview", m_View == View.Overview);
            AddButtonLabel(120, y, ButtonDaily, "Last 7 days", m_View == View.Daily);
            AddButtonLabel(235, y, ButtonSessions, "Sessions", m_View == View.Sessions);
            AddButtonLabel(340, y, ButtonNetwork, "Network", false);
            AddButtonLabel(445, y, ButtonRefresh, "Refresh", false);

            if (staff.AccessLevel >= AccessLevel.GameMaster && BotWatch.IsOnline(rec.Serial))
                AddButtonLabel(545, y, ButtonGoTo, "Go to", false);

            if (m_FromList)
                AddButtonLabel(640, y, ButtonList, "List", false);
        }

        private void AddButtonLabel(int x, int y, int id, string label, bool selected)
        {
            AddButton(x, y, 4005, 4007, id, GumpButtonType.Reply, 0);
            AddHtml(x + 35, y + 2, 75, 20, Text(label, selected ? "#FFD080" : "#FFFFFF"), false, false);
        }

        public override void OnResponse(NetState sender, RelayInfo info)
        {
            Mobile from = sender.Mobile;

            if (from == null || from.AccessLevel < AccessLevel.Counselor)
                return;

            switch (info.ButtonID)
            {
                case ButtonOverview:
                    from.SendGump(new ProfileGump(from, m_Serial, View.Overview, m_FromList));
                    break;
                case ButtonDaily:
                    from.SendGump(new ProfileGump(from, m_Serial, View.Daily, m_FromList));
                    break;
                case ButtonSessions:
                    from.SendGump(new ProfileGump(from, m_Serial, View.Sessions, m_FromList));
                    break;
                case ButtonRefresh:
                    from.SendGump(new ProfileGump(from, m_Serial, m_View, m_FromList));
                    break;
                case ButtonGoTo:
                    {
                        Mobile m = World.FindMobile(m_Serial);

                        if (m != null && from.AccessLevel >= AccessLevel.GameMaster && m.Map != null && m.Map != Map.Internal)
                            from.MoveToWorld(m.Location, m.Map);

                        from.SendGump(new ProfileGump(from, m_Serial, m_View, m_FromList));
                        break;
                    }
                case ButtonList:
                    from.SendGump(new ProfilesGump(from, ProfilesGump.SortBy.Watch, 0));
                    break;
                case ButtonNetwork:
                    {
                        if (BotWatch.Characters.TryGetValue(m_Serial, out CharacterRecord rec) && rec.Account != null)
                            from.SendGump(new NetworkGump(from, rec.Account, 0));

                        break;
                    }
            }
        }
    }
}
