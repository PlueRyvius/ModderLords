using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using ModderLords.CompatSync;

namespace ModderLords.CompatSync.Coop.LivingEconomy;

/// <summary>
/// The server runs the economy; players' games do not (docs/LIVING-ECONOMY-LAYER.md, part 1).
///
/// Living Economy is 18 campaign behaviours that act on daily, weekly and settlement events. Left alone, every player's
/// game would run its own copy, each drifting from the server's, and each spending gold and moving troops only on that
/// player's screen. On a player's game in a co-op session these handlers return straight away; the server's run is the
/// only one, and its results reach players through Coop's own sync (gold, rosters, prosperity) and the state mirror
/// (the mod's own records).
///
/// Kept on players' games: the menus (SettlementMenuBehavior), the save store's session hooks (EconomySaveBehavior),
/// settlement intel (each player's own fog of war), the daily statistics log, and every game model, which read the
/// mirrored records and the host's settings and so answer the same as the server.
/// </summary>
internal sealed class LeServerOnlyComponent : ILeComponent
{
    private const string Owner = "ModderLords.LivingEconomy.ServerOnly";
    private const string B = "BetterEconomy.Behaviors.";

    /// <summary>(type, method, parameter count): the simulation entry points, plus the two helpers patches call directly.</summary>
    internal static readonly (string Type, string Method, int Params)[] Handlers =
    {
        (B + "CaravanCampaignBehavior", "OnDailyTick", 0),
        (B + "CaravanCampaignBehavior", "OnSettlementEntered", 3),
        (B + "CastleEconomyCampaignBehavior", "OnDailyTickSettlement", 1),
        (B + "CulturalMarketCampaignBehavior", "OnSettlementDailyTick", 1),
        (B + "EconomicEventCampaignBehavior", "OnDailyTick", 0),
        (B + "FeudalEconomyCampaignBehavior", "OnSettlementDailyTick", 1),
        (B + "FeudalEconomyCampaignBehavior", "OnDailyTick", 0),
        (B + "LordInvestmentCampaignBehavior", "OnSettlementDailyTick", 1),
        (B + "MigrationCampaignBehavior", "OnWeeklyTick", 0),
        (B + "PopulationCampaignBehavior", "OnSettlementDailyTick", 1),
        (B + "RecruitmentAuditCampaignBehavior", "OnSettlementEntered", 3),
        (B + "RecruitmentAuditCampaignBehavior", "OnSettlementLeft", 2),
        (B + "RecruitmentAuditCampaignBehavior", "OnPartySizeChanged", 1),
        (B + "RecruitmentAuditCampaignBehavior", "OnTick", 1),
        (B + "RosterSanitizerCampaignBehavior", "Run", 1),
        (B + "RouteDangerCampaignBehavior", "OnSettlementDailyTick", 1),
        (B + "TownEconomyCampaignBehavior", "OnSettlementDailyTick", 1),
        (B + "TradeAgreementCampaignBehavior", "OnDailyTick", 0),
        (B + "TradeAgreementCampaignBehavior", "OnWarDeclared", 3),
        (B + "VillageDevelopmentCampaignBehavior", "OnSettlementDailyTick", 1),
        (B + "VillageInvestmentCampaignBehavior", "OnSettlementDailyTick", 1),
        (B + "VillageInvestmentCampaignBehavior", "OnClanDailyTick", 1),
        (B + "VillageSupplyCampaignBehavior", "OnSettlementDailyTick", 1),
        (B + "VillageSupplyCampaignBehavior", "OnVillageBeingRaided", 1),
        (B + "VillageSupplyCampaignBehavior", "OnRaidCompleted", 2),
        // Called from the raid state-setter patch as well as the raid event: raid damage to the supply link.
        (B + "VillageSupplyCampaignBehavior", "ApplyRaidImpact", 1),
        (B + "WealthAuditCampaignBehavior", "OnDailyTick", 0),
        // Recruitment drains the settlement's manpower; called from the mod's recruitment and garrison patches.
        ("BetterEconomy.Patches.RecruitmentPatch", "ApplyRecruitmentDrain", 5),
    };

    private static long _skipped;
    private static readonly List<MethodInfo> Found = new List<MethodInfo>();

    public string Id => "server-only";

    public string? SkipReason(LeContext context)
    {
        if (context.IsServer) return "server (it is the one that runs the economy)";
        Found.Clear();
        var missing = new List<string>();
        foreach (var (type, method, count) in Handlers)
        {
            var m = context.Method(type, method, count);
            if (m == null) missing.Add(LivingEconomyLayer.Short(type) + "." + method);
            else Found.Add(m);
        }
        if (Found.Count == 0) return "Living Economy changed; none of its simulation handlers were found";
        if (missing.Count > 0) Log.Warn(LivingEconomyLayer.Tag + "server-only: not found (Living Economy changed?): " + LivingEconomyLayer.Some(missing));
        return null;
    }

    public string Install(LeContext context)
    {
        var h = new Harmony(Owner);
        var prefix = new HarmonyMethod(typeof(LeServerOnlyComponent), nameof(SkipOnCoopClient)) { priority = Priority.First };
        foreach (var m in Found) h.Patch(m, prefix: prefix);
        return $"{Found.Count} simulation handler(s) run only on the server during co-op";
    }

    private static bool SkipOnCoopClient()
    {
        if (!LivingEconomyLayer.IsCoopClient) return true;
        Interlocked.Increment(ref _skipped);
        return false;
    }

    /// <summary>For the verification line: how often a player's game skipped a handler the server runs.</summary>
    internal static long Skipped => Interlocked.Read(ref _skipped);
}
