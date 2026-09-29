using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ModderLords.Operations;

/// <summary>
/// Bellum Civile 1.3.1's replicated read model. It contains stable IDs and values only: never live TaleWorlds or
/// Bellum behavior objects. The dedicated server remains the persistence and simulation authority.
/// </summary>
public sealed class BellumStateSnapshot
{
    public int SchemaVersion { get; set; } = 1;
    public long Revision { get; set; }
    public double CapturedDay { get; set; }
    public List<BellumTitleState> Titles { get; set; } = new List<BellumTitleState>();
    public List<BellumClaimState> Claims { get; set; } = new List<BellumClaimState>();
    public List<BellumFactionState> Factions { get; set; } = new List<BellumFactionState>();
    public List<BellumTreatyState> Treaties { get; set; } = new List<BellumTreatyState>();
    public List<BellumTributeState> Tributes { get; set; } = new List<BellumTributeState>();
    public List<BellumSuccessionLawState> SuccessionLaws { get; set; } = new List<BellumSuccessionLawState>();
    public List<BellumRegencyState> Regencies { get; set; } = new List<BellumRegencyState>();
    public List<BellumCouncilOfficeState> CouncilOffices { get; set; } = new List<BellumCouncilOfficeState>();
    public List<BellumClaimFeudState> ClaimFeuds { get; set; } = new List<BellumClaimFeudState>();
    public List<BellumMercenaryBandState> MercenaryBands { get; set; } = new List<BellumMercenaryBandState>();
    public List<BellumWarScoreState> WarScores { get; set; } = new List<BellumWarScoreState>();
    public List<BellumWarWillState> WarWill { get; set; } = new List<BellumWarWillState>();
    public List<BellumWarWillPressureState> WarWillPressures { get; set; } = new List<BellumWarWillPressureState>();
    public List<BellumClientKingdomState> ClientKingdoms { get; set; } = new List<BellumClientKingdomState>();
    public List<BellumFeudalServiceState> FeudalServices { get; set; } = new List<BellumFeudalServiceState>();
    public List<BellumDeJureDriftState> DeJureDrifts { get; set; } = new List<BellumDeJureDriftState>();
    public List<BellumClaimFabricationState> ClaimFabrications { get; set; } = new List<BellumClaimFabricationState>();
    public List<BellumForeignWarState> ForeignWars { get; set; } = new List<BellumForeignWarState>();
    public List<BellumForeignWarState> PendingForeignWars { get; set; } = new List<BellumForeignWarState>();
    public List<BellumObjectTimerState> ObjectTimers { get; set; } = new List<BellumObjectTimerState>();
    public List<BellumObjectIntegerState> ObjectIntegers { get; set; } = new List<BellumObjectIntegerState>();
    public List<string> PendingSettlementRefreshClanIds { get; set; } = new List<string>();
    public List<BellumClaimFeudWarState> ClaimFeudWars { get; set; } = new List<BellumClaimFeudWarState>();
    public List<BellumMercenaryDepartureState> MercenaryDepartures { get; set; } = new List<BellumMercenaryDepartureState>();
    public List<BellumDynamicRelationState> DynamicRelations { get; set; } = new List<BellumDynamicRelationState>();
    public int RelationMemorySchemaVersion { get; set; }
    public double RelationMemoryDurationMultiplier { get; set; } = 1d;
    public List<BellumRelationMaterializedValueState> RelationMaterializedValues { get; set; } = new List<BellumRelationMaterializedValueState>();
    public List<BellumRelationMemoryState> RelationMemories { get; set; } = new List<BellumRelationMemoryState>();
    public List<BellumDynasticSuccessionState> DynasticSuccessions { get; set; } = new List<BellumDynasticSuccessionState>();
    public List<BellumPendingCadetMarriageState> PendingCadetMarriages { get; set; } = new List<BellumPendingCadetMarriageState>();
    public List<BellumPendingFabricationOutcomeState> PendingFabricationOutcomes { get; set; } = new List<BellumPendingFabricationOutcomeState>();
    public List<BellumPendingUsurpationState> PendingUsurpations { get; set; } = new List<BellumPendingUsurpationState>();
    public List<BellumPendingTreatyRebelResolutionState> PendingTreatyRebelResolutions { get; set; } = new List<BellumPendingTreatyRebelResolutionState>();
    public List<BellumPendingPartitionState> PendingPartitions { get; set; } = new List<BellumPendingPartitionState>();
    public List<BellumPendingGenderLineEscheatState> PendingGenderLineEscheats { get; set; } = new List<BellumPendingGenderLineEscheatState>();
}

public sealed class BellumTitleState
{
    public string TitleId { get; set; } = "";
    public string Name { get; set; } = "";
    public int TitleType { get; set; }
    public string DeJureHolderClanId { get; set; } = "";
    public string DeFactoHolderClanId { get; set; } = "";
    public string ParentTitleId { get; set; } = "";
    public string DeFactoParentTitleId { get; set; } = "";
    public string CapitalSettlementId { get; set; } = "";
    public string AssociatedKingdomId { get; set; } = "";
    public double CreatedDay { get; set; }
    public double LastSyncedDay { get; set; }
    public bool IsActive { get; set; }
    public string FallbackCultureRef { get; set; } = "";
    public bool IsDeliberatelyDissolved { get; set; }
}

