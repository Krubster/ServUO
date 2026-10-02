using Server.Accounting;
using Server.Commands;
using Server.Engines.PartySystem;
using Server.Guilds;
using Server.Items;
using Server.Mobiles;
using Server.Network;
using Server.Regions;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Server.Services.BotWatch
{
    /// <summary>
    /// Records what player characters do, whom they see and when they play, so staff can
    /// tell real players from scout bots. Observe-only. Configured in Config/BotWatch.cfg.
    /// </summary>
    public static class BotWatch
    {
        public static readonly string SavePath = Path.Combine("Saves", "BotWatch", "BotWatch.bin");

        public static bool Enabled { get; private set; }
        public static TimeSpan ScanInterval { get; private set; }
        public static string[] TownRegions { get; private set; }
        public static TimeSpan EncounterCooldown { get; private set; }
        public static TimeSpan IdleWindow { get; private set; }
        public static TimeSpan ReactionWindow { get; private set; }
        public static int ReactionMoveTiles { get; private set; }
        public static int TeleporterRange { get; private set; }
        public static TimeSpan TeleporterGrace { get; private set; }
        public static TimeSpan ActivityRetention { get; private set; }
        public static TimeSpan SessionRetention { get; private set; }

        public static readonly Dictionary<Serial, CharacterRecord> Characters = new Dictionary<Serial, CharacterRecord>();
        public static readonly List<SessionRecord> Sessions = new List<SessionRecord>();
        public static readonly Dictionary<string, AddressRecord> Addresses = new Dictionary<string, AddressRecord>();

        private static readonly Dictionary<Mobile, LiveState> m_Live = new Dictionary<Mobile, LiveState>();
        private static DateTime m_LastScan = DateTime.UtcNow;

        public static int LiveCount => m_Live.Count;
        public static int PendingEncounterCount => m_Live.Values.Sum(l => l.Pending.Count);

        /// <summary>
        /// Runtime state of a character that is online, or offline with encounters still
        /// waiting for their idle window to pass.
        /// </summary>
        private class LiveState
        {
            public CharacterRecord Record;
            public SessionRecord Session;
            public bool Online;

            public readonly List<DateTime> Meaningful = new List<DateTime>();
            public DateTime LastMeaningful = DateTime.MinValue;

            public HashSet<Mobile> InView = new HashSet<Mobile>();
            public readonly Dictionary<Serial, DateTime> LastEncounter = new Dictionary<Serial, DateTime>();
            public readonly List<PendingEncounter> Pending = new List<PendingEncounter>();

            public DateTime TeleporterSince = DateTime.MinValue;

            public long CellsHour;
            public readonly HashSet<int> CellsSeen = new HashSet<int>();
        }

        private class PendingEncounter
        {
            public DateTime Time;
            public Serial Other;
            public Map Map;
            public Point3D Location;
            public bool NearTeleporter;
            public bool Reacted;
        }

        public static void Configure()
        {
            Enabled = Config.Get("BotWatch.Enabled", true);
            ScanInterval = Config.Get("BotWatch.ScanInterval", TimeSpan.FromSeconds(2));
            TownRegions = Config.Get("BotWatch.TownRegions", String.Empty)
                .Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => s.Length > 0)
                .ToArray();
            EncounterCooldown = Config.Get("BotWatch.EncounterCooldown", TimeSpan.FromMinutes(10));
            IdleWindow = Config.Get("BotWatch.IdleWindow", TimeSpan.FromMinutes(5));
            ReactionWindow = Config.Get("BotWatch.ReactionWindow", TimeSpan.FromSeconds(30));
            ReactionMoveTiles = Config.Get("BotWatch.ReactionMoveTiles", 8);
            TeleporterRange = Config.Get("BotWatch.TeleporterRange", 12);
            TeleporterGrace = Config.Get("BotWatch.TeleporterGrace", TimeSpan.FromMinutes(2));
            ActivityRetention = Config.Get("BotWatch.ActivityRetention", TimeSpan.FromDays(30));
            SessionRetention = Config.Get("BotWatch.SessionRetention", TimeSpan.FromDays(90));

            EventSink.WorldSave += e => Save();
            EventSink.WorldLoad += Load;
        }

        public static void Initialize()
        {
            if (!Enabled)
                return;

            EventSink.Login += OnLogin;
            EventSink.Disconnected += OnDisconnected;

            EventSink.AggressiveAction += OnAggressiveAction;
            EventSink.CreatureDeath += e => OnKill(e.Killer, e.Creature);
            EventSink.PlayerDeath += OnPlayerDeath;
            EventSink.CraftSuccess += e => Record(e.Crafter, Activity.Craft);
            EventSink.ResourceHarvestSuccess += e => Record(e.Harvester, Activity.Gather);
            EventSink.ValidVendorPurchase += e => Record(e.Mobile, Activity.Trade);
            EventSink.ValidVendorSell += e => Record(e.Mobile, Activity.Trade);
            EventSink.SkillGain += e => Record(e.From, Activity.SkillGain);
            EventSink.SkillCheck += e => Record(e.From, Activity.SkillUse);
            EventSink.CastSpellRequest += e => Record(e.Mobile, Activity.Spell);
            EventSink.OnItemUse += e => Record(e.From, Activity.ItemUse);
            EventSink.Speech += OnSpeech;

            TeleporterIndex.Initialize();

            Timer.DelayCall(ScanInterval, ScanInterval, Scan);

            BotWatchCommands.Initialize();
        }

        #region Helpers
        public static bool IsTracked(Mobile m)
        {
            return m is PlayerMobile && !m.Deleted && m.AccessLevel == AccessLevel.Player;
        }

        /// <summary>
        /// Activities that count against idleness. Self-heals, self-buffs, skill use, spells,
        /// item use and speech are left out because a bot can repeat them at its post.
        /// </summary>
        private static readonly HashSet<Activity> m_Meaningful = new HashSet<Activity>
        {
            Activity.PvMAttack, Activity.PvMKill, Activity.PvPAttack, Activity.PvPKill,
            Activity.Craft, Activity.Gather, Activity.Trade,
            Activity.HealOther, Activity.BuffOther, Activity.PlayerTrade,
            Activity.LootPvM, Activity.LootPvP
        };

        public static bool IsMeaningful(Activity a)
        {
            return m_Meaningful.Contains(a);
        }

        public static bool IsTown(Region r)
        {
            if (TownRegions.Length > 0)
                return TownRegions.Any(r.IsPartOf);

            GuardedRegion g = (GuardedRegion)r.GetRegion(typeof(GuardedRegion));

            return g != null && !g.IsDisabled();
        }

        /// <summary>Towns and houses: nothing that happens there counts as watching.</summary>
        public static bool IsSafe(Mobile m)
        {
            return m.Map == null || m.Map == Map.Internal || IsTown(m.Region) || m.Region.IsPartOf<HouseRegion>();
        }

        public static CharacterRecord GetRecord(Mobile m)
        {
            if (!Characters.TryGetValue(m.Serial, out CharacterRecord rec))
            {
                Characters[m.Serial] = rec = new CharacterRecord { Serial = m.Serial };
            }

            rec.Name = m.RawName;
            rec.Account = m.Account != null ? m.Account.Username : rec.Account;
            rec.CharacterCreated = m.CreationTime;

            if (m.Account is Account a)
                rec.AccountCreated = a.Created;

            return rec;
        }

        public static CharacterRecord FindRecord(string name)
        {
            return Characters.Values
                .Where(r => String.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(r => r.Hours.Count > 0 ? r.Hours.Keys.Last() : 0)
                .FirstOrDefault();
        }

        private static LiveState GetLive(Mobile m)
        {
            if (!m_Live.TryGetValue(m, out LiveState live))
            {
                m_Live[m] = live = new LiveState { Record = GetRecord(m) };
            }

            return live;
        }

        /// <summary>Pets and summons act on behalf of their master.</summary>
        private static Mobile Owner(Mobile m)
        {
            if (m is BaseCreature bc)
                return bc.GetMaster();

            return m;
        }
        #endregion

        #region Activity events
        public static void Record(Mobile m, Activity activity)
        {
            if (!IsTracked(m))
                return;

            DateTime now = DateTime.UtcNow;
            LiveState live = GetLive(m);

            live.Record.GetHour(now).Counts[(int)activity]++;

            if (IsMeaningful(activity))
            {
                live.Meaningful.Add(now);
                live.LastMeaningful = now;
            }
        }

        private static void OnAggressiveAction(AggressiveActionEventArgs e)
        {
            Mobile aggressor = Owner(e.Aggressor);
            Mobile aggressed = e.Aggressed;

            if (!IsTracked(aggressor) || aggressed == null || aggressor == aggressed)
                return;

            Mobile victimOwner = Owner(aggressed);

            if (IsTracked(victimOwner))
            {
                Record(aggressor, Activity.PvPAttack);
                MarkReaction(aggressor, victimOwner);
            }
            else if (aggressed is BaseCreature)
            {
                Record(aggressor, Activity.PvMAttack);
            }
        }

        private static void OnKill(Mobile killer, Mobile victim)
        {
            Mobile owner = Owner(killer);

            if (IsTracked(owner) && victim is BaseCreature)
                Record(owner, Activity.PvMKill);
        }

        private static void OnPlayerDeath(PlayerDeathEventArgs e)
        {
            Record(e.Mobile, Activity.PvPDeath);

            Mobile killer = Owner(e.Killer);

            if (IsTracked(killer) && killer != e.Mobile)
                Record(killer, Activity.PvPKill);
        }

        private static void OnSpeech(SpeechEventArgs e)
        {
            if (e.Speech != null && e.Speech.StartsWith(CommandSystem.Prefix))
                return;

            Record(e.Mobile, Activity.Speech);
        }

        /// <summary>
        /// Doing something to a player shortly after encountering them is a reaction to
        /// that encounter.
        /// </summary>
        private static void MarkReaction(Mobile m, Mobile other)
        {
            if (!IsTracked(m) || other == null || !m_Live.TryGetValue(m, out LiveState live))
                return;

            DateTime now = DateTime.UtcNow;

            foreach (PendingEncounter p in live.Pending)
            {
                if (p.Other == other.Serial && now - p.Time <= ReactionWindow)
                    p.Reacted = true;
            }
        }
        #endregion

        #region Handlers for hooks without an EventSink event
        // ServUO raises no event for these. Call the handlers from the places noted below.

        /// <summary>
        /// A heal landed. Call where hits are restored with a known source, e.g.
        /// Mobile.Heal(int amount, Mobile from, bool message) in Server/Mobile.cs, which
        /// bandages, potions and SpellHelper.Heal all go through.
        /// </summary>
        public static void OnHeal(Mobile healer, Mobile target, int amount)
        {
            healer = Owner(healer);

            if (!IsTracked(healer) || target == null || amount <= 0)
                return;

            if (healer == target)
            {
                Record(healer, Activity.HealSelf);
                return;
            }

            Record(healer, Activity.HealOther);
            MarkReaction(healer, Owner(target));
        }

        /// <summary>
        /// A beneficial effect (buff, cure, protection...) was applied. Call from
        /// BuffInfo.AddBuff in Scripts/Misc/BuffIcons.cs or from the individual spells.
        /// </summary>
        public static void OnBuff(Mobile caster, Mobile target)
        {
            caster = Owner(caster);

            if (!IsTracked(caster) || target == null)
                return;

            if (caster == target)
            {
                Record(caster, Activity.BuffSelf);
                return;
            }

            Record(caster, Activity.BuffOther);
            MarkReaction(caster, Owner(target));
        }

        /// <summary>
        /// A secure trade between two players completed. Call from SecureTrade.Update in
        /// Server/SecureTrade.cs once both sides accepted and items were exchanged.
        /// </summary>
        public static void OnPlayerTrade(Mobile a, Mobile b)
        {
            if (a == null || b == null || a == b)
                return;

            Record(a, Activity.PlayerTrade);
            Record(b, Activity.PlayerTrade);

            MarkReaction(a, b);
            MarkReaction(b, a);
        }

        /// <summary>
        /// An item was taken from a corpse. Call from Corpse.OnItemLifted in
        /// Scripts/Items/Corpses/Corpse.cs. Looting your own corpse is not counted.
        /// </summary>
        public static void OnCorpseLoot(Mobile looter, Corpse corpse, Item item)
        {
            if (!IsTracked(looter) || corpse == null || corpse.Owner == looter)
                return;

            Record(looter, corpse.Owner is PlayerMobile ? Activity.LootPvP : Activity.LootPvM);
        }
        #endregion

        #region Sessions
        private static void OnLogin(LoginEventArgs e)
        {
            Mobile m = e.Mobile;

            if (!IsTracked(m))
                return;

            DateTime now = DateTime.UtcNow;
            LiveState live = GetLive(m);

            if (live.Session != null && live.Session.Open)
                CloseSession(m, live, now);

            string address = m.NetState?.Address?.ToString() ?? "?";

            live.Online = true;
            live.InView.Clear();
            live.Session = new SessionRecord
            {
                Account = live.Record.Account,
                Character = m.Serial,
                CharacterName = m.RawName,
                Address = address,
                Start = now,
                End = DateTime.MinValue,
                LastSeen = now,
                StartMap = m.Map,
                StartLocation = m.Location,
                StartInTown = IsSafe(m)
            };

            Sessions.Add(live.Session);

            string key = live.Record.Account + "|" + address;

            if (!Addresses.TryGetValue(key, out AddressRecord ar))
            {
                Addresses[key] = ar = new AddressRecord { Account = live.Record.Account, Address = address, FirstSeen = now };
            }

            ar.LastSeen = now;
            ar.Sessions++;
        }

        private static void OnDisconnected(DisconnectedEventArgs e)
        {
            Mobile m = e.Mobile;

            if (m == null || !m_Live.TryGetValue(m, out LiveState live))
                return;

            CloseSession(m, live, DateTime.UtcNow);
        }

        private static void CloseSession(Mobile m, LiveState live, DateTime now)
        {
            live.Online = false;
            live.InView.Clear();
            live.TeleporterSince = DateTime.MinValue;

            SessionRecord s = live.Session;

            if (s != null && s.Open)
            {
                s.End = now;
                s.LastSeen = now;
                s.EndMap = m.Map;
                s.EndLocation = m.Location;
                s.EndInTown = IsSafe(m);
            }

            CharacterRecord rec = live.Record;

            rec.SkillsTotal = m.SkillsTotal;
            rec.BackpackItems = m.Backpack != null ? m.Backpack.TotalItems : 0;

            BankBox bank = m.FindBankNoCreate();
            rec.BankItems = bank != null ? bank.TotalItems : 0;
        }

        public static IEnumerable<SessionRecord> GetSessions(Serial character)
        {
            return Sessions.Where(s => s.Character == character);
        }
        #endregion

        #region Scan
        private static void Scan()
        {
            DateTime now = DateTime.UtcNow;
            int seconds = (int)Math.Round(Math.Min((now - m_LastScan).TotalSeconds, ScanInterval.TotalSeconds * 5));
            m_LastScan = now;

            foreach (KeyValuePair<Mobile, LiveState> kv in m_Live.ToList())
            {
                Mobile m = kv.Key;
                LiveState live = kv.Value;

                if (live.Online && m.NetState != null && !m.Deleted && m.Map != null && m.Map != Map.Internal)
                    ScanOnline(m, live, now, seconds);

                ResolveEncounters(m, live, now);

                live.Meaningful.RemoveAll(t => now - t > IdleWindow + IdleWindow);

                foreach (Serial s in live.LastEncounter.Where(p => now - p.Value > EncounterCooldown).Select(p => p.Key).ToList())
                    live.LastEncounter.Remove(s);

                if (!live.Online && live.Pending.Count == 0)
                    m_Live.Remove(m);
            }
        }

        private static void ScanOnline(Mobile m, LiveState live, DateTime now, int seconds)
        {
            HourBucket bucket = live.Record.GetHour(now);
            bool outdoor = !IsSafe(m);

            if (live.Session != null)
                live.Session.LastSeen = now;

            bucket.OnlineSeconds += seconds;

            if (outdoor)
                bucket.OutdoorSeconds += seconds;

            // Exploration: distinct 16x16 areas visited this hour.
            long hour = CharacterRecord.HourOf(now);

            if (live.CellsHour != hour)
            {
                live.CellsHour = hour;
                live.CellsSeen.Clear();
            }

            if (live.CellsSeen.Add((m.Map.MapID << 24) | ((m.X >> 4) << 12) | (m.Y >> 4)))
                bucket.Cells++;

            if (!m.Alive && outdoor)
                bucket.GhostOutdoorSeconds += seconds;

            // Idle presence near a teleporter. Walking around it does not reset this, only
            // leaving its range or doing something meaningful does.
            if (outdoor && TeleporterIndex.IsNear(m.Map, m.Location, TeleporterRange))
            {
                if (live.TeleporterSince == DateTime.MinValue)
                    live.TeleporterSince = now;

                if (now - live.TeleporterSince >= TeleporterGrace && now - live.LastMeaningful >= TeleporterGrace)
                    bucket.TeleporterIdleSeconds += seconds;
            }
            else
            {
                live.TeleporterSince = DateTime.MinValue;
            }

            ScanEncounters(m, live, now, outdoor);
        }

        private static void ScanEncounters(Mobile m, LiveState live, DateTime now, bool outdoor)
        {
            HashSet<Mobile> inView = new HashSet<Mobile>();

            if (outdoor)
            {
                IPooledEnumerable<Mobile> eable = m.Map.GetMobilesInRange(m.Location, Core.GlobalUpdateRange);

                foreach (Mobile o in eable)
                {
                    if (IsEncounterCandidate(m, o))
                        inView.Add(o);
                }

                eable.Free();
            }

            foreach (Mobile o in inView)
            {
                if (live.InView.Contains(o))
                    continue;

                if (live.LastEncounter.TryGetValue(o.Serial, out DateTime last) && now - last < EncounterCooldown)
                    continue;

                live.LastEncounter[o.Serial] = now;
                live.Pending.Add(new PendingEncounter
                {
                    Time = now,
                    Other = o.Serial,
                    Map = m.Map,
                    Location = m.Location,
                    NearTeleporter = TeleporterIndex.IsNear(m.Map, m.Location, TeleporterRange)
                });
            }

            live.InView = inView;
        }

        /// <summary>
        /// Another online player this character can see, outside towns and houses, and not
        /// a guildmate, ally, party member or a character from the same account or address.
        /// </summary>
        private static bool IsEncounterCandidate(Mobile m, Mobile o)
        {
            if (o == m || !IsTracked(o) || o.NetState == null || !m.CanSee(o) || IsSafe(o))
                return false;

            if (o.Account == m.Account)
                return false;

            if (m.NetState != null && m.NetState.Address != null && m.NetState.Address.Equals(o.NetState.Address))
                return false;

            Party party = Party.Get(m);

            if (party != null && party == Party.Get(o))
                return false;

            if (m.Guild is Guild g && o.Guild is Guild og && (g == og || g.IsAlly(og)))
                return false;

            return true;
        }

        private static void ResolveEncounters(Mobile m, LiveState live, DateTime now)
        {
            for (int i = live.Pending.Count - 1; i >= 0; i--)
            {
                PendingEncounter p = live.Pending[i];

                // Leaving the spot (walking away, recalling, gating) right after seeing
                // someone counts as reacting to them.
                if (!p.Reacted && live.Online && now - p.Time <= ReactionWindow &&
                    (m.Map != p.Map || !Utility.InRange(m.Location, p.Location, ReactionMoveTiles)))
                {
                    p.Reacted = true;
                }

                if (now - p.Time < IdleWindow)
                    continue;

                bool idle = !live.Meaningful.Any(t => t >= p.Time - IdleWindow && t <= p.Time + IdleWindow);
                HourBucket bucket = live.Record.GetHour(p.Time);

                bucket.Encounters++;

                if (idle)
                    bucket.IdleEncounters++;

                if (p.Reacted)
                    bucket.ReactedEncounters++;

                if (p.NearTeleporter)
                {
                    bucket.TeleporterEncounters++;

                    if (idle)
                        bucket.TeleporterIdleEncounters++;
                }

                live.Pending.RemoveAt(i);
            }
        }
        #endregion

        #region Persistence
        private static void Save()
        {
            DateTime now = DateTime.UtcNow;

            foreach (CharacterRecord rec in Characters.Values)
                rec.Prune(now - ActivityRetention);

            foreach (Serial s in Characters.Where(kv => kv.Value.Hours.Count == 0 && World.FindMobile(kv.Key) == null).Select(kv => kv.Key).ToList())
                Characters.Remove(s);

            Sessions.RemoveAll(s => !s.Open && now - s.End > SessionRetention);

            foreach (string key in Addresses.Where(kv => now - kv.Value.LastSeen > SessionRetention).Select(kv => kv.Key).ToList())
                Addresses.Remove(key);

            Persistence.Serialize(SavePath, writer =>
            {
                writer.Write(0); // version

                writer.Write(Characters.Count);

                foreach (CharacterRecord rec in Characters.Values)
                    rec.Serialize(writer);

                writer.Write(Sessions.Count);

                foreach (SessionRecord s in Sessions)
                    s.Serialize(writer);

                writer.Write(Addresses.Count);

                foreach (AddressRecord a in Addresses.Values)
                    a.Serialize(writer);
            });
        }

        private static void Load()
        {
            Persistence.Deserialize(SavePath, reader =>
            {
                reader.ReadInt(); // version

                int count = reader.ReadInt();

                for (int i = 0; i < count; i++)
                {
                    CharacterRecord rec = new CharacterRecord();
                    rec.Deserialize(reader);
                    Characters[rec.Serial] = rec;
                }

                count = reader.ReadInt();

                for (int i = 0; i < count; i++)
                {
                    SessionRecord s = new SessionRecord();
                    s.Deserialize(reader);

                    // Sessions still open were cut by a crash or shutdown.
                    if (s.Open)
                        s.End = s.LastSeen;

                    Sessions.Add(s);
                }

                count = reader.ReadInt();

                for (int i = 0; i < count; i++)
                {
                    AddressRecord a = new AddressRecord();
                    a.Deserialize(reader);
                    Addresses[a.Account + "|" + a.Address] = a;
                }
            });
        }
        #endregion
    }
}
