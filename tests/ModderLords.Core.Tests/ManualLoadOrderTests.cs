using Bannerlord.ModuleManager;
using ModderLords.Core.Modules;
using Xunit;

namespace ModderLords.Core.Tests;

/// <summary>
/// The computed order is a suggestion assembled from module metadata, and metadata is sometimes wrong. The real case:
/// TAOM_Map ships <c>&lt;DependedModuleMetadata id="TAOM" order="LoadBeforeThis"/&gt;</c>, which contradicts the load
/// order TAOM's own authors publish (Armory, TAOM_Map, TAOM). Under the default policy the tool kept dragging TAOM
/// above the map and the published order could not be entered at all.
///
/// So the user has to be able to overrule it — and has to be told what they overruled.
/// </summary>
public class ManualLoadOrderTests
{
    private static DiscoveredModule Official(string folder, string id) =>
        new(id, "v1.4.8", @"G\Modules\" + folder, ModuleSourceKind.GameModules, new ModuleInfoExtended
        { Id = id, Name = id, Version = ApplicationVersion.Empty, IsOfficial = true });

    /// <summary>A mod that declares "<paramref name="loadBeforeThis"/> must load before me", as TAOM_Map does of TAOM.</summary>
    private static DiscoveredModule Mod(string id, string[]? loadBeforeThis = null) =>
        new(id, "v2.0.27", @"W\" + id, ModuleSourceKind.Workshop, new ModuleInfoExtended
        {
            Id = id, Name = id, Version = ApplicationVersion.Empty,
            DependentModuleMetadatas = (loadBeforeThis ?? [])
                .Select(n => new DependentModuleMetadata { Id = n, LoadType = LoadType.LoadBeforeThis }).ToList(),
        });

    private static readonly IReadOnlyList<DiscoveredModule> Stock =
        [Official("Native", "Native"), Official("SandBoxCore", "SandBoxCore"), Official("SandBox", "Sandbox")];

    private static IReadOnlyList<DiscoveredModule> TaomStack() =>
        [Mod("LOTRLOME_Armory"), Mod("TAOM_Map", loadBeforeThis: ["TAOM"]), Mod("TAOM")];

    /// <summary>The order TAOM's authors publish, which the manifest disagrees with.</summary>
    private static readonly string[] Published = ["LOTRLOME_Armory", "TAOM_Map", "TAOM"];

    private static List<string> Community(LoadOrder.Result r) =>
        r.ModuleIds.Where(id => id is "LOTRLOME_Armory" or "TAOM_Map" or "TAOM").ToList();

    [Fact]
    public void By_default_the_manifest_wins_and_says_so()
    {
        var r = LoadOrder.Compute(Stock, TaomStack(), Published, LoadOrder.Profile.Client);
        Assert.Equal(["LOTRLOME_Armory", "TAOM", "TAOM_Map"], Community(r));
        Assert.Contains(r.Issues, i => i.StartsWith("moved to satisfy a dependency:") && i.Contains("TAOM_Map"));
    }

    [Fact]
    public void Manual_keeps_the_published_order_exactly()
    {
        var r = LoadOrder.Compute(Stock, TaomStack(), Published, LoadOrder.Profile.Client, LoadOrder.OrderPolicy.Manual);
        Assert.Equal(Published, Community(r));
    }

    [Fact]
    public void Overriding_is_never_silent()
    {
        var r = LoadOrder.Compute(Stock, TaomStack(), Published, LoadOrder.Profile.Client, LoadOrder.OrderPolicy.Manual);
        Assert.Contains(r.Issues, i => i.StartsWith("kept your order despite a declared dependency:") && i.Contains("TAOM_Map"));
    }

    [Fact]
    public void Manual_changes_who_decides_not_where_the_game_sits()
    {
        // Overriding mod order must not let a mod jump the game's own modules; that band split is structural.
        var r = LoadOrder.Compute(Stock, TaomStack(), Published, LoadOrder.Profile.Client, LoadOrder.OrderPolicy.Manual);
        var native = r.ModuleIds.ToList().IndexOf("Native");
        Assert.All(Published, id => Assert.True(r.ModuleIds.ToList().IndexOf(id) > native, id + " must stay after Native"));
    }

    [Fact]
    public void An_order_the_manifests_already_agree_with_is_unaffected_by_the_policy()
    {
        var agreeable = new[] { "LOTRLOME_Armory", "TAOM", "TAOM_Map" };
        var suggested = LoadOrder.Compute(Stock, TaomStack(), agreeable, LoadOrder.Profile.Client);
        var manual = LoadOrder.Compute(Stock, TaomStack(), agreeable, LoadOrder.Profile.Client, LoadOrder.OrderPolicy.Manual);
        Assert.Equal(Community(suggested), Community(manual));
        Assert.DoesNotContain(manual.Issues, i => i.StartsWith("kept your order despite"));
    }

    [Fact]
    public void A_mod_the_preference_does_not_mention_is_still_placed()
    {
        var mods = TaomStack().Append(Mod("ModularSmithing2")).ToList();
        var r = LoadOrder.Compute(Stock, mods, Published, LoadOrder.Profile.Client, LoadOrder.OrderPolicy.Manual);
        Assert.Contains("ModularSmithing2", r.ModuleIds);
        Assert.Equal(Published, Community(r));
    }
}
