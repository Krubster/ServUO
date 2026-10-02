using Server.Accounting;
using Server.Commands;
using Server.Gumps;
using Server.Mobiles;
using Server.Network;
using Server.Regions;
using System;

namespace Server.Services.BotWatch
{
    /// <summary>
    /// Quick staff actions from the BotWatch gumps: go to a character, jail it and release it.
    /// Every action is written to the command log, the BotWatch log, the account comments and
    /// the character's open flag, if any.
    /// </summary>
    public static class StaffActions
    {
        public static AccessLevel GoToAccess = AccessLevel.Counselor;
        public static AccessLevel JailAccess = AccessLevel.GameMaster;

        public static Map JailMap { get; private set; }
        public static Point3D JailLocation { get; private set; }
        public static Map ReleaseMap { get; private set; }
        public static Point3D ReleaseLocation { get; private set; }

        public static void Configure()
        {
            ParseLocation(Config.Get("BotWatch.JailLocation", "Felucca 5275 1163 0"), Map.Felucca, new Point3D(5275, 1163, 0), out Map jm, out Point3D jl);
            JailMap = jm;
            JailLocation = jl;

            ParseLocation(Config.Get("BotWatch.ReleaseLocation", "Felucca 1434 1699 0"), Map.Felucca, new Point3D(1434, 1699, 0), out Map rm, out Point3D rl);
            ReleaseMap = rm;
            ReleaseLocation = rl;
        }

        private static void ParseLocation(string value, Map defMap, Point3D defLoc, out Map map, out Point3D loc)
        {
            map = defMap;
            loc = defLoc;

            string[] parts = (value ?? String.Empty).Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length == 4 && Int32.TryParse(parts[1], out int x) && Int32.TryParse(parts[2], out int y) && Int32.TryParse(parts[3], out int z))
            {
                Map m = Map.Parse(parts[0]);

                if (m != null && m != Map.Internal)
                {
                    map = m;
                    loc = new Point3D(x, y, z);
                    return;
                }
            }

            Utility.WriteConsoleColor(ConsoleColor.Yellow, "BotWatch: invalid location '{0}', using {1} {2}", value, defMap, defLoc);
        }

        /// <summary>Where the character is, or where it will appear when it logs in.</summary>
        private static bool GetPosition(Mobile m, out Map map, out Point3D loc)
        {
            if (m.Map != null && m.Map != Map.Internal)
            {
                map = m.Map;
                loc = m.Location;
                return true;
            }

            map = m.LogoutMap;
            loc = m.LogoutLocation;

            return map != null && map != Map.Internal;
        }

        public static bool IsJailed(Mobile m)
        {
            if (m == null || !GetPosition(m, out Map map, out Point3D loc))
                return false;

            return Region.Find(loc, map).IsPartOf<Jail>();
        }

        public static void GoTo(Mobile staff, Serial serial)
        {
            Mobile m = World.FindMobile(serial);

            if (staff.AccessLevel < GoToAccess)
                return;

            if (m == null || m.Deleted)
            {
                staff.SendMessage("That character no longer exists.");
                return;
            }

            if (!GetPosition(m, out Map map, out Point3D loc))
            {
                staff.SendMessage("{0} has no known location.", m.RawName);
                return;
            }

            staff.MoveToWorld(loc, map);

            if (m.Map == Map.Internal)
                staff.SendMessage("{0} is logged out; you are where they will log back in.", m.RawName);

            CommandLogging.WriteLine(staff, "{0} {1} went to {2} through BotWatch", staff.AccessLevel, CommandLogging.Format(staff), CommandLogging.Format(m));
        }

        public static void Jail(Mobile staff, Serial serial, string reason)
        {
            Move(staff, serial, JailMap, JailLocation, "Jailed", reason);
        }

        public static void Release(Mobile staff, Serial serial)
        {
            Move(staff, serial, ReleaseMap, ReleaseLocation, "Released from jail", null);
        }

