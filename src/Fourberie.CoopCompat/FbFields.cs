using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace ModderLords.CompatSync.Coop.Fourberie;

/// <summary>
/// What a Fourberie player book holds (docs/FOURBERIE-LAYER-PLAN.md, fourb-book). Reviewed against Fourberie 1.4.8.2.
///
/// Persisted: exactly the fields FourberieBehavior.SyncData saves, so a book holds what a single-player save holds.
/// Scratch: campaign-level statics a job or a tick leaves set between calls (a sabotage's start time, the bandit
/// party being talked to, the horde's crime cache). On the server they are swapped with the book, so one player's
/// half-finished job never leaks into another's; they are not saved, as in single player.
///
/// Left out on purpose: UI (Gauntlet layers, movies, view models), mission-only state (agents, timers, mission views;
/// missions run on players' games, never on the server), constant Location definitions, settings, Main's install flags,
/// and HelperSubTerritory (its static initializer reads Campaign.Current, and its statics are UI selection only).
/// </summary>
internal static class FbFields
{
    internal const string Behavior = "Fourberie.FourberieBehavior";

    internal static readonly string[] Persisted =
    {
        "_townScamTiming", "_townTributeTiming", "_townExtoTiming", "_townRobTiming", "_townInsuScamTiming",
        "_townGreedyTiming", "_townCarambushTiming", "_townDomiTiming", "_lastVisitSetAlley", "_larcenyDailyTiming",
        "_larcenyJobsTiming", "_InfiltrationAlertTiming", "_supportedBandits", "_partnerRecomList", "_territoryList",
        "_partnershipList", "_playerTroopsF", "_gangLeader", "_FourbParty", "_extoVillage", "_robCastle", "_getSomeHelp",
        "_crimeBase", "_stringHeroDico", "_crimeValue", "_crimeBaseParty", "_insucaraF", "_insubandF", "_catchbandF",
        "_banditsFollowers", "_agentsParty", "_campaignTimeDictio", "_assignedGl", "_stringIntDico", "_stringClanDico",
        "_stringHeroIdDico",
    };

    /// <summary>(type, field) of the swapped-but-unsaved statics.</summary>
    internal static readonly (string Type, string Field)[] Scratch =
    {
        (Behavior, "_BanditryMissionStarted"), (Behavior, "_isBanditryMission"), (Behavior, "_processchck"),
        (Behavior, "_textObjectAlley"), (Behavior, "_targetSab"), (Behavior, "_influenceCost"), (Behavior, "_stash"),
        (Behavior, "_fortune"), (Behavior, "_equipSwitch"), (Behavior, "_equipBckup"), (Behavior, "_kingval"),
        (Behavior, "_clanval"), (Behavior, "_clanval2"), (Behavior, "_playerEncounterRoster"), (Behavior, "_AiEncounterRoster"),
        (Behavior, "_playerEncounterRosterXP"), (Behavior, "_isDisguisedGuard"), (Behavior, "_sceneString"),
        ("Fourberie.FourbBanditBehavior", "_partyTemp"), ("Fourberie.FourbBanditBehavior", "_mapAction"),
        ("Fourberie.FourbBanditBehavior", "_recruitedBanditsOnMap"), ("Fourberie.FourbBanditBehavior", "_isStashing"),
        ("Fourberie.FourbBanditBehavior", "_dummyTroopRooster"),
        ("Fourberie.FourbRecruitableBehavior", "_recruitTiming"), ("Fourberie.FourbRecruitableBehavior", "_dummyTroopRooster"),
        ("Fourberie.FourbRecruitableBehavior", "_recruitCost"), ("Fourberie.FourbRecruitableBehavior", "_maxCoef"),
        ("Fourberie.FourbEscapeBehavior", "_partyTempEscape"),
        ("Fourberie.FourbFightClubBehavior", "_weaponType"), ("Fourberie.FourbFightClubBehavior", "_playerTier"),
        ("Fourberie.FourbFightClubBehavior", "_isPlayerSideA"),
        ("Fourberie.FModelHelperCrime", "_hordeCrimeImpact"),
        ("Fourberie.HelperSubCarambush", "_banditryMissionChara"), ("Fourberie.HelperSubCarambush", "_startTime"), ("Fourberie.HelperSubCarambush", "_duration"),
        ("Fourberie.HelperSubForcedTribute", "_startTime"),
        ("Fourberie.HelperSubGreedyMilitia", "_startTime"), ("Fourberie.HelperSubGreedyMilitia", "_duration"),
        ("Fourberie.HelperSubNotableExtortion", "_startTime"), ("Fourberie.HelperSubNotableExtortion", "_duration"),
        ("Fourberie.HelperSubPlot", "_startTime"), ("Fourberie.HelperSubPlot", "_duration"),
        ("Fourberie.HelperSubRobbery", "_startTime"),
        ("Fourberie.HelperSubSabotage", "_tempoProc"), ("Fourberie.HelperSubSabotage", "_reportval"),
        ("Fourberie.HelperSubSabotage", "_startTime"), ("Fourberie.HelperSubSabotage", "_duration"),
    };

