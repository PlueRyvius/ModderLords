using System.Collections.Generic;

namespace ModderLords.Operations;

// Remaining Bellum 1.3.1 save records whose values are not already represented by the primary political DTOs.
// These are deliberately flat ID/value records; no TaleWorlds or Bellum runtime object crosses the wire.
public sealed class BellumObjectTimerState { public string Scope { get; set; } = ""; public string ObjectId { get; set; } = ""; public double Day { get; set; } }
public sealed class BellumObjectIntegerState { public string Scope { get; set; } = ""; public string ObjectId { get; set; } = ""; public int Value { get; set; } }

public sealed class BellumClaimFeudWarState
{
    public string WarId { get; set; } = ""; public string FeudRecordId { get; set; } = ""; public string ParentKingdomId { get; set; } = "";
    public string ClaimantKingdomId { get; set; } = ""; public string HolderKingdomId { get; set; } = ""; public string TargetTitleId { get; set; } = "";
    public string ClaimantLeaderClanId { get; set; } = ""; public string HolderLeaderClanId { get; set; } = "";
    public string ClaimantClanIds { get; set; } = ""; public string HolderClanIds { get; set; } = "";
    public string InfluenceSnapshot { get; set; } = ""; public string FiefSnapshot { get; set; } = ""; public double StartedDay { get; set; }
    public bool IsActive { get; set; } public string PendingCaptureWinnerClanId { get; set; } = ""; public bool ClaimantControlsObjective { get; set; }
    public bool ClaimantHasControlledObjective { get; set; } public int PendingResolutionOutcome { get; set; } public string PendingResolutionReason { get; set; } = "";
}

public sealed class BellumMercenaryDepartureState { public string HeroId { get; set; } = ""; public double QueuedDay { get; set; } public double ObedienceRoll { get; set; } public int Resolution { get; set; } }
public sealed class BellumDynamicRelationState { public string PairKey { get; set; } = ""; public int LastRecordedValue { get; set; } public double LastUpdateDay { get; set; } }
public sealed class BellumRelationMaterializedValueState { public string PairKey { get; set; } = ""; public int Value { get; set; } }
public sealed class BellumRelationMemoryState
{
    public string MemoryKey { get; set; } = ""; public int Scope { get; set; } public string FirstId { get; set; } = ""; public string SecondId { get; set; } = "";
    public string SourceId { get; set; } = ""; public string ContextText { get; set; } = ""; public int Value { get; set; }
    public double StartDay { get; set; } public double ExpiryDay { get; set; } public double LegacyWeeklyDecay { get; set; }
}

public sealed class BellumDynasticSuccessionState
{
    public string KingdomId { get; set; } = ""; public string RightfulDynastyClanId { get; set; } = ""; public string HeirHeroId { get; set; } = "";
    public string ClaimCarrierClanId { get; set; } = ""; public string ClaimCarrierHeroId { get; set; } = ""; public string Source { get; set; } = "";
    public double LockedUntilDay { get; set; } public double LastUpdatedDay { get; set; }
}

public sealed class BellumPendingCadetMarriageState
{
    public string HeiressId { get; set; } = ""; public string SpouseId { get; set; } = ""; public string OriginKingdomId { get; set; } = "";
    public string DynastyClanId { get; set; } = ""; public string BrideClanId { get; set; } = ""; public string MarriedClanId { get; set; } = "";
    public double ReadyDay { get; set; } public int MarriageGold { get; set; } public int Attempts { get; set; } public double ExpiresDay { get; set; }
}

public sealed class BellumPendingFabricationOutcomeState { public int Sequence { get; set; } public string Title { get; set; } = ""; public string Body { get; set; } = ""; }
public sealed class BellumPendingUsurpationState { public string TitleId { get; set; } = ""; public string ClaimantClanId { get; set; } = ""; }
public sealed class BellumPendingTreatyRebelResolutionState { public string ProposalId { get; set; } = ""; public string RebelKingdomId { get; set; } = ""; public string ParentKingdomId { get; set; } = ""; public string BeneficiaryKingdomId { get; set; } = ""; }

public sealed class BellumPendingPartitionState
{
    public string DeadLeaderId { get; set; } = ""; public string ParentClanId { get; set; } = ""; public string KingdomId { get; set; } = "";
    public string FiefIds { get; set; } = ""; public string HeirIds { get; set; } = ""; public double ReadyDay { get; set; }
    public bool ParentWasRulingClanAtDeath { get; set; } public string TitleIds { get; set; } = ""; public string PrimarySovereignTitleId { get; set; } = "";
}

public sealed class BellumPendingGenderLineEscheatState
{
    public string TriggerHeroId { get; set; } = ""; public string ClanId { get; set; } = ""; public string KingdomId { get; set; } = "";
    public int GenderLaw { get; set; } public double ReadyDay { get; set; } public bool WasRulingClan { get; set; } public bool DynasticLineExtinction { get; set; }
}