public sealed class BellumClaimState
{
    public string ClaimId { get; set; } = "";
    public string ClaimantClanId { get; set; } = "";
    public string TargetTitleId { get; set; } = "";
    public int Strength { get; set; }
    public string Source { get; set; } = "";
    public string SourceHeroId { get; set; } = "";
    public string OriginClanId { get; set; } = "";
    public double CreatedDay { get; set; }
    public double ExpiresDay { get; set; }
    public bool IsActive { get; set; }
    public int GenerationDepth { get; set; }
    public string CarrierHeroId { get; set; } = "";
}

public sealed class BellumFactionState
{
    public string FactionKey { get; set; } = "";
    public string Name { get; set; } = "";
    public string ParentKingdomId { get; set; } = "";
    public string LeaderClanId { get; set; } = "";
    public List<string> MemberClanIds { get; set; } = new List<string>();
    public int Type { get; set; }
    public double Discontent { get; set; }
    public double PeakDiscontent { get; set; }
    public double Mood { get; set; }
    public string LoyalClanId { get; set; } = "";
    public double CreationDay { get; set; }
    public string RebelKingdomId { get; set; } = "";
    public string RebelKingdomStringId { get; set; } = "";
    public List<BellumClanIntState> CivilWarStartFiefs { get; set; } = new List<BellumClanIntState>();
    public List<BellumClanNumberState> CivilWarStartInfluence { get; set; } = new List<BellumClanNumberState>();
    public bool IsUltimatumPending { get; set; }
    public bool IsGrandCoalition { get; set; }
    public int GrandCoalitionSourceIdeology { get; set; }
    public bool HasGrandCoalitionSourceIdeology { get; set; }
    public bool UltimatumPending { get; set; }
    public bool UltimatumResolved { get; set; }
    public bool SolidarityRecruitmentApplied { get; set; }
    public bool RebellionCreationStarted { get; set; }
    public bool RebellionCreationCompleted { get; set; }
}

public sealed class BellumClanIntState
{
    public string ClanId { get; set; } = "";
    public int Value { get; set; }
}

public sealed class BellumClanNumberState
{
    public string ClanId { get; set; } = "";
    public double Value { get; set; }
}

public sealed class BellumTreatyState
{
    public string ProposalId { get; set; } = "";
    public string WarKey { get; set; } = "";
    public string WinnerKingdomId { get; set; } = "";
    public string LoserKingdomId { get; set; } = "";
    public double CreatedDay { get; set; }
    public int WarScoreBudget { get; set; }
    public bool IsForced { get; set; }
    public int State { get; set; }
    public List<BellumTreatyTermState> Terms { get; set; } = new List<BellumTreatyTermState>();
    public int UsedWarScore { get; set; }
    public double WinnerSupport { get; set; }
    public double LoserSupport { get; set; }
    public int WinnerOverrideCost { get; set; }
    public int LoserOverrideCost { get; set; }
    public string ResolutionNote { get; set; } = "";
    public int DraftRevision { get; set; }
    public int PlayerVoteStance { get; set; }
    public int PlayerInfluenceCommitment { get; set; }
    public bool PlayerInfluenceSpent { get; set; }
    public bool PlayerVoteSubmitted { get; set; }
    public string DrafterKingdomId { get; set; } = "";
}

public sealed class BellumTreatyTermState
{
    public int Type { get; set; }
    public int WarScoreCost { get; set; }
    public string SettlementId { get; set; } = "";
    public int GoldAmount { get; set; }
    public int DailyGold { get; set; }
    public int DurationDays { get; set; }
    public string FromKingdomId { get; set; } = "";
    public string ToKingdomId { get; set; } = "";
    public bool WasOccupiedAtDrafting { get; set; }
    public string HeroId { get; set; } = "";
    public string ClanId { get; set; } = "";
    public string TitleId { get; set; } = "";
    public string SecondaryHeroId { get; set; } = "";
    public string ThirdKingdomId { get; set; } = "";
    public bool WasVoluntaryOffering { get; set; }
}

public sealed class BellumTributeState
{
    public string TributeKey { get; set; } = "";
    public string PayerKingdomId { get; set; } = "";
    public string RecipientKingdomId { get; set; } = "";
    public int DailyGold { get; set; }
    public int RemainingDays { get; set; }
}

public sealed class BellumSuccessionLawState
{
    public string KingdomId { get; set; } = "";
    public int GenderLaw { get; set; }
    public int SuccessionLaw { get; set; }
}

public sealed class BellumRegencyState
{
    public string ClanId { get; set; } = "";
    public string WardHeroId { get; set; } = "";
    public string RegentHeroId { get; set; } = "";
    public string PredecessorHeroId { get; set; } = "";
    public double StartedDay { get; set; }
    public bool RegentWasGenerated { get; set; }
}

public sealed class BellumCouncilOfficeState
{
    public string RecordId { get; set; } = "";
    public string KingdomId { get; set; } = "";
    public int Office { get; set; }
    public string HolderClanId { get; set; } = "";
    public double Controversy { get; set; }
    public double VacancyStartedDay { get; set; }
    public double AppointedDay { get; set; }
    public double LastUpdatedDay { get; set; }
    public bool IsInitialized { get; set; }
    public bool LegacyControversyMigrated { get; set; }
    public string LastReason { get; set; } = "";
    public double LastChange { get; set; }
    public double LastRealmMetric { get; set; }
    public string AssignmentId { get; set; } = "";
    public double LastAssignmentChangedDay { get; set; }
    public double NextAssignmentReviewDay { get; set; }
}

