using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using HarmonyLib;
using ModderLords.Operations;
using TaleWorlds.CampaignSystem;

namespace ModderLords.CompatSync.Coop.Operations;

/// <summary>
/// Read-only foundation for the version-pinned Bellum Civile 1.3.1 adapter. It deliberately reflects only reviewed
/// fields/properties into ID-only DTOs. Simulation gates and player commands are separate later contract targets.
/// </summary>
public sealed class BellumCivileAdapter : ICompatibilityAdapter
{
    internal const string OperationId = "bellum-civile.state.v1";
    private static BellumStateOperation? currentServerOperation;
    private BellumStateOperation? operation;
    private bool clientBound;
    public string Id => "bellum-civile.v1";
    public AdapterReadiness Readiness { get; private set; } = AdapterReadiness.Waiting;
    public string Detail { get; private set; } = "Waiting for Bellum campaign state";

    public bool ValidateTargets(out string reason)
        => BellumStateReader.Validate(out reason) && BellumClientProjection.Validate(out reason);

    public void Install()
    {
        if (!ValidateTargets(out var reason)) { Readiness = AdapterReadiness.Failed; Detail = reason; throw new InvalidOperationException(reason); }
        if (OperationProcessSide.IsServer)
        {
            operation = new BellumStateOperation();
            currentServerOperation = operation;
            global::Coop.Core.Server.Services.ModderLordsCompat.Handlers.OperationServerHandler.Register(operation);
            Readiness = AdapterReadiness.Ready;
            Detail = "Server-authoritative Bellum read model registered";
        }
        else
        {
            var client = global::Coop.Core.Client.Services.ModderLordsCompat.Handlers.OperationClientHandler.Current
                ?? throw new InvalidOperationException("Operation client handler is unavailable");
            client.BindSnapshot(OperationId, () => Campaign.Current != null, BellumStateMirror.Apply, BellumStateMirror.Refresh);
            clientBound = true;
            Readiness = AdapterReadiness.Ready;
            Detail = "Bellum client read model bound";
        }
    }

    public void Dispose()
    {
        if (operation != null) global::Coop.Core.Server.Services.ModderLordsCompat.Handlers.OperationServerHandler.Unregister(operation);
        if (clientBound) global::Coop.Core.Client.Services.ModderLordsCompat.Handlers.OperationClientHandler.Current?.UnbindSnapshot(OperationId);
        if (ReferenceEquals(currentServerOperation, operation)) currentServerOperation = null;
        operation = null; clientBound = false; Readiness = AdapterReadiness.Disabled; Detail = "Disabled";
        BellumSnapshotBroadcast.Reset();
        BellumStateMirror.Clear();
    }

    private sealed class BellumStateOperation : ISharedSnapshotOperation
    {
        private long revision;
        private string canonical = "";
        public string Id => OperationId;
        public int MaxPayloadBytes => 1024;
        public long SnapshotRevision => revision;
        public bool Validate(Actor actor, string payload, out string reason) { reason = "Bellum state is read-only in this validation tier"; return false; }
        public string Execute(Actor actor, string payload) => throw new InvalidOperationException("Bellum state operation is read-only");
        public bool CanReadSnapshot(Actor actor) => !string.IsNullOrEmpty(actor.ControllerId) && Campaign.Current != null;
        public string CaptureSnapshot(Actor actor) => CaptureCurrent();
        public string CaptureSharedSnapshot() => CaptureCurrent();
        internal long RefreshRevision() { CaptureCurrent(); return revision; }
        private string CaptureCurrent()
        {
            var snapshot = BellumStateReader.Capture(revision);
            var content = BellumStateCodec.PoliticalContent(snapshot);
            if (!string.Equals(content, canonical, StringComparison.Ordinal)) { canonical = content; revision++; }
            snapshot.Revision = revision;
            return BellumStateCodec.Serialize(snapshot);
        }
    }

}

/// <summary>Coalesces authoritative Bellum mutations before broadcasting one actor-independent snapshot.</summary>
internal static class BellumSnapshotBroadcast
{
    private static readonly long MinimumInterval = Math.Max(1L, System.Diagnostics.Stopwatch.Frequency * 2L);
    private static int dirty;
    private static long nextCapture;

    public static void MarkDirty()
    {
        try { if (!OperationProcessSide.IsServer) return; }
        catch { return; }
        Interlocked.Exchange(ref dirty, 1);
    }

    public static void Tick()
    {
        if (Volatile.Read(ref dirty) == 0) return;
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (now < Interlocked.Read(ref nextCapture)) return;
        if (Interlocked.CompareExchange(ref dirty, 0, 1) != 1) return;
        Interlocked.Exchange(ref nextCapture, now + MinimumInterval);
        var handler = global::Coop.Core.Server.Services.ModderLordsCompat.Handlers.OperationServerHandler.Current;
        if (handler == null || !handler.BroadcastSharedSnapshot(BellumCivileAdapter.OperationId))
            Interlocked.Exchange(ref dirty, 1);
    }

    public static void Reset()
    {
        Interlocked.Exchange(ref dirty, 0);
        Interlocked.Exchange(ref nextCapture, 0);
    }
}

public static class BellumStateMirror
{
    public static BellumStateSnapshot? Current { get; private set; }
    public static event Action? Changed;
    internal static void Apply(string json)
    {
        var next = BellumStateCodec.Deserialize(json);
        BellumClientProjection.Apply(next);
        Current = next;
    }
    internal static void Refresh() => Changed?.Invoke();
    internal static void Clear() { Current = null; Changed?.Invoke(); }
}

/// <summary>One-shot live validation only. Supplying an absolute output path never activates the adapter.</summary>
internal static class BellumSnapshotProbe
{
    private static bool finished;
    public static void Tick()
    {
        if (finished || Campaign.Current == null) return;
        var output = Environment.GetEnvironmentVariable("MODDERLORDS_BELLUM_SNAPSHOT_PROBE");
        if (string.IsNullOrEmpty(output)) { finished = true; return; }
        finished = true;
        try
        {
            if (!Path.IsPathRooted(output)) throw new InvalidOperationException("Bellum snapshot probe path must be absolute");
            if (!BellumClientProjection.Validate(out var projectionReason)) throw new InvalidOperationException(projectionReason);
            var snapshot = BellumStateReader.Capture(0);
            BellumClientProjection.ValidateMaterialization(snapshot);
            var coverageFixture = BuildProjectionCoverageFixture(snapshot);
            BellumStateCodec.Validate(coverageFixture);
            BellumClientProjection.ValidateMaterialization(coverageFixture);
            var json = BellumStateCodec.Serialize(snapshot);
            File.WriteAllText(output, json, new UTF8Encoding(false));
            Log.Info($"Bellum snapshot probe passed (captured state plus discarded full-family projection fixture): bytes={Encoding.UTF8.GetByteCount(json)} titles={snapshot.Titles.Count} claims={snapshot.Claims.Count} factions={snapshot.Factions.Count} treaties={snapshot.Treaties.Count} tributes={snapshot.Tributes.Count} succession={snapshot.SuccessionLaws.Count} regencies={snapshot.Regencies.Count} offices={snapshot.CouncilOffices.Count} feuds={snapshot.ClaimFeuds.Count} feudWars={snapshot.ClaimFeudWars.Count} mercenaries={snapshot.MercenaryBands.Count} departures={snapshot.MercenaryDepartures.Count} warScores={snapshot.WarScores.Count} warWill={snapshot.WarWill.Count} pressures={snapshot.WarWillPressures.Count} clients={snapshot.ClientKingdoms.Count} services={snapshot.FeudalServices.Count} drifts={snapshot.DeJureDrifts.Count} fabrications={snapshot.ClaimFabrications.Count} foreignWars={snapshot.ForeignWars.Count} pendingForeignWars={snapshot.PendingForeignWars.Count} relations={snapshot.DynamicRelations.Count} memories={snapshot.RelationMemories.Count} dynasties={snapshot.DynasticSuccessions.Count} pendingMarriages={snapshot.PendingCadetMarriages.Count} pendingPartitions={snapshot.PendingPartitions.Count} pendingEscheats={snapshot.PendingGenderLineEscheats.Count}");
        }
        catch (Exception ex) { Log.Warn("Bellum snapshot probe failed: " + ex.GetBaseException()); }
    }

