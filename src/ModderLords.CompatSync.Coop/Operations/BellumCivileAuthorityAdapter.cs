using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using ModderLords.Operations;

namespace ModderLords.CompatSync.Coop.Operations;

/// <summary>
/// The first Bellum authority boundary. These are deliberately individual mutation handlers (or mutation-only
/// callees of mixed UI/setup handlers), never whole campaign behaviours. Bellum stays installed on clients so its
/// menus, dialogs, models and presentation callbacks remain available; only the reviewed simulation bodies are
/// prevented from running there.
/// </summary>
public sealed class BellumCivileAuthorityAdapter : ICompatibilityAdapter
{
    private const string HarmonyId = "ModderLords.Compat.BellumCivile.Authority.v1";
    private readonly Harmony harmony = new Harmony(HarmonyId);
    private bool installed;

    // Bellum 1.3.1 / Bannerlord 1.4.8. Every entry is also a target surface in the authority contract. Do not add a
    // method merely because a static scan labels it: mixed presentation handlers belong in MixedHandlerExclusions
    // until their mutation-only callees and the state needed by their UI have both been reviewed.
    //
    // The deliberation ticks fire queued votes into Kingdom.AddDecision. Clients load the server's queue with the save,
    // so every client fired the same AI votes as the server, and Coop turns a client's AddDecision into a request:
    // duplicate decisions in one kingdom. Their RemoveDecision clean-up is refused on clients, so duplicates stayed.
    // The player's own proposals now reach the server through BellumCivileCommandAdapter instead.
    internal static readonly string[] Targets =
    {
        // A relation read, not a handler (RelationRead): patched with RelationReadPrefix instead of the server-only gate.
        "BellumCivile.Behaviors.DynamicRelationBehavior::GetRelationForRead",
        "BellumCivile.Behaviors.CivilWarResolutionBehavior::OnDailyTick",
        "BellumCivile.Behaviors.CivilWarResolutionBehavior::OnHeroKilled",
        "BellumCivile.Behaviors.CivilWarResolutionBehavior::OnHourlyTick",
        "BellumCivile.Behaviors.ClaimFeudBehavior::OnClanChangedKingdom",
        "BellumCivile.Behaviors.ClaimFeudBehavior::OnClanDestroyed",
        "BellumCivile.Behaviors.ClaimFeudBehavior::OnDailyTick",
        "BellumCivile.Behaviors.ClaimFeudWarBehavior::OnDailyTick",
        "BellumCivile.Behaviors.ClientKingdomBehavior::OnDailyTick",
        "BellumCivile.Behaviors.ClientKingdomBehavior::OnMakePeace",
        "BellumCivile.Behaviors.ClientKingdomBehavior::OnWarDeclared",
        "BellumCivile.Behaviors.ComradesInArmsBehavior::OnDailyTickHero",
        "BellumCivile.Behaviors.ComradesInArmsBehavior::OnMapEventEnded",
        "BellumCivile.Behaviors.ComradesInArmsBehavior::OnSettlementOwnerChanged",
        "BellumCivile.Behaviors.CouncilAppointmentDeliberationBehavior::OnDailyTick",
        "BellumCivile.Behaviors.CouncilIncidentBehavior::OnDailyTick",
        "BellumCivile.Behaviors.DynamicMercenaryBandBehavior::OnClanDestroyed",
        "BellumCivile.Behaviors.DynamicMercenaryBandBehavior::OnDailyTick",
        "BellumCivile.Behaviors.DynamicMercenaryBandBehavior::OnDailyTickClan",
        "BellumCivile.Behaviors.DynamicRelationBehavior::OnHeroGainedSkill",
        "BellumCivile.Behaviors.DynamicRelationBehavior::OnPlayerTraitChanged",
        "BellumCivile.Behaviors.DynasticClaimBehavior::OnDailyTick",
        "BellumCivile.Behaviors.DynasticHeirBehavior::OnBeforeHeroesMarried",
        "BellumCivile.Behaviors.DynasticHeirBehavior::OnDailyTick",
        "BellumCivile.Behaviors.DynasticHeirBehavior::OnHourlyTick",
        "BellumCivile.Behaviors.ExiledClanRecoveryBehavior::OnDailyTick",
        "BellumCivile.Behaviors.ExiledClanRecoveryBehavior::OnWeeklyTick",
        "BellumCivile.Behaviors.ExpulsionDeliberationBehavior::OnDailyTick",
        "BellumCivile.Behaviors.FactionManagerBehavior::OnClanChangedKingdom",
        "BellumCivile.Behaviors.FactionManagerBehavior::OnDailyTick",
        "BellumCivile.Behaviors.FactionManagerBehavior::OnKingdomDestroyed",
        "BellumCivile.Behaviors.FactionManagerBehavior::OnMakePeace",
        "BellumCivile.Behaviors.FactionManagerBehavior::OnRulingClanChanged",
        "BellumCivile.Behaviors.FactionManagerBehavior::OnWeeklyTick",
        "BellumCivile.Behaviors.FeudalClaimFabricationBehavior::OnClanChangedKingdom",
        "BellumCivile.Behaviors.FeudalClaimFabricationBehavior::OnDailyTick",
        "BellumCivile.Behaviors.FeudalClaimFabricationBehavior::OnHeroKilled",
        "BellumCivile.Behaviors.FeudalClaimFabricationBehavior::OnSettlementOwnerChanged",
        "BellumCivile.Behaviors.FeudalPoliticalOptionsBehavior::OnClanChangedKingdom",
        "BellumCivile.Behaviors.FeudalPoliticalOptionsBehavior::OnDailyTick",
        "BellumCivile.Behaviors.FeudalTitleBehavior::OnBeforeHeroesMarried",
        "BellumCivile.Behaviors.FeudalTitleBehavior::OnHeroKilled",
        "BellumCivile.Behaviors.FeudalTitleBehavior::OnWeeklyTick",
        "BellumCivile.Behaviors.FiefDeliberationBehavior::OnDailyTick",
        "BellumCivile.Behaviors.FiefDeliberationBehavior::OnHourlyTick",
        "BellumCivile.Behaviors.FiefDeliberationBehavior::OnSettlementOwnerChanged",
        "BellumCivile.Behaviors.ForeignPolicyBehavior::OnWarDeclared",
        "BellumCivile.Behaviors.ForeignTreatyBehavior::OnHourlyTick",
        "BellumCivile.Behaviors.IdeologyBehavior::OnDailyTick",
        "BellumCivile.Behaviors.MercenaryRelationMemoryBehavior::OnClanChangedKingdom",
        "BellumCivile.Behaviors.PartitionSuccessionBehavior::OnDailyTick",
        "BellumCivile.Behaviors.PartitionSuccessionBehavior::OnHourlyTick",
        "BellumCivile.Behaviors.PolicyDeliberationBehavior::OnDailyTick",
        "BellumCivile.Behaviors.PrivyCouncilBehavior::OnDailyTick",
        "BellumCivile.Behaviors.PrivyCouncilBehavior::OnSettlementEntered",
        "BellumCivile.Behaviors.PrivyCouncilBehavior::OnWeeklyTick",
        "BellumCivile.Behaviors.ProxyWarBehavior::OnDailyTick",
        "BellumCivile.Behaviors.RegencyBehavior::OnBeforeHeroKilled",
        "BellumCivile.Behaviors.RegencyBehavior::OnDailyTick",
        "BellumCivile.Behaviors.RetainedTreatyPrisonerBehavior::OnDailyTick",
        "BellumCivile.Behaviors.StrategicMarriageBehavior::OnDailyTickHero",
        "BellumCivile.Behaviors.SuccessionLawBehavior::OnDailyTick",
        "BellumCivile.Behaviors.SuccessionLawBehavior::OnHourlyTick",
        "BellumCivile.Behaviors.WarPeaceRevampBehavior::OnClanChangedKingdom",
        "BellumCivile.Behaviors.WarPeaceRevampBehavior::OnDailyTick",
        "BellumCivile.Behaviors.WarPeaceRevampBehavior::OnPeaceMade",
        "BellumCivile.Behaviors.WarPeaceRevampBehavior::OnTick",
        "BellumCivile.Behaviors.WarScoreBehavior::OnDailyTick",
        "BellumCivile.Behaviors.WarScoreBehavior::OnHeroPrisonerTaken",
        "BellumCivile.Behaviors.WarScoreBehavior::OnMapEventEnded",
        "BellumCivile.Behaviors.WarScoreBehavior::OnSettlementOwnerChanged",
        "BellumCivile.Behaviors.WarScoreBehavior::OnVillageLooted",

        // Mutation-only callees retained from mixed OnSessionLaunched / OnDailyTick handlers.
        "BellumCivile.Behaviors.ArmySummonsConversationBehavior::JoinConversationLordToPlayerArmy",
        "BellumCivile.Behaviors.DynamicMercenaryBandBehavior::ReconcileBands",
        "BellumCivile.Behaviors.ForeignTreatyBehavior::ProcessTributes",
        "BellumCivile.Behaviors.ForeignTreatyBehavior::ResolvePendingParley",
        "BellumCivile.Behaviors.ProxyWarBehavior::CleanUpStuckHighwaymenParties",
        "BellumCivile.Behaviors.ProxyWarBehavior::SeedMissingAiEvaluationSchedules",
    };

