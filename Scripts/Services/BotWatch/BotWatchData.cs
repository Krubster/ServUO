using System;
using System.Collections.Generic;
using System.Linq;

namespace Server.Services.BotWatch
{
    /// <summary>
    /// Counted activity types. Values are saved by index, so new ones go at the end.
    /// See BotWatch.IsMeaningful for which ones count against idleness.
    /// </summary>
    public enum Activity
    {
        PvMAttack,
        PvMKill,
        PvPAttack,
        PvPKill,
        Craft,
        Gather,
        Trade,

        PvPDeath,
        SkillGain,
        SkillUse,
        Spell,
        ItemUse,
        Speech,

        HealSelf,
        HealOther,
        BuffSelf,
        BuffOther,
        PlayerTrade,
        LootPvM,
        LootPvP,

        PvMDeath,
        Whisper,
        Yell,
        Emote,
        GuildChat,
        TravelJump,
        TravelSpell,
        DungeonEnter,
        QuestComplete,
        BODTaken,
        BODTurnedIn,
        Tame,
        GuildJoin,
        PartyJoin,
        TargetPlayer,
        AssistOther,
        FastWalk,
        ItemObtained,
        Consume,
        InterfaceUse,
        HealthBarRequest,
        RevealedOther,
        RevealedByOther,
        AttackedByPlayer,
        AttackedNoResponse
    }

    public class HourBucket
    {
        public static readonly int ActivityCount = Enum.GetValues(typeof(Activity)).Length;

        public readonly int[] Counts = new int[ActivityCount];

        public int OnlineSeconds;
        public int OutdoorSeconds;
        public int GhostOutdoorSeconds;
        public int HiddenOutdoorSeconds;
        public int TeleporterIdleSeconds;
        public int Cells;

        public int Encounters;
        public int IdleEncounters;
        public int ReactedEncounters;
        public int TeleporterEncounters;
        public int TeleporterIdleEncounters;

        // Walking outside towns, in windows of 100 steps: how many distinct tiles each
        // window covered. Looping a fixed path keeps this low.
        public int Steps;
        public int StepWindows;
        public int StepWindowDistinct;

        public int DamageDealtPvM;
        public int DamageDealtPvP;
        public int DamageTaken;
        public int GoldGained;
        public int GoldLost;

        public int this[Activity a] => Counts[(int)a];

        public void Add(HourBucket b)
        {
            for (int i = 0; i < ActivityCount; i++)
                Counts[i] += b.Counts[i];

            OnlineSeconds += b.OnlineSeconds;
            OutdoorSeconds += b.OutdoorSeconds;
            GhostOutdoorSeconds += b.GhostOutdoorSeconds;
            HiddenOutdoorSeconds += b.HiddenOutdoorSeconds;
            TeleporterIdleSeconds += b.TeleporterIdleSeconds;
            Cells += b.Cells;
            Encounters += b.Encounters;
            IdleEncounters += b.IdleEncounters;
            ReactedEncounters += b.ReactedEncounters;
            TeleporterEncounters += b.TeleporterEncounters;
            TeleporterIdleEncounters += b.TeleporterIdleEncounters;
            Steps += b.Steps;
            StepWindows += b.StepWindows;
            StepWindowDistinct += b.StepWindowDistinct;
            DamageDealtPvM += b.DamageDealtPvM;
            DamageDealtPvP += b.DamageDealtPvP;
            DamageTaken += b.DamageTaken;
            GoldGained += b.GoldGained;
            GoldLost += b.GoldLost;
        }

        public void Serialize(GenericWriter writer)
        {
            writer.Write(1); // version

            writer.Write(ActivityCount);

            for (int i = 0; i < ActivityCount; i++)
                writer.Write(Counts[i]);

            writer.Write(OnlineSeconds);
            writer.Write(OutdoorSeconds);
            writer.Write(GhostOutdoorSeconds);
            writer.Write(TeleporterIdleSeconds);
            writer.Write(Cells);
            writer.Write(Encounters);
            writer.Write(IdleEncounters);
            writer.Write(ReactedEncounters);
            writer.Write(TeleporterEncounters);
            writer.Write(TeleporterIdleEncounters);

            // version 1
            writer.Write(HiddenOutdoorSeconds);
            writer.Write(Steps);
            writer.Write(StepWindows);
            writer.Write(StepWindowDistinct);
            writer.Write(DamageDealtPvM);
            writer.Write(DamageDealtPvP);
            writer.Write(DamageTaken);
            writer.Write(GoldGained);
            writer.Write(GoldLost);
        }

