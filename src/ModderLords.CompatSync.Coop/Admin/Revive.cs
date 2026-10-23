using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Common;
using Common.Messaging;
using Common.Network;
using GameInterface;
using GameInterface.Services.Players;
using HarmonyLib;
using ModderLords.CompatSync;
using ModderLords.CompatSync.Coop;
using ModderLords.CompatSync.Coop.Admin;
using ModderLords.CompatSync.Messages;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.CharacterDevelopment;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Library;

namespace ModderLords.CompatSync.Coop.Admin
{
    /// <summary>
    /// Brings a dead hero back. When a hero dies the game's Hero.OnDeath throws away their skills, perks, traits,
    /// attributes, skill progress and equipment, so a revived hero needs all of that again. <see cref="Rebuild"/> makes it
    /// the way the game's HeroCreator first did: empty stores, the creation model's traits and skills for the hero, then
    /// HeroDeveloper.InitializeHeroDeveloper for level, focus and attributes, and the template's first equipment.
    /// <para>
    /// The same rebuild runs on every client (<see cref="NetworkHeroRevived"/>), sent before the server changes anything:
    /// a client must have the stores before Coop relays the new state, skills and traits, or applying them fails there.
    /// </para>
    /// </summary>
    internal static class HeroRevival
    {
        private static readonly FieldInfo? Developer = AccessTools.Field(typeof(Hero), "_heroDeveloper");
        private static readonly FieldInfo? SkillStore = AccessTools.Field(typeof(Hero), "_heroSkills");
        private static readonly FieldInfo? TraitStore = AccessTools.Field(typeof(Hero), "_heroTraits");
        private static readonly FieldInfo? PerkStore = AccessTools.Field(typeof(Hero), "_heroPerks");
        private static readonly FieldInfo? AttributeStore = AccessTools.Field(typeof(Hero), "_characterAttributes");
        private static readonly FieldInfo? BattleEquipment = AccessTools.Field(typeof(Hero), "_battleEquipment");
        private static readonly FieldInfo? CivilianEquipment = AccessTools.Field(typeof(Hero), "_civilianEquipment");
        private static readonly FieldInfo? StealthEquipment = AccessTools.Field(typeof(Hero), "_stealthEquipment");
        private static readonly PropertyInfo? DeathMark = AccessTools.Property(typeof(Hero), nameof(Hero.DeathMark));
        private static readonly ConstructorInfo? NewDeveloper = AccessTools.Constructor(typeof(HeroDeveloper), new[] { typeof(Hero) });

        /// <summary>Null when this game version has every member a rebuild needs; otherwise which are missing.</summary>
        public static string? Missing()
        {
            var missing = new[]
            {
                ("_heroDeveloper", (object?)Developer), ("_heroSkills", SkillStore), ("_heroTraits", TraitStore), ("_heroPerks", PerkStore),
                ("_characterAttributes", AttributeStore), ("_battleEquipment", BattleEquipment), ("_civilianEquipment", CivilianEquipment),
                ("DeathMark", DeathMark), ("HeroDeveloper(Hero)", NewDeveloper),
            }.Where(m => m.Item2 is null).Select(m => m.Item1).ToList();
            return missing.Count == 0 ? null : string.Join(", ", missing);
        }

        /// <summary>Gives a dead hero back what death took. Does not change their state or place them anywhere.</summary>
        public static void Rebuild(Hero hero)
        {
            if (Developer!.GetValue(hero) is null) Developer.SetValue(hero, NewDeveloper!.Invoke(new object[] { hero }));
            if (SkillStore!.GetValue(hero) is null) SkillStore.SetValue(hero, new PropertyOwner<SkillObject>());
            if (TraitStore!.GetValue(hero) is null) TraitStore.SetValue(hero, new PropertyOwner<TraitObject>());
            if (PerkStore!.GetValue(hero) is null) PerkStore.SetValue(hero, new PropertyOwner<PerkObject>());
            if (AttributeStore!.GetValue(hero) is null) AttributeStore.SetValue(hero, new PropertyOwner<CharacterAttribute>());
            hero.VolunteerTypes ??= new CharacterObject[6];

            var model = Campaign.Current.Models.HeroCreationModel;
            foreach (var (trait, value) in model.GetTraitsForHero(hero)) hero.SetTraitLevel(trait, value);
            foreach (var (skill, value) in model.GetDefaultSkillsForHero(hero)) hero.SetSkillValue(skill, value);
            if (hero.Age >= Campaign.Current.Models.AgeModel.HeroComesOfAge) hero.HeroDeveloper.InitializeHeroDeveloper();

            // The template's first sets, so the server and every client end up with the same clothes.
            var source = hero.Template ?? hero.CharacterObject;
            if (BattleEquipment!.GetValue(hero) is null && source?.FirstBattleEquipment is { } battle) BattleEquipment.SetValue(hero, battle.Clone());
            if (CivilianEquipment!.GetValue(hero) is null && source?.FirstCivilianEquipment is { } civilian) CivilianEquipment.SetValue(hero, civilian.Clone());
            if (StealthEquipment?.GetValue(hero) is null && source?.FirstStealthEquipment is { } stealth) StealthEquipment?.SetValue(hero, stealth.Clone());
            DeathMark!.SetValue(hero, KillCharacterAction.KillCharacterActionDetail.None);
        }

