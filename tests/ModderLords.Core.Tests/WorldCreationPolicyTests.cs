using ModderLords.Core.Modules;
using ModderLords.Core.Profiles;
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

/// <summary>
/// The dependency the 2026-09-19 failure turned up: world creation is performed by the compat module, so a launch
/// that asks for a generated world has to load it whether or not the profile ticks Server guards. Without this the
/// creation variables are set, nothing reads them, and the process serves a template world instead.
/// </summary>
public sealed class WorldCreationNeedsCompatTests
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mlwc-" + Guid.NewGuid().ToString("N"));

    private DiscoveredModule Bundled(string id)
    {
        var dir = Path.Combine(_dir, id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "SubModule.xml"),
            $"<Module><Name value=\"{id}\" /><Id value=\"{id}\" /><Version value=\"v1.0.0\" /><SubModules /></Module>");
        return ModuleCatalog.TryParse(dir, ModuleSourceKind.Custom, out _)!;
    }

    [Fact]
    public void CreationLoadsTheCompatModuleEvenWithGuardsOff()
    {
        var messages = new List<string>();
        var list = LaunchSession.WithCompat(new Profile { CompatGuards = false }, [], messages,
            Bundled, requireCompat: true);

        Assert.Contains(list, s => s.Module.Id == LaunchSession.CompatModuleId);
        Assert.Contains(messages, m => m.Contains("even though Server guards is off"));
    }

    [Fact]
    public void WithoutCreationGuardsOffStillMeansNoCompatModule()
    {
        var messages = new List<string>();
        var list = LaunchSession.WithCompat(new Profile { CompatGuards = false }, [], messages, Bundled);

        Assert.DoesNotContain(list, s => s.Module.Id == LaunchSession.CompatModuleId);
        Assert.Empty(messages);
    }

    [Fact]
    public void GuardsOnIsUnchangedAndSaysNothingExtra()
    {
        var messages = new List<string>();
        var list = LaunchSession.WithCompat(new Profile { CompatGuards = true }, [], messages,
            Bundled, requireCompat: true);

        Assert.Contains(list, s => s.Module.Id == LaunchSession.CompatModuleId);
        Assert.DoesNotContain(messages, m => m.Contains("even though Server guards is off"));
    }

    [Fact]
    public void AMissingCompatModuleSaysTheWorldCannotBeGenerated()
    {
        var messages = new List<string>();
        LaunchSession.WithCompat(new Profile { CompatGuards = false }, [], messages, _ => null, requireCompat: true);

        Assert.Contains(messages, m => m.Contains("WARNING") && m.Contains("cannot be generated"));
    }
}
