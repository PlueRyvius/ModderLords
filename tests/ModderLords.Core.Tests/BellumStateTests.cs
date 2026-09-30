using System.IO;
using ModderLords.Operations;
using Newtonsoft.Json;

namespace ModderLords.Core.Tests;

public sealed class BellumStateTests
{
    private static BellumStateSnapshot Snapshot() => new BellumStateSnapshot
    {
        Revision = 12,
        CapturedDay = 44.5,
        Titles =
        [
            new BellumTitleState { TitleId = "z_title", Name = "Z", TitleType = 2, IsActive = true },
            new BellumTitleState { TitleId = "a_title", Name = "A", TitleType = 1, IsActive = true },
        ],
        Claims = [new BellumClaimState { ClaimId = "claim", ClaimantClanId = "clan", TargetTitleId = "a_title", Source = "inheritance", IsActive = true }],
        Factions = [new BellumFactionState { FactionKey = "kingdom:0:44", Name = "Royalists", ParentKingdomId = "kingdom", LeaderClanId = "clan", MemberClanIds = ["clan"], CivilWarStartFiefs = [new BellumClanIntState { ClanId = "clan", Value = 2 }], CivilWarStartInfluence = [new BellumClanNumberState { ClanId = "clan", Value = 45.5 }] }],
        Treaties = [new BellumTreatyState { ProposalId = "proposal", WarKey = "a|b", WinnerKingdomId = "a", LoserKingdomId = "b", Terms = [new BellumTreatyTermState { Type = 1, FromKingdomId = "b", ToKingdomId = "a" }] }],
        Tributes = [new BellumTributeState { TributeKey = "a|b", PayerKingdomId = "b", RecipientKingdomId = "a", DailyGold = 100 }],
        SuccessionLaws = [new BellumSuccessionLawState { KingdomId = "kingdom", GenderLaw = 1, SuccessionLaw = 2 }],
        Regencies = [new BellumRegencyState { ClanId = "clan", WardHeroId = "ward", RegentHeroId = "regent" }],
        CouncilOffices = [new BellumCouncilOfficeState { RecordId = "kingdom:marshal", KingdomId = "kingdom", Office = 1 }],
        ClaimFeuds = [new BellumClaimFeudState { RecordId = "feud", ParentKingdomId = "kingdom", ClaimantClanId = "clan", HolderClanId = "holder", TargetTitleId = "a_title", ClaimantSupporterClanIds = ["clan"], HolderSupporterClanIds = ["holder"] }],
        MercenaryBands = [new BellumMercenaryBandState { ClanId = "merc", FounderHeroId = "founder", CultureId = "culture" }],
        WarScores = [new BellumWarScoreState { WarKey = "a|b", AttackerKingdomId = "a", DefenderKingdomId = "b", IsActive = true, FiefSnapshots = [new BellumWarScoreFiefState { SettlementId = "town", OwnerKingdomId = "a", OwnerClanId = "clan", IsTown = true }], Events = [new BellumWarScoreEventState { EventId = "event", ActorKingdomId = "a", TargetKingdomId = "b", DebugText = "battle" }] }],
        WarWill = [new BellumWarWillState { ClanId = "clan", Value = 55, PreferredTargetKingdomId = "b" }],
        WarWillPressures = [new BellumWarWillPressureState { RecordId = "pressure", ClanId = "clan", TargetKingdomId = "b", Reason = "claim", IsActive = true }],
        ClientKingdoms = [new BellumClientKingdomState { ClientKingdomId = "client", SuzerainKingdomId = "suzerain" }],
        FeudalServices = [new BellumFeudalServiceState { RecordId = "child|parent", ChildTitleId = "child", ParentTitleId = "parent", Reason = "test" }],
        DeJureDrifts = [new BellumDeJureDriftState { TitleId = "child", TargetKingdomId = "kingdom", IsActive = true }],
        ClaimFabrications = [new BellumClaimFabricationState { FabricationId = "fabrication", FabricatorHeroId = "hero", FabricatorClanId = "clan", TargetTitleId = "a_title", IsActive = true }],
        ForeignWars = [new BellumForeignWarState { WarKey = "a|b", AttackerKingdomId = "a", DefenderKingdomId = "b", TargetTitleIds = ["a_title"] }],
        PendingForeignWars = [new BellumForeignWarState { WarKey = "b|c", AttackerKingdomId = "b", DefenderKingdomId = "c", TargetTitleIds = ["a_title"] }],
        ObjectTimers = [new BellumObjectTimerState { Scope = "pacified-clan", ObjectId = "clan", Day = 50 }],
        ObjectIntegers = [new BellumObjectIntegerState { Scope = "kingdom-controversy", ObjectId = "kingdom", Value = 3 }],
        PendingSettlementRefreshClanIds = ["clan"],
        ClaimFeudWars = [new BellumClaimFeudWarState { WarId = "feud-war", FeudRecordId = "feud", ParentKingdomId = "kingdom", ClaimantKingdomId = "a", HolderKingdomId = "b", TargetTitleId = "a_title" }],
        MercenaryDepartures = [new BellumMercenaryDepartureState { HeroId = "founder", QueuedDay = 45, ObedienceRoll = 0.5 }],
        DynamicRelations = [new BellumDynamicRelationState { PairKey = "hero|regent", LastRecordedValue = 4, LastUpdateDay = 44 }],
        RelationMemorySchemaVersion = 2,
        RelationMemoryDurationMultiplier = 1.25,
        RelationMaterializedValues = [new BellumRelationMaterializedValueState { PairKey = "hero|regent", Value = 7 }],
        RelationMemories = [new BellumRelationMemoryState { MemoryKey = "00000001", FirstId = "hero", SecondId = "regent", ContextText = "test" }],
        DynasticSuccessions = [new BellumDynasticSuccessionState { KingdomId = "kingdom", Source = "test" }],
        PendingCadetMarriages = [new BellumPendingCadetMarriageState { HeiressId = "heiress", SpouseId = "spouse", OriginKingdomId = "kingdom" }],
        PendingFabricationOutcomes = [new BellumPendingFabricationOutcomeState { Sequence = 0, Title = "Report", Body = "Succeeded" }],
        PendingUsurpations = [new BellumPendingUsurpationState { TitleId = "a_title", ClaimantClanId = "clan" }],
        PendingTreatyRebelResolutions = [new BellumPendingTreatyRebelResolutionState { ProposalId = "proposal", RebelKingdomId = "b", ParentKingdomId = "a", BeneficiaryKingdomId = "c" }],
        PendingPartitions = [new BellumPendingPartitionState { DeadLeaderId = "hero", ParentClanId = "clan", KingdomId = "kingdom" }],
        PendingGenderLineEscheats = [new BellumPendingGenderLineEscheatState { TriggerHeroId = "hero", ClanId = "clan", KingdomId = "kingdom" }],
    };