    internal static readonly string[] MixedHandlerExclusions =
    {
        "BellumCivile.Behaviors.CouncilIncidentBehavior::OnTick",
        "BellumCivile.Behaviors.DynamicMercenaryBandBehavior::OnTick",
        "BellumCivile.Behaviors.ForeignTreatyBehavior::OnDailyTick",
        "BellumCivile.Behaviors.ForeignTreatyBehavior::OnTick",
        "BellumCivile.UI.Map.WarScoreMapWidgetVM::OnTick",
    };

    private const string RelationRead = "BellumCivile.Behaviors.DynamicRelationBehavior::GetRelationForRead";
    private static long _relationReadsServed;

    private static readonly Dictionary<string, long[]> Counts = new Dictionary<string, long[]>(StringComparer.Ordinal);

    public string Id => "bellum-civile.authority.v1";
    public AdapterReadiness Readiness { get; private set; } = AdapterReadiness.Waiting;
    public string Detail { get; private set; } = "Waiting for pinned Bellum authority targets";

    public bool ValidateTargets(out string reason)
    {
        foreach (var target in Targets)
        {
            var methods = Resolve(target);
            if (methods.Count != 1)
            {
                reason = methods.Count == 0 ? "Pinned Bellum authority target is missing: " + target
                    : "Pinned Bellum authority target is ambiguous: " + target;
                return false;
            }
            if (methods[0].IsAbstract || methods[0].ContainsGenericParameters || methods[0].GetMethodBody() == null)
            {
                reason = "Pinned Bellum authority target is not patchable: " + target;
                return false;
            }
        }
        reason = "";
        return true;
    }