    private static BellumStateSnapshot BuildProjectionCoverageFixture(BellumStateSnapshot captured)
    {
        // The round trip gives this diagnostic a deep copy. It must never mutate either the live campaign or the
        // authentic snapshot written by the probe.
        var state = BellumStateCodec.Deserialize(BellumStateCodec.Serialize(captured));
        const string prefix = "modderlords-projection-fixture";
        var kingdomIds = state.SuccessionLaws.Select(x => x.KingdomId)
            .Concat(state.Factions.Select(x => x.ParentKingdomId))
            .Concat(state.WarScores.SelectMany(x => new[] { x.AttackerKingdomId, x.DefenderKingdomId }))
            .Concat(state.ForeignWars.SelectMany(x => new[] { x.AttackerKingdomId, x.DefenderKingdomId }))
            .Concat(state.DynasticSuccessions.Select(x => x.KingdomId))
            .Where(x => !string.IsNullOrEmpty(x)).Distinct(StringComparer.Ordinal).ToList();
        var clanIds = state.Factions.SelectMany(x => x.MemberClanIds.Concat(new[] { x.LeaderClanId }))
            .Concat(state.Titles.SelectMany(x => new[] { x.DeJureHolderClanId, x.DeFactoHolderClanId }))
            .Concat(state.Claims.Select(x => x.ClaimantClanId))
            .Concat(state.WarWill.Select(x => x.ClanId))
            .Where(x => !string.IsNullOrEmpty(x)).Distinct(StringComparer.Ordinal).ToList();
        if (kingdomIds.Count == 0 || clanIds.Count == 0)
            throw new InvalidOperationException("The Bellum projection coverage fixture requires at least one captured kingdom and clan");

        var kingdom = kingdomIds[0];
        var otherKingdom = kingdomIds.Count > 1 ? kingdomIds[1] : kingdom;
        var clan = clanIds[0];
        var otherClan = clanIds.Count > 1 ? clanIds[1] : clan;
        var hero = state.Claims.SelectMany(x => new[] { x.SourceHeroId, x.CarrierHeroId })
            .Concat(state.DynasticSuccessions.SelectMany(x => new[] { x.HeirHeroId, x.ClaimCarrierHeroId }))
            .FirstOrDefault(x => !string.IsNullOrEmpty(x)) ?? prefix + "-hero";
        var title = state.Titles.Select(x => x.TitleId).FirstOrDefault(x => !string.IsNullOrEmpty(x)) ?? prefix + "-title";
        var otherTitle = state.Titles.Select(x => x.TitleId).FirstOrDefault(x => !string.IsNullOrEmpty(x) && x != title) ?? title;
        var settlement = state.Titles.Select(x => x.CapitalSettlementId).FirstOrDefault(x => !string.IsNullOrEmpty(x)) ?? prefix + "-settlement";
        var now = state.CapturedDay;

        if (state.Titles.Count == 0) state.Titles.Add(new BellumTitleState { TitleId = title, Name = "Projection fixture", DeJureHolderClanId = clan, DeFactoHolderClanId = clan, CapitalSettlementId = settlement, AssociatedKingdomId = kingdom, CreatedDay = now, LastSyncedDay = now, IsActive = true });
        if (state.Claims.Count == 0) state.Claims.Add(new BellumClaimState { ClaimId = prefix + "-claim", ClaimantClanId = clan, TargetTitleId = title, Source = "projection-fixture", SourceHeroId = hero, OriginClanId = clan, CreatedDay = now, ExpiresDay = now + 1, IsActive = true, CarrierHeroId = hero });
        if (state.Factions.Count == 0) state.Factions.Add(new BellumFactionState { FactionKey = prefix + "-faction", Name = "Projection fixture", ParentKingdomId = kingdom, LeaderClanId = clan, MemberClanIds = new List<string> { clan }, LoyalClanId = clan, CreationDay = now });
        if (state.Treaties.Count == 0) state.Treaties.Add(new BellumTreatyState { ProposalId = prefix + "-treaty", WarKey = prefix + "-war", WinnerKingdomId = kingdom, LoserKingdomId = otherKingdom, DrafterKingdomId = kingdom, CreatedDay = now, Terms = new List<BellumTreatyTermState>() });
        if (state.Treaties.All(x => x.Terms.Count == 0)) state.Treaties[0].Terms.Add(new BellumTreatyTermState { FromKingdomId = kingdom, ToKingdomId = otherKingdom, SettlementId = settlement, HeroId = hero, ClanId = clan, TitleId = title, SecondaryHeroId = hero, ThirdKingdomId = kingdom });
        if (state.Tributes.Count == 0) state.Tributes.Add(new BellumTributeState { TributeKey = prefix + "-tribute", PayerKingdomId = kingdom, RecipientKingdomId = otherKingdom, DailyGold = 1, RemainingDays = 1 });
        if (state.SuccessionLaws.Count == 0) state.SuccessionLaws.Add(new BellumSuccessionLawState { KingdomId = kingdom });
        if (state.Regencies.Count == 0) state.Regencies.Add(new BellumRegencyState { ClanId = clan, WardHeroId = hero, RegentHeroId = hero, PredecessorHeroId = hero, StartedDay = now });
        if (state.CouncilOffices.Count == 0) state.CouncilOffices.Add(new BellumCouncilOfficeState { RecordId = prefix + "-office", KingdomId = kingdom, HolderClanId = clan, LastReason = "projection-fixture", AppointedDay = now, LastUpdatedDay = now });
        if (state.ClaimFeuds.Count == 0) state.ClaimFeuds.Add(new BellumClaimFeudState { RecordId = prefix + "-feud", ParentKingdomId = kingdom, ClaimantClanId = clan, HolderClanId = otherClan, TargetTitleId = title, SourceClaimId = prefix + "-claim", DebugReason = "projection-fixture", StartedDay = now, LastTickDay = now, ClaimantSupporterClanIds = new List<string> { clan }, HolderSupporterClanIds = new List<string> { otherClan }, PauseReason = "" });
        if (state.MercenaryBands.Count == 0) state.MercenaryBands.Add(new BellumMercenaryBandState { ClanId = clan, FounderHeroId = hero, SourceClanId = otherClan, CultureId = prefix + "-culture", HomeSettlementId = settlement, CreatedDay = now });
        if (state.WarScores.Count == 0) state.WarScores.Add(new BellumWarScoreState { WarKey = prefix + "-score", AttackerKingdomId = kingdom, DefenderKingdomId = otherKingdom, StartedDay = now, IsActive = true });
        if (state.WarScores.All(x => x.FiefSnapshots.Count == 0)) state.WarScores[0].FiefSnapshots.Add(new BellumWarScoreFiefState { SettlementId = settlement, OwnerKingdomId = kingdom, OwnerClanId = clan, IsTown = true });
        if (state.WarScores.All(x => x.Events.Count == 0)) state.WarScores[0].Events.Add(new BellumWarScoreEventState { EventId = prefix + "-score-event", ActorKingdomId = kingdom, TargetKingdomId = otherKingdom, SettlementId = settlement, HeroId = hero, DebugText = "projection-fixture", Day = now });
        if (state.WarWill.Count == 0) state.WarWill.Add(new BellumWarWillState { ClanId = clan, PreferredTargetKingdomId = otherKingdom });
        if (state.WarWillPressures.Count == 0) state.WarWillPressures.Add(new BellumWarWillPressureState { RecordId = prefix + "-pressure", ClanId = clan, TargetKingdomId = otherKingdom, ConflictKey = prefix + "-war", Reason = "projection-fixture", ContextSettlementId = settlement, ContextTitleId = title, ContextHeroId = hero, CreatedDay = now, ExpiresDay = now + 1, IsActive = true });
        if (state.ClientKingdoms.Count == 0) state.ClientKingdoms.Add(new BellumClientKingdomState { ClientKingdomId = kingdom, SuzerainKingdomId = otherKingdom, StartedDay = now, LiberationCooldownUntilDay = now + 1 });
        if (state.FeudalServices.Count == 0) state.FeudalServices.Add(new BellumFeudalServiceState { RecordId = prefix + "-service", ChildTitleId = title, ParentTitleId = otherTitle, ChangedByClanId = clan, Reason = "projection-fixture", LastChangedDay = now });
        if (state.DeJureDrifts.Count == 0) state.DeJureDrifts.Add(new BellumDeJureDriftState { TitleId = title, OriginalParentTitleId = otherTitle, TargetParentTitleId = otherTitle, TargetKingdomId = kingdom, StartedDay = now, LastEvaluatedDay = now, IsActive = true });
        if (state.ClaimFabrications.Count == 0) state.ClaimFabrications.Add(new BellumClaimFabricationState { FabricationId = prefix + "-fabrication", FabricatorHeroId = hero, FabricatorClanId = clan, TargetTitleId = title, StartedDay = now, IsActive = true });
        if (state.ForeignWars.Count == 0) state.ForeignWars.Add(ForeignWar(prefix + "-foreign-war", kingdom, otherKingdom, clan, title, now));
        if (state.PendingForeignWars.Count == 0) state.PendingForeignWars.Add(ForeignWar(prefix + "-pending-foreign-war", kingdom, otherKingdom, clan, title, now));

        EnsureTimer(state, "protected-rebel-kingdom", kingdom, now);
        EnsureTimer(state, "pacified-clan", clan, now);
        EnsureTimer(state, "settlement-sync-clan", clan, now);
        if (!state.ObjectIntegers.Any(x => x.Scope == "kingdom-controversy")) state.ObjectIntegers.Add(new BellumObjectIntegerState { Scope = "kingdom-controversy", ObjectId = kingdom, Value = 1 });
        if (state.PendingSettlementRefreshClanIds.Count == 0) state.PendingSettlementRefreshClanIds.Add(clan);
        if (state.ClaimFeudWars.Count == 0) state.ClaimFeudWars.Add(new BellumClaimFeudWarState { WarId = prefix + "-feud-war", FeudRecordId = prefix + "-feud", ParentKingdomId = kingdom, ClaimantKingdomId = kingdom, HolderKingdomId = otherKingdom, TargetTitleId = title, ClaimantLeaderClanId = clan, HolderLeaderClanId = otherClan, ClaimantClanIds = clan, HolderClanIds = otherClan, InfluenceSnapshot = "", FiefSnapshot = "", StartedDay = now, IsActive = true, PendingResolutionReason = "" });
        if (state.MercenaryDepartures.Count == 0) state.MercenaryDepartures.Add(new BellumMercenaryDepartureState { HeroId = hero, QueuedDay = now });
        if (state.DynamicRelations.Count == 0) state.DynamicRelations.Add(new BellumDynamicRelationState { PairKey = prefix + "-relation", LastUpdateDay = now });
        if (state.RelationMaterializedValues.Count == 0) state.RelationMaterializedValues.Add(new BellumRelationMaterializedValueState { PairKey = prefix + "-relation" });
        if (state.RelationMemories.Count == 0) state.RelationMemories.Add(new BellumRelationMemoryState { MemoryKey = prefix + "-memory", FirstId = clan, SecondId = otherClan, SourceId = prefix, ContextText = "projection-fixture", StartDay = now, ExpiryDay = now + 1 });
        if (state.DynasticSuccessions.Count == 0) state.DynasticSuccessions.Add(new BellumDynasticSuccessionState { KingdomId = kingdom, RightfulDynastyClanId = clan, HeirHeroId = hero, ClaimCarrierClanId = clan, ClaimCarrierHeroId = hero, Source = "projection-fixture", LastUpdatedDay = now });
        if (state.PendingCadetMarriages.Count == 0) state.PendingCadetMarriages.Add(new BellumPendingCadetMarriageState { HeiressId = hero, SpouseId = hero, OriginKingdomId = kingdom, DynastyClanId = clan, BrideClanId = clan, MarriedClanId = clan, ReadyDay = now, ExpiresDay = now + 1 });
        if (state.PendingFabricationOutcomes.Count == 0) state.PendingFabricationOutcomes.Add(new BellumPendingFabricationOutcomeState { Sequence = 1, Title = "Projection fixture", Body = "Projection fixture" });
        if (state.PendingUsurpations.Count == 0) state.PendingUsurpations.Add(new BellumPendingUsurpationState { TitleId = title, ClaimantClanId = clan });
        if (state.PendingTreatyRebelResolutions.Count == 0) state.PendingTreatyRebelResolutions.Add(new BellumPendingTreatyRebelResolutionState { ProposalId = prefix + "-treaty", RebelKingdomId = kingdom, ParentKingdomId = kingdom, BeneficiaryKingdomId = otherKingdom });
        if (state.PendingPartitions.Count == 0) state.PendingPartitions.Add(new BellumPendingPartitionState { DeadLeaderId = hero, ParentClanId = clan, KingdomId = kingdom, FiefIds = settlement, HeirIds = hero, ReadyDay = now, TitleIds = title, PrimarySovereignTitleId = title });
        if (state.PendingGenderLineEscheats.Count == 0) state.PendingGenderLineEscheats.Add(new BellumPendingGenderLineEscheatState { TriggerHeroId = hero, ClanId = clan, KingdomId = kingdom, ReadyDay = now });
        return state;
    }

