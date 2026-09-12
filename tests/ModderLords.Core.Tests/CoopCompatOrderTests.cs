using Bannerlord.ModuleManager;
using ModderLords.Core.Modules;
using Xunit;

namespace ModderLords.Core.Tests;

/// <summary>
/// The dedicated server pins Coop after the community block and DedicatedServer.Windows last. That is right for a
/// mod that knows nothing about Coop, and wrong for one that patches it: TAOM's own TAOM.CoopCompat declares that
/// CoopNightly loads before it, and its bootstrap binds to Coop's assemblies as it loads.
/// </summary>
public class CoopCompatOrderTests
{
    private static DiscoveredModule Stock(string folder, string id) =>
        new(id, "v1.4.8", @"S\Modules\" + folder, ModuleSourceKind.GameModules, new ModuleInfoExtended
        { Id = id, Name = id, Version = ApplicationVersion.Empty, IsOfficial = true });

    private static DiscoveredModule Mod(string id, params string[] loadBeforeThis) =>
        new(id, "v1.0.0", @"W\" + id, ModuleSourceKind.Workshop, new ModuleInfoExtended
        {
            Id = id, Name = id, Version = ApplicationVersion.Empty,
            DependentModuleMetadatas = loadBeforeThis
                .Select(n => new DependentModuleMetadata { Id = n, LoadType = LoadType.LoadBeforeThis }).ToList(),
        });

    /// <summary>The server's stock set, with Coop carrying the workshop build's "CoopNightly" id.</summary>
    private static List<DiscoveredModule> ServerStock() =>
    [
        Stock("Native", "Native"), Stock("SandBoxCore", "SandBoxCore"), Stock("SandBox", "Sandbox"),
        Stock("Coop", "CoopNightly"), Stock("DedicatedServer.Windows", "DedicatedServer.Windows"),
    ];

    private static IReadOnlyList<string> Order(params DiscoveredModule[] community) =>
        LoadOrder.Compute(ServerStock(), community.ToList(), community.Select(m => m.Id).ToList(),
            profile: LoadOrder.Profile.DedicatedServer).ModuleIds;

    [Fact]
    public void Detects_the_declaration_by_the_coop_folder_id()
        => Assert.True(LoadOrder.LoadsAfterCoop(Mod("TAOM.CoopCompat", "CoopNightly"), ["Coop", "CoopNightly"]));

    [Fact]
    public void An_ordinary_mod_is_not_moved()
        => Assert.False(LoadOrder.LoadsAfterCoop(Mod("ImprovedGarrisons", "Native"), ["Coop", "CoopNightly"]));

    [Fact]
    public void A_coop_patching_module_lands_between_Coop_and_DedicatedServer_Windows()
    {
        var ids = Order(Mod("TAOM.CoopCompat", "CoopNightly"));

        var coop = ids.ToList().IndexOf("CoopNightly");
        var compat = ids.ToList().IndexOf("TAOM.CoopCompat");
        var ds = ids.ToList().IndexOf("DedicatedServer.Windows");

        Assert.True(coop >= 0 && compat >= 0 && ds >= 0, "all three must be present: " + string.Join(", ", ids));
        Assert.True(coop < compat, "the module that patches Coop must load after it");
        Assert.True(compat < ds, "DedicatedServer.Windows stays last; the host pins it there");
        Assert.Equal(ids.Count - 1, ds);
    }

    [Fact]
    public void Ordinary_mods_still_load_before_Coop()
    {
        var ids = Order(Mod("ImprovedGarrisons"), Mod("TAOM.CoopCompat", "CoopNightly")).ToList();

        Assert.True(ids.IndexOf("ImprovedGarrisons") < ids.IndexOf("CoopNightly"),
            "a mod with no claim on Coop keeps the host's order: " + string.Join(", ", ids));
        Assert.True(ids.IndexOf("CoopNightly") < ids.IndexOf("TAOM.CoopCompat"));
    }

    [Fact]
    public void Each_module_appears_exactly_once()
    {
        var ids = Order(Mod("ImprovedGarrisons"), Mod("TAOM.CoopCompat", "CoopNightly"));
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void With_nothing_pinned_at_the_tail_the_module_simply_goes_last()
    {
        // The player's own game: Coop is an ordinary module there and nothing is pinned after the mods.
        var stock = new List<DiscoveredModule> { Stock("Native", "Native"), Stock("SandBox", "Sandbox") };
        var community = new List<DiscoveredModule> { Mod("TAOM.CoopCompat", "CoopNightly") };

        var ids = LoadOrder.Compute(stock, community, ["TAOM.CoopCompat"], profile: LoadOrder.Profile.Client).ModuleIds;

        Assert.Contains("TAOM.CoopCompat", ids);
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }
}