    internal enum Reach { Connected, Everyone }

    /// <summary>
    /// FbTicks: (type, handler, parameter count, who it runs for). Periodic ticks and the player's own movements run for
    /// connected players only; world events reach every book.
    /// </summary>
    internal static readonly (string Type, string Method, int Params, Reach Reach)[] Handlers =
    {
        ("Fourberie.FourberieBehavior", "HourlyTick", 0, Reach.Connected),
        ("Fourberie.FourberieBehavior", "DailyTick", 0, Reach.Connected),
        ("Fourberie.FourberieBehavior", "WeeklyTick", 0, Reach.Connected),
        ("Fourberie.FourberieBehavior", "DailyTickSet", 1, Reach.Connected),
        ("Fourberie.FourberieBehavior", "DailyTickHero", 1, Reach.Connected),
        ("Fourberie.FourberieBehavior", "OnSettlementEntered", 3, Reach.Connected),
        ("Fourberie.FourberieBehavior", "OnSettlementLeft", 2, Reach.Connected),
        ("Fourberie.FourberieBehavior", "FMapEventEnded", 1, Reach.Connected),
        ("Fourberie.FourberieBehavior", "OnAlleyClearedByPlayer", 1, Reach.Connected),
        ("Fourberie.FourberieBehavior", "OnAlleyOccupiedByPlayer", 2, Reach.Connected),
        ("Fourberie.FourberieBehavior", "FOnForceSupplies", 2, Reach.Connected),
        ("Fourberie.FourberieBehavior", "FOnForceVolunteers", 2, Reach.Connected),
        ("Fourberie.FourberieBehavior", "FOnRaidCompleted", 2, Reach.Connected),
        ("Fourberie.FourberieBehavior", "FOnMobilePartyDestroyed", 2, Reach.Everyone),
        ("Fourberie.FourberieBehavior", "FOnheroKilled", 4, Reach.Everyone),
        ("Fourberie.FourberieBehavior", "HeroBecomePrisoner", 2, Reach.Everyone),
        ("Fourberie.FourberieBehavior", "HideoutDeactivated", 1, Reach.Everyone),
        ("Fourberie.FourberieBehavior", "FOnClanChanged", 2, Reach.Everyone),
        ("Fourberie.FourberieBehavior", "OnClanDestroyed", 1, Reach.Everyone),
        ("Fourberie.FourberieBehavior", "OnKingdomDestroyed", 1, Reach.Everyone),
        ("Fourberie.FourberieBehavior", "OnCompanionRemoved", 2, Reach.Everyone),

        ("Fourberie.FourbBanditBehavior", "BanditHourlyTick", 0, Reach.Connected),
        ("Fourberie.FourbBanditBehavior", "BanditDailTick", 0, Reach.Connected),
        ("Fourberie.FourbBanditBehavior", "BanditWeeklyTick", 0, Reach.Connected),
        ("Fourberie.FourbBanditBehavior", "FOnDailyTickParty", 1, Reach.Connected),
        ("Fourberie.FourbBanditBehavior", "FOnDailyTickSettlement", 1, Reach.Connected),
        ("Fourberie.FourbBanditBehavior", "BanditMapEventStarted", 3, Reach.Connected),
        ("Fourberie.FourbBanditBehavior", "OnWarDeclared", 3, Reach.Everyone),
        ("Fourberie.FourbBanditBehavior", "OnMakePeace", 3, Reach.Everyone),
        ("Fourberie.FourbBanditBehavior", "OnClanChangedKingdomEvent", 5, Reach.Everyone),
        ("Fourberie.FourbBanditBehavior", "OnKingdomDestroyed", 1, Reach.Everyone),

        ("Fourberie.FourbContractBehavior", "HourlyTick", 0, Reach.Connected),
        ("Fourberie.FourbContractBehavior", "DailyTickClan", 1, Reach.Connected),
        ("Fourberie.FourbContractBehavior", "OnWarDeclared", 3, Reach.Everyone),
        ("Fourberie.FourbContractBehavior", "OnheroKilled", 4, Reach.Everyone),
        ("Fourberie.FourbContractBehavior", "OnClanDestroyed", 1, Reach.Everyone),

        ("Fourberie.FourbFightClubBehavior", "PitWeeklyTick", 0, Reach.Connected),
        ("Fourberie.FourbFightClubBehavior", "PitDailyTickHero", 1, Reach.Connected),
        ("Fourberie.FourbFightClubBehavior", "PitOnheroKilled", 4, Reach.Everyone),
        ("Fourberie.FourbFightClubBehavior", "PitOnHeroRelationChanged", 7, Reach.Everyone),
        ("Fourberie.FourbFightClubBehavior", "PitOnWarDeclared", 3, Reach.Everyone),

        ("Fourberie.FourbSafeHouseBehavior", "SHSHourlyTickF", 0, Reach.Connected),
        ("Fourberie.FourbSafeHouseBehavior", "SHDailyTickF", 0, Reach.Connected),
        ("Fourberie.FourbSafeHouseBehavior", "SHOnSiegeBombardmentWallHit", 5, Reach.Connected),
        ("Fourberie.FourbSafeHouseBehavior", "OnGameLoadFinished", 0, Reach.Everyone),
    };

