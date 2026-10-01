using ModderLords.Core.Logs;

namespace ModderLords.Core.Tests;

public sealed class KnownNoiseTests
{
    private const string Visual = "Engine     [Coop] [\"ObjectManager\"] Failed to get id for object of type \"A.f+A\"";

    [Theory]
    [InlineData("[Coop] [\"ObjectManager\"] Failed to get id for object of type \"A.f+A\"")]
    [InlineData("[Coop] [\"ObjectManager\"] Failed to get id for object of type \"B.gH+c\"")]   // a rebuilt server core
    public void TheStandInBattleVisualIsKnownNoise(string line) => Assert.Same(KnownNoise.StandInBattleVisual, KnownNoise.Match(line));

    [Theory]
    [InlineData("[Coop] [\"ObjectManager\"] Failed to get id for object of type \"TaleWorlds.CampaignSystem.Hero\", \"CharacterObject_1\"")]
    [InlineData("[Coop] [\"ObjectManager\"] Failed to get id for object of type \"TaleWorlds.CampaignSystem.Party.MobileParty\",")]
    [InlineData("[Coop] Failed to tick moving party \"patrol_party_1_party_314\" in ParallelTickMovingParties")]
    [InlineData("A.f+A")]
    public void RealTypesAndOtherLinesAreKept(string line) => Assert.Null(KnownNoise.Match(line));

    [Fact]
    public void TheFirstHiddenLineIsExplainedAndTheRestAreCounted()
    {
        var counter = new NoiseCounter();
        Assert.True(counter.Hide(Visual, out var first));
        Assert.Contains("stand-in battle visual", first);
        for (var i = 0; i < 9; i++) { Assert.True(counter.Hide(Visual, out var again)); Assert.Null(again); }
        Assert.False(counter.Hide("Server     [DedicatedServer] SERVING", out var none));
        Assert.Null(none);
        Assert.Equal(new[] { "[ModderLords] hid 10 known harmless server line(s): coop-id-standin-visual" }, counter.Totals());
    }
}
