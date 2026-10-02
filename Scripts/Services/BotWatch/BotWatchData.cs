using System;
using System.Collections.Generic;
using System.Linq;

namespace Server.Services.BotWatch
{
    /// <summary>
    /// Counted activity types. The first group counts as "meaningful" when deciding whether
    /// a character was idle; the rest are recorded for the profile only, because they are
    /// cheap to automate while standing at a post.
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
        Speech
    }

    public class HourBucket
    {
        public static readonly int ActivityCount = Enum.GetValues(typeof(Activity)).Length;

        public readonly int[] Counts = new int[ActivityCount];

        public int OnlineSeconds;
        public int OutdoorSeconds;
        public int GhostOutdoorSeconds;
        public int TeleporterIdleSeconds;
        public int Cells;

        public int Encounters;
        public int IdleEncounters;
        public int ReactedEncounters;
        public int TeleporterEncounters;
        public int TeleporterIdleEncounters;

        public void Add(HourBucket b)
        {
            for (int i = 0; i < ActivityCount; i++)
                Counts[i] += b.Counts[i];

            OnlineSeconds += b.OnlineSeconds;
            OutdoorSeconds += b.OutdoorSeconds;
            GhostOutdoorSeconds += b.GhostOutdoorSeconds;
            TeleporterIdleSeconds += b.TeleporterIdleSeconds;
            Cells += b.Cells;
            Encounters += b.Encounters;
            IdleEncounters += b.IdleEncounters;
            ReactedEncounters += b.ReactedEncounters;
            TeleporterEncounters += b.TeleporterEncounters;
            TeleporterIdleEncounters += b.TeleporterIdleEncounters;
        }

        public void Serialize(GenericWriter writer)
        {
            writer.Write(0); // version

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
        }

        public void Deserialize(GenericReader reader)
        {
            reader.ReadInt(); // version

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

        // Snapshot taken at the end of each session, for disposable-character signals.
        public int SkillsTotal;
        public int BackpackItems;
        public int BankItems;

        public readonly SortedDictionary<long, HourBucket> Hours = new SortedDictionary<long, HourBucket>();

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
            long from = HourOf(fromUtc);
            HourBucket total = new HourBucket();

            foreach (var kv in Hours.Where(kv => kv.Key >= from))
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
            writer.Write(0); // version

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
        }

        public void Deserialize(GenericReader reader)
        {
            reader.ReadInt(); // version

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

        public bool Open => End == DateTime.MinValue;

        public TimeSpan Duration => (Open ? LastSeen : End) - Start;

        public void Serialize(GenericWriter writer)
        {
            writer.Write(0); // version

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
        }

        public void Deserialize(GenericReader reader)
        {
            reader.ReadInt(); // version

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
