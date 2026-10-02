using Server.Commands;
using Server.Items;
using Server.Mobiles;
using Server.Network;
using Server.Regions;
using System;
using System.Collections.Generic;

namespace Server.Services.BotWatch
{
    public static partial class BotWatch
    {
        /// <summary>
        /// Beneficial spells (zero-based spell IDs, see Scripts/Spells/Initializer.cs) whose
        /// use on another player counts as assisting them.
        /// </summary>
        private static readonly HashSet<int> m_BeneficialSpells = new HashSet<int>
        {
            3,   // Heal
            8,   // Agility
            9,   // Cunning
            10,  // Cure
            15,  // Strength
            16,  // Bless
            24,  // Arch Cure
            25,  // Arch Protection
            28,  // Greater Heal
            58,  // Resurrection
            200, // Cleanse by Fire
            201, // Close Wounds
            208  // Remove Curse
        };

        private static void SubscribeEvents()
        {
            EventSink.Login += e => StartSession(e.Mobile);
            EventSink.Disconnected += e => EndSession(e.Mobile);
            EventSink.CharacterCreated += e => { if (IsTracked(e.Mobile)) GetRecord(e.Mobile); };
            EventSink.MobileDeleted += OnMobileDeleted;

            // Combat
            EventSink.AggressiveAction += OnAggressiveAction;
            EventSink.CreatureDeath += e => OnKill(e.Killer, e.Creature);
            EventSink.PlayerDeath += OnPlayerDeath;

            // Production and economy
            EventSink.CraftSuccess += e => Record(e.Crafter, Activity.Craft);
            EventSink.ResourceHarvestSuccess += e => Record(e.Harvester, Activity.Gather);
            EventSink.ValidVendorPurchase += e => Record(e.Mobile, Activity.Trade);
            EventSink.ValidVendorSell += e => Record(e.Mobile, Activity.Trade);
            EventSink.BODOffered += e => Record(e.Player, Activity.BODTaken);
            EventSink.BODUsed += e => Record(e.User, Activity.BODTurnedIn);
            EventSink.QuestComplete += e => Record(e.Mobile, Activity.QuestComplete);
            EventSink.TameCreature += e => Record(e.Mobile, Activity.Tame);

            // Skills, spells and items
            EventSink.SkillGain += e => Record(e.From, Activity.SkillGain);
            EventSink.SkillCheck += e => Record(e.From, Activity.SkillUse);
            EventSink.CastSpellRequest += e => Record(e.Mobile, Activity.Spell);
            EventSink.OnItemUse += e => Record(e.From, Activity.ItemUse);
            EventSink.OnItemObtained += e => Record(e.Mobile, Activity.ItemObtained);
            EventSink.OnConsume += e => Record(e.Consumer, Activity.Consume);
            EventSink.TargetedSpell += OnTargetedSpell;
            EventSink.TargetedSkill += e => OnTargetedMobile(e.Mobile, e.Target as Mobile, false);
            EventSink.TargetedItemUse += e => OnTargetedMobile(e.Mobile, e.Target as Mobile, e.Source is Bandage);

            // Movement and travel
            EventSink.Movement += e => RecordStep(e.Mobile);
            EventSink.TeleportMovement += OnTeleportMovement;
            EventSink.OnEnterRegion += OnEnterRegion;
            EventSink.FastWalk += e => Record(e.NetState?.Mobile, Activity.FastWalk);

            // Social
            EventSink.Speech += OnSpeech;
            EventSink.JoinGuild += e => Record(e.Mobile, Activity.GuildJoin);

            // Interface use: things scripts rarely bother with.
            EventSink.SetAbility += e => Record(e.Mobile, Activity.InterfaceUse);
            EventSink.DisarmRequest += e => Record(e.Mobile, Activity.InterfaceUse);
            EventSink.StunRequest += e => Record(e.Mobile, Activity.InterfaceUse);
            EventSink.EquipMacro += e => Record(e.Mobile, Activity.InterfaceUse);
            EventSink.UnequipMacro += e => Record(e.Mobile, Activity.InterfaceUse);
            EventSink.OpenSpellbookRequest += e => Record(e.Mobile, Activity.InterfaceUse);
            EventSink.AnimateRequest += e => Record(e.Mobile, Activity.InterfaceUse);
            EventSink.OpenDoorMacroUsed += e => Record(e.Mobile, Activity.InterfaceUse);
            EventSink.VirtueMacroRequest += e => Record(e.Mobile, Activity.InterfaceUse);
            EventSink.VirtueGumpRequest += e => Record(e.Beholder, Activity.InterfaceUse);
            EventSink.RenameRequest += e => Record(e.From, Activity.InterfaceUse);
            EventSink.HelpRequest += e => Record(e.Mobile, Activity.InterfaceUse);
            EventSink.ContextMenu += e => Record(e.Mobile, Activity.InterfaceUse);
            EventSink.PaperdollRequest += e => Record(e.Beholder, Activity.InterfaceUse);
            EventSink.ProfileRequest += e => Record(e.Beholder, Activity.InterfaceUse);
        }

        #region EventSink handlers
        private static void OnMobileDeleted(MobileDeletedEventArgs e)
        {
            if (e.Mobile is PlayerMobile && Characters.TryGetValue(e.Mobile.Serial, out CharacterRecord rec))
                rec.Deleted = DateTime.UtcNow;
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
                RecordAttacked(victimOwner, aggressor);
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
            Mobile killer = Owner(e.Killer);

            if (IsTracked(killer) && killer != e.Mobile)
            {
                Record(e.Mobile, Activity.PvPDeath);
                Record(killer, Activity.PvPKill);
            }
            else
            {
                Record(e.Mobile, Activity.PvMDeath);
            }
        }

