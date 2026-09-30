using System;
using System.Collections;
using System.Globalization;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using ModderLords.Operations;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Localization;

namespace ModderLords.CompatSync.Coop.Operations;

/// <summary>
/// Materializes the first reviewed Bellum client read projections from the ID-only wire model. Every reflected
/// constructor, field and cache hook is part of the pinned Bellum 1.3.1 surface; no arbitrary object graph is read
/// from the wire. More Bellum UI domains are added here only after their runtime-object references are reviewed.
/// </summary>
internal static class BellumClientProjection
{
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private const string TitleBehaviorName = "BellumCivile.Behaviors.FeudalTitleBehavior";
    private const string TitleRecordName = "BellumCivile.FeudalTitleRecord";
    private const string ClaimRecordName = "BellumCivile.FeudalClaimRecord";
    private const string SuccessionBehaviorName = "BellumCivile.Behaviors.SuccessionLawBehavior";
    private const string SuccessionRecordName = "BellumCivile.KingdomSuccessionLawRecord";
    private const string FactionBehaviorName = "BellumCivile.Behaviors.FactionManagerBehavior";
    private const string FactionRecordName = "BellumCivile.FactionObject";
    private const string TreatyBehaviorName = "BellumCivile.Behaviors.ForeignTreatyBehavior";
    private const string TreatyRecordName = "BellumCivile.TreatyProposalRecord";
    private const string TreatyTermRecordName = "BellumCivile.TreatyTermRecord";
    private const string TributeRecordName = "BellumCivile.ActiveTreatyTributeRecord";
    private const string WarScoreBehaviorName = "BellumCivile.Behaviors.WarScoreBehavior";
    private const string WarScoreRecordName = "BellumCivile.WarScoreRecord";
    private const string WarScoreFiefRecordName = "BellumCivile.WarScoreFiefSnapshotRecord";
    private const string WarScoreEventRecordName = "BellumCivile.WarScoreEventRecord";
    private const string CouncilBehaviorName = "BellumCivile.Behaviors.PrivyCouncilBehavior";
    private const string CouncilOfficeRecordName = "BellumCivile.PrivyCouncilOfficeRecord";
    private const string WarWillBehaviorName = "BellumCivile.Behaviors.WarPeaceRevampBehavior";
    private const string WarWillPressureRecordName = "BellumCivile.WarWillPressureRecord";
    private const string ForeignPolicyBehaviorName = "BellumCivile.Behaviors.ForeignPolicyBehavior";
    private const string ForeignWarRecordName = "BellumCivile.ActiveForeignWarRecord";
    private const string ForeignPolicyCacheName = "BellumCivile.ForeignPolicyEvaluationService";
    private const string DynasticBehaviorName = "BellumCivile.Behaviors.DynasticHeirBehavior";
    private const string DynasticRecordName = "BellumCivile.DynasticSuccessionStateRecord";
    private const string RelationBehaviorName = "BellumCivile.Behaviors.DynamicRelationBehavior";
    private const string RelationRecordName = "BellumCivile.DynamicRelationRecord";
    private const string RelationMemoryRecordName = "BellumCivile.RelationMemoryRecord";
    private const string RegencyBehaviorName = "BellumCivile.Behaviors.RegencyBehavior";
    private const string RegencyRecordName = "BellumCivile.RegencyRecord";
    private const string ClaimFeudBehaviorName = "BellumCivile.Behaviors.ClaimFeudBehavior";
    private const string ClaimFeudRecordName = "BellumCivile.ClaimFeudRecord";
    private const string MercenaryBehaviorName = "BellumCivile.Behaviors.DynamicMercenaryBandBehavior";
    private const string MercenaryBandRecordName = "BellumCivile.DynamicMercenaryBandRecord";
    private const string MercenaryDepartureRecordName = "BellumCivile.DynamicMercenaryDepartureIntent";
    private const string ClientKingdomBehaviorName = "BellumCivile.Behaviors.ClientKingdomBehavior";
    private const string ClientKingdomRecordName = "BellumCivile.ClientKingdomRecord";
    private const string FeudalServiceBehaviorName = "BellumCivile.Behaviors.FeudalServiceBehavior";
    private const string FeudalServiceRecordName = "BellumCivile.FeudalServiceRecord";
    private const string DeJureDriftBehaviorName = "BellumCivile.Behaviors.FeudalDeJureDriftBehavior";
    private const string DeJureDriftRecordName = "BellumCivile.FeudalDeJureDriftRecord";
    private const string FabricationBehaviorName = "BellumCivile.Behaviors.FeudalClaimFabricationBehavior";
    private const string FabricationRecordName = "BellumCivile.FeudalClaimFabricationRecord";
    private const string FabricationOutcomeRecordName = "BellumCivile.Behaviors.FeudalClaimFabricationBehavior+PendingFabricationOutcome";
    private const string CivilWarInterventionBehaviorName = "BellumCivile.Behaviors.CivilWarInterventionBehavior";
    private const string ControversyBehaviorName = "BellumCivile.Behaviors.ControversyBehavior";
    private const string ClaimFeudWarBehaviorName = "BellumCivile.Behaviors.ClaimFeudWarBehavior";
    private const string ClaimFeudWarRecordName = "BellumCivile.ClaimFeudWarRecord";
    private const string UsurpationBehaviorName = "BellumCivile.Behaviors.FeudalTitleUsurpationBehavior";
    private const string UsurpationRecordName = "BellumCivile.FeudalUsurpationCandidateRecord";
    private const string TreatyRebelResolutionRecordName = "BellumCivile.PendingTreatyRebelResolutionRecord";
    private const string PartitionBehaviorName = "BellumCivile.Behaviors.PartitionSuccessionBehavior";
    private const string PartitionRecordName = "BellumCivile.PendingPartitionSuccessionRecord";
    private const string GenderEscheatRecordName = "BellumCivile.PendingGenderLineEscheatRecord";
    private const string CadetMarriageRecordName = "BellumCivile.PendingCadetMarriageRecord";

