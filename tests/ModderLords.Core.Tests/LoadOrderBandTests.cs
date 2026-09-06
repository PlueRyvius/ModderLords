using Bannerlord.ModuleManager;
using ModderLords.Core.Modules;
using Xunit;

namespace ModderLords.Core.Tests;

/// <summary>
/// The mod list in the app groups rows into three bands - frameworks, the game's own modules, everything else -
/// and it must group them by exactly the rule <see cref="LoadOrder.Compute"/> uses, or the list will disagree with
/// the engine order shown beside it. That was the bug: the list claimed "mods load after the game's own modules"
/// while the order panel put Harmony and ButterLib above Native.
/// </summary>
public class LoadOrderBandTests
{
    private static DiscoveredModule Official(string folder, string id) =>
        new(id, "v1.4.8", @"G\Modules\" + folder, ModuleSourceKind.GameModules, new ModuleInfoExtended
        { Id = id, Name = id, Version = ApplicationVersion.Empty, IsOfficial = true });

    private static DiscoveredModule Mod(string id, string[]? loadAfterThis = null, string[]? loadAfterThisMeta = null) =>
        new(id, "v1.0.0", @"W\" + id, ModuleSourceKind.Workshop, new ModuleInfoExtended
        {
            Id = id, Name = id, Version = ApplicationVersion.Empty,
            ModulesToLoadAfterThis = (loadAfterThis ?? []).Select(n => new DependentModule { Id = n }).ToList(),
            DependentModuleMetadatas = (loadAfterThisMeta ?? [])
                .Select(n => new DependentModuleMetadata { Id = n, LoadType = LoadType.LoadAfterThis }).ToList(),
        });

    [Fact]
    public void A_framework_that_declares_Native_after_itself_loads_before_the_game()
        => Assert.True(LoadOrder.LoadsBeforeNative(Mod("Bannerlord.Harmony", loadAfterThis: ["Native"])));

    /// <summary>The same claim can arrive as a metadata entry rather than ModulesToLoadAfterThis; both count.</summary>
    [Fact]
    public void The_metadata_form_of_the_same_claim_counts()
        => Assert.True(LoadOrder.LoadsBeforeNative(Mod("Bannerlord.ButterLib", loadAfterThisMeta: ["Native"])));

    [Fact]
    public void An_ordinary_mod_does_not()
        => Assert.False(LoadOrder.LoadsBeforeNative(Mod("ModularSmithing2")));

    /// <summary>Asking for something else to load after it says nothing about Native.</summary>
    [Fact]
    public void Wanting_another_mod_to_load_after_it_is_not_enough()
        => Assert.False(LoadOrder.LoadsBeforeNative(Mod("SomeMod", loadAfterThis: ["ImprovedGarrisons"])));

    /// <summary>
    /// The point of the whole thing: what the helper says about each mod is what Compute actually does with it.
    /// </summary>
    [Fact]
    public void The_band_rule_agrees_with_the_computed_order()
    {
        DiscoveredModule[] officials =
        [
            Official("Native", "Native"), Official("SandBoxCore", "SandBoxCore"),
            Official("SandBox", "Sandbox"), Official("StoryMode", "StoryMode"),
        ];
        DiscoveredModule[] mods =
        [
            Mod("Bannerlord.Harmony", loadAfterThis: ["Native"]),
            Mod("Bannerlord.ButterLib", loadAfterThisMeta: ["Native"]),
            Mod("ModularSmithing2"),
            Mod("ImprovedGarrisons"),
        ];

        var order = LoadOrder.Compute(officials, mods, mods.Select(m => m.Id).ToList(), LoadOrder.Profile.Client);
        var ids = order.ModuleIds.ToList();
        var nativeAt = ids.IndexOf("Native");

        foreach (var m in mods)
        {
            var at = ids.IndexOf(m.Id);
            Assert.True(at >= 0, m.Id + " is missing from the order");
            Assert.Equal(LoadOrder.LoadsBeforeNative(m), at < nativeAt);
        }
    }

    /// <summary>ButterLib pops a modal recommending the game be terminated when it loads after Native, so this is
    /// the ordering the whole band model exists to preserve.</summary>
    [Fact]
    public void Frameworks_come_first_of_all()
    {
        DiscoveredModule[] officials = [Official("Native", "Native"), Official("SandBoxCore", "SandBoxCore"), Official("SandBox", "Sandbox")];
        DiscoveredModule[] mods = [Mod("ModularSmithing2"), Mod("Bannerlord.Harmony", loadAfterThis: ["Native"])];

        var ids = LoadOrder.Compute(officials, mods, ["ModularSmithing2", "Bannerlord.Harmony"], LoadOrder.Profile.Client).ModuleIds.ToList();

        Assert.Equal("Bannerlord.Harmony", ids[0]);
        Assert.True(ids.IndexOf("ModularSmithing2") > ids.IndexOf("Native"));
    }
}