    /// <summary>
    /// Event handlers Fourberie registers that are deliberately NOT run per player: one-time setup (menus and dialogs,
    /// the new-game reset) and things that only happen on a player's own game (menus opening, inventory screens,
    /// missions). FourberieSurfaceTests checks that every registered handler is in one list or the other.
    /// </summary>
    internal static readonly (string Type, string Method)[] NotFannedOut =
    {
        ("Fourberie.FourberieBehavior", "AddGameMenus"), ("Fourberie.FourberieBehavior", "DataDelete"),
        ("Fourberie.FourberieBehavior", "FourbOnGaMenOpened"),
        ("Fourberie.FourbBanditBehavior", "BanditAddMenu"), ("Fourberie.FourbBanditBehavior", "BanditOnGaMenOpened"),
        ("Fourberie.FourbBanditBehavior", "OnPlayerInventoryChanged"),
        ("Fourberie.FourbContactMenu", "AddContactMenusF"), ("Fourberie.FourbContactMenu", "FOnGaMenOpened"),
        ("Fourberie.FourbContractBehavior", "AddGameMenus"),
        ("Fourberie.FourbEscapeBehavior", "FourbEscapMenu"),
        ("Fourberie.FourbFightClubBehavior", "PitAddGameMenus"), ("Fourberie.FourbFightClubBehavior", "PitLocationCharactersAreReadyToSpawn"),
        ("Fourberie.FourbRecruitableBehavior", "AddGameMenus"),
        ("Fourberie.FourbSafeHouseBehavior", "SHAddGameMenus"), ("Fourberie.FourbSafeHouseBehavior", "SHOnGaMenOpened"),
        ("Fourberie.FourbSafeHouseBehavior", "SHOnMissionEnded"), ("Fourberie.FourbSafeHouseBehavior", "SHOnMissionStarted"),
        ("Fourberie.FourbSafeHouseBehavior", "SHLocationCharactersAreReadyToSpawn"),
        ("Fourberie.AddOnHS", "MenuHomeSteads"),
    };

    private const BindingFlags Statics = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly;