    private static BellumForeignWarState ForeignWar(string key, string attacker, string defender, string clan, string title, double day)
        => new BellumForeignWarState { WarKey = key, AttackerKingdomId = attacker, DefenderKingdomId = defender, SponsorFactionType = "projection-fixture", SponsorClanId = clan, TargetTitleIds = new List<string> { title }, StartedDay = day, ContextCreatedDay = day, PublicDeclarationMotiveSubject = "Projection fixture" };

    private static void EnsureTimer(BellumStateSnapshot state, string scope, string objectId, double day)
    {
        if (!state.ObjectTimers.Any(x => x.Scope == scope)) state.ObjectTimers.Add(new BellumObjectTimerState { Scope = scope, ObjectId = objectId, Day = day });
    }
}

internal static class BellumStateReader
{
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly string[] RequiredTypes =
    {
        "BellumCivile.Behaviors.FeudalTitleBehavior", "BellumCivile.Behaviors.FactionManagerBehavior",
        "BellumCivile.Behaviors.ForeignTreatyBehavior", "BellumCivile.Behaviors.RegencyBehavior",
        "BellumCivile.Behaviors.SuccessionLawBehavior", "BellumCivile.Behaviors.PrivyCouncilBehavior",
        "BellumCivile.Behaviors.ClaimFeudBehavior", "BellumCivile.Behaviors.DynamicMercenaryBandBehavior",
        "BellumCivile.Behaviors.WarScoreBehavior", "BellumCivile.Behaviors.WarPeaceRevampBehavior",
        "BellumCivile.Behaviors.ClientKingdomBehavior", "BellumCivile.Behaviors.FeudalServiceBehavior",
        "BellumCivile.Behaviors.FeudalDeJureDriftBehavior", "BellumCivile.Behaviors.FeudalClaimFabricationBehavior",
        "BellumCivile.Behaviors.ForeignPolicyBehavior",
        "BellumCivile.Behaviors.CivilWarInterventionBehavior", "BellumCivile.Behaviors.ControversyBehavior",
        "BellumCivile.Behaviors.ClaimFeudWarBehavior", "BellumCivile.Behaviors.DynamicRelationBehavior",
        "BellumCivile.Behaviors.DynasticHeirBehavior", "BellumCivile.Behaviors.FeudalTitleUsurpationBehavior",
        "BellumCivile.Behaviors.PartitionSuccessionBehavior",
        "BellumCivile.FeudalTitleRecord", "BellumCivile.FeudalClaimRecord", "BellumCivile.FactionObject",
        "BellumCivile.TreatyProposalRecord", "BellumCivile.TreatyTermRecord", "BellumCivile.ActiveTreatyTributeRecord",
        "BellumCivile.KingdomSuccessionLawRecord", "BellumCivile.RegencyRecord", "BellumCivile.PrivyCouncilOfficeRecord",
        "BellumCivile.ClaimFeudRecord", "BellumCivile.DynamicMercenaryBandRecord", "BellumCivile.WarScoreRecord",
        "BellumCivile.WarScoreFiefSnapshotRecord", "BellumCivile.WarScoreEventRecord",
        "BellumCivile.WarWillPressureRecord", "BellumCivile.ClientKingdomRecord", "BellumCivile.FeudalServiceRecord",
        "BellumCivile.FeudalDeJureDriftRecord", "BellumCivile.FeudalClaimFabricationRecord", "BellumCivile.ActiveForeignWarRecord",
        "BellumCivile.ClaimFeudWarRecord", "BellumCivile.DynamicMercenaryDepartureIntent", "BellumCivile.DynamicRelationRecord",
        "BellumCivile.RelationMemoryRecord", "BellumCivile.DynasticSuccessionStateRecord", "BellumCivile.PendingCadetMarriageRecord",
        "BellumCivile.Behaviors.FeudalClaimFabricationBehavior+PendingFabricationOutcome", "BellumCivile.FeudalUsurpationCandidateRecord",
        "BellumCivile.PendingTreatyRebelResolutionRecord", "BellumCivile.PendingPartitionSuccessionRecord", "BellumCivile.PendingGenderLineEscheatRecord",
    };