        private static void OnSpeech(SpeechEventArgs e)
        {
            if (e.Speech != null && e.Speech.StartsWith(CommandSystem.Prefix))
                return;

            switch (e.Type)
            {
                case MessageType.Whisper: Record(e.Mobile, Activity.Whisper); break;
                case MessageType.Yell: Record(e.Mobile, Activity.Yell); break;
                case MessageType.Emote: Record(e.Mobile, Activity.Emote); break;
                case MessageType.Guild:
                case MessageType.Alliance: Record(e.Mobile, Activity.GuildChat); break;
                default: Record(e.Mobile, Activity.Speech); break;
            }
        }

        private static void OnTargetedSpell(TargetedSpellEventArgs e)
        {
            OnTargetedMobile(e.Mobile, e.Target as Mobile, m_BeneficialSpells.Contains(e.SpellID));
        }

        /// <summary>
        /// Something was used on a player. Using it on someone just encountered is a reaction;
        /// beneficial use (heal spells, bandages...) on another player also counts as assisting.
        /// </summary>
        private static void OnTargetedMobile(Mobile from, Mobile target, bool beneficial)
        {
            Mobile other = Owner(target);

            if (!IsTracked(from) || !IsTracked(other) || other == from)
                return;

            Record(from, Activity.TargetPlayer);

            if (beneficial)
                Record(from, Activity.AssistOther);

            MarkReaction(from, other);
        }

        private static void OnTeleportMovement(TeleportMovementEventArgs e)
        {
            if (IsTracked(e.Mobile) && !Utility.InRange(e.OldLocation, e.NewLocation, 1))
                Record(e.Mobile, Activity.TravelJump);
        }

        private static void OnEnterRegion(OnEnterRegionEventArgs e)
        {
            Region dungeon = e.NewRegion?.GetRegion(typeof(DungeonRegion));

            if (dungeon != null && dungeon != e.OldRegion?.GetRegion(typeof(DungeonRegion)))
                Record(e.From, Activity.DungeonEnter);
        }
        #endregion

        #region Handlers for hooks without an EventSink event
        // ServUO raises no event for these. Call the handlers from the places noted below.
        // Hooks inside the Server project cannot reference Scripts directly; add a static
        // event or callback there and subscribe these handlers to it.

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

        /// <summary>
        /// A target cursor was answered. Call from Target.Invoke in
        /// Server/Targeting/Target.cs to cover every targeted action; the TargetedSpell,
        /// TargetedSkill and TargetedItemUse events only fire for the client's
        /// "use on target" macros.
        /// </summary>
        public static void OnTarget(Mobile from, object targeted)
        {
            OnTargetedMobile(from, targeted as Mobile, false);
        }

        /// <summary>
        /// A hidden character was revealed by someone else. Call from the Detect Hidden
        /// skill and the Reveal spell (Scripts/Skills/DetectHidden.cs, Scripts/Spells/Sixth/Reveal.cs).
        /// </summary>
        public static void OnRevealed(Mobile revealer, Mobile revealed)
        {
            revealer = Owner(revealer);

            if (revealed == null || revealer == revealed)
                return;

            Record(revealed, Activity.RevealedByOther);

            if (IsTracked(revealed))
            {
                Record(revealer, Activity.RevealedOther);
                MarkReaction(revealer, revealed);
            }
        }

        /// <summary>
        /// Damage was dealt. Call from AOS.Damage in Scripts/Misc/AOS.cs or Mobile.Damage
        /// in Server/Mobile.cs.
        /// </summary>
        public static void OnDamage(Mobile from, Mobile to, int amount)
        {
            if (to == null || amount <= 0)
                return;

            DateTime now = DateTime.UtcNow;

            if (IsTracked(to))
                GetLive(to).Record.GetHour(now).DamageTaken += amount;

            from = Owner(from);

            if (!IsTracked(from) || from == to)
                return;

            HourBucket bucket = GetLive(from).Record.GetHour(now);

            if (IsTracked(Owner(to)))
                bucket.DamageDealtPvP += amount;
            else if (to is BaseCreature)
                bucket.DamageDealtPvM += amount;
        }

        /// <summary>
        /// A travel spell or item was used (recall, gate, sacred journey, runebook...).
        /// Call from the travel spells' effect methods.
        /// </summary>
        public static void OnTravel(Mobile m)
        {
            Record(m, Activity.TravelSpell);
        }

        /// <summary>
        /// The character's gold changed. Call where gold enters or leaves the backpack or
        /// bank (Banker.Deposit/Withdraw, gold pickup, vendor payments).
        /// </summary>
        public static void OnGoldChange(Mobile m, int delta)
        {
            if (!IsTracked(m) || delta == 0)
                return;

            HourBucket bucket = GetLive(m).Record.GetHour(DateTime.UtcNow);

            if (delta > 0)
                bucket.GoldGained += delta;
            else
                bucket.GoldLost -= delta;
        }

        /// <summary>
        /// The character joined a party. Call from Party.Add in Scripts/Services/Party/Party.cs.
        /// </summary>
        public static void OnPartyJoin(Mobile m)
        {
            Record(m, Activity.PartyJoin);
        }

        /// <summary>
        /// The client asked for another mobile's health bar. Call from the mobile status
        /// request handler in Server/Network/PacketHandlers.cs.
        /// </summary>
        public static void OnHealthBarRequest(Mobile from, Mobile target)
        {
            if (target != null && target != from)
                Record(from, Activity.HealthBarRequest);
        }
        #endregion
    }
}