public sealed class BellumClaimFeudState
{
    public string RecordId { get; set; } = "";
    public string ParentKingdomId { get; set; } = "";
    public string ClaimantClanId { get; set; } = "";
    public string HolderClanId { get; set; } = "";
    public string TargetTitleId { get; set; } = "";
    public int ClaimStrength { get; set; }
    public int State { get; set; }
    public double Pressure { get; set; }
    public double StartedDay { get; set; }
    public double LastTickDay { get; set; }
    public double CooldownUntilDay { get; set; }
    public string SourceClaimId { get; set; } = "";
    public string DebugReason { get; set; } = "";
    public int Judgment { get; set; }
    public int ClaimantResponse { get; set; }
    public int HolderResponse { get; set; }
    public double RulingDay { get; set; }
    public double ClaimantSidePower { get; set; }
    public double HolderSidePower { get; set; }
    public List<string> ClaimantSupporterClanIds { get; set; } = new List<string>();
    public List<string> HolderSupporterClanIds { get; set; } = new List<string>();
    public bool HasPassed33 { get; set; }
    public bool HasPassed66 { get; set; }
    public int ResumeState { get; set; }
    public string PauseReason { get; set; } = "";
    public double PausedDay { get; set; }
}

public sealed class BellumMercenaryBandState
{
    public string ClanId { get; set; } = "";
    public string FounderHeroId { get; set; } = "";
    public string SourceClanId { get; set; } = "";
    public string CultureId { get; set; } = "";
    public string HomeSettlementId { get; set; } = "";
    public double CreatedDay { get; set; }
}

public sealed class BellumWarScoreState
{
    public string WarKey { get; set; } = "";
    public string AttackerKingdomId { get; set; } = "";
    public string DefenderKingdomId { get; set; } = "";
    public double StartedDay { get; set; }
    public double EndedDay { get; set; }
    public double Score { get; set; }
    public bool IsActive { get; set; }
    public List<BellumWarScoreFiefState> FiefSnapshots { get; set; } = new List<BellumWarScoreFiefState>();
    public List<BellumWarScoreEventState> Events { get; set; } = new List<BellumWarScoreEventState>();
    public double OccupationScore { get; set; }
    public double BattleScore { get; set; }
    public double RaidScore { get; set; }
    public double PrisonerScore { get; set; }
    public double TickingScore { get; set; }
    public double LastTickDay { get; set; }
    public int ConflictType { get; set; }
    public string ContextId { get; set; } = "";
    public bool ResolutionPending { get; set; }
    public double ObjectiveScore { get; set; }
    public double NextWhitePeaceCheckDay { get; set; }
    public bool WhitePeaceOfferPending { get; set; }
    public bool ParleyPending { get; set; }
    public bool ParleyForced { get; set; }
    public double ParleyOpenedDay { get; set; }
    public bool TerminalResolutionQueued { get; set; }
    public string TerminalResolutionReason { get; set; } = "";
    public double LandlessPressureScore { get; set; }
}

public sealed class BellumWarScoreFiefState
{
    public string SettlementId { get; set; } = "";
    public string OwnerKingdomId { get; set; } = "";
    public string OwnerClanId { get; set; } = "";
    public bool IsTown { get; set; }
    public bool IsCastle { get; set; }
}

public sealed class BellumWarScoreEventState
{
    public string EventId { get; set; } = "";
    public int EventType { get; set; }
    public double Day { get; set; }
    public double Delta { get; set; }
    public double ScoreAfter { get; set; }
    public string ActorKingdomId { get; set; } = "";
    public string TargetKingdomId { get; set; } = "";
    public string SettlementId { get; set; } = "";
    public string HeroId { get; set; } = "";
    public string DebugText { get; set; } = "";
}

public sealed class BellumWarWillState
{
    public string ClanId { get; set; } = "";
    public double Value { get; set; }
    public int NextEvaluationDay { get; set; }
    public string PreferredTargetKingdomId { get; set; } = "";
}

public sealed class BellumWarWillPressureState
{
    public string RecordId { get; set; } = "";
    public string ClanId { get; set; } = "";
    public string TargetKingdomId { get; set; } = "";
    public string ConflictKey { get; set; } = "";
    public double Amount { get; set; }
    public string Reason { get; set; } = "";
    public string ContextSettlementId { get; set; } = "";
    public string ContextTitleId { get; set; } = "";
    public string ContextHeroId { get; set; } = "";
    public double CreatedDay { get; set; }
    public double ExpiresDay { get; set; }
    public bool IsActive { get; set; }
    public int ReasonType { get; set; }
}

public sealed class BellumClientKingdomState
{
    public string ClientKingdomId { get; set; } = "";
    public string SuzerainKingdomId { get; set; } = "";
    public double StartedDay { get; set; }
    public bool WasVoluntary { get; set; }
    public double LiberationCooldownUntilDay { get; set; }
}