    public static bool Validate(out string reason)
    {
        try
        {
            var titleBehavior = Type(TitleBehaviorName);
            var title = Type(TitleRecordName);
            var claim = Type(ClaimRecordName);
            var successionBehavior = Type(SuccessionBehaviorName);
            var succession = Type(SuccessionRecordName);
            var factionBehavior = Type(FactionBehaviorName);
            var faction = Type(FactionRecordName);
            var treatyBehavior = Type(TreatyBehaviorName);
            var treaty = Type(TreatyRecordName);
            var treatyTerm = Type(TreatyTermRecordName);
            var tribute = Type(TributeRecordName);
            var warScoreBehavior = Type(WarScoreBehaviorName);
            var warScore = Type(WarScoreRecordName);
            var warScoreFief = Type(WarScoreFiefRecordName);
            var warScoreEvent = Type(WarScoreEventRecordName);
            var councilBehavior = Type(CouncilBehaviorName);
            var councilOffice = Type(CouncilOfficeRecordName);
            var warWillBehavior = Type(WarWillBehaviorName);
            var warWillPressure = Type(WarWillPressureRecordName);
            var foreignPolicyBehavior = Type(ForeignPolicyBehaviorName);
            var foreignWar = Type(ForeignWarRecordName);
            var foreignPolicyCache = Type(ForeignPolicyCacheName);
            var dynasticBehavior = Type(DynasticBehaviorName);
            var dynasticRecord = Type(DynasticRecordName);
            var relationBehavior = Type(RelationBehaviorName);
            var relationRecord = Type(RelationRecordName);
            var relationMemoryRecord = Type(RelationMemoryRecordName);
            var regencyBehavior = Type(RegencyBehaviorName); var regencyRecord = Type(RegencyRecordName);
            var claimFeudBehavior = Type(ClaimFeudBehaviorName); var claimFeudRecord = Type(ClaimFeudRecordName);
            var mercenaryBehavior = Type(MercenaryBehaviorName); var mercenaryBandRecord = Type(MercenaryBandRecordName);
            var mercenaryDepartureRecord = Type(MercenaryDepartureRecordName);
            var clientKingdomBehavior = Type(ClientKingdomBehaviorName); var clientKingdomRecord = Type(ClientKingdomRecordName);
            var serviceBehavior = Type(FeudalServiceBehaviorName); var serviceRecord = Type(FeudalServiceRecordName);
            var driftBehavior = Type(DeJureDriftBehaviorName); var driftRecord = Type(DeJureDriftRecordName);
            var fabricationBehavior = Type(FabricationBehaviorName); var fabricationRecord = Type(FabricationRecordName);
            var fabricationOutcome = Type(FabricationOutcomeRecordName);
            var interventionBehavior = Type(CivilWarInterventionBehaviorName); var controversyBehavior = Type(ControversyBehaviorName);
            var claimFeudWarBehavior = Type(ClaimFeudWarBehaviorName); var claimFeudWarRecord = Type(ClaimFeudWarRecordName);
            var usurpationBehavior = Type(UsurpationBehaviorName); var usurpationRecord = Type(UsurpationRecordName);
            var treatyRebelResolutionRecord = Type(TreatyRebelResolutionRecordName);
            var partitionBehavior = Type(PartitionBehaviorName); var partitionRecord = Type(PartitionRecordName);
            var genderEscheatRecord = Type(GenderEscheatRecordName); var cadetMarriageRecord = Type(CadetMarriageRecordName);
            RequireField(titleBehavior, "_titlesById");
            RequireField(titleBehavior, "_claims");
            RequireMethod(titleBehavior, "RebuildRuntimeIndexes", typeof(bool));
            RequireConstructor(title, 12);
            RequireMethod(title, "SetDeFactoParentTitle", typeof(string));
            RequireMethod(title, "SetDeliberatelyDissolved", typeof(bool));
            RequireConstructor(claim, 12);
            RequireField(successionBehavior, "_realmLaws");
            RequireConstructor(succession, 0);
            RequireField(succession, "KingdomId");
            RequireField(succession, "GenderLaw");
            RequireField(succession, "SuccessionLaw");
            RequireField(factionBehavior, "_activeFactions");
            RequireMethod(factionBehavior, "InvalidateFactionLookupCache", typeof(bool));
            RequireConstructor(faction, 4);
            foreach (var field in new[] { "_members", "_discontent", "_peakDiscontent", "_mood", "_loyalClan",
                "_creationDate", "_rebelKingdom", "_rebelKingdomStringId", "_civilWarStartFiefClans",
                "_civilWarStartFiefCounts", "_civilWarStartInfluenceClans", "_civilWarStartInfluenceValues",
                "_isGrandCoalition", "_grandCoalitionSourceIdeology", "_hasGrandCoalitionSourceIdeology",
                "_ultimatumPending", "_ultimatumResolved", "_solidarityRecruitmentApplied",
                "_rebellionCreationStarted", "_rebellionCreationCompleted" }) RequireField(faction, field);
            RequireField(treatyBehavior, "_proposals");
            RequireField(treatyBehavior, "_activeTributes");
            RequireConstructor(treaty, 7);
            foreach (var field in new[] { "_proposalId", "_warKey", "_winnerKingdomId", "_loserKingdomId",
                "_createdDay", "_warScoreBudget", "_isForced", "_state", "_terms", "_winnerSupport",
                "_loserSupport", "_winnerOverrideCost", "_loserOverrideCost", "_resolutionNote", "_draftRevision",
                "_playerVoteStance", "_playerInfluenceCommitment", "_playerInfluenceSpent", "_playerVoteSubmitted",
                "_drafterKingdomId" }) RequireField(treaty, field);
            RequireConstructor(treatyTerm, 15);
            RequireConstructor(tribute, 4);
            foreach (var field in new[] { "_payerKingdomId", "_recipientKingdomId", "_dailyGold", "_remainingDays" })
                RequireField(tribute, field);
            RequireField(warScoreBehavior, "_wars");
            RequireField(warScoreBehavior, "_runtimeRevision");
            RequireMethod(warScoreBehavior, "TouchRuntimeRevision");
            RequireConstructor(warScore, 7);
            foreach (var field in new[] { "_warKey", "_attackerKingdomId", "_defenderKingdomId", "_startedDay",
                "_endedDay", "_score", "_isActive", "_fiefSnapshots", "_events", "_occupationScore", "_battleScore",
                "_raidScore", "_prisonerScore", "_tickingScore", "_lastTickDay", "_conflictType", "_contextId",
                "_resolutionPending", "_objectiveScore", "_nextWhitePeaceCheckDay", "_whitePeaceOfferPending",
                "_parleyPending", "_parleyForced", "_parleyOpenedDay", "_terminalResolutionQueued",
                "_terminalResolutionReason", "_landlessPressureScore" }) RequireField(warScore, field);
            RequireConstructor(warScoreFief, 5);
            RequireConstructor(warScoreEvent, 9);
            RequireField(warScoreEvent, "_eventId");
            RequireField(councilBehavior, "_officeRecords");
            RequireMethod(councilBehavior, "InvalidateRuntimeCache", typeof(bool));
            RequireConstructor(councilOffice, 3);
            foreach (var field in new[] { "_recordId", "_kingdomId", "_office", "_holderClanId", "_controversy",
                "_vacancyStartedDay", "_appointedDay", "_lastUpdatedDay", "_isInitialized",
                "_legacyControversyMigrated", "_lastReason", "_lastChange", "_lastRealmMetric", "_assignmentId",
                "_lastAssignmentChangedDay", "_nextAssignmentReviewDay" }) RequireField(councilOffice, field);
            foreach (var field in new[] { "_warWillByClanId", "_nextEvaluationDayByClanId", "_preferredTargetByClanId",
                "_pressureRecords", "_targetScoresByClanId" }) RequireField(warWillBehavior, field);
            RequireConstructor(warWillPressure, 12);
            RequireField(warWillPressure, "_isActive");
            RequireField(foreignPolicyBehavior, "_activeWars");
            RequireField(foreignPolicyBehavior, "_pendingWarContexts");
            RequireConstructor(foreignWar, 11);
            foreach (var field in new[] { "_publicDeclarationMotiveType", "_publicDeclarationMotiveSubject",
                "_hasPublicDeclarationMotive" }) RequireField(foreignWar, field);
            RequireMethod(foreignPolicyCache, "InvalidateCache");
            RequireField(dynasticBehavior, "_dynasticStates");
            RequireMethod(dynasticBehavior, "RebuildHeroResolutionCache");
            RequireConstructor(dynasticRecord, 8);
            foreach (var field in new[] { "_serializedRelationRecords", "_memorySchemaVersion",
                "_appliedMemoryDurationMultiplier", "_memoryDurationReady", "_relationMemories",
                "_materializedPairValues" }) RequireField(relationBehavior, field);
            foreach (var method in new[] { "RebuildObjectResolutionCaches", "RebuildRuntimeRecordsFromSerialized",
                "RebuildRuntimeMaterializedValues", "RebuildMemoryIndexes", "InvalidateBaselineCache" })
                RequireMethod(relationBehavior, method);
            RequireConstructor(relationRecord, 2);
            RequireConstructor(relationMemoryRecord, 9);
            RequireField(regencyBehavior, "_regencies"); RequireConstructor(regencyRecord, 6);
            RequireField(claimFeudBehavior, "_feuds"); RequireConstructor(claimFeudRecord, 10);
            foreach (var field in new[] { "_state", "_lastTickDay", "_cooldownUntilDay", "_judgment", "_claimantResponse",
                "_holderResponse", "_rulingDay", "_claimantSidePower", "_holderSidePower", "_claimantSupporterIds",
                "_holderSupporterIds", "_hasPassed33", "_hasPassed66", "_resumeState", "_pauseReason", "_pausedDay" })
                RequireField(claimFeudRecord, field);
            RequireField(mercenaryBehavior, "_bands"); RequireField(mercenaryBehavior, "_playerDepartureIntents");
            RequireConstructor(mercenaryBandRecord, 6); RequireConstructor(mercenaryDepartureRecord, 3);
            RequireField(mercenaryDepartureRecord, "_resolution");
            RequireField(clientKingdomBehavior, "_clients"); RequireConstructor(clientKingdomRecord, 5);
            RequireField(serviceBehavior, "_contractsByTitlePair"); RequireConstructor(serviceRecord, 7);
            RequireField(driftBehavior, "_drifts"); RequireMethod(driftBehavior, "RebuildRuntimeState"); RequireConstructor(driftRecord, 9);
            RequireField(fabricationBehavior, "_fabrications"); RequireField(fabricationBehavior, "_pendingPlayerOutcomes");
            RequireConstructor(fabricationRecord, 11); RequireField(fabricationRecord, "_hasPassed33");
            RequireField(fabricationRecord, "_hasPassed66"); RequireField(fabricationRecord, "_hasPassed100");
            RequireField(fabricationRecord, "_legacyIsPaused"); RequireField(fabricationRecord, "_failedStage");
            RequireConstructor(fabricationOutcome, 2);
            RequireField(interventionBehavior, "_protectedRebelKingdoms");
            foreach (var field in new[] { "_pacifiedClans", "_settlementSyncStamps", "_pendingSettlementRefreshClans" })
                RequireField(factionBehavior, field);
            RequireField(controversyBehavior, "_trackedControversyById"); RequireField(controversyBehavior, "_dailyDynamicCacheById");
            RequireField(claimFeudWarBehavior, "_wars"); RequireConstructor(claimFeudWarRecord, 13);
            foreach (var field in new[] { "_isActive", "_pendingCaptureWinnerClanId", "_claimantControlsObjective",
                "_claimantHasControlledObjective", "_pendingResolutionOutcome", "_pendingResolutionReason" })
                RequireField(claimFeudWarRecord, field);
            RequireField(dynasticBehavior, "_pendingCadetMarriages"); RequireConstructor(cadetMarriageRecord, 8);
            RequireField(cadetMarriageRecord, "_attempts"); RequireField(cadetMarriageRecord, "_expiresOn");
            RequireField(usurpationBehavior, "_pendingCandidates"); RequireMethod(usurpationBehavior, "RebuildPendingKeyIndex");
            RequireConstructor(usurpationRecord, 2);
            RequireField(treatyBehavior, "_pendingRebelResolutions"); RequireConstructor(treatyRebelResolutionRecord, 4);
            RequireField(partitionBehavior, "_pendingPartitions"); RequireConstructor(partitionRecord, 9);
            RequireField(successionBehavior, "_pendingGenderLineEscheats"); RequireConstructor(genderEscheatRecord, 7);
            reason = "";
            return true;
        }
        catch (Exception ex)
        {
            reason = "Pinned Bellum client projection surface is unavailable: " + ex.GetBaseException().Message;
            return false;
        }
    }