    [Fact] public void RoundTripIsCanonicalAndUsesOnlyStableValues()
    {
        var first = BellumStateCodec.Serialize(Snapshot());
        var state = BellumStateCodec.Deserialize(first);
        var second = BellumStateCodec.Serialize(state);
        Assert.Equal(first, second);
        Assert.Equal("a_title", state.Titles[0].TitleId);
        Assert.Equal("a|b", Assert.Single(state.WarScores).WarKey);
        Assert.Equal("event", Assert.Single(Assert.Single(state.WarScores).Events).EventId);
        Assert.Equal(2, Assert.Single(Assert.Single(state.Factions).CivilWarStartFiefs).Value);
        Assert.Equal("feud-war", Assert.Single(state.ClaimFeudWars).WarId);
        Assert.Equal("hero|regent", Assert.Single(state.DynamicRelations).PairKey);
        Assert.Equal(2, state.RelationMemorySchemaVersion);
        Assert.Equal(1.25, state.RelationMemoryDurationMultiplier);
        Assert.Equal(7, Assert.Single(state.RelationMaterializedValues).Value);
        Assert.Equal("child|parent", Assert.Single(state.FeudalServices).RecordId);
        Assert.DoesNotContain("TaleWorlds", first); Assert.DoesNotContain("BellumCivile.", first);
    }

    [Fact] public void RejectsDuplicateIdsUnknownFieldsAndNonFiniteNumbers()
    {
        var duplicate = Snapshot(); duplicate.Titles.Add(new BellumTitleState { TitleId = "a_title", Name = "duplicate" });
        Assert.Throws<InvalidDataException>(() => BellumStateCodec.Serialize(duplicate));
        var json = BellumStateCodec.Serialize(Snapshot()).Replace("\"SchemaVersion\":1", "\"SchemaVersion\":1,\"Unknown\":true");
        Assert.Throws<JsonSerializationException>(() => BellumStateCodec.Deserialize(json));
        var nonFinite = Snapshot(); nonFinite.CapturedDay = double.NaN;
        Assert.Throws<InvalidDataException>(() => BellumStateCodec.Serialize(nonFinite));
    }

    [Fact] public void RejectsUnboundedOrMalformedState()
    {
        var state = Snapshot(); state.Titles = Enumerable.Range(0, BellumStateCodec.MaxRecordsPerSection + 1)
            .Select(i => new BellumTitleState { TitleId = "title" + i, Name = "title" }).ToList();
        Assert.Throws<InvalidDataException>(() => BellumStateCodec.Serialize(state));
        Assert.Throws<InvalidDataException>(() => BellumStateCodec.Deserialize("null"));
        Assert.ThrowsAny<Exception>(() => BellumStateCodec.Deserialize("{}{}"));
    }

    [Fact] public void RelationHistoryHasAnExplicitLargerBound()
    {
        var state = Snapshot();
        state.RelationMemories = Enumerable.Range(0, BellumStateCodec.MaxRecordsPerSection + 1)
            .Select(i => new BellumRelationMemoryState { MemoryKey = i.ToString("D8"), FirstId = "hero", SecondId = "other", ContextText = "memory" }).ToList();
        Assert.NotEmpty(BellumStateCodec.Serialize(state));
        state.RelationMemories = Enumerable.Range(0, BellumStateCodec.MaxRelationMemories + 1)
            .Select(i => new BellumRelationMemoryState { MemoryKey = i.ToString("D8"), FirstId = "hero", SecondId = "other", ContextText = "memory" }).ToList();
        Assert.Throws<InvalidDataException>(() => BellumStateCodec.Serialize(state));
    }

    [Fact]
    public void Passing_campaign_time_is_not_a_political_change()
    {
        var earlier = Snapshot(); earlier.Revision = 4; earlier.CapturedDay = 100.25;
        var later = Snapshot(); later.Revision = 9; later.CapturedDay = 100.75;
        Assert.Equal(BellumStateCodec.PoliticalContent(earlier), BellumStateCodec.PoliticalContent(later));
        Assert.Equal(4, earlier.Revision); Assert.Equal(100.25, earlier.CapturedDay);
    }
}