        /// <summary>Where a revived hero appears: their home, their clan's, or else any town.</summary>
        public static Settlement? Place(Hero hero) =>
            hero.HomeSettlement ?? hero.Clan?.HomeSettlement ?? hero.BornSettlement ?? Settlement.All.FirstOrDefault(s => s.IsTown);

        /// <summary>
        /// <c>modderlords.revive &lt;req&gt; hero:&lt;id&gt;</c>: brings a dead hero back to life in their home settlement,
        /// with full health and skills rebuilt from their template. A player's own hero is refused: Coop handles a
        /// player's death itself.
        /// </summary>
        [CommandLineFunctionality.CommandLineArgumentFunction("revive", AdminWire.CommandGroup)]
        public static string ReviveCommand(List<string> args) => PlayerAdmin.Run("revive", args, req =>
        {
            if (args.Count < 2) return AdminWire.Reply("revive", req, "Usage: modderlords.revive <request id> hero:<id>");
            if (HeroAdmin.FindTarget(args[1], out var steamId, out var hero, out _) is { } missing) return AdminWire.Reply("revive", req, missing);
            if (hero!.IsAlive) return AdminWire.Reply("revive", req, $"{hero.Name} is alive.");
            if (steamId.Length > 0 || (ContainerProvider.TryResolve<IPlayerManager>(out var players) && HeroAdmin.PlayerLeads(players).Contains(hero)))
                return AdminWire.Reply("revive", req, $"{hero.Name} is a player's hero; Coop handles a player's death.");
            if (Missing() is { } members) return AdminWire.Reply("revive", req, "This game version has no " + members + ", so a hero cannot be revived.");
            if (Place(hero) is not { } settlement) return AdminWire.Reply("revive", req, "There is no settlement to bring them back in.");

            // Clients first, so they hold the stores before Coop relays anything below.
            global::Coop.Core.Server.Services.ModderLordsCompat.Handlers.ReviveServerHandler.Current?.Send(hero.StringId);
            Rebuild(hero);
            hero.SetDeathDay(CampaignTime.Never);
            hero.ChangeState(Hero.CharacterStates.Active);
            hero.HitPoints = hero.MaxHitPoints;
            TeleportHeroAction.ApplyImmediateTeleportToSettlement(hero, settlement);
            Log.Info($"host revived {hero.Name} ({hero.StringId}) in {settlement.Name}");

            var data = HeroAdmin.Describe("", hero, hero.PartyBelongedTo);
            data["changes"] = new List<object?> { $"Revived in {settlement.Name}" };
            data["notApplied"] = new List<object?>();
            return AdminWire.Reply("revive", req, null, data);
        });
    }
}

// Coop discovers handlers by namespace prefix; see ServerSettingsHandler.
namespace Coop.Core.Server.Services.ModderLordsCompat.Handlers
{
    /// <summary>Server: tells every client a hero is being revived, so each rebuilds what the hero's death took.</summary>
    public sealed class ReviveServerHandler : IHandler
    {
        public const int ProtocolVersion = 1;
        private readonly INetwork network;

        public static ReviveServerHandler? Current { get; private set; }

        public ReviveServerHandler(INetwork network)
        {
            this.network = network;
            if (!CoopProbe.Present) return;
            Current = this;
        }

        public void Send(string heroId) => network.SendAll(new NetworkHeroRevived { HeroId = heroId, ProtocolVersion = ProtocolVersion });

        public void Dispose()
        {
            if (ReferenceEquals(Current, this)) Current = null;
        }
    }
}

namespace Coop.Core.Client.Services.ModderLordsCompat.Handlers
{
    /// <summary>Client: rebuilds a revived hero's skills, traits and equipment, as the server does.</summary>
    public sealed class ReviveClientHandler : IHandler
    {
        private readonly IMessageBroker broker;
        private readonly bool active;

        public ReviveClientHandler(IMessageBroker broker)
        {
            this.broker = broker;
            if (!CoopProbe.Present) return;
            active = true;
            broker.Subscribe<NetworkHeroRevived>(Handle);
        }

        public void Dispose()
        {
            if (active) broker.Unsubscribe<NetworkHeroRevived>(Handle);
        }

        private static void Handle(MessagePayload<NetworkHeroRevived> payload)
        {
            var id = payload.What.HeroId;
            GameThread.RunSafe(() =>
            {
                if (Campaign.Current is null || HeroRevival.Missing() is not null) return;
                if (Hero.Find(id) is not { } hero) { Log.Warn("revive: no hero " + id + " on this game"); return; }
                HeroRevival.Rebuild(hero);
                Log.Info("revive: rebuilt " + hero.Name + " for the server's revive");
            }, false, "ModderLords revive");
        }
    }
}