    public static void Apply(BellumStateSnapshot snapshot)
    {
        if (Campaign.Current == null) return;
        try { if (OperationProcessSide.IsServer) return; }
        catch { return; }
        Materialize(snapshot, commit: true);
    }

    /// <summary>Builds the exact runtime graph without swapping it into the live campaign.</summary>
    public static void ValidateMaterialization(BellumStateSnapshot snapshot)
    {
        if (Campaign.Current == null) throw new InvalidOperationException("Campaign is unavailable");
        Materialize(snapshot, commit: false);
    }

    private static void Materialize(BellumStateSnapshot snapshot, bool commit)
    {
        if (!Validate(out var reason)) throw new InvalidOperationException(reason);

        // Build every replacement before mutating either live behavior. A malformed record cannot leave a partial
        // projection behind. The snapshot codec has already enforced IDs, bounds, uniqueness and finite numbers.
        var titleType = Type(TitleRecordName);
        var claimType = Type(ClaimRecordName);
        var successionType = Type(SuccessionRecordName);
        var factionType = Type(FactionRecordName);
        var treatyType = Type(TreatyRecordName);
        var treatyTermType = Type(TreatyTermRecordName);
        var tributeType = Type(TributeRecordName);
        var warScoreType = Type(WarScoreRecordName);
        var warScoreFiefType = Type(WarScoreFiefRecordName);
        var warScoreEventType = Type(WarScoreEventRecordName);
        var councilOfficeType = Type(CouncilOfficeRecordName);
        var warWillPressureType = Type(WarWillPressureRecordName);
        var foreignWarType = Type(ForeignWarRecordName);
        var dynasticType = Type(DynasticRecordName);
        var relationRecordType = Type(RelationRecordName);
        var relationMemoryType = Type(RelationMemoryRecordName);
        var regencyType = Type(RegencyRecordName);
        var claimFeudType = Type(ClaimFeudRecordName);
        var mercenaryBandType = Type(MercenaryBandRecordName);
        var mercenaryDepartureType = Type(MercenaryDepartureRecordName);
        var clientKingdomType = Type(ClientKingdomRecordName);
        var serviceType = Type(FeudalServiceRecordName);
        var driftType = Type(DeJureDriftRecordName);
        var fabricationType = Type(FabricationRecordName);
        var fabricationOutcomeType = Type(FabricationOutcomeRecordName);
        var claimFeudWarType = Type(ClaimFeudWarRecordName);
        var cadetMarriageType = Type(CadetMarriageRecordName);
        var usurpationType = Type(UsurpationRecordName);
        var treatyRebelResolutionType = Type(TreatyRebelResolutionRecordName);
        var partitionType = Type(PartitionRecordName);
        var genderEscheatType = Type(GenderEscheatRecordName);
        var titleBehavior = Behavior(TitleBehaviorName);
        var successionBehavior = Behavior(SuccessionBehaviorName);
        var factionBehavior = Behavior(FactionBehaviorName);
        var treatyBehavior = Behavior(TreatyBehaviorName);
        var warScoreBehavior = Behavior(WarScoreBehaviorName);
        var councilBehavior = Behavior(CouncilBehaviorName);
        var warWillBehavior = Behavior(WarWillBehaviorName);
        var foreignPolicyBehavior = Behavior(ForeignPolicyBehaviorName);
        var dynasticBehavior = Behavior(DynasticBehaviorName);
        var relationBehavior = Behavior(RelationBehaviorName);
        var regencyBehavior = Behavior(RegencyBehaviorName);
        var claimFeudBehavior = Behavior(ClaimFeudBehaviorName);
        var mercenaryBehavior = Behavior(MercenaryBehaviorName);
        var clientKingdomBehavior = Behavior(ClientKingdomBehaviorName);
        var serviceBehavior = Behavior(FeudalServiceBehaviorName);
        var driftBehavior = Behavior(DeJureDriftBehaviorName);
        var fabricationBehavior = Behavior(FabricationBehaviorName);
        var interventionBehavior = Behavior(CivilWarInterventionBehaviorName);
        var controversyBehavior = Behavior(ControversyBehaviorName);
        var claimFeudWarBehavior = Behavior(ClaimFeudWarBehaviorName);
        var usurpationBehavior = Behavior(UsurpationBehaviorName);
        var partitionBehavior = Behavior(PartitionBehaviorName);
        var titlesField = RequireField(titleBehavior.GetType(), "_titlesById");
        var claimsField = RequireField(titleBehavior.GetType(), "_claims");
        var lawsField = RequireField(successionBehavior.GetType(), "_realmLaws");
        var factionsField = RequireField(factionBehavior.GetType(), "_activeFactions");
        var proposalsField = RequireField(treatyBehavior.GetType(), "_proposals");
        var tributesField = RequireField(treatyBehavior.GetType(), "_activeTributes");
        var warsField = RequireField(warScoreBehavior.GetType(), "_wars");
        var officesField = RequireField(councilBehavior.GetType(), "_officeRecords");
        var warWillField = RequireField(warWillBehavior.GetType(), "_warWillByClanId");
        var warWillNextField = RequireField(warWillBehavior.GetType(), "_nextEvaluationDayByClanId");
        var warWillTargetField = RequireField(warWillBehavior.GetType(), "_preferredTargetByClanId");
        var pressuresField = RequireField(warWillBehavior.GetType(), "_pressureRecords");
        var foreignWarsField = RequireField(foreignPolicyBehavior.GetType(), "_activeWars");
        var pendingForeignWarsField = RequireField(foreignPolicyBehavior.GetType(), "_pendingWarContexts");
        var dynasticStatesField = RequireField(dynasticBehavior.GetType(), "_dynasticStates");
        var serializedRelationsField = RequireField(relationBehavior.GetType(), "_serializedRelationRecords");
        var relationMemoriesField = RequireField(relationBehavior.GetType(), "_relationMemories");
        var materializedRelationsField = RequireField(relationBehavior.GetType(), "_materializedPairValues");
        var regenciesField = RequireField(regencyBehavior.GetType(), "_regencies");
        var claimFeudsField = RequireField(claimFeudBehavior.GetType(), "_feuds");
        var mercenaryBandsField = RequireField(mercenaryBehavior.GetType(), "_bands");
        var mercenaryDeparturesField = RequireField(mercenaryBehavior.GetType(), "_playerDepartureIntents");
        var clientKingdomsField = RequireField(clientKingdomBehavior.GetType(), "_clients");
        var servicesField = RequireField(serviceBehavior.GetType(), "_contractsByTitlePair");
        var driftsField = RequireField(driftBehavior.GetType(), "_drifts");
        var fabricationsField = RequireField(fabricationBehavior.GetType(), "_fabrications");
        var fabricationOutcomesField = RequireField(fabricationBehavior.GetType(), "_pendingPlayerOutcomes");
        var protectedKingdomsField = RequireField(interventionBehavior.GetType(), "_protectedRebelKingdoms");
        var pacifiedClansField = RequireField(factionBehavior.GetType(), "_pacifiedClans");
        var settlementStampsField = RequireField(factionBehavior.GetType(), "_settlementSyncStamps");
        var pendingSettlementClansField = RequireField(factionBehavior.GetType(), "_pendingSettlementRefreshClans");
        var controversyField = RequireField(controversyBehavior.GetType(), "_trackedControversyById");
        var claimFeudWarsField = RequireField(claimFeudWarBehavior.GetType(), "_wars");
        var cadetMarriagesField = RequireField(dynasticBehavior.GetType(), "_pendingCadetMarriages");
        var usurpationsField = RequireField(usurpationBehavior.GetType(), "_pendingCandidates");
        var treatyRebelResolutionsField = RequireField(treatyBehavior.GetType(), "_pendingRebelResolutions");
        var partitionsField = RequireField(partitionBehavior.GetType(), "_pendingPartitions");
        var genderEscheatsField = RequireField(successionBehavior.GetType(), "_pendingGenderLineEscheats");
        var titles = (IDictionary)Activator.CreateInstance(titlesField.FieldType)!;
        var claims = (IList)Activator.CreateInstance(claimsField.FieldType)!;
        var laws = (IDictionary)Activator.CreateInstance(lawsField.FieldType)!;
        var factions = (IList)Activator.CreateInstance(factionsField.FieldType)!;
        var proposals = (IList)Activator.CreateInstance(proposalsField.FieldType)!;
        var tributes = (IList)Activator.CreateInstance(tributesField.FieldType)!;
        var wars = (IList)Activator.CreateInstance(warsField.FieldType)!;
        var offices = (IList)Activator.CreateInstance(officesField.FieldType)!;
        var warWill = (IDictionary)Activator.CreateInstance(warWillField.FieldType)!;
        var warWillNext = (IDictionary)Activator.CreateInstance(warWillNextField.FieldType)!;
        var warWillTargets = (IDictionary)Activator.CreateInstance(warWillTargetField.FieldType)!;
        var pressures = (IList)Activator.CreateInstance(pressuresField.FieldType)!;
        var foreignWars = (IList)Activator.CreateInstance(foreignWarsField.FieldType)!;
        var pendingForeignWars = (IList)Activator.CreateInstance(pendingForeignWarsField.FieldType)!;
        var dynasticStates = (IDictionary)Activator.CreateInstance(dynasticStatesField.FieldType)!;
        var serializedRelations = (IDictionary)Activator.CreateInstance(serializedRelationsField.FieldType)!;
        var relationMemories = (IList)Activator.CreateInstance(relationMemoriesField.FieldType)!;
        var materializedRelations = (IDictionary)Activator.CreateInstance(materializedRelationsField.FieldType)!;
        var regencies = (IList)Activator.CreateInstance(regenciesField.FieldType)!;
        var claimFeuds = (IList)Activator.CreateInstance(claimFeudsField.FieldType)!;
        var mercenaryBands = (IList)Activator.CreateInstance(mercenaryBandsField.FieldType)!;
        var mercenaryDepartures = (IList)Activator.CreateInstance(mercenaryDeparturesField.FieldType)!;
        var clientKingdoms = (IList)Activator.CreateInstance(clientKingdomsField.FieldType)!;
        var services = (IDictionary)Activator.CreateInstance(servicesField.FieldType)!;
        var drifts = (IList)Activator.CreateInstance(driftsField.FieldType)!;
        var fabrications = (IList)Activator.CreateInstance(fabricationsField.FieldType)!;
        var fabricationOutcomes = (IList)Activator.CreateInstance(fabricationOutcomesField.FieldType)!;
        var protectedKingdoms = (IDictionary)Activator.CreateInstance(protectedKingdomsField.FieldType)!;
        var pacifiedClans = (IDictionary)Activator.CreateInstance(pacifiedClansField.FieldType)!;
        var settlementStamps = (IDictionary)Activator.CreateInstance(settlementStampsField.FieldType)!;
        var pendingSettlementClans = (IList)Activator.CreateInstance(pendingSettlementClansField.FieldType)!;
        var controversy = (IDictionary)Activator.CreateInstance(controversyField.FieldType)!;
        var claimFeudWars = (IList)Activator.CreateInstance(claimFeudWarsField.FieldType)!;
        var cadetMarriages = (IList)Activator.CreateInstance(cadetMarriagesField.FieldType)!;
        var usurpations = (IList)Activator.CreateInstance(usurpationsField.FieldType)!;
        var treatyRebelResolutions = (IList)Activator.CreateInstance(treatyRebelResolutionsField.FieldType)!;
        var partitions = (IList)Activator.CreateInstance(partitionsField.FieldType)!;
        var genderEscheats = (IList)Activator.CreateInstance(genderEscheatsField.FieldType)!;

        foreach (var state in snapshot.Titles)
        {
            var enumType = titleType.GetConstructors(Any).Single(c => c.GetParameters().Length == 12).GetParameters()[2].ParameterType;
            var record = Construct(titleType,
                state.TitleId, state.Name, Enum.ToObject(enumType, state.TitleType), state.DeJureHolderClanId,
                state.DeFactoHolderClanId, state.ParentTitleId, state.CapitalSettlementId, state.AssociatedKingdomId,
                Convert.ToSingle(state.CreatedDay, CultureInfo.InvariantCulture), Convert.ToSingle(state.LastSyncedDay, CultureInfo.InvariantCulture),
                state.IsActive, state.FallbackCultureRef);
            RequireMethod(titleType, "SetDeFactoParentTitle", typeof(string)).Invoke(record, new object[] { state.DeFactoParentTitleId });
            RequireMethod(titleType, "SetDeliberatelyDissolved", typeof(bool)).Invoke(record, new object[] { state.IsDeliberatelyDissolved });
            titles.Add(state.TitleId, record);
        }
        foreach (var state in snapshot.Claims)
        {
            var enumType = claimType.GetConstructors(Any).Single(c => c.GetParameters().Length == 12).GetParameters()[3].ParameterType;
            claims.Add(Construct(claimType,
                state.ClaimId, state.ClaimantClanId, state.TargetTitleId, Enum.ToObject(enumType, state.Strength),
                state.Source, state.SourceHeroId, state.OriginClanId,
                Convert.ToSingle(state.CreatedDay, CultureInfo.InvariantCulture), Convert.ToSingle(state.ExpiresDay, CultureInfo.InvariantCulture),
                state.GenerationDepth, state.CarrierHeroId, state.IsActive));
        }
        foreach (var state in snapshot.SuccessionLaws)
        {
            var record = Construct(successionType);
            RequireField(successionType, "KingdomId").SetValue(record, state.KingdomId);
            var gender = RequireField(successionType, "GenderLaw");
            var house = RequireField(successionType, "SuccessionLaw");
            gender.SetValue(record, Enum.ToObject(gender.FieldType, state.GenderLaw));
            house.SetValue(record, Enum.ToObject(house.FieldType, state.SuccessionLaw));
            laws.Add(state.KingdomId, record);
        }
        foreach (var state in snapshot.Factions)
        {
            var parent = ResolveKingdom(state.ParentKingdomId);
            var leader = ResolveClan(state.LeaderClanId);
            var enumType = factionType.GetConstructors(Any).Single(c => c.GetParameters().Length == 4).GetParameters()[3].ParameterType;
            var record = Construct(factionType, state.Name, parent, leader, Enum.ToObject(enumType, state.Type));
            Set(record, "_members", List(RequireField(factionType, "_members").FieldType, state.MemberClanIds.Select(ResolveClan)));
            Set(record, "_discontent", Convert.ToSingle(state.Discontent, CultureInfo.InvariantCulture));
            Set(record, "_peakDiscontent", Convert.ToSingle(state.PeakDiscontent, CultureInfo.InvariantCulture));
            Set(record, "_mood", Convert.ToSingle(state.Mood, CultureInfo.InvariantCulture));
            Set(record, "_loyalClan", OptionalClan(state.LoyalClanId));
            Set(record, "_creationDate", CampaignTime.Days(Convert.ToSingle(state.CreationDay, CultureInfo.InvariantCulture)));
            Set(record, "_rebelKingdom", OptionalKingdom(state.RebelKingdomId));
            Set(record, "_rebelKingdomStringId", state.RebelKingdomStringId);
            Set(record, "_civilWarStartFiefClans", List(RequireField(factionType, "_civilWarStartFiefClans").FieldType,
                state.CivilWarStartFiefs.Select(x => ResolveClan(x.ClanId))));
            Set(record, "_civilWarStartFiefCounts", List(RequireField(factionType, "_civilWarStartFiefCounts").FieldType,
                state.CivilWarStartFiefs.Select(x => (object)x.Value)));
            Set(record, "_civilWarStartInfluenceClans", List(RequireField(factionType, "_civilWarStartInfluenceClans").FieldType,
                state.CivilWarStartInfluence.Select(x => ResolveClan(x.ClanId))));
            Set(record, "_civilWarStartInfluenceValues", List(RequireField(factionType, "_civilWarStartInfluenceValues").FieldType,
                state.CivilWarStartInfluence.Select(x => (object)Convert.ToSingle(x.Value, CultureInfo.InvariantCulture))));
            Set(record, "_isGrandCoalition", state.IsGrandCoalition);
            Set(record, "_grandCoalitionSourceIdeology", Enum.ToObject(RequireField(factionType, "_grandCoalitionSourceIdeology").FieldType, state.GrandCoalitionSourceIdeology));
            Set(record, "_hasGrandCoalitionSourceIdeology", state.HasGrandCoalitionSourceIdeology);
            Set(record, "_ultimatumPending", state.UltimatumPending);
            Set(record, "_ultimatumResolved", state.UltimatumResolved);
            Set(record, "_solidarityRecruitmentApplied", state.SolidarityRecruitmentApplied);
            Set(record, "_rebellionCreationStarted", state.RebellionCreationStarted);
            Set(record, "_rebellionCreationCompleted", state.RebellionCreationCompleted);
            factions.Add(record);
        }
        foreach (var state in snapshot.Treaties)
        {
            var stateField = RequireField(treatyType, "_state");
            var termsField = RequireField(treatyType, "_terms");
            var terms = (IList)Activator.CreateInstance(termsField.FieldType)!;
            var termEnumType = treatyTermType.GetConstructors(Any).Single(c => c.GetParameters().Length == 15).GetParameters()[0].ParameterType;
            foreach (var term in state.Terms)
            {
                terms.Add(Construct(treatyTermType, Enum.ToObject(termEnumType, term.Type), term.WarScoreCost,
                    term.SettlementId, term.GoldAmount, term.DailyGold, term.DurationDays, term.FromKingdomId,
                    term.ToKingdomId, term.WasOccupiedAtDrafting, term.HeroId, term.ClanId, term.TitleId,
                    term.SecondaryHeroId, term.ThirdKingdomId, term.WasVoluntaryOffering));
            }
            var record = Construct(treatyType, state.WarKey, state.WinnerKingdomId, state.LoserKingdomId,
                state.DrafterKingdomId, Convert.ToSingle(state.CreatedDay, CultureInfo.InvariantCulture),
                state.WarScoreBudget, state.IsForced);
            Set(record, "_proposalId", state.ProposalId);
            Set(record, "_warKey", state.WarKey);
            Set(record, "_winnerKingdomId", state.WinnerKingdomId);
            Set(record, "_loserKingdomId", state.LoserKingdomId);
            Set(record, "_createdDay", Convert.ToSingle(state.CreatedDay, CultureInfo.InvariantCulture));
            Set(record, "_warScoreBudget", state.WarScoreBudget);
            Set(record, "_isForced", state.IsForced);
            stateField.SetValue(record, Enum.ToObject(stateField.FieldType, state.State));
            termsField.SetValue(record, terms);
            Set(record, "_winnerSupport", Convert.ToSingle(state.WinnerSupport, CultureInfo.InvariantCulture));
            Set(record, "_loserSupport", Convert.ToSingle(state.LoserSupport, CultureInfo.InvariantCulture));
            Set(record, "_winnerOverrideCost", state.WinnerOverrideCost);
            Set(record, "_loserOverrideCost", state.LoserOverrideCost);
            Set(record, "_resolutionNote", state.ResolutionNote);
            Set(record, "_draftRevision", state.DraftRevision);
            Set(record, "_playerVoteStance", state.PlayerVoteStance);
            Set(record, "_playerInfluenceCommitment", state.PlayerInfluenceCommitment);
            Set(record, "_playerInfluenceSpent", state.PlayerInfluenceSpent);
            Set(record, "_playerVoteSubmitted", state.PlayerVoteSubmitted);
            Set(record, "_drafterKingdomId", state.DrafterKingdomId);
            proposals.Add(record);
        }
        foreach (var state in snapshot.Tributes)
        {
            var record = Construct(tributeType, state.PayerKingdomId, state.RecipientKingdomId, state.DailyGold, state.RemainingDays);
            Set(record, "_payerKingdomId", state.PayerKingdomId);
            Set(record, "_recipientKingdomId", state.RecipientKingdomId);
            Set(record, "_dailyGold", state.DailyGold);
            Set(record, "_remainingDays", state.RemainingDays);
            tributes.Add(record);
        }
        foreach (var state in snapshot.WarScores)
        {
            var fiefsField = RequireField(warScoreType, "_fiefSnapshots");
            var eventsField = RequireField(warScoreType, "_events");
            var conflictField = RequireField(warScoreType, "_conflictType");
            var fiefs = (IList)Activator.CreateInstance(fiefsField.FieldType)!;
            foreach (var fief in state.FiefSnapshots)
                fiefs.Add(Construct(warScoreFiefType, fief.SettlementId, fief.OwnerKingdomId, fief.OwnerClanId, fief.IsTown, fief.IsCastle));
            var conflictType = Enum.ToObject(conflictField.FieldType, state.ConflictType);
            var record = Construct(warScoreType, state.WarKey, state.AttackerKingdomId, state.DefenderKingdomId,
                Convert.ToSingle(state.StartedDay, CultureInfo.InvariantCulture), fiefs, conflictType, state.ContextId);
            var events = (IList)Activator.CreateInstance(eventsField.FieldType)!;
            var eventEnumType = warScoreEventType.GetConstructors(Any).Single(c => c.GetParameters().Length == 9).GetParameters()[0].ParameterType;
            foreach (var scoreEvent in state.Events)
            {
                var eventRecord = Construct(warScoreEventType, Enum.ToObject(eventEnumType, scoreEvent.EventType),
                    Convert.ToSingle(scoreEvent.Day, CultureInfo.InvariantCulture), Convert.ToSingle(scoreEvent.Delta, CultureInfo.InvariantCulture),
                    Convert.ToSingle(scoreEvent.ScoreAfter, CultureInfo.InvariantCulture), scoreEvent.ActorKingdomId,
                    scoreEvent.TargetKingdomId, scoreEvent.SettlementId, scoreEvent.HeroId, scoreEvent.DebugText);
                Set(eventRecord, "_eventId", scoreEvent.EventId);
                events.Add(eventRecord);
            }
            Set(record, "_warKey", state.WarKey);
            Set(record, "_attackerKingdomId", state.AttackerKingdomId);
            Set(record, "_defenderKingdomId", state.DefenderKingdomId);
            Set(record, "_startedDay", Convert.ToSingle(state.StartedDay, CultureInfo.InvariantCulture));
            Set(record, "_endedDay", Convert.ToSingle(state.EndedDay, CultureInfo.InvariantCulture));
            Set(record, "_score", Convert.ToSingle(state.Score, CultureInfo.InvariantCulture));
            Set(record, "_isActive", state.IsActive);
            fiefsField.SetValue(record, fiefs);
            eventsField.SetValue(record, events);
            Set(record, "_occupationScore", Convert.ToSingle(state.OccupationScore, CultureInfo.InvariantCulture));
            Set(record, "_battleScore", Convert.ToSingle(state.BattleScore, CultureInfo.InvariantCulture));
            Set(record, "_raidScore", Convert.ToSingle(state.RaidScore, CultureInfo.InvariantCulture));
            Set(record, "_prisonerScore", Convert.ToSingle(state.PrisonerScore, CultureInfo.InvariantCulture));
            Set(record, "_tickingScore", Convert.ToSingle(state.TickingScore, CultureInfo.InvariantCulture));
            Set(record, "_lastTickDay", Convert.ToSingle(state.LastTickDay, CultureInfo.InvariantCulture));
            conflictField.SetValue(record, conflictType);
            Set(record, "_contextId", state.ContextId);
            Set(record, "_resolutionPending", state.ResolutionPending);
            Set(record, "_objectiveScore", Convert.ToSingle(state.ObjectiveScore, CultureInfo.InvariantCulture));
            Set(record, "_nextWhitePeaceCheckDay", Convert.ToSingle(state.NextWhitePeaceCheckDay, CultureInfo.InvariantCulture));
            Set(record, "_whitePeaceOfferPending", state.WhitePeaceOfferPending);
            Set(record, "_parleyPending", state.ParleyPending);
            Set(record, "_parleyForced", state.ParleyForced);
            Set(record, "_parleyOpenedDay", Convert.ToSingle(state.ParleyOpenedDay, CultureInfo.InvariantCulture));
            Set(record, "_terminalResolutionQueued", state.TerminalResolutionQueued);
            Set(record, "_terminalResolutionReason", state.TerminalResolutionReason);
            Set(record, "_landlessPressureScore", Convert.ToSingle(state.LandlessPressureScore, CultureInfo.InvariantCulture));
            wars.Add(record);
        }
        foreach (var state in snapshot.CouncilOffices)
        {
            var officeField = RequireField(councilOfficeType, "_office");
            var office = Enum.ToObject(officeField.FieldType, state.Office);
            var record = Construct(councilOfficeType, state.KingdomId, office,
                Convert.ToSingle(state.LastUpdatedDay, CultureInfo.InvariantCulture));
            Set(record, "_recordId", state.RecordId);
            Set(record, "_kingdomId", state.KingdomId);
            officeField.SetValue(record, office);
            Set(record, "_holderClanId", state.HolderClanId);
            Set(record, "_controversy", Convert.ToSingle(state.Controversy, CultureInfo.InvariantCulture));
            Set(record, "_vacancyStartedDay", Convert.ToSingle(state.VacancyStartedDay, CultureInfo.InvariantCulture));
            Set(record, "_appointedDay", Convert.ToSingle(state.AppointedDay, CultureInfo.InvariantCulture));
            Set(record, "_lastUpdatedDay", Convert.ToSingle(state.LastUpdatedDay, CultureInfo.InvariantCulture));
            Set(record, "_isInitialized", state.IsInitialized);
            Set(record, "_legacyControversyMigrated", state.LegacyControversyMigrated);
            Set(record, "_lastReason", state.LastReason);
            Set(record, "_lastChange", Convert.ToSingle(state.LastChange, CultureInfo.InvariantCulture));
            Set(record, "_lastRealmMetric", Convert.ToSingle(state.LastRealmMetric, CultureInfo.InvariantCulture));
            Set(record, "_assignmentId", state.AssignmentId);
            Set(record, "_lastAssignmentChangedDay", Convert.ToSingle(state.LastAssignmentChangedDay, CultureInfo.InvariantCulture));
            Set(record, "_nextAssignmentReviewDay", Convert.ToSingle(state.NextAssignmentReviewDay, CultureInfo.InvariantCulture));
            offices.Add(record);
        }
        foreach (var state in snapshot.WarWill)
        {
            warWill.Add(state.ClanId, Convert.ToSingle(state.Value, CultureInfo.InvariantCulture));
            warWillNext.Add(state.ClanId, state.NextEvaluationDay);
            warWillTargets.Add(state.ClanId, state.PreferredTargetKingdomId);
        }
        foreach (var state in snapshot.WarWillPressures)
        {
            var enumType = warWillPressureType.GetConstructors(Any).Single(c => c.GetParameters().Length == 12).GetParameters()[8].ParameterType;
            var record = Construct(warWillPressureType, state.RecordId, state.ClanId, state.TargetKingdomId,
                state.ConflictKey, Convert.ToSingle(state.Amount, CultureInfo.InvariantCulture), state.Reason,
                Convert.ToSingle(state.CreatedDay, CultureInfo.InvariantCulture), Convert.ToSingle(state.ExpiresDay, CultureInfo.InvariantCulture),
                Enum.ToObject(enumType, state.ReasonType), state.ContextSettlementId, state.ContextTitleId, state.ContextHeroId);
            Set(record, "_isActive", state.IsActive);
            pressures.Add(record);
        }
        foreach (var state in snapshot.ForeignWars) foreignWars.Add(ForeignWar(foreignWarType, state));
        foreach (var state in snapshot.PendingForeignWars) pendingForeignWars.Add(ForeignWar(foreignWarType, state));
        foreach (var state in snapshot.DynasticSuccessions)
        {
            var record = Construct(dynasticType, state.KingdomId, state.RightfulDynastyClanId, state.HeirHeroId,
                state.ClaimCarrierClanId, state.ClaimCarrierHeroId, state.Source,
                CampaignTime.Days(Convert.ToSingle(state.LockedUntilDay, CultureInfo.InvariantCulture)),
                Convert.ToSingle(state.LastUpdatedDay, CultureInfo.InvariantCulture));
            dynasticStates.Add(state.KingdomId, record);
        }
        foreach (var state in snapshot.DynamicRelations)
            serializedRelations.Add(state.PairKey, Construct(relationRecordType, state.LastRecordedValue,
                Convert.ToSingle(state.LastUpdateDay, CultureInfo.InvariantCulture)));
        foreach (var state in snapshot.RelationMaterializedValues)
            materializedRelations.Add(state.PairKey, state.Value);
        var memoryScopeType = relationMemoryType.GetConstructors(Any).Single(c => c.GetParameters().Length == 9).GetParameters()[0].ParameterType;
        foreach (var state in snapshot.RelationMemories)
            relationMemories.Add(Construct(relationMemoryType, Enum.ToObject(memoryScopeType, state.Scope), state.FirstId,
                state.SecondId, state.SourceId, state.ContextText, state.Value,
                Convert.ToSingle(state.StartDay, CultureInfo.InvariantCulture), Convert.ToSingle(state.ExpiryDay, CultureInfo.InvariantCulture),
                Convert.ToSingle(state.LegacyWeeklyDecay, CultureInfo.InvariantCulture)));
        foreach (var state in snapshot.Regencies)
            regencies.Add(Construct(regencyType, state.ClanId, state.WardHeroId, state.RegentHeroId, state.PredecessorHeroId,
                CampaignTime.Days(Convert.ToSingle(state.StartedDay, CultureInfo.InvariantCulture)), state.RegentWasGenerated));
        var claimStrengthType = claimFeudType.GetConstructors(Any).Single(c => c.GetParameters().Length == 10).GetParameters()[5].ParameterType;
        foreach (var state in snapshot.ClaimFeuds)
        {
            var record = Construct(claimFeudType, state.RecordId, state.ParentKingdomId, state.ClaimantClanId,
                state.HolderClanId, state.TargetTitleId, Enum.ToObject(claimStrengthType, state.ClaimStrength),
                Convert.ToSingle(state.Pressure, CultureInfo.InvariantCulture), Convert.ToSingle(state.StartedDay, CultureInfo.InvariantCulture),
                state.SourceClaimId, state.DebugReason);
            Set(record, "_state", Enum.ToObject(RequireField(claimFeudType, "_state").FieldType, state.State));
            Set(record, "_lastTickDay", Convert.ToSingle(state.LastTickDay, CultureInfo.InvariantCulture));
            Set(record, "_cooldownUntilDay", Convert.ToSingle(state.CooldownUntilDay, CultureInfo.InvariantCulture));
            Set(record, "_judgment", Enum.ToObject(RequireField(claimFeudType, "_judgment").FieldType, state.Judgment));
            Set(record, "_claimantResponse", Enum.ToObject(RequireField(claimFeudType, "_claimantResponse").FieldType, state.ClaimantResponse));
            Set(record, "_holderResponse", Enum.ToObject(RequireField(claimFeudType, "_holderResponse").FieldType, state.HolderResponse));
            Set(record, "_rulingDay", Convert.ToSingle(state.RulingDay, CultureInfo.InvariantCulture));
            Set(record, "_claimantSidePower", Convert.ToSingle(state.ClaimantSidePower, CultureInfo.InvariantCulture));
            Set(record, "_holderSidePower", Convert.ToSingle(state.HolderSidePower, CultureInfo.InvariantCulture));
            Set(record, "_claimantSupporterIds", string.Join(",", state.ClaimantSupporterClanIds));
            Set(record, "_holderSupporterIds", string.Join(",", state.HolderSupporterClanIds));
            Set(record, "_hasPassed33", state.HasPassed33); Set(record, "_hasPassed66", state.HasPassed66);
            Set(record, "_resumeState", Enum.ToObject(RequireField(claimFeudType, "_resumeState").FieldType, state.ResumeState));
            Set(record, "_pauseReason", state.PauseReason); Set(record, "_pausedDay", Convert.ToSingle(state.PausedDay, CultureInfo.InvariantCulture));
            claimFeuds.Add(record);
        }
        foreach (var state in snapshot.MercenaryBands)
            mercenaryBands.Add(Construct(mercenaryBandType, state.ClanId, state.FounderHeroId, state.SourceClanId,
                state.CultureId, state.HomeSettlementId, CampaignTime.Days(Convert.ToSingle(state.CreatedDay, CultureInfo.InvariantCulture))));
        foreach (var state in snapshot.MercenaryDepartures)
        {
            var record = Construct(mercenaryDepartureType, state.HeroId,
                CampaignTime.Days(Convert.ToSingle(state.QueuedDay, CultureInfo.InvariantCulture)),
                Convert.ToSingle(state.ObedienceRoll, CultureInfo.InvariantCulture));
            Set(record, "_resolution", state.Resolution); mercenaryDepartures.Add(record);
        }
        foreach (var state in snapshot.ClientKingdoms)
            clientKingdoms.Add(Construct(clientKingdomType, state.ClientKingdomId, state.SuzerainKingdomId,
                Convert.ToSingle(state.StartedDay, CultureInfo.InvariantCulture), state.WasVoluntary,
                Convert.ToSingle(state.LiberationCooldownUntilDay, CultureInfo.InvariantCulture)));
        var serviceLevelType = serviceType.GetConstructors(Any).Single(c => c.GetParameters().Length == 7).GetParameters()[3].ParameterType;
        foreach (var state in snapshot.FeudalServices)
            services.Add(state.RecordId, Construct(serviceType, state.RecordId, state.ChildTitleId, state.ParentTitleId,
                Enum.ToObject(serviceLevelType, state.Level), Convert.ToSingle(state.LastChangedDay, CultureInfo.InvariantCulture),
                state.ChangedByClanId, state.Reason));
        var driftStateType = driftType.GetConstructors(Any).Single(c => c.GetParameters().Length == 9).GetParameters()[7].ParameterType;
        foreach (var state in snapshot.DeJureDrifts)
            drifts.Add(Construct(driftType, state.TitleId, state.OriginalParentTitleId, state.TargetParentTitleId,
                state.TargetKingdomId, Convert.ToSingle(state.Progress, CultureInfo.InvariantCulture),
                Convert.ToSingle(state.StartedDay, CultureInfo.InvariantCulture), Convert.ToSingle(state.LastEvaluatedDay, CultureInfo.InvariantCulture),
                Enum.ToObject(driftStateType, state.State), state.IsActive));
        var fabricationTrackType = fabricationType.GetConstructors(Any).Single(c => c.GetParameters().Length == 11).GetParameters()[4].ParameterType;
        foreach (var state in snapshot.ClaimFabrications)
        {
            var record = Construct(fabricationType, state.FabricationId, state.FabricatorHeroId, state.FabricatorClanId,
                state.TargetTitleId, Enum.ToObject(fabricationTrackType, state.Track), Convert.ToSingle(state.Progress, CultureInfo.InvariantCulture),
                Convert.ToSingle(state.DailyProgressDelta, CultureInfo.InvariantCulture), Convert.ToSingle(state.StartedDay, CultureInfo.InvariantCulture),
                state.PaidGoldCost, Convert.ToSingle(state.PaidInfluenceCost, CultureInfo.InvariantCulture), state.IsActive);
            Set(record, "_hasPassed33", state.HasPassed33); Set(record, "_hasPassed66", state.HasPassed66);
            Set(record, "_hasPassed100", state.HasPassed100); Set(record, "_legacyIsPaused", state.LegacyIsPaused);
            Set(record, "_failedStage", state.FailedStage); fabrications.Add(record);
        }
        foreach (var state in snapshot.PendingFabricationOutcomes)
            fabricationOutcomes.Add(Construct(fabricationOutcomeType, state.Title, state.Body));
        foreach (var state in snapshot.ObjectTimers)
        {
            var when = CampaignTime.Days(Convert.ToSingle(state.Day, CultureInfo.InvariantCulture));
            if (state.Scope == "protected-rebel-kingdom") protectedKingdoms.Add(ResolveKingdom(state.ObjectId), when);
            else if (state.Scope == "pacified-clan") pacifiedClans.Add(ResolveClan(state.ObjectId), when);
            else if (state.Scope == "settlement-sync-clan") settlementStamps.Add(ResolveClan(state.ObjectId), when);
        }
        foreach (var clanId in snapshot.PendingSettlementRefreshClanIds) pendingSettlementClans.Add(ResolveClan(clanId));
        foreach (var state in snapshot.ObjectIntegers)
            if (state.Scope == "kingdom-controversy") controversy.Add(state.ObjectId, state.Value);
        foreach (var state in snapshot.ClaimFeudWars)
        {
            var record = Construct(claimFeudWarType, state.WarId, state.FeudRecordId, state.ParentKingdomId,
                state.ClaimantKingdomId, state.HolderKingdomId, state.TargetTitleId, state.ClaimantLeaderClanId,
                state.HolderLeaderClanId, state.ClaimantClanIds, state.HolderClanIds, state.InfluenceSnapshot,
                state.FiefSnapshot, Convert.ToSingle(state.StartedDay, CultureInfo.InvariantCulture));
            Set(record, "_isActive", state.IsActive); Set(record, "_pendingCaptureWinnerClanId", state.PendingCaptureWinnerClanId);
            Set(record, "_claimantControlsObjective", state.ClaimantControlsObjective);
            Set(record, "_claimantHasControlledObjective", state.ClaimantHasControlledObjective);
            Set(record, "_pendingResolutionOutcome", state.PendingResolutionOutcome);
            Set(record, "_pendingResolutionReason", state.PendingResolutionReason); claimFeudWars.Add(record);
        }
        foreach (var state in snapshot.PendingCadetMarriages)
        {
            var record = Construct(cadetMarriageType, state.HeiressId, state.SpouseId, state.OriginKingdomId,
                state.DynastyClanId, state.BrideClanId, state.MarriedClanId,
                CampaignTime.Days(Convert.ToSingle(state.ReadyDay, CultureInfo.InvariantCulture)), state.MarriageGold);
            Set(record, "_attempts", state.Attempts);
            Set(record, "_expiresOn", CampaignTime.Days(Convert.ToSingle(state.ExpiresDay, CultureInfo.InvariantCulture)));
            cadetMarriages.Add(record);
        }
        foreach (var state in snapshot.PendingUsurpations) usurpations.Add(Construct(usurpationType, state.TitleId, state.ClaimantClanId));
        foreach (var state in snapshot.PendingTreatyRebelResolutions)
            treatyRebelResolutions.Add(Construct(treatyRebelResolutionType, state.ProposalId, state.RebelKingdomId,
                state.ParentKingdomId, state.BeneficiaryKingdomId));
        foreach (var state in snapshot.PendingPartitions)
            partitions.Add(Construct(partitionType, state.DeadLeaderId, state.ParentClanId, state.KingdomId,
                state.FiefIds, state.HeirIds, CampaignTime.Days(Convert.ToSingle(state.ReadyDay, CultureInfo.InvariantCulture)),
                state.ParentWasRulingClanAtDeath, state.TitleIds, state.PrimarySovereignTitleId));
        var genderLawType = genderEscheatType.GetConstructors(Any).Single(c => c.GetParameters().Length == 7).GetParameters()[3].ParameterType;
        foreach (var state in snapshot.PendingGenderLineEscheats)
            genderEscheats.Add(Construct(genderEscheatType, state.TriggerHeroId, state.ClanId, state.KingdomId,
                Enum.ToObject(genderLawType, state.GenderLaw), CampaignTime.Days(Convert.ToSingle(state.ReadyDay, CultureInfo.InvariantCulture)),
                state.WasRulingClan, state.DynasticLineExtinction));

        if (!commit) return;
        titlesField.SetValue(titleBehavior, titles);
        claimsField.SetValue(titleBehavior, claims);
        lawsField.SetValue(successionBehavior, laws);
        factionsField.SetValue(factionBehavior, factions);
        proposalsField.SetValue(treatyBehavior, proposals);
        tributesField.SetValue(treatyBehavior, tributes);
        warsField.SetValue(warScoreBehavior, wars);
        officesField.SetValue(councilBehavior, offices);
        warWillField.SetValue(warWillBehavior, warWill);
        warWillNextField.SetValue(warWillBehavior, warWillNext);
        warWillTargetField.SetValue(warWillBehavior, warWillTargets);
        pressuresField.SetValue(warWillBehavior, pressures);
        foreignWarsField.SetValue(foreignPolicyBehavior, foreignWars);
        pendingForeignWarsField.SetValue(foreignPolicyBehavior, pendingForeignWars);
        dynasticStatesField.SetValue(dynasticBehavior, dynasticStates);
        serializedRelationsField.SetValue(relationBehavior, serializedRelations);
        RequireField(relationBehavior.GetType(), "_memorySchemaVersion").SetValue(relationBehavior, snapshot.RelationMemorySchemaVersion);
        RequireField(relationBehavior.GetType(), "_appliedMemoryDurationMultiplier").SetValue(relationBehavior,
            Convert.ToSingle(snapshot.RelationMemoryDurationMultiplier, CultureInfo.InvariantCulture));
        RequireField(relationBehavior.GetType(), "_memoryDurationReady").SetValue(relationBehavior, true);
        relationMemoriesField.SetValue(relationBehavior, relationMemories);
        materializedRelationsField.SetValue(relationBehavior, materializedRelations);
        regenciesField.SetValue(regencyBehavior, regencies);
        claimFeudsField.SetValue(claimFeudBehavior, claimFeuds);
        mercenaryBandsField.SetValue(mercenaryBehavior, mercenaryBands);
        mercenaryDeparturesField.SetValue(mercenaryBehavior, mercenaryDepartures);
        clientKingdomsField.SetValue(clientKingdomBehavior, clientKingdoms);
        servicesField.SetValue(serviceBehavior, services);
        driftsField.SetValue(driftBehavior, drifts);
        fabricationsField.SetValue(fabricationBehavior, fabrications);
        fabricationOutcomesField.SetValue(fabricationBehavior, fabricationOutcomes);
        protectedKingdomsField.SetValue(interventionBehavior, protectedKingdoms);
        pacifiedClansField.SetValue(factionBehavior, pacifiedClans);
        settlementStampsField.SetValue(factionBehavior, settlementStamps);
        pendingSettlementClansField.SetValue(factionBehavior, pendingSettlementClans);
        controversyField.SetValue(controversyBehavior, controversy);
        claimFeudWarsField.SetValue(claimFeudWarBehavior, claimFeudWars);
        cadetMarriagesField.SetValue(dynasticBehavior, cadetMarriages);
        usurpationsField.SetValue(usurpationBehavior, usurpations);
        treatyRebelResolutionsField.SetValue(treatyBehavior, treatyRebelResolutions);
        partitionsField.SetValue(partitionBehavior, partitions);
        genderEscheatsField.SetValue(successionBehavior, genderEscheats);
        RequireMethod(titleBehavior.GetType(), "RebuildRuntimeIndexes", typeof(bool)).Invoke(titleBehavior, new object[] { false });
        RequireMethod(factionBehavior.GetType(), "InvalidateFactionLookupCache", typeof(bool)).Invoke(factionBehavior, new object[] { false });
        RequireMethod(warScoreBehavior.GetType(), "TouchRuntimeRevision").Invoke(warScoreBehavior, null);
        RequireMethod(councilBehavior.GetType(), "InvalidateRuntimeCache", typeof(bool)).Invoke(councilBehavior, new object[] { true });
        ((IDictionary)RequireField(warWillBehavior.GetType(), "_targetScoresByClanId").GetValue(warWillBehavior)!).Clear();
        RequireMethod(Type(ForeignPolicyCacheName), "InvalidateCache").Invoke(null, null);
        RequireMethod(dynasticBehavior.GetType(), "RebuildHeroResolutionCache").Invoke(dynasticBehavior, null);
        RequireMethod(relationBehavior.GetType(), "RebuildObjectResolutionCaches").Invoke(relationBehavior, null);
        RequireMethod(relationBehavior.GetType(), "RebuildRuntimeRecordsFromSerialized").Invoke(relationBehavior, null);
        RequireMethod(relationBehavior.GetType(), "RebuildRuntimeMaterializedValues").Invoke(relationBehavior, null);
        RequireMethod(relationBehavior.GetType(), "RebuildMemoryIndexes").Invoke(relationBehavior, null);
        RequireMethod(relationBehavior.GetType(), "InvalidateBaselineCache").Invoke(relationBehavior, null);
        RequireMethod(driftBehavior.GetType(), "RebuildRuntimeState").Invoke(driftBehavior, null);
        RequireMethod(usurpationBehavior.GetType(), "RebuildPendingKeyIndex").Invoke(usurpationBehavior, null);
        ((IDictionary)RequireField(controversyBehavior.GetType(), "_dailyDynamicCacheById").GetValue(controversyBehavior)!).Clear();
        var serviceRevision = serviceBehavior.GetType().GetProperty("RuntimeRevision", Any)
            ?? throw new MissingMemberException(serviceBehavior.GetType().FullName, "RuntimeRevision");
        serviceRevision.SetValue(serviceBehavior, Convert.ToInt32(serviceRevision.GetValue(serviceBehavior, null), CultureInfo.InvariantCulture) + 1, null);
    }