public sealed class BellumFeudalServiceState
{
    public string RecordId { get; set; } = "";
    public string ChildTitleId { get; set; } = "";
    public string ParentTitleId { get; set; } = "";
    public int Level { get; set; }
    public double LastChangedDay { get; set; }
    public string ChangedByClanId { get; set; } = "";
    public string Reason { get; set; } = "";
}

public sealed class BellumDeJureDriftState
{
    public string TitleId { get; set; } = "";
    public string OriginalParentTitleId { get; set; } = "";
    public string TargetParentTitleId { get; set; } = "";
    public string TargetKingdomId { get; set; } = "";
    public double Progress { get; set; }
    public double StartedDay { get; set; }
    public double LastEvaluatedDay { get; set; }
    public int State { get; set; }
    public bool IsActive { get; set; }
}

public sealed class BellumClaimFabricationState
{
    public string FabricationId { get; set; } = "";
    public string FabricatorHeroId { get; set; } = "";
    public string FabricatorClanId { get; set; } = "";
    public string TargetTitleId { get; set; } = "";
    public int Track { get; set; }
    public double Progress { get; set; }
    public double DailyProgressDelta { get; set; }
    public double StartedDay { get; set; }
    public int PaidGoldCost { get; set; }
    public double PaidInfluenceCost { get; set; }
    public bool IsActive { get; set; }
    public bool HasPassed33 { get; set; }
    public bool HasPassed66 { get; set; }
    public bool HasPassed100 { get; set; }
    public bool LegacyIsPaused { get; set; }
    public int FailedStage { get; set; }
}

public sealed class BellumForeignWarState
{
    public string WarKey { get; set; } = "";
    public string AttackerKingdomId { get; set; } = "";
    public string DefenderKingdomId { get; set; } = "";
    public int DeclaredMotive { get; set; }
    public string SponsorFactionType { get; set; } = "";
    public string SponsorClanId { get; set; } = "";
    public List<string> TargetTitleIds { get; set; } = new List<string>();
    public double StartingAttackerStrength { get; set; }
    public double StartingDefenderStrength { get; set; }
    public double StartedDay { get; set; }
    public double ContextCreatedDay { get; set; }
    public int PublicDeclarationMotiveType { get; set; }
    public string PublicDeclarationMotiveSubject { get; set; } = "";
    public bool HasPublicDeclarationMotive { get; set; }
}

public static class BellumStateCodec
{
    public const int MaxRecordsPerSection = 10000;
    public const int MaxRelationMemories = 100000;
    public const int MaxTerms = 100000;
    public const int MaxIdLength = 512;
    public const int MaxTextLength = 4096;

    private static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
    {
        Culture = CultureInfo.InvariantCulture,
        Formatting = Formatting.None,
        MissingMemberHandling = MissingMemberHandling.Error,
        NullValueHandling = NullValueHandling.Include,
        FloatFormatHandling = FloatFormatHandling.String,
    };

    public static string Serialize(BellumStateSnapshot snapshot)
    {
        Validate(snapshot);
        var ordered = Ordered(snapshot);
        var json = JsonConvert.SerializeObject(ordered, Settings);
        if (Encoding.UTF8.GetByteCount(json) > SnapshotTransfer.MaxSnapshotBytes) throw new InvalidOperationException("Bellum snapshot exceeds transfer bounds");
        return json;
    }

    public static BellumStateSnapshot Deserialize(string json)
    {
        if (json == null || Encoding.UTF8.GetByteCount(json) > SnapshotTransfer.MaxSnapshotBytes) throw new InvalidDataException("Bellum snapshot exceeds transfer bounds");
        BellumStateSnapshot? snapshot;
        using (var text = new StringReader(json))
        using (var reader = new JsonTextReader(text) { DateParseHandling = DateParseHandling.None, MaxDepth = 32 })
        {
            var serializer = JsonSerializer.Create(Settings);
            var token = JToken.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
            if (reader.Read()) throw new InvalidDataException("Bellum snapshot contains trailing content");
            snapshot = token.ToObject<BellumStateSnapshot>(serializer);
        }
        if (snapshot == null) throw new InvalidDataException("Bellum snapshot is empty");
        try { Validate(snapshot); }
        catch (Exception ex) when (!(ex is InvalidDataException)) { throw new InvalidDataException(ex.Message, ex); }
        return Ordered(snapshot);
    }

