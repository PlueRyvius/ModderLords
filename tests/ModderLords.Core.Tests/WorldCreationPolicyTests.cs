using ModderLords.Coop.Launch;

namespace ModderLords.Core.Tests;

public sealed class WorldCreationPolicyTests
{
    private static readonly string[] Modded = ["CoopNightly", "MyBigMod"];

    /// <summary>The whole point of the feature: an existing campaign is loaded, never generated over.</summary>
    [Fact]
    public void AnExistingSaveIsNeverGeneratedOver()
    {
        var d = WorldCreationPolicy.Decide(Modded, "my_campaign", _ => true, generateWithActiveMods: true);
        Assert.False(d.ShouldCreate);
        Assert.Null(d.Message);
    }

    [Fact]
    public void AnExistingTaomSaveIsAlsoLoadedRatherThanGenerated()
    {
        var d = WorldCreationPolicy.Decide(["TAOM", "TAOM_Map", "LOTRLOME_Armory"], "taom_world", _ => true, false);
        Assert.False(d.ShouldCreate);
    }

    [Fact]
    public void TickedAndCreating_Generates()
    {
        var d = WorldCreationPolicy.Decide(Modded, "fresh", _ => false, generateWithActiveMods: true);
        Assert.True(d.ShouldCreate);
        Assert.Contains("generated with 2 active mod(s)", d.Message);
    }

    /// <summary>Off is allowed, but it must not be silent: the world really will not contain the mods.</summary>
    [Fact]
    public void UntickedAndCreating_WarnsThatTheWorldWillBeVanilla()
    {
        var d = WorldCreationPolicy.Decide(Modded, "fresh", _ => false, generateWithActiveMods: false);
        Assert.False(d.ShouldCreate);
        Assert.Contains("WARNING", d.Message);
        Assert.Contains("will NOT contain your 2 active mod(s)", d.Message);
    }

    [Fact]
    public void AnUnmoddedProfileSaysNothing()
    {
        var d = WorldCreationPolicy.Decide([], "fresh", _ => false, generateWithActiveMods: true);
        Assert.False(d.ShouldCreate);
        Assert.Null(d.Message);
    }

    /// <summary>TAOM decides for itself; the tick box neither enables nor disables it.</summary>
    [Fact]
    public void CompleteTaomRecipeGeneratesWhateverTheBoxSays()
    {
        string[] ids = ["TAOM", "TAOM_Map", "LOTRLOME_Armory"];
        Assert.True(WorldCreationPolicy.Decide(ids, "fresh", _ => false, false).ShouldCreate);
        Assert.True(WorldCreationPolicy.Decide(ids, "fresh", _ => false, true).ShouldCreate);
    }

    [Fact]
    public void IncompleteTaomRecipeDoesNotGenerate()
    {
        var d = WorldCreationPolicy.Decide(["TAOM"], "fresh", _ => false, generateWithActiveMods: true);
        Assert.False(d.ShouldCreate);
    }

    /// <summary>An unnamed save is one the caller is about to name, so it is a creation, not a load.</summary>
    [Fact]
    public void ABlankSaveNameIsACreation()
    {
        var called = false;
        var d = WorldCreationPolicy.Decide(Modded, "", _ => { called = true; return true; }, true);
        Assert.True(d.ShouldCreate);
        Assert.False(called);
    }
}