        public void Deserialize(GenericReader reader)
        {
            int version = reader.ReadInt();

            int count = reader.ReadInt();

            for (int i = 0; i < count; i++)
            {
                int v = reader.ReadInt();

                if (i < ActivityCount)
                    Counts[i] = v;
            }

            OnlineSeconds = reader.ReadInt();
            OutdoorSeconds = reader.ReadInt();
            GhostOutdoorSeconds = reader.ReadInt();
            TeleporterIdleSeconds = reader.ReadInt();
            Cells = reader.ReadInt();
            Encounters = reader.ReadInt();
            IdleEncounters = reader.ReadInt();
            ReactedEncounters = reader.ReadInt();
            TeleporterEncounters = reader.ReadInt();
            TeleporterIdleEncounters = reader.ReadInt();

            if (version >= 1)
            {
                HiddenOutdoorSeconds = reader.ReadInt();
                Steps = reader.ReadInt();
                StepWindows = reader.ReadInt();
                StepWindowDistinct = reader.ReadInt();
                DamageDealtPvM = reader.ReadInt();
                DamageDealtPvP = reader.ReadInt();
                DamageTaken = reader.ReadInt();
                GoldGained = reader.ReadInt();
                GoldLost = reader.ReadInt();
            }
        }
    }

    /// <summary>
    /// Everything BotWatch keeps about one character. Survives the character's deletion so
    /// throwaway scouts stay on record.
    /// </summary>
    public class CharacterRecord
    {
        public Serial Serial;
        public string Name;
        public string Account;
        public DateTime CharacterCreated;
        public DateTime AccountCreated;
        public DateTime Deleted = DateTime.MinValue;

        // Snapshot taken at the end of each session, for disposable-character signals.
        public int SkillsTotal;
        public int BackpackItems;
        public int BankItems;

        public readonly SortedDictionary<long, HourBucket> Hours = new SortedDictionary<long, HourBucket>();

        public bool IsDeleted => Deleted != DateTime.MinValue;

        public DateTime LastActive => Hours.Count > 0 ? new DateTime(Hours.Keys.Last() * TimeSpan.TicksPerHour, DateTimeKind.Utc) : DateTime.MinValue;

        public static long HourOf(DateTime utc)
        {
            return utc.Ticks / TimeSpan.TicksPerHour;
        }

        public HourBucket GetHour(DateTime utc)
        {
            long hour = HourOf(utc);

            if (!Hours.TryGetValue(hour, out HourBucket b))
                Hours[hour] = b = new HourBucket();

            return b;
        }

        public HourBucket Sum(DateTime fromUtc)
        {
            return Sum(fromUtc, DateTime.MaxValue);
        }

        public HourBucket Sum(DateTime fromUtc, DateTime toUtc)
        {
            long from = HourOf(fromUtc);
            long to = toUtc == DateTime.MaxValue ? Int64.MaxValue : HourOf(toUtc);
            HourBucket total = new HourBucket();

            foreach (var kv in Hours.Where(kv => kv.Key >= from && kv.Key < to))
                total.Add(kv.Value);

            return total;
        }

        public void Prune(DateTime cutoffUtc)
        {
            long cutoff = HourOf(cutoffUtc);

            foreach (long hour in Hours.Keys.Where(h => h < cutoff).ToList())
                Hours.Remove(hour);
        }

        public void Serialize(GenericWriter writer)
        {
            writer.Write(1); // version

            writer.Write(Serial.Value);
            writer.Write(Name);
            writer.Write(Account);
            writer.Write(CharacterCreated);
            writer.Write(AccountCreated);
            writer.Write(SkillsTotal);
            writer.Write(BackpackItems);
            writer.Write(BankItems);

            writer.Write(Hours.Count);

            foreach (var kv in Hours)
            {
                writer.Write(kv.Key);
                kv.Value.Serialize(writer);
            }

            // version 1
            writer.Write(Deleted);
        }