    public static void Validate(BellumStateSnapshot snapshot)
    {
        if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
        if (snapshot.SchemaVersion != 1) throw new InvalidDataException("Unsupported Bellum snapshot schema");
        if (snapshot.Revision < 0 || !Finite(snapshot.CapturedDay)) throw new InvalidDataException("Invalid Bellum snapshot revision or day");
        Require(snapshot.Titles, nameof(snapshot.Titles)); Require(snapshot.Claims, nameof(snapshot.Claims)); Require(snapshot.Factions, nameof(snapshot.Factions));
        Require(snapshot.Treaties, nameof(snapshot.Treaties)); Require(snapshot.Tributes, nameof(snapshot.Tributes)); Require(snapshot.SuccessionLaws, nameof(snapshot.SuccessionLaws));
        Require(snapshot.Regencies, nameof(snapshot.Regencies)); Require(snapshot.CouncilOffices, nameof(snapshot.CouncilOffices)); Require(snapshot.ClaimFeuds, nameof(snapshot.ClaimFeuds));
        Require(snapshot.MercenaryBands, nameof(snapshot.MercenaryBands));
        Require(snapshot.WarScores, nameof(snapshot.WarScores)); Require(snapshot.WarWill, nameof(snapshot.WarWill)); Require(snapshot.WarWillPressures, nameof(snapshot.WarWillPressures));
        Require(snapshot.ClientKingdoms, nameof(snapshot.ClientKingdoms)); Require(snapshot.FeudalServices, nameof(snapshot.FeudalServices)); Require(snapshot.DeJureDrifts, nameof(snapshot.DeJureDrifts));
        Require(snapshot.ClaimFabrications, nameof(snapshot.ClaimFabrications)); Require(snapshot.ForeignWars, nameof(snapshot.ForeignWars));
        Require(snapshot.PendingForeignWars, nameof(snapshot.PendingForeignWars)); Require(snapshot.ObjectTimers, nameof(snapshot.ObjectTimers));
        Require(snapshot.ObjectIntegers, nameof(snapshot.ObjectIntegers)); IdList(snapshot.PendingSettlementRefreshClanIds);
        Require(snapshot.ClaimFeudWars, nameof(snapshot.ClaimFeudWars)); Require(snapshot.MercenaryDepartures, nameof(snapshot.MercenaryDepartures));
        Require(snapshot.DynamicRelations, nameof(snapshot.DynamicRelations)); Require(snapshot.RelationMaterializedValues, nameof(snapshot.RelationMaterializedValues));
        Require(snapshot.RelationMemories, nameof(snapshot.RelationMemories), MaxRelationMemories);
        Require(snapshot.DynasticSuccessions, nameof(snapshot.DynasticSuccessions)); Require(snapshot.PendingCadetMarriages, nameof(snapshot.PendingCadetMarriages));
        Require(snapshot.PendingFabricationOutcomes, nameof(snapshot.PendingFabricationOutcomes)); Require(snapshot.PendingUsurpations, nameof(snapshot.PendingUsurpations));
        Require(snapshot.PendingTreatyRebelResolutions, nameof(snapshot.PendingTreatyRebelResolutions)); Require(snapshot.PendingPartitions, nameof(snapshot.PendingPartitions));
        Require(snapshot.PendingGenderLineEscheats, nameof(snapshot.PendingGenderLineEscheats));
        Unique(snapshot.Titles, x => Id(x.TitleId), "title");
        Unique(snapshot.Claims, x => Id(x.ClaimId), "claim");
        Unique(snapshot.Factions, x => Id(x.FactionKey), "faction");
        Unique(snapshot.Treaties, x => Id(x.ProposalId), "treaty");
        Unique(snapshot.Tributes, x => Id(x.TributeKey), "tribute");
        Unique(snapshot.SuccessionLaws, x => Id(x.KingdomId), "succession law");
        Unique(snapshot.Regencies, x => Id(x.ClanId), "regency");
        Unique(snapshot.CouncilOffices, x => Id(x.RecordId), "council office");
        Unique(snapshot.ClaimFeuds, x => Id(x.RecordId), "claim feud");
        Unique(snapshot.MercenaryBands, x => Id(x.ClanId), "mercenary band");
        Unique(snapshot.WarScores, x => Id(x.WarKey), "war score");
        Unique(snapshot.WarWill, x => Id(x.ClanId), "war will");
        Unique(snapshot.WarWillPressures, x => Id(x.RecordId), "war-will pressure");
        Unique(snapshot.ClientKingdoms, x => Id(x.ClientKingdomId), "client kingdom");
        Unique(snapshot.FeudalServices, x => Id(x.RecordId), "feudal service");
        Unique(snapshot.DeJureDrifts, x => Id(x.TitleId), "de jure drift");
        Unique(snapshot.ClaimFabrications, x => Id(x.FabricationId), "claim fabrication");
        Unique(snapshot.ForeignWars, x => Id(x.WarKey), "foreign war");
        Unique(snapshot.PendingForeignWars, x => Id(x.WarKey), "pending foreign war");
        Unique(snapshot.ObjectTimers, x => Id(x.Scope) + "|" + Id(x.ObjectId), "object timer");
        Unique(snapshot.ObjectIntegers, x => Id(x.Scope) + "|" + Id(x.ObjectId), "object integer");
        Unique(snapshot.ClaimFeudWars, x => Id(x.WarId), "claim-feud war");
        Unique(snapshot.MercenaryDepartures, x => Id(x.HeroId), "mercenary departure");
        Unique(snapshot.DynamicRelations, x => Id(x.PairKey), "dynamic relation");
        Unique(snapshot.RelationMaterializedValues, x => Id(x.PairKey), "materialized relation");
        Unique(snapshot.RelationMemories, x => Id(x.MemoryKey), "relation memory");
        Unique(snapshot.DynasticSuccessions, x => Id(x.KingdomId), "dynastic succession");
        Unique(snapshot.PendingUsurpations, x => Id(x.TitleId) + "|" + Id(x.ClaimantClanId), "pending usurpation");
        Unique(snapshot.PendingTreatyRebelResolutions, x => Id(x.ProposalId) + "|" + Id(x.RebelKingdomId), "pending treaty rebel resolution");
        if (snapshot.Treaties.Sum(x => Require(x.Terms, "treaty terms").Count) > MaxTerms) throw new InvalidDataException("Too many treaty terms");

        foreach (var title in snapshot.Titles) { Text(title.Name); FiniteAll(title.CreatedDay, title.LastSyncedDay); }
        foreach (var claim in snapshot.Claims) { Id(claim.ClaimantClanId); Id(claim.TargetTitleId); Text(claim.Source); FiniteAll(claim.CreatedDay, claim.ExpiresDay); }
        foreach (var faction in snapshot.Factions)
        {
            Text(faction.Name); Id(faction.ParentKingdomId); IdList(faction.MemberClanIds);
            Require(faction.CivilWarStartFiefs, "faction fief snapshots"); Require(faction.CivilWarStartInfluence, "faction influence snapshots");
            foreach (var value in faction.CivilWarStartFiefs) Id(value.ClanId);
            foreach (var value in faction.CivilWarStartInfluence) { Id(value.ClanId); FiniteAll(value.Value); }
            FiniteAll(faction.Discontent, faction.PeakDiscontent, faction.Mood, faction.CreationDay);
        }
        foreach (var treaty in snapshot.Treaties) { Id(treaty.WarKey); Id(treaty.WinnerKingdomId); Id(treaty.LoserKingdomId); Text(treaty.ResolutionNote); FiniteAll(treaty.CreatedDay, treaty.WinnerSupport, treaty.LoserSupport); foreach (var term in treaty.Terms) ValidateTerm(term); }
        foreach (var tribute in snapshot.Tributes) { Id(tribute.PayerKingdomId); Id(tribute.RecipientKingdomId); }
        foreach (var regency in snapshot.Regencies) FiniteAll(regency.StartedDay);
        foreach (var office in snapshot.CouncilOffices) { Id(office.KingdomId); Text(office.LastReason); FiniteAll(office.Controversy, office.VacancyStartedDay, office.AppointedDay, office.LastUpdatedDay, office.LastChange, office.LastRealmMetric, office.LastAssignmentChangedDay, office.NextAssignmentReviewDay); }
        foreach (var feud in snapshot.ClaimFeuds) { Id(feud.ParentKingdomId); Id(feud.ClaimantClanId); Id(feud.HolderClanId); Id(feud.TargetTitleId); IdList(feud.ClaimantSupporterClanIds); IdList(feud.HolderSupporterClanIds); Text(feud.DebugReason); Text(feud.PauseReason); FiniteAll(feud.Pressure, feud.StartedDay, feud.LastTickDay, feud.CooldownUntilDay, feud.RulingDay, feud.ClaimantSidePower, feud.HolderSidePower, feud.PausedDay); }
        foreach (var band in snapshot.MercenaryBands) FiniteAll(band.CreatedDay);
        foreach (var war in snapshot.WarScores)
        {
            Id(war.AttackerKingdomId); Id(war.DefenderKingdomId); Text(war.TerminalResolutionReason);
            Require(war.FiefSnapshots, "war-score fief snapshots"); Require(war.Events, "war-score events");
            foreach (var fief in war.FiefSnapshots) { Id(fief.SettlementId); if (fief.OwnerKingdomId.Length > 0) Id(fief.OwnerKingdomId); if (fief.OwnerClanId.Length > 0) Id(fief.OwnerClanId); }
            Unique(war.Events, x => Id(x.EventId), "war-score event");
            foreach (var item in war.Events) { if (item.ActorKingdomId.Length > 0) Id(item.ActorKingdomId); if (item.TargetKingdomId.Length > 0) Id(item.TargetKingdomId); if (item.SettlementId.Length > 0) Id(item.SettlementId); if (item.HeroId.Length > 0) Id(item.HeroId); Text(item.DebugText); FiniteAll(item.Day, item.Delta, item.ScoreAfter); }
            FiniteAll(war.StartedDay, war.EndedDay, war.Score, war.OccupationScore, war.BattleScore, war.RaidScore, war.PrisonerScore, war.TickingScore, war.LastTickDay, war.ObjectiveScore, war.NextWhitePeaceCheckDay, war.ParleyOpenedDay, war.LandlessPressureScore);
        }
        foreach (var will in snapshot.WarWill) FiniteAll(will.Value);
        foreach (var pressure in snapshot.WarWillPressures) { Id(pressure.ClanId); Text(pressure.Reason); FiniteAll(pressure.Amount, pressure.CreatedDay, pressure.ExpiresDay); }
        foreach (var client in snapshot.ClientKingdoms) { Id(client.SuzerainKingdomId); FiniteAll(client.StartedDay, client.LiberationCooldownUntilDay); }
        foreach (var service in snapshot.FeudalServices) { Id(service.ChildTitleId); Id(service.ParentTitleId); Text(service.Reason); FiniteAll(service.LastChangedDay); }
        foreach (var drift in snapshot.DeJureDrifts) { Id(drift.TargetKingdomId); FiniteAll(drift.Progress, drift.StartedDay, drift.LastEvaluatedDay); }
        foreach (var fabrication in snapshot.ClaimFabrications) { Id(fabrication.FabricatorHeroId); Id(fabrication.FabricatorClanId); Id(fabrication.TargetTitleId); FiniteAll(fabrication.Progress, fabrication.DailyProgressDelta, fabrication.StartedDay, fabrication.PaidInfluenceCost); }
        foreach (var foreignWar in snapshot.ForeignWars) { Id(foreignWar.AttackerKingdomId); Id(foreignWar.DefenderKingdomId); IdList(foreignWar.TargetTitleIds); Text(foreignWar.PublicDeclarationMotiveSubject); FiniteAll(foreignWar.StartingAttackerStrength, foreignWar.StartingDefenderStrength, foreignWar.StartedDay, foreignWar.ContextCreatedDay); }
        foreach (var foreignWar in snapshot.PendingForeignWars) { Id(foreignWar.AttackerKingdomId); Id(foreignWar.DefenderKingdomId); IdList(foreignWar.TargetTitleIds); Text(foreignWar.PublicDeclarationMotiveSubject); FiniteAll(foreignWar.StartingAttackerStrength, foreignWar.StartingDefenderStrength, foreignWar.StartedDay, foreignWar.ContextCreatedDay); }
        foreach (var timer in snapshot.ObjectTimers) FiniteAll(timer.Day);
        foreach (var war in snapshot.ClaimFeudWars) { Id(war.FeudRecordId); Id(war.ParentKingdomId); Id(war.ClaimantKingdomId); Id(war.HolderKingdomId); Id(war.TargetTitleId); Text(war.ClaimantClanIds); Text(war.HolderClanIds); Text(war.InfluenceSnapshot); Text(war.FiefSnapshot); Text(war.PendingResolutionReason); FiniteAll(war.StartedDay); }
        foreach (var departure in snapshot.MercenaryDepartures) FiniteAll(departure.QueuedDay, departure.ObedienceRoll);
        foreach (var relation in snapshot.DynamicRelations) FiniteAll(relation.LastUpdateDay);
        FiniteAll(snapshot.RelationMemoryDurationMultiplier);
        foreach (var memory in snapshot.RelationMemories) { Id(memory.FirstId); Id(memory.SecondId); Text(memory.ContextText); FiniteAll(memory.StartDay, memory.ExpiryDay, memory.LegacyWeeklyDecay); }
        foreach (var succession in snapshot.DynasticSuccessions) { Text(succession.Source); FiniteAll(succession.LockedUntilDay, succession.LastUpdatedDay); }
        foreach (var marriage in snapshot.PendingCadetMarriages) FiniteAll(marriage.ReadyDay, marriage.ExpiresDay);
        foreach (var outcome in snapshot.PendingFabricationOutcomes) { Text(outcome.Title); Text(outcome.Body); }
        foreach (var partition in snapshot.PendingPartitions) { Id(partition.DeadLeaderId); Id(partition.ParentClanId); Id(partition.KingdomId); Text(partition.FiefIds); Text(partition.HeirIds); Text(partition.TitleIds); FiniteAll(partition.ReadyDay); }
        foreach (var escheat in snapshot.PendingGenderLineEscheats) { Id(escheat.TriggerHeroId); Id(escheat.ClanId); Id(escheat.KingdomId); FiniteAll(escheat.ReadyDay); }
    }

