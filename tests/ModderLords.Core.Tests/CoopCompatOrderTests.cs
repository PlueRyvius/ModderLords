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
    public void With_no_Coop_installed_a_follower_is_an_ordinary_mod()
    {
        // Someone using this purely as a mod loader: there is no Coop to follow, so nothing is forced anywhere.
        var stock = new List<DiscoveredModule> { Stock("Native", "Native"), Stock("SandBox", "Sandbox") };
        var community = new List<DiscoveredModule> { Mod("TAOM.CoopCompat", "CoopNightly"), Mod("ImprovedGarrisons") };

        var ids = LoadOrder.Compute(stock, community, ["TAOM.CoopCompat", "ImprovedGarrisons"],
            profile: LoadOrder.Profile.Client).ModuleIds.ToList();

        Assert.True(ids.IndexOf("TAOM.CoopCompat") < ids.IndexOf("ImprovedGarrisons"),
            "with no Coop present the preferred order stands: " + string.Join(", ", ids));
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    // ---- the player's own game: Coop is an ordinary community module ------------------------------------------

    /// <summary>The player's stock set. Coop is NOT here: on their machine it is a workshop mod like any other.</summary>
    private static List<DiscoveredModule> ClientStock() =>
        [Stock("Native", "Native"), Stock("SandBoxCore", "SandBoxCore"), Stock("SandBox", "Sandbox")];

    private static LoadOrder.Result Client(IReadOnlyList<string> preferred, IReadOnlyCollection<string>? knownToFollowCoop = null,
                                           LoadOrder.OrderPolicy policy = LoadOrder.OrderPolicy.Suggest, params DiscoveredModule[] community) =>
        LoadOrder.Compute(ClientStock(), community.ToList(), preferred, LoadOrder.Profile.Client, policy, knownToFollowCoop);

    [Fact]
    public void A_curated_record_makes_a_mod_follow_Coop_without_a_manifest_claim()
    {
        // CoopMarriage declares nothing about Coop at all -- only the compatibility database knows.
        Assert.False(LoadOrder.LoadsAfterCoop(Mod("CoopMarriage"), ["Coop", "CoopNightly"]));
        Assert.True(LoadOrder.LoadsAfterCoop(Mod("CoopMarriage"), ["Coop", "CoopNightly"], ["CoopMarriage"]));
    }

    [Fact]
    public void On_the_client_a_follower_lands_immediately_after_Coop_wherever_the_user_put_it()
    {
        // Coop in the middle of the list, and a mod that must follow it placed first. Appending followers to the
        // END of the order would also satisfy "after Coop" here, so assert the tighter property: right after it.
        var ids = Client(["CoopMarriage", "CoopNightly", "ImprovedGarrisons"], ["CoopMarriage"], community:
            [Mod("CoopMarriage"), Mod("CoopNightly"), Mod("ImprovedGarrisons")]).ModuleIds.ToList();

        Assert.Equal(ids.IndexOf("CoopNightly") + 1, ids.IndexOf("CoopMarriage"));
        Assert.True(ids.IndexOf("CoopMarriage") < ids.IndexOf("ImprovedGarrisons"),
            "the follower goes next to Coop, not to the end of the list: " + string.Join(", ", ids));
    }

    [Fact]
    public void On_the_client_a_follower_never_precedes_Coop_even_when_Coop_is_last()
    {
        // The exact order that crashed Bannerlord at startup on 2026-09-18: Coop parked at the end of the list.
        var result = Client(["CoopMarriage", "ImprovedGarrisons", "CoopNightly"], ["CoopMarriage"], community:
            [Mod("CoopMarriage"), Mod("ImprovedGarrisons"), Mod("CoopNightly")]);
        var ids = result.ModuleIds.ToList();

        Assert.True(ids.IndexOf("CoopNightly") < ids.IndexOf("CoopMarriage"),
            "CoopMarriage patches Coop and crashes the game if it loads first: " + string.Join(", ", ids));
        Assert.Contains(result.Issues, i => i.Contains("CoopMarriage") && i.Contains("moved after"));
    }

    [Fact]
    public void My_order_wins_keeps_the_crashing_order_but_says_what_it_costs()
    {
        var result = Client(["CoopMarriage", "CoopNightly"], ["CoopMarriage"], LoadOrder.OrderPolicy.Manual,
            Mod("CoopMarriage"), Mod("CoopNightly"));
        var ids = result.ModuleIds.ToList();

        Assert.True(ids.IndexOf("CoopMarriage") < ids.IndexOf("CoopNightly"), "Manual means the user's order stands");
        Assert.Contains(result.Issues, i => i.Contains("CoopMarriage") && i.Contains("crash at startup"));
    }

    [Fact]
    public void A_follower_the_user_already_placed_after_Coop_is_not_remarked_on()
    {
        var result = Client(["CoopNightly", "CoopMarriage"], ["CoopMarriage"], community:
            [Mod("CoopNightly"), Mod("CoopMarriage")]);

        Assert.DoesNotContain(result.Issues, i => i.Contains("CoopMarriage"));
    }

    [Fact]
    public void The_server_order_is_unchanged_by_a_curated_record()
    {
        // Coop stays pinned after the community block and DedicatedServer.Windows last, exactly as the host does it.
        var ids = LoadOrder.Compute(ServerStock(), [Mod("CoopMarriage"), Mod("ImprovedGarrisons")],
            ["CoopMarriage", "ImprovedGarrisons"], LoadOrder.Profile.DedicatedServer,
            knownToFollowCoop: ["CoopMarriage"]).ModuleIds.ToList();

        Assert.Equal(ids.Count - 1, ids.IndexOf("DedicatedServer.Windows"));
        Assert.True(ids.IndexOf("CoopNightly") < ids.IndexOf("CoopMarriage"));
        Assert.True(ids.IndexOf("ImprovedGarrisons") < ids.IndexOf("CoopNightly"));
    }
}