    public static bool Validate(out string reason)
    {
        foreach (var name in RequiredTypes) if (AccessTools.TypeByName(name) == null) { reason = "Pinned Bellum type is missing: " + name; return false; }
        foreach (var check in new[]
        {
            ("BellumCivile.Behaviors.FeudalTitleBehavior", "_titlesById"), ("BellumCivile.Behaviors.FeudalTitleBehavior", "_claims"),
            ("BellumCivile.Behaviors.FactionManagerBehavior", "_activeFactions"), ("BellumCivile.Behaviors.ForeignTreatyBehavior", "_proposals"),
            ("BellumCivile.Behaviors.ForeignTreatyBehavior", "_activeTributes"), ("BellumCivile.Behaviors.RegencyBehavior", "_regencies"),
            ("BellumCivile.Behaviors.SuccessionLawBehavior", "_realmLaws"), ("BellumCivile.Behaviors.PrivyCouncilBehavior", "_officeRecords"),
            ("BellumCivile.Behaviors.ClaimFeudBehavior", "_feuds"), ("BellumCivile.Behaviors.DynamicMercenaryBandBehavior", "_bands"),
            ("BellumCivile.Behaviors.WarScoreBehavior", "_wars"), ("BellumCivile.Behaviors.WarPeaceRevampBehavior", "_warWillByClanId"),
            ("BellumCivile.Behaviors.WarPeaceRevampBehavior", "_pressureRecords"), ("BellumCivile.Behaviors.ClientKingdomBehavior", "_clients"),
            ("BellumCivile.Behaviors.FeudalServiceBehavior", "_contractsByTitlePair"), ("BellumCivile.Behaviors.FeudalDeJureDriftBehavior", "_drifts"),
            ("BellumCivile.Behaviors.FeudalClaimFabricationBehavior", "_fabrications"), ("BellumCivile.Behaviors.ForeignPolicyBehavior", "_activeWars"),
            ("BellumCivile.Behaviors.ForeignPolicyBehavior", "_pendingWarContexts"),
            ("BellumCivile.Behaviors.CivilWarInterventionBehavior", "_protectedRebelKingdoms"),
            ("BellumCivile.Behaviors.FactionManagerBehavior", "_pacifiedClans"), ("BellumCivile.Behaviors.FactionManagerBehavior", "_settlementSyncStamps"),
            ("BellumCivile.Behaviors.FactionManagerBehavior", "_pendingSettlementRefreshClans"), ("BellumCivile.Behaviors.ControversyBehavior", "_trackedControversyById"),
            ("BellumCivile.Behaviors.ClaimFeudWarBehavior", "_wars"), ("BellumCivile.Behaviors.DynamicMercenaryBandBehavior", "_playerDepartureIntents"),
            ("BellumCivile.Behaviors.DynamicRelationBehavior", "_serializedRelationRecords"), ("BellumCivile.Behaviors.DynamicRelationBehavior", "_relationMemories"),
            ("BellumCivile.Behaviors.DynamicRelationBehavior", "_memorySchemaVersion"), ("BellumCivile.Behaviors.DynamicRelationBehavior", "_appliedMemoryDurationMultiplier"),
            ("BellumCivile.Behaviors.DynamicRelationBehavior", "_materializedPairValues"),
            ("BellumCivile.Behaviors.DynasticHeirBehavior", "_dynasticStates"), ("BellumCivile.Behaviors.DynasticHeirBehavior", "_pendingCadetMarriages"),
            ("BellumCivile.Behaviors.FeudalClaimFabricationBehavior", "_pendingPlayerOutcomes"), ("BellumCivile.Behaviors.FeudalTitleUsurpationBehavior", "_pendingCandidates"),
            ("BellumCivile.Behaviors.ForeignTreatyBehavior", "_pendingRebelResolutions"), ("BellumCivile.Behaviors.PartitionSuccessionBehavior", "_pendingPartitions"),
            ("BellumCivile.Behaviors.SuccessionLawBehavior", "_pendingGenderLineEscheats"),
        })
        {
            var type = AccessTools.TypeByName(check.Item1);
            if (type?.GetField(check.Item2, Any) == null) { reason = "Pinned Bellum state member is missing: " + check.Item1 + "." + check.Item2; return false; }
        }
        reason = ""; return true;
    }