        private static void Move(Mobile staff, Serial serial, Map map, Point3D loc, string action, string reason)
        {
            Mobile m = World.FindMobile(serial);

            if (staff.AccessLevel < JailAccess)
                return;

            if (m == null || m.Deleted || !m.Player)
            {
                staff.SendMessage("That character no longer exists.");
                return;
            }

            if (m.AccessLevel >= staff.AccessLevel && m != staff)
            {
                staff.SendMessage("You cannot do that to {0}.", m.RawName);
                return;
            }

            bool online = m.Map != null && m.Map != Map.Internal;

            if (online)
            {
                m.MoveToWorld(loc, map);
                m.SendMessage(0x22, action == "Jailed" ? "You have been jailed by staff." : "You have been released from jail.");
            }
            else
            {
                // Logged out: they will appear here when they log back in.
                m.LogoutMap = map;
                m.LogoutLocation = loc;
            }

            string text = String.Format("{0} by {1}{2}{3}", action, staff.RawName,
                online ? String.Empty : " (while logged out)",
                String.IsNullOrWhiteSpace(reason) ? String.Empty : ": " + reason.Trim());

            if (m.Account is Account a)
                a.Comments.Add(new AccountComment(staff.RawName, String.Format("BotWatch: {0} {1}", m.RawName, text)));

            FlagRecord flag = Flags.LatestFor(serial);

            if (flag != null && flag.Status == FlagStatus.Open)
            {
                string note = String.Format("{0:yyyy-MM-dd} {1}", DateTime.UtcNow, text);
                flag.StaffNote = String.IsNullOrEmpty(flag.StaffNote) ? note : flag.StaffNote + " | " + note;
                flag.Updated = DateTime.UtcNow;
            }

            Alerts.Raise(AlertKind.StaffAction, m.Account?.Username, serial, m.RawName,
                String.Format("{0} (account {1}): {2}", m.RawName, m.Account?.Username, text), true);

            CommandLogging.WriteLine(staff, "{0} {1} BotWatch: {2} {3}", staff.AccessLevel, CommandLogging.Format(staff), action, CommandLogging.Format(m));

            staff.SendMessage("{0}: {1}.", m.RawName, text);
        }
    }

    /// <summary>Confirmation before jailing, with an optional reason.</summary>
    public class JailConfirmGump : Gump
    {
        private const int ButtonJail = 1;
        private const int ReasonEntry = 0;

        private readonly Serial m_Serial;
        private readonly Action<Mobile> m_Return;

        public JailConfirmGump(Serial serial, string name, Action<Mobile> onReturn)
            : base(120, 120)
        {
            m_Serial = serial;
            m_Return = onReturn;

            AddPage(0);
            AddBackground(0, 0, 420, 170, 9270);
            AddAlphaRegion(10, 10, 400, 150);

            AddHtml(20, 20, 380, 20, ProfileGump.Text(String.Format("Jail {0}?", ProfileGump.Escape(name)), "#FFD080"), false, false);
            AddHtml(20, 45, 380, 20, ProfileGump.Text("Reason (optional, saved in the account comments):", "#C0C0C0"), false, false);

            AddImageTiled(20, 70, 380, 22, 2624);
            AddTextEntry(24, 72, 372, 20, 0x481, ReasonEntry, String.Empty, 150);

            AddButton(20, 120, 4005, 4007, ButtonJail, GumpButtonType.Reply, 0);
            AddHtml(55, 122, 100, 20, ProfileGump.Text("Jail"), false, false);

            AddButton(250, 120, 4017, 4019, 0, GumpButtonType.Reply, 0);
            AddHtml(285, 122, 100, 20, ProfileGump.Text("Cancel"), false, false);
        }

        public override void OnResponse(NetState sender, RelayInfo info)
        {
            Mobile from = sender.Mobile;

            if (from == null || from.AccessLevel < StaffActions.JailAccess)
                return;

            if (info.ButtonID == ButtonJail)
                StaffActions.Jail(from, m_Serial, info.GetTextEntry(ReasonEntry)?.Text);

            m_Return?.Invoke(from);
        }
    }
}