    public void Install()
    {
        if (!ValidateTargets(out var reason))
        {
            Readiness = AdapterReadiness.Failed;
            Detail = reason;
            throw new InvalidOperationException(reason);
        }

        try
        {
            var prefix = new HarmonyMethod(typeof(BellumCivileAuthorityAdapter), nameof(ServerAuthorityPrefix));
            var postfix = new HarmonyMethod(typeof(BellumCivileAuthorityAdapter), nameof(ServerMutationPostfix));
            foreach (var target in Targets.Where(t => t != RelationRead)) harmony.Patch(Resolve(target).Single(), prefix: prefix, postfix: postfix);
            harmony.Patch(Resolve(RelationRead).Single(), prefix: new HarmonyMethod(typeof(BellumCivileAuthorityAdapter), nameof(RelationReadPrefix)));
            installed = true;
            Readiness = AdapterReadiness.Ready;
            Detail = (Targets.Length - 1) + " pinned mutation handler(s) are server-authoritative; players' relation reads return the synced relation";
        }
        catch
        {
            harmony.UnpatchAll(HarmonyId);
            Readiness = AdapterReadiness.Failed;
            Detail = "Bellum authority patches could not be installed";
            throw;
        }
    }

    public void Dispose()
    {
        if (installed) harmony.UnpatchAll(HarmonyId);
        installed = false;
        Readiness = AdapterReadiness.Disabled;
        Detail = "Disabled";
    }

    public static bool ServerAuthorityPrefix(MethodBase __originalMethod)
    {
        var client = !OperationProcessSide.IsServer;
        var key = (__originalMethod.DeclaringType?.Name ?? "?") + "." + __originalMethod.Name;
        lock (Counts)
        {
            if (!Counts.TryGetValue(key, out var count)) Counts[key] = count = new long[2];
            count[client ? 1 : 0]++;
        }
        return !client;
    }

    /// <summary>
    /// A player's game: Bellum's relation read returns the relation as synced. The server works out Bellum's relation (its
    /// drift, memories and politics) and writes it into the game's own relation, which Coop sends to every player; run on
    /// a player's game, the read redid that from a partial copy of Bellum's state, added "prior history" memories of its
    /// own and wrote the relation locally. It also made the server send its relation cache, which outgrew the snapshot.
    /// </summary>
    public static bool RelationReadPrefix(int vanillaStoredValue, ref int __result)
    {
        if (OperationProcessSide.IsServer) return true;
        _relationReadsServed++;
        __result = vanillaStoredValue;
        return false;
    }

    public static void ServerMutationPostfix()
    {
        BellumSnapshotBroadcast.MarkDirty();
    }

    internal static string VerificationSummary()
    {
        lock (Counts)
        {
            if (Counts.Count == 0) return "Bellum authority gates: none has fired yet";
            var ran = Counts.Values.Sum(c => c[0]);
            var skipped = Counts.Values.Sum(c => c[1]);
            return $"Bellum authority gates: {Counts.Count} handler(s), {ran} server run(s), {skipped} client skip(s), {_relationReadsServed} relation read(s) answered from the synced relation";
        }
    }

    private static List<MethodBase> Resolve(string identity)
    {
        var split = identity.Split(new[] { "::" }, 2, StringSplitOptions.None);
        if (split.Length != 2) return new List<MethodBase>();
        var type = AccessTools.TypeByName(split[0]);
        return type == null ? new List<MethodBase>() : AccessTools.GetDeclaredMethods(type)
            .Where(m => m.Name == split[1]).Cast<MethodBase>().ToList();
    }
}