    /// <summary>The book's fields: persisted ones (all required) then scratch ones (each optional). Null + reason when a persisted one is missing.</summary>
    internal static (List<FieldInfo>? Fields, string? Problem, List<string> MissingScratch) Resolve(Assembly mod)
    {
        var fields = new List<FieldInfo>();
        var behavior = mod.GetType(Behavior, false);
        if (behavior == null) return (null, Behavior + " not found", new List<string>());
        var missing = Persisted.Where(n => behavior.GetField(n, Statics) is not { IsInitOnly: false, IsLiteral: false }).ToList();
        if (missing.Count > 0) return (null, "saved field(s) moved: " + string.Join(", ", missing), new List<string>());
        fields.AddRange(Persisted.Select(n => behavior.GetField(n, Statics)!));

        var missingScratch = new List<string>();
        foreach (var (type, name) in Scratch)
        {
            var f = mod.GetType(type, false)?.GetField(name, Statics);
            if (f == null || f.IsInitOnly || f.IsLiteral) missingScratch.Add(type.Substring(type.LastIndexOf('.') + 1) + "." + name);
            else fields.Add(f);
        }
        return (fields, null, missingScratch);
    }
}

/// <summary>The ledger parties Fourberie makes with CreateVirtualParty (FbLedgers), with the names it gives them.</summary>
internal static class FbLedgerRoles
{
    internal static readonly (string Role, string Name)[] Roles =
    {
        ("fb_crimebase_party", "{=FoSafHou23}Your lads"),
        ("fb_saboteurs_party", "{=FoAgeOp17}Saboteurs"),
    };

    internal static IEnumerable<string> Ids => Roles.Select(r => r.Role);
}

/// <summary>Player actions the server carries out (FbRelay): (type, static void method, parameter count), run in order.</summary>
internal static class FbRelayTable
{
    internal static readonly (string Type, string Method, int Params)[] Methods =
    {
        ("Fourberie.HelperSubInsuScam", "SpawnCaravan", 2),
        ("Fourberie.HelperSubInsuScam", "SpawnBandits", 1),
    };
}

/// <summary>Actions skipped on a player's game in co-op with a "not available in co-op" line (FbGaps).</summary>
internal static class FbGapsTable
{
    internal static readonly (string Type, string Method, int Params)[] Blocked =
    {
        ("Fourberie.FourbSafeHouseBehavior", "CrookedEncounterStart", 0),
        ("Fourberie.FourbSafeHouseBehavior", "BanditsRelEncounterStart", 0),
        ("Fourberie.HelperSubNotableExtortion", "TroopRosterManageExto", 1),
        ("Fourberie.HelperSubCarambush", "TroopRosterManageCarambush", 1),
    };
}

/// <summary>Fourberie model methods the server answers as their owner, or summed over players (FbModels).</summary>
internal static class FbModelTable
{
    internal enum Kind { Clan, Party, Follower, PartyBase, Survival, Stance, Summed }

    /// <summary>(model type, method, parameter count, how its owner is found).</summary>
    internal static readonly (string Type, string Method, int Params, Kind Kind)[] Methods =
    {
        ("Fourberie.FModelClanFinance", "CalculateClanGoldChange", 4, Kind.Clan),
        ("Fourberie.FModelClanFinance", "CalculateClanIncome", 4, Kind.Clan),
        ("Fourberie.FModelClanFinance", "CalculateClanExpenses", 4, Kind.Clan),
        ("Fourberie.FModelMobileFood", "CalculateDailyBaseFoodConsumptionf", 2, Kind.Party),
        ("Fourberie.FModelMapSpeed", "CalculateBaseSpeed", 4, Kind.Follower),
        ("Fourberie.FModelPartyTransition", "GetTransitionTimeDisembarking", 1, Kind.Follower),
        ("Fourberie.FModelPartyTransition", "GetTransitionTimeForEmbarking", 1, Kind.Follower),
        ("Fourberie.FModelPower", "GetPowerOfParty", 3, Kind.PartyBase),
        ("Fourberie.FModelDeath", "GetSurvivalChance", 5, Kind.Survival),
        ("Fourberie.FModelDiplo", "GetShallowDiplomaticStance", 2, Kind.Stance),
        ("Fourberie.FModelDiplo", "IsAtConstantWar", 2, Kind.Stance),
        ("Fourberie.FModelLoyalty", "CalculateLoyaltyChange", 2, Kind.Summed),
        ("Fourberie.FModelSecurity", "CalculateSecurityChange", 2, Kind.Summed),
        ("Fourberie.FModelCrime", "GetDailyCrimeRatingChange", 2, Kind.Summed),
    };
}