    private static BellumStateSnapshot Ordered(BellumStateSnapshot s) => new BellumStateSnapshot
    {
        SchemaVersion = s.SchemaVersion, Revision = s.Revision, CapturedDay = s.CapturedDay,
        Titles = s.Titles.OrderBy(x => x.TitleId, StringComparer.Ordinal).ToList(),
        Claims = s.Claims.OrderBy(x => x.ClaimId, StringComparer.Ordinal).ToList(),
        Factions = s.Factions.OrderBy(x => x.FactionKey, StringComparer.Ordinal).ToList(),
        Treaties = s.Treaties.OrderBy(x => x.ProposalId, StringComparer.Ordinal).ToList(),
        Tributes = s.Tributes.OrderBy(x => x.TributeKey, StringComparer.Ordinal).ToList(),
        SuccessionLaws = s.SuccessionLaws.OrderBy(x => x.KingdomId, StringComparer.Ordinal).ToList(),
        Regencies = s.Regencies.OrderBy(x => x.ClanId, StringComparer.Ordinal).ToList(),
        CouncilOffices = s.CouncilOffices.OrderBy(x => x.RecordId, StringComparer.Ordinal).ToList(),
        ClaimFeuds = s.ClaimFeuds.OrderBy(x => x.RecordId, StringComparer.Ordinal).ToList(),
        MercenaryBands = s.MercenaryBands.OrderBy(x => x.ClanId, StringComparer.Ordinal).ToList(),
        WarScores = s.WarScores.OrderBy(x => x.WarKey, StringComparer.Ordinal).ToList(),
        WarWill = s.WarWill.OrderBy(x => x.ClanId, StringComparer.Ordinal).ToList(),
        WarWillPressures = s.WarWillPressures.OrderBy(x => x.RecordId, StringComparer.Ordinal).ToList(),
        ClientKingdoms = s.ClientKingdoms.OrderBy(x => x.ClientKingdomId, StringComparer.Ordinal).ToList(),
        FeudalServices = s.FeudalServices.OrderBy(x => x.RecordId, StringComparer.Ordinal).ToList(),
        DeJureDrifts = s.DeJureDrifts.OrderBy(x => x.TitleId, StringComparer.Ordinal).ToList(),
        ClaimFabrications = s.ClaimFabrications.OrderBy(x => x.FabricationId, StringComparer.Ordinal).ToList(),
        ForeignWars = s.ForeignWars.OrderBy(x => x.WarKey, StringComparer.Ordinal).ToList(),
        PendingForeignWars = s.PendingForeignWars.OrderBy(x => x.WarKey, StringComparer.Ordinal).ToList(),
        ObjectTimers = s.ObjectTimers.OrderBy(x => x.Scope, StringComparer.Ordinal).ThenBy(x => x.ObjectId, StringComparer.Ordinal).ToList(),
        ObjectIntegers = s.ObjectIntegers.OrderBy(x => x.Scope, StringComparer.Ordinal).ThenBy(x => x.ObjectId, StringComparer.Ordinal).ToList(),
        PendingSettlementRefreshClanIds = s.PendingSettlementRefreshClanIds.OrderBy(x => x, StringComparer.Ordinal).ToList(),
        ClaimFeudWars = s.ClaimFeudWars.OrderBy(x => x.WarId, StringComparer.Ordinal).ToList(),
        MercenaryDepartures = s.MercenaryDepartures.OrderBy(x => x.HeroId, StringComparer.Ordinal).ToList(),
        DynamicRelations = s.DynamicRelations.OrderBy(x => x.PairKey, StringComparer.Ordinal).ToList(),
        RelationMemorySchemaVersion = s.RelationMemorySchemaVersion,
        RelationMemoryDurationMultiplier = s.RelationMemoryDurationMultiplier,
        RelationMaterializedValues = s.RelationMaterializedValues.OrderBy(x => x.PairKey, StringComparer.Ordinal).ToList(),
        RelationMemories = s.RelationMemories.OrderBy(x => x.MemoryKey, StringComparer.Ordinal).ToList(),
        DynasticSuccessions = s.DynasticSuccessions.OrderBy(x => x.KingdomId, StringComparer.Ordinal).ToList(),
        PendingCadetMarriages = s.PendingCadetMarriages.OrderBy(x => x.HeiressId, StringComparer.Ordinal).ThenBy(x => x.SpouseId, StringComparer.Ordinal).ToList(),
        PendingFabricationOutcomes = s.PendingFabricationOutcomes.OrderBy(x => x.Sequence).ToList(),
        PendingUsurpations = s.PendingUsurpations.OrderBy(x => x.TitleId, StringComparer.Ordinal).ThenBy(x => x.ClaimantClanId, StringComparer.Ordinal).ToList(),
        PendingTreatyRebelResolutions = s.PendingTreatyRebelResolutions.OrderBy(x => x.ProposalId, StringComparer.Ordinal).ThenBy(x => x.RebelKingdomId, StringComparer.Ordinal).ToList(),
        PendingPartitions = s.PendingPartitions.OrderBy(x => x.DeadLeaderId, StringComparer.Ordinal).ToList(),
        PendingGenderLineEscheats = s.PendingGenderLineEscheats.OrderBy(x => x.TriggerHeroId, StringComparer.Ordinal).ToList(),
    };