    private static object Behavior(string name)
    {
        var type = Type(name);
        var instance = type.GetProperty("Instance", Any)?.GetValue(null, null);
        if (instance != null) return instance;
        var getter = typeof(Campaign).GetMethods(Any).Single(m => m.Name == "GetCampaignBehavior" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0);
        return getter.MakeGenericMethod(type).Invoke(Campaign.Current, null)
            ?? throw new InvalidOperationException("Bellum behavior is unavailable: " + name);
    }

    private static Type Type(string name) => AccessTools.TypeByName(name) ?? throw new MissingMemberException(name);
    private static FieldInfo RequireField(Type type, string name)
        => type.GetField(name, Any) ?? throw new MissingFieldException(type.FullName, name);
    private static MethodInfo RequireMethod(Type type, string name, params Type[] arguments)
        => type.GetMethod(name, Any, null, arguments, null) ?? throw new MissingMethodException(type.FullName, name);
    private static ConstructorInfo RequireConstructor(Type type, int parameterCount)
        => type.GetConstructors(Any).SingleOrDefault(c => c.GetParameters().Length == parameterCount)
            ?? throw new MissingMethodException(type.FullName, ".ctor/" + parameterCount);
    private static object Construct(Type type, params object[] arguments)
        => RequireConstructor(type, arguments.Length).Invoke(arguments);
    private static object ForeignWar(Type type, BellumForeignWarState state)
    {
        var motiveType = type.GetConstructors(Any).Single(c => c.GetParameters().Length == 11).GetParameters()[3].ParameterType;
        var record = Construct(type, state.WarKey, state.AttackerKingdomId, state.DefenderKingdomId,
            Enum.ToObject(motiveType, state.DeclaredMotive), state.SponsorFactionType, state.SponsorClanId,
            state.TargetTitleIds, Convert.ToSingle(state.StartingAttackerStrength, CultureInfo.InvariantCulture),
            Convert.ToSingle(state.StartingDefenderStrength, CultureInfo.InvariantCulture),
            Convert.ToSingle(state.StartedDay, CultureInfo.InvariantCulture), Convert.ToSingle(state.ContextCreatedDay, CultureInfo.InvariantCulture));
        Set(record, "_publicDeclarationMotiveType", state.PublicDeclarationMotiveType);
        Set(record, "_publicDeclarationMotiveSubject", new TextObject(state.PublicDeclarationMotiveSubject));
        Set(record, "_hasPublicDeclarationMotive", state.HasPublicDeclarationMotive);
        return record;
    }
    private static void Set(object owner, string field, object? value) => RequireField(owner.GetType(), field).SetValue(owner, value);
    private static IList List(Type type, System.Collections.Generic.IEnumerable<object> values)
    {
        var list = (IList)Activator.CreateInstance(type)!;
        foreach (var value in values) list.Add(value);
        return list;
    }
    private static Clan ResolveClan(string id) => OptionalClan(id) ?? throw new InvalidOperationException("Bellum faction clan is unavailable: " + id);
    private static Clan? OptionalClan(string id) => string.IsNullOrEmpty(id) ? null : Clan.All.FirstOrDefault(x => x != null && x.StringId == id);
    private static Kingdom ResolveKingdom(string id) => OptionalKingdom(id) ?? throw new InvalidOperationException("Bellum faction kingdom is unavailable: " + id);
    private static Kingdom? OptionalKingdom(string id) => string.IsNullOrEmpty(id) ? null : Kingdom.All.FirstOrDefault(x => x != null && x.StringId == id);
}