    public static BellumStateSnapshot Capture(long revision)
    {
        if (Campaign.Current == null) throw new InvalidOperationException("Campaign is unavailable");
        if (!Validate(out var reason)) throw new InvalidOperationException(reason);
        var state = new BellumStateSnapshot { Revision = revision, CapturedDay = CampaignTime.Now.ToDays };

        var titles = Behavior("BellumCivile.Behaviors.FeudalTitleBehavior");
        foreach (var item in Items(Field(titles, "_titlesById"))) state.Titles.Add(new BellumTitleState
        {
            TitleId = S(item, "TitleId"), Name = S(item, "Name"), TitleType = I(item, "TitleType"),
            DeJureHolderClanId = S(item, "DeJureHolderClanId"), DeFactoHolderClanId = S(item, "DeFactoHolderClanId"),
            ParentTitleId = S(item, "ParentTitleId"), DeFactoParentTitleId = S(item, "DeFactoParentTitleId"),
            CapitalSettlementId = S(item, "CapitalSettlementId"), AssociatedKingdomId = S(item, "AssociatedKingdomId"),
            CreatedDay = D(item, "CreatedDay"), LastSyncedDay = D(item, "LastSyncedDay"), IsActive = B(item, "IsActive"),
            FallbackCultureRef = S(item, "FallbackCultureRef"), IsDeliberatelyDissolved = B(item, "IsDeliberatelyDissolved"),
        });
        foreach (var item in Items(Field(titles, "_claims"))) state.Claims.Add(new BellumClaimState
        {
            ClaimId = S(item, "ClaimId"), ClaimantClanId = S(item, "ClaimantClanId"), TargetTitleId = S(item, "TargetTitleId"),
            Strength = I(item, "Strength"), Source = S(item, "Source"), SourceHeroId = S(item, "SourceHeroId"), OriginClanId = S(item, "OriginClanId"),
            CreatedDay = D(item, "CreatedDay"), ExpiresDay = D(item, "ExpiresDay"), IsActive = B(item, "IsActive"),
            GenerationDepth = I(item, "GenerationDepth"), CarrierHeroId = S(item, "CarrierHeroId"),
        });

        foreach (var item in Items(Field(Behavior("BellumCivile.Behaviors.FactionManagerBehavior"), "_activeFactions")))
        {
            var parent = ObjectId(V(item, "ParentKingdom")); var type = I(item, "Type"); var created = Days(V(item, "CreationDate"));
            var faction = new BellumFactionState
            {
                FactionKey = parent + ":" + type.ToString(CultureInfo.InvariantCulture) + ":" + created.ToString("R", CultureInfo.InvariantCulture),
                Name = S(item, "Name"), ParentKingdomId = parent, LeaderClanId = ObjectId(V(item, "Leader")),
                MemberClanIds = Items(V(item, "Members")).Select(ObjectId).Where(x => x.Length > 0).Distinct(StringComparer.Ordinal).ToList(),
                Type = type, Discontent = D(item, "Discontent"), PeakDiscontent = D(item, "PeakDiscontent"), Mood = D(item, "Mood"),
                LoyalClanId = ObjectId(V(item, "LoyalClan")), CreationDay = created,
                RebelKingdomId = ObjectId(item.GetType().GetMethod("GetRebelKingdom", Any)?.Invoke(item, null)),
                RebelKingdomStringId = S(item, "_rebelKingdomStringId"), IsUltimatumPending = B(item, "IsUltimatumPending"),
                IsGrandCoalition = B(item, "IsGrandCoalition"), GrandCoalitionSourceIdeology = I(item, "_grandCoalitionSourceIdeology"),
                HasGrandCoalitionSourceIdeology = B(item, "_hasGrandCoalitionSourceIdeology"), UltimatumPending = B(item, "_ultimatumPending"),
                UltimatumResolved = B(item, "_ultimatumResolved"), SolidarityRecruitmentApplied = B(item, "_solidarityRecruitmentApplied"),
                RebellionCreationStarted = B(item, "_rebellionCreationStarted"), RebellionCreationCompleted = B(item, "_rebellionCreationCompleted"),
            };
            var fiefClans = Items(V(item, "_civilWarStartFiefClans")).ToList();
            var fiefCounts = Items(V(item, "_civilWarStartFiefCounts")).ToList();
            for (var index = 0; index < Math.Min(fiefClans.Count, fiefCounts.Count); index++)
            {
                var clanId = ObjectId(fiefClans[index]);
                if (clanId.Length > 0) faction.CivilWarStartFiefs.Add(new BellumClanIntState { ClanId = clanId, Value = Convert.ToInt32(fiefCounts[index], CultureInfo.InvariantCulture) });
            }
            var influenceClans = Items(V(item, "_civilWarStartInfluenceClans")).ToList();
            var influenceValues = Items(V(item, "_civilWarStartInfluenceValues")).ToList();
            for (var index = 0; index < Math.Min(influenceClans.Count, influenceValues.Count); index++)
            {
                var clanId = ObjectId(influenceClans[index]);
                if (clanId.Length > 0) faction.CivilWarStartInfluence.Add(new BellumClanNumberState { ClanId = clanId, Value = Convert.ToDouble(influenceValues[index], CultureInfo.InvariantCulture) });
            }
            state.Factions.Add(faction);
        }

        var treatyBehavior = Behavior("BellumCivile.Behaviors.ForeignTreatyBehavior");
        foreach (var item in Items(Field(treatyBehavior, "_proposals")))
        {
            var treaty = new BellumTreatyState
            {
                ProposalId = S(item, "ProposalId"), WarKey = S(item, "WarKey"), WinnerKingdomId = S(item, "WinnerKingdomId"), LoserKingdomId = S(item, "LoserKingdomId"),
                CreatedDay = D(item, "CreatedDay"), WarScoreBudget = I(item, "WarScoreBudget"), IsForced = B(item, "IsForced"), State = I(item, "State"),
                UsedWarScore = I(item, "UsedWarScore"), WinnerSupport = D(item, "WinnerSupport"), LoserSupport = D(item, "LoserSupport"),
                WinnerOverrideCost = I(item, "WinnerOverrideCost"), LoserOverrideCost = I(item, "LoserOverrideCost"), ResolutionNote = S(item, "ResolutionNote"),
                DraftRevision = I(item, "DraftRevision"), PlayerVoteStance = I(item, "PlayerVoteStance"), PlayerInfluenceCommitment = I(item, "PlayerInfluenceCommitment"),
                PlayerInfluenceSpent = B(item, "PlayerInfluenceSpent"), PlayerVoteSubmitted = B(item, "PlayerVoteSubmitted"), DrafterKingdomId = S(item, "DrafterKingdomId"),
            };
            foreach (var term in Items(V(item, "Terms"))) treaty.Terms.Add(new BellumTreatyTermState
            {
                Type = I(term, "Type"), WarScoreCost = I(term, "WarScoreCost"), SettlementId = S(term, "SettlementId"), GoldAmount = I(term, "GoldAmount"),
                DailyGold = I(term, "DailyGold"), DurationDays = I(term, "DurationDays"), FromKingdomId = S(term, "FromKingdomId"), ToKingdomId = S(term, "ToKingdomId"),
                WasOccupiedAtDrafting = B(term, "WasOccupiedAtDrafting"), HeroId = S(term, "HeroId"), ClanId = S(term, "ClanId"), TitleId = S(term, "TitleId"),
                SecondaryHeroId = S(term, "SecondaryHeroId"), ThirdKingdomId = S(term, "ThirdKingdomId"), WasVoluntaryOffering = B(term, "WasVoluntaryOffering"),
            });
            state.Treaties.Add(treaty);
        }
        foreach (var item in Items(Field(treatyBehavior, "_activeTributes")))
        {
            var payer = S(item, "PayerKingdomId"); var recipient = S(item, "RecipientKingdomId");
            state.Tributes.Add(new BellumTributeState { TributeKey = payer + "|" + recipient, PayerKingdomId = payer, RecipientKingdomId = recipient,
                DailyGold = I(item, "DailyGold"), RemainingDays = I(item, "RemainingDays") });
        }

        foreach (var item in Items(Field(Behavior("BellumCivile.Behaviors.SuccessionLawBehavior"), "_realmLaws"))) state.SuccessionLaws.Add(new BellumSuccessionLawState
        { KingdomId = S(item, "KingdomId"), GenderLaw = I(item, "GenderLaw"), SuccessionLaw = I(item, "SuccessionLaw") });
        foreach (var item in Items(Field(Behavior("BellumCivile.Behaviors.RegencyBehavior"), "_regencies"))) state.Regencies.Add(new BellumRegencyState
        { ClanId = S(item, "ClanId"), WardHeroId = S(item, "WardHeroId"), RegentHeroId = S(item, "RegentHeroId"), PredecessorHeroId = S(item, "PredecessorHeroId"), StartedDay = Days(V(item, "StartedAt")), RegentWasGenerated = B(item, "RegentWasGenerated") });
        foreach (var item in Items(Field(Behavior("BellumCivile.Behaviors.PrivyCouncilBehavior"), "_officeRecords"))) state.CouncilOffices.Add(new BellumCouncilOfficeState
        {
            RecordId = S(item, "RecordId"), KingdomId = S(item, "KingdomId"), Office = I(item, "Office"), HolderClanId = S(item, "HolderClanId"),
            Controversy = D(item, "Controversy"), VacancyStartedDay = D(item, "VacancyStartedDay"), AppointedDay = D(item, "AppointedDay"), LastUpdatedDay = D(item, "LastUpdatedDay"),
            IsInitialized = B(item, "IsInitialized"), LegacyControversyMigrated = B(item, "LegacyControversyMigrated"), LastReason = S(item, "LastReason"), LastChange = D(item, "LastChange"),
            LastRealmMetric = D(item, "LastRealmMetric"), AssignmentId = S(item, "AssignmentId"), LastAssignmentChangedDay = D(item, "LastAssignmentChangedDay"), NextAssignmentReviewDay = D(item, "NextAssignmentReviewDay"),
        });
        foreach (var item in Items(Field(Behavior("BellumCivile.Behaviors.ClaimFeudBehavior"), "_feuds"))) state.ClaimFeuds.Add(new BellumClaimFeudState
        {
            RecordId = S(item, "RecordId"), ParentKingdomId = S(item, "ParentKingdomId"), ClaimantClanId = S(item, "ClaimantClanId"), HolderClanId = S(item, "HolderClanId"),
            TargetTitleId = S(item, "TargetTitleId"), ClaimStrength = I(item, "ClaimStrength"), State = I(item, "State"), Pressure = D(item, "Pressure"), StartedDay = D(item, "StartedDay"),
            LastTickDay = D(item, "LastTickDay"), CooldownUntilDay = D(item, "CooldownUntilDay"), SourceClaimId = S(item, "SourceClaimId"), DebugReason = S(item, "DebugReason"), Judgment = I(item, "Judgment"),
            ClaimantResponse = I(item, "ClaimantResponse"), HolderResponse = I(item, "HolderResponse"), RulingDay = D(item, "RulingDay"), ClaimantSidePower = D(item, "ClaimantSidePower"),
            HolderSidePower = D(item, "HolderSidePower"), ClaimantSupporterClanIds = SplitIds(S(item, "ClaimantSupporterIds")), HolderSupporterClanIds = SplitIds(S(item, "HolderSupporterIds")),
            HasPassed33 = B(item, "HasPassed33"), HasPassed66 = B(item, "HasPassed66"), ResumeState = I(item, "ResumeState"), PauseReason = S(item, "PauseReason"), PausedDay = D(item, "PausedDay"),
        });
        foreach (var item in Items(Field(Behavior("BellumCivile.Behaviors.DynamicMercenaryBandBehavior"), "_bands"))) state.MercenaryBands.Add(new BellumMercenaryBandState
        { ClanId = S(item, "ClanId"), FounderHeroId = S(item, "FounderHeroId"), SourceClanId = S(item, "SourceClanId"), CultureId = S(item, "CultureId"), HomeSettlementId = S(item, "HomeSettlementId"), CreatedDay = Days(V(item, "CreatedAt")) });

        foreach (var item in Items(Field(Behavior("BellumCivile.Behaviors.WarScoreBehavior"), "_wars")))
        {
            var war = new BellumWarScoreState
            {
            WarKey = S(item, "WarKey"), AttackerKingdomId = S(item, "AttackerKingdomId"), DefenderKingdomId = S(item, "DefenderKingdomId"),
            StartedDay = D(item, "StartedDay"), EndedDay = D(item, "EndedDay"), Score = D(item, "Score"), IsActive = B(item, "IsActive"),
            OccupationScore = D(item, "OccupationScore"), BattleScore = D(item, "BattleScore"), RaidScore = D(item, "RaidScore"), PrisonerScore = D(item, "PrisonerScore"),
            TickingScore = D(item, "TickingScore"), LastTickDay = D(item, "LastTickDay"), ConflictType = I(item, "ConflictType"), ContextId = S(item, "ContextId"),
            ResolutionPending = B(item, "ResolutionPending"), ObjectiveScore = D(item, "ObjectiveScore"), NextWhitePeaceCheckDay = D(item, "NextWhitePeaceCheckDay"),
            WhitePeaceOfferPending = B(item, "WhitePeaceOfferPending"), ParleyPending = B(item, "ParleyPending"), ParleyForced = B(item, "ParleyForced"),
            ParleyOpenedDay = D(item, "ParleyOpenedDay"), TerminalResolutionQueued = B(item, "TerminalResolutionQueued"),
            TerminalResolutionReason = S(item, "TerminalResolutionReason"), LandlessPressureScore = D(item, "LandlessPressureScore"),
            };
            foreach (var fief in Items(V(item, "FiefSnapshots"))) war.FiefSnapshots.Add(new BellumWarScoreFiefState
            {
                SettlementId = S(fief, "SettlementId"), OwnerKingdomId = S(fief, "OwnerKingdomId"), OwnerClanId = S(fief, "OwnerClanId"),
                IsTown = B(fief, "IsTown"), IsCastle = B(fief, "IsCastle"),
            });
            foreach (var scoreEvent in Items(V(item, "Events"))) war.Events.Add(new BellumWarScoreEventState
            {
                EventId = S(scoreEvent, "EventId"), EventType = I(scoreEvent, "EventType"), Day = D(scoreEvent, "Day"), Delta = D(scoreEvent, "Delta"),
                ScoreAfter = D(scoreEvent, "ScoreAfter"), ActorKingdomId = S(scoreEvent, "ActorKingdomId"), TargetKingdomId = S(scoreEvent, "TargetKingdomId"),
                SettlementId = S(scoreEvent, "SettlementId"), HeroId = S(scoreEvent, "HeroId"), DebugText = S(scoreEvent, "DebugText"),
            });
            state.WarScores.Add(war);
        }

        var warWill = Behavior("BellumCivile.Behaviors.WarPeaceRevampBehavior");
        var nextEvaluation = Map(Field(warWill, "_nextEvaluationDayByClanId"));
        var preferredTarget = Map(Field(warWill, "_preferredTargetByClanId"));
        foreach (var entry in Map(Field(warWill, "_warWillByClanId"))) state.WarWill.Add(new BellumWarWillState
        {
            ClanId = entry.Key, Value = Convert.ToDouble(entry.Value, CultureInfo.InvariantCulture),
            NextEvaluationDay = nextEvaluation.TryGetValue(entry.Key, out var next) ? Convert.ToInt32(next, CultureInfo.InvariantCulture) : 0,
            PreferredTargetKingdomId = preferredTarget.TryGetValue(entry.Key, out var preferred) ? Convert.ToString(preferred, CultureInfo.InvariantCulture) ?? "" : "",
        });
        foreach (var item in Items(Field(warWill, "_pressureRecords"))) state.WarWillPressures.Add(new BellumWarWillPressureState
        {
            RecordId = S(item, "RecordId"), ClanId = S(item, "ClanId"), TargetKingdomId = S(item, "TargetKingdomId"), ConflictKey = S(item, "ConflictKey"),
            Amount = D(item, "Amount"), Reason = S(item, "Reason"), ContextSettlementId = S(item, "ContextSettlementId"), ContextTitleId = S(item, "ContextTitleId"),
            ContextHeroId = S(item, "ContextHeroId"), CreatedDay = D(item, "CreatedDay"), ExpiresDay = D(item, "ExpiresDay"), IsActive = B(item, "IsActive"), ReasonType = I(item, "ReasonType"),
        });

        foreach (var item in Items(Field(Behavior("BellumCivile.Behaviors.ClientKingdomBehavior"), "_clients"))) state.ClientKingdoms.Add(new BellumClientKingdomState
        { ClientKingdomId = S(item, "ClientKingdomId"), SuzerainKingdomId = S(item, "SuzerainKingdomId"), StartedDay = D(item, "StartedDay"), WasVoluntary = B(item, "WasVoluntary"), LiberationCooldownUntilDay = D(item, "LiberationCooldownUntilDay") });
        foreach (var item in Items(Field(Behavior("BellumCivile.Behaviors.FeudalServiceBehavior"), "_contractsByTitlePair"))) state.FeudalServices.Add(new BellumFeudalServiceState
        { RecordId = S(item, "RecordId"), ChildTitleId = S(item, "ChildTitleId"), ParentTitleId = S(item, "ParentTitleId"), Level = I(item, "Level"), LastChangedDay = D(item, "LastChangedDay"), ChangedByClanId = S(item, "ChangedByClanId"), Reason = S(item, "Reason") });
        foreach (var item in Items(Field(Behavior("BellumCivile.Behaviors.FeudalDeJureDriftBehavior"), "_drifts"))) state.DeJureDrifts.Add(new BellumDeJureDriftState
        { TitleId = S(item, "TitleId"), OriginalParentTitleId = S(item, "OriginalParentTitleId"), TargetParentTitleId = S(item, "TargetParentTitleId"), TargetKingdomId = S(item, "TargetKingdomId"), Progress = D(item, "Progress"), StartedDay = D(item, "StartedDay"), LastEvaluatedDay = D(item, "LastEvaluatedDay"), State = I(item, "State"), IsActive = B(item, "IsActive") });
        foreach (var item in Items(Field(Behavior("BellumCivile.Behaviors.FeudalClaimFabricationBehavior"), "_fabrications"))) state.ClaimFabrications.Add(new BellumClaimFabricationState
        { FabricationId = S(item, "FabricationId"), FabricatorHeroId = S(item, "FabricatorHeroId"), FabricatorClanId = S(item, "FabricatorClanId"), TargetTitleId = S(item, "TargetTitleId"), Track = I(item, "Track"), Progress = D(item, "Progress"), DailyProgressDelta = D(item, "DailyProgressDelta"), StartedDay = D(item, "StartedDay"), PaidGoldCost = I(item, "PaidGoldCost"), PaidInfluenceCost = D(item, "PaidInfluenceCost"), IsActive = B(item, "IsActive"), HasPassed33 = B(item, "HasPassed33"), HasPassed66 = B(item, "HasPassed66"), HasPassed100 = B(item, "HasPassed100"), LegacyIsPaused = B(item, "_legacyIsPaused"), FailedStage = I(item, "_failedStage") });
        var foreignPolicy = Behavior("BellumCivile.Behaviors.ForeignPolicyBehavior");
        foreach (var item in Items(Field(foreignPolicy, "_activeWars"))) state.ForeignWars.Add(ForeignWar(item));
        foreach (var item in Items(Field(foreignPolicy, "_pendingWarContexts"))) state.PendingForeignWars.Add(ForeignWar(item));

        foreach (DictionaryEntry entry in Entries(Field(Behavior("BellumCivile.Behaviors.CivilWarInterventionBehavior"), "_protectedRebelKingdoms")))
        {
            var id = ObjectId(entry.Key); if (id.Length > 0) state.ObjectTimers.Add(new BellumObjectTimerState { Scope = "protected-rebel-kingdom", ObjectId = id, Day = Days(entry.Value) });
        }
        var factionManager = Behavior("BellumCivile.Behaviors.FactionManagerBehavior");
        foreach (DictionaryEntry entry in Entries(Field(factionManager, "_pacifiedClans")))
        {
            var id = ObjectId(entry.Key); if (id.Length > 0) state.ObjectTimers.Add(new BellumObjectTimerState { Scope = "pacified-clan", ObjectId = id, Day = Days(entry.Value) });
        }
        foreach (DictionaryEntry entry in Entries(Field(factionManager, "_settlementSyncStamps")))
        {
            var id = ObjectId(entry.Key); if (id.Length > 0) state.ObjectTimers.Add(new BellumObjectTimerState { Scope = "settlement-sync-clan", ObjectId = id, Day = Days(entry.Value) });
        }
        state.PendingSettlementRefreshClanIds = Items(Field(factionManager, "_pendingSettlementRefreshClans")).Select(ObjectId).Where(x => x.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        foreach (var entry in Map(Field(Behavior("BellumCivile.Behaviors.ControversyBehavior"), "_trackedControversyById")))
            state.ObjectIntegers.Add(new BellumObjectIntegerState { Scope = "kingdom-controversy", ObjectId = entry.Key, Value = Convert.ToInt32(entry.Value, CultureInfo.InvariantCulture) });

        foreach (var item in Items(Field(Behavior("BellumCivile.Behaviors.ClaimFeudWarBehavior"), "_wars"))) state.ClaimFeudWars.Add(new BellumClaimFeudWarState
        {
            WarId = S(item, "WarId"), FeudRecordId = S(item, "FeudRecordId"), ParentKingdomId = S(item, "ParentKingdomId"), ClaimantKingdomId = S(item, "ClaimantKingdomId"),
            HolderKingdomId = S(item, "HolderKingdomId"), TargetTitleId = S(item, "TargetTitleId"), ClaimantLeaderClanId = S(item, "ClaimantLeaderClanId"), HolderLeaderClanId = S(item, "HolderLeaderClanId"),
            ClaimantClanIds = S(item, "ClaimantClanIds"), HolderClanIds = S(item, "HolderClanIds"), InfluenceSnapshot = S(item, "InfluenceSnapshot"), FiefSnapshot = S(item, "FiefSnapshot"),
            StartedDay = D(item, "StartedDay"), IsActive = B(item, "IsActive"), PendingCaptureWinnerClanId = S(item, "PendingCaptureWinnerClanId"), ClaimantControlsObjective = B(item, "ClaimantControlsObjective"),
            ClaimantHasControlledObjective = B(item, "ClaimantHasControlledObjective"), PendingResolutionOutcome = I(item, "PendingResolutionOutcome"), PendingResolutionReason = S(item, "PendingResolutionReason"),
        });

        var mercenaryBehavior = Behavior("BellumCivile.Behaviors.DynamicMercenaryBandBehavior");
        foreach (var item in Items(Field(mercenaryBehavior, "_playerDepartureIntents"))) state.MercenaryDepartures.Add(new BellumMercenaryDepartureState
        { HeroId = S(item, "HeroId"), QueuedDay = Days(V(item, "QueuedAt")), ObedienceRoll = D(item, "ObedienceRoll"), Resolution = I(item, "Resolution") });

        var relations = Behavior("BellumCivile.Behaviors.DynamicRelationBehavior");
        foreach (var entry in Map(Field(relations, "_serializedRelationRecords"))) state.DynamicRelations.Add(new BellumDynamicRelationState
        { PairKey = entry.Key, LastRecordedValue = I(entry.Value, "LastRecordedValue"), LastUpdateDay = D(entry.Value, "LastUpdateDay") });
        state.RelationMemorySchemaVersion = Convert.ToInt32(Field(relations, "_memorySchemaVersion"), CultureInfo.InvariantCulture);
        state.RelationMemoryDurationMultiplier = Convert.ToDouble(Field(relations, "_appliedMemoryDurationMultiplier"), CultureInfo.InvariantCulture);
        foreach (var entry in Map(Field(relations, "_materializedPairValues"))) state.RelationMaterializedValues.Add(new BellumRelationMaterializedValueState
        { PairKey = entry.Key, Value = Convert.ToInt32(entry.Value, CultureInfo.InvariantCulture) });
        var memorySequence = 0;
        foreach (var item in Items(Field(relations, "_relationMemories"))) state.RelationMemories.Add(new BellumRelationMemoryState
        {
            MemoryKey = (++memorySequence).ToString("D8", CultureInfo.InvariantCulture), Scope = I(item, "Scope"), FirstId = S(item, "FirstId"), SecondId = S(item, "SecondId"),
            SourceId = S(item, "SourceId"), ContextText = S(item, "ContextText"), Value = I(item, "Value"), StartDay = D(item, "StartDay"), ExpiryDay = D(item, "ExpiryDay"), LegacyWeeklyDecay = D(item, "LegacyWeeklyDecay"),
        });

        var dynastic = Behavior("BellumCivile.Behaviors.DynasticHeirBehavior");
        foreach (var entry in Map(Field(dynastic, "_dynasticStates"))) state.DynasticSuccessions.Add(new BellumDynasticSuccessionState
        {
            KingdomId = S(entry.Value, "KingdomId"), RightfulDynastyClanId = S(entry.Value, "RightfulDynastyClanId"), HeirHeroId = S(entry.Value, "HeirHeroId"),
            ClaimCarrierClanId = S(entry.Value, "ClaimCarrierClanId"), ClaimCarrierHeroId = S(entry.Value, "ClaimCarrierHeroId"), Source = S(entry.Value, "Source"),
            LockedUntilDay = Days(V(entry.Value, "LockedUntil")), LastUpdatedDay = D(entry.Value, "LastUpdatedDay"),
        });
        foreach (var item in Items(Field(dynastic, "_pendingCadetMarriages"))) state.PendingCadetMarriages.Add(new BellumPendingCadetMarriageState
        {
            HeiressId = S(item, "HeiressId"), SpouseId = S(item, "SpouseId"), OriginKingdomId = S(item, "OriginKingdomId"), DynastyClanId = S(item, "DynastyClanId"),
            BrideClanId = S(item, "BrideClanId"), MarriedClanId = S(item, "MarriedClanId"), ReadyDay = Days(V(item, "ReadyDate")), MarriageGold = I(item, "MarriageGold"),
            Attempts = I(item, "Attempts"), ExpiresDay = Days(V(item, "ExpiresOn")),
        });

        var fabricationBehavior = Behavior("BellumCivile.Behaviors.FeudalClaimFabricationBehavior");
        var outcomeSequence = 0;
        foreach (var item in Items(Field(fabricationBehavior, "_pendingPlayerOutcomes"))) state.PendingFabricationOutcomes.Add(new BellumPendingFabricationOutcomeState
        { Sequence = outcomeSequence++, Title = S(item, "Title"), Body = S(item, "Body") });
        foreach (var item in Items(Field(Behavior("BellumCivile.Behaviors.FeudalTitleUsurpationBehavior"), "_pendingCandidates"))) state.PendingUsurpations.Add(new BellumPendingUsurpationState
        { TitleId = S(item, "TitleId"), ClaimantClanId = S(item, "ClaimantClanId") });
        foreach (var item in Items(Field(treatyBehavior, "_pendingRebelResolutions"))) state.PendingTreatyRebelResolutions.Add(new BellumPendingTreatyRebelResolutionState
        { ProposalId = S(item, "ProposalId"), RebelKingdomId = S(item, "RebelKingdomId"), ParentKingdomId = S(item, "ParentKingdomId"), BeneficiaryKingdomId = S(item, "BeneficiaryKingdomId") });
        foreach (var item in Items(Field(Behavior("BellumCivile.Behaviors.PartitionSuccessionBehavior"), "_pendingPartitions"))) state.PendingPartitions.Add(new BellumPendingPartitionState
        {
            DeadLeaderId = S(item, "DeadLeaderId"), ParentClanId = S(item, "ParentClanId"), KingdomId = S(item, "KingdomId"), FiefIds = S(item, "FiefIds"), HeirIds = S(item, "HeirIds"),
            ReadyDay = Days(V(item, "ReadyDate")), ParentWasRulingClanAtDeath = B(item, "ParentWasRulingClanAtDeath"), TitleIds = S(item, "TitleIds"), PrimarySovereignTitleId = S(item, "PrimarySovereignTitleId"),
        });
        foreach (var item in Items(Field(Behavior("BellumCivile.Behaviors.SuccessionLawBehavior"), "_pendingGenderLineEscheats"))) state.PendingGenderLineEscheats.Add(new BellumPendingGenderLineEscheatState
        {
            TriggerHeroId = S(item, "TriggerHeroId"), ClanId = S(item, "ClanId"), KingdomId = S(item, "KingdomId"), GenderLaw = I(item, "GenderLaw"),
            ReadyDay = Days(V(item, "ReadyDate")), WasRulingClan = B(item, "WasRulingClan"), DynasticLineExtinction = B(item, "DynasticLineExtinction"),
        });
        BellumStateCodec.Validate(state);
        return state;
    }

    private static BellumForeignWarState ForeignWar(object item) => new BellumForeignWarState
    {
        WarKey = S(item, "WarKey"), AttackerKingdomId = S(item, "AttackerKingdomId"), DefenderKingdomId = S(item, "DefenderKingdomId"), DeclaredMotive = I(item, "DeclaredMotive"),
        SponsorFactionType = S(item, "SponsorFactionType"), SponsorClanId = S(item, "SponsorClanId"), TargetTitleIds = Items(V(item, "TargetTitleIds")).Select(x => Convert.ToString(x, CultureInfo.InvariantCulture) ?? "").Where(x => x.Length > 0).Distinct(StringComparer.Ordinal).ToList(),
        StartingAttackerStrength = D(item, "StartingAttackerStrength"), StartingDefenderStrength = D(item, "StartingDefenderStrength"), StartedDay = D(item, "StartedDay"), ContextCreatedDay = D(item, "ContextCreatedDay"),
        PublicDeclarationMotiveType = I(item, "PublicDeclarationMotiveType"), PublicDeclarationMotiveSubject = S(item, "PublicDeclarationMotiveSubject"), HasPublicDeclarationMotive = B(item, "HasPublicDeclarationMotive"),
    };

    private static object Behavior(string name)
    {
        var type = AccessTools.TypeByName(name) ?? throw new InvalidOperationException("Bellum type missing: " + name);
        var instance = type.GetProperty("Instance", Any)?.GetValue(null, null);
        if (instance != null) return instance;
        var getter = typeof(Campaign).GetMethods(Any).Single(m => m.Name == "GetCampaignBehavior" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0);
        return getter.MakeGenericMethod(type).Invoke(Campaign.Current, null) ?? throw new InvalidOperationException("Bellum behavior is unavailable: " + name);
    }
    private static object? Field(object owner, string name) => owner.GetType().GetField(name, Any)?.GetValue(owner) ?? throw new InvalidOperationException("Bellum field unavailable: " + name);
    private static object? V(object owner, string name) => owner.GetType().GetProperty(name, Any)?.GetValue(owner, null) ?? owner.GetType().GetField(name, Any)?.GetValue(owner);
    private static string S(object owner, string name) => Convert.ToString(V(owner, name), CultureInfo.InvariantCulture) ?? "";
    private static int I(object owner, string name) => V(owner, name) is { } value ? Convert.ToInt32(value, CultureInfo.InvariantCulture) : 0;
    private static double D(object owner, string name) => V(owner, name) is { } value ? Convert.ToDouble(value, CultureInfo.InvariantCulture) : 0;
    private static bool B(object owner, string name) => V(owner, name) is { } value && Convert.ToBoolean(value, CultureInfo.InvariantCulture);
    private static string ObjectId(object? value) => value == null ? "" : Convert.ToString(value.GetType().GetProperty("StringId", Any)?.GetValue(value, null), CultureInfo.InvariantCulture) ?? "";
    private static double Days(object? value) => value == null ? 0 : Convert.ToDouble(value.GetType().GetProperty("ToDays", Any)?.GetValue(value, null) ?? 0, CultureInfo.InvariantCulture);
    private static IEnumerable<object> Items(object? value)
    {
        if (value is IDictionary dictionary)
        {
            foreach (DictionaryEntry entry in dictionary) if (entry.Value != null) yield return entry.Value;
        }
        else if (value is IEnumerable enumerable)
        {
            foreach (var item in enumerable) if (item != null) yield return item;
        }
    }
    private static IEnumerable<DictionaryEntry> Entries(object? value)
    {
        if (value is IDictionary dictionary) foreach (DictionaryEntry entry in dictionary) yield return entry;
    }
    private static List<string> SplitIds(string encoded) => encoded.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
        .Select(x => x.Trim()).Where(x => x.Length > 0).Distinct(StringComparer.Ordinal).ToList();
    private static Dictionary<string, object> Map(object? value)
    {
        var result = new Dictionary<string, object>(StringComparer.Ordinal);
        if (value is IDictionary dictionary)
            foreach (DictionaryEntry entry in dictionary)
                if (entry.Key != null && entry.Value != null) result[Convert.ToString(entry.Key, CultureInfo.InvariantCulture) ?? ""] = entry.Value;
        return result;
    }
}