        public void Deserialize(GenericReader reader)
        {
            int version = reader.ReadInt();

            Serial = (Serial)reader.ReadInt();
            Name = reader.ReadString();
            Account = reader.ReadString();
            CharacterCreated = reader.ReadDateTime();
            AccountCreated = reader.ReadDateTime();
            SkillsTotal = reader.ReadInt();
            BackpackItems = reader.ReadInt();
            BankItems = reader.ReadInt();

            int count = reader.ReadInt();

            for (int i = 0; i < count; i++)
            {
                long hour = reader.ReadLong();
                HourBucket b = new HourBucket();
                b.Deserialize(reader);
                Hours[hour] = b;
            }

            if (version >= 1)
                Deleted = reader.ReadDateTime();
        }
    }

    /// <summary>
    /// One client session of one character, with timestamps and where it started and ended.
    /// </summary>
    public class SessionRecord
    {
        public string Account;
        public Serial Character;
        public string CharacterName;
        public string Address;

        public DateTime Start;
        public DateTime End;      // DateTime.MinValue while the session is open
        public DateTime LastSeen; // used to close sessions left open by a crash

        public Map StartMap;
        public Point3D StartLocation;
        public bool StartInTown;

        public Map EndMap;
        public Point3D EndLocation;
        public bool EndInTown;

        public int MeaningfulActions;

        public bool Open => End == DateTime.MinValue;

        public DateTime EndOrLastSeen => Open ? LastSeen : End;

        public TimeSpan Duration => EndOrLastSeen - Start;

        /// <summary>
        /// Started and ended outside towns at (nearly) the same spot: the character was
        /// switched on at its post and switched off again without going anywhere.
        /// </summary>
        public bool IsParked(int range)
        {
            return !Open && !StartInTown && !EndInTown && StartMap == EndMap && Utility.InRange(StartLocation, EndLocation, range);
        }

        public void Serialize(GenericWriter writer)
        {
            writer.Write(1); // version

            writer.Write(Account);
            writer.Write(Character.Value);
            writer.Write(CharacterName);
            writer.Write(Address);
            writer.Write(Start);
            writer.Write(End);
            writer.Write(LastSeen);
            writer.Write(StartMap);
            writer.Write(StartLocation);
            writer.Write(StartInTown);
            writer.Write(EndMap);
            writer.Write(EndLocation);
            writer.Write(EndInTown);

            // version 1
            writer.Write(MeaningfulActions);
        }

        public void Deserialize(GenericReader reader)
        {
            int version = reader.ReadInt();

            Account = reader.ReadString();
            Character = (Serial)reader.ReadInt();
            CharacterName = reader.ReadString();
            Address = reader.ReadString();
            Start = reader.ReadDateTime();
            End = reader.ReadDateTime();
            LastSeen = reader.ReadDateTime();
            StartMap = reader.ReadMap();
            StartLocation = reader.ReadPoint3D();
            StartInTown = reader.ReadBool();
            EndMap = reader.ReadMap();
            EndLocation = reader.ReadPoint3D();
            EndInTown = reader.ReadBool();

            if (version >= 1)
                MeaningfulActions = reader.ReadInt();
        }
    }

    /// <summary>
    /// When an account was seen on an address. Kept independently of ServUO's own
    /// LoginIPs list, which has no timestamps.
    /// </summary>
    public class AddressRecord
    {
        public string Account;
        public string Address;
        public DateTime FirstSeen;
        public DateTime LastSeen;
        public int Sessions;

        public void Serialize(GenericWriter writer)
        {
            writer.Write(0); // version

            writer.Write(Account);
            writer.Write(Address);
            writer.Write(FirstSeen);
            writer.Write(LastSeen);
            writer.Write(Sessions);
        }

        public void Deserialize(GenericReader reader)
        {
            reader.ReadInt(); // version

            Account = reader.ReadString();
            Address = reader.ReadString();
            FirstSeen = reader.ReadDateTime();
            LastSeen = reader.ReadDateTime();
            Sessions = reader.ReadInt();
        }
    }
}