    private static List<T> Require<T>(List<T>? values, string name)
        => Require(values, name, MaxRecordsPerSection);
    private static List<T> Require<T>(List<T>? values, string name, int maximum)
    {
        if (values == null) throw new InvalidDataException(name + " is null");
        if (values.Count > maximum) throw new InvalidDataException(name + " exceeds record bounds");
        if (values.Any(x => x == null)) throw new InvalidDataException(name + " contains null");
        return values;
    }
    private static void Unique<T>(List<T> values, Func<T, string> key, string name)
    {
        if (values.Select(key).Distinct(StringComparer.Ordinal).Count() != values.Count) throw new InvalidDataException("Duplicate Bellum " + name + " id");
    }
    private static string Id(string? value)
    {
        if (value == null || string.IsNullOrWhiteSpace(value) || value.Length > MaxIdLength || value.Any(char.IsControl)) throw new InvalidDataException("Invalid Bellum object id");
        return value;
    }
    private static void IdList(List<string>? values)
    {
        if (values == null || values.Count > MaxRecordsPerSection) throw new InvalidDataException("Invalid Bellum id list");
        foreach (var value in values) Id(value);
        if (values.Distinct(StringComparer.Ordinal).Count() != values.Count) throw new InvalidDataException("Duplicate Bellum id list entry");
    }
    private static void Text(string? value)
    {
        if (value == null || value.Length > MaxTextLength) throw new InvalidDataException("Invalid Bellum text");
    }
    private static void FiniteAll(params double[] values)
    {
        if (values.Any(v => !Finite(v))) throw new InvalidDataException("Bellum snapshot contains a non-finite number");
    }
    private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    private static void ValidateTerm(BellumTreatyTermState term)
    {
        if (term == null) throw new InvalidDataException("Null Bellum treaty term");
        foreach (var value in new[] { term.SettlementId, term.FromKingdomId, term.ToKingdomId, term.HeroId, term.ClanId, term.TitleId, term.SecondaryHeroId, term.ThirdKingdomId })
            if (!string.IsNullOrEmpty(value)) Id(value);
    }
}
