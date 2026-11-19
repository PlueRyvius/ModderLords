using ModderLords.Core.Modules;

namespace ModderLords.Core.Tests;

/// <summary>
/// Which folders in the dedicated server's engine\Modules count as the server's own (stock) modules.
///
/// Regression cover for 2026-11-18: another launcher had copied its mod preset into the server's Modules folder as
/// real folders. Every real folder there was taken as stock, so three of those mods went into the server's module
/// list for a profile that had them all off, and Coop refused every join ("module 'Improved Garrisons' is required").
/// </summary>
public class ServerStockClassificationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ml-stock-" + Guid.NewGuid().ToString("N"));
    private string Server => Path.Combine(_root, "Modules");

    public ServerStockClassificationTests() => Directory.CreateDirectory(Server);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private void MakeModule(string folderName, string id)
    {
        var folder = Path.Combine(Server, folderName);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "SubModule.xml"), $"""
            <?xml version="1.0" encoding="utf-8"?>
            <Module>
              <Name value="{id}"/>
              <Id value="{id}"/>
              <Version value="v1.0.0"/>
              <SubModules/>
            </Module>
            """);
    }

    /// <summary>What the Coop dedicated server ships, by folder and id.</summary>
    private void MakeServerPackage()
    {
        MakeModule("Native", "Native");
        MakeModule("SandBoxCore", "SandBoxCore");
        MakeModule("SandBox", "Sandbox");
        MakeModule("Coop", "CoopNightly");
        MakeModule("DedicatedServer.Windows", "DedicatedServer.Windows");
    }

    private ModuleCatalog Scan() => ModuleCatalog.Scan(Server, null, [], []);

    [Fact]
    public void The_server_package_modules_are_stock()
    {
        MakeServerPackage();

        var catalog = Scan();

        Assert.Equal(new[] { "Coop", "DedicatedServer.Windows", "Native", "SandBox", "SandBoxCore" },
            catalog.Modules.Where(m => m.IsStock).Select(m => m.FolderName).OrderBy(n => n, StringComparer.Ordinal));
        Assert.Empty(catalog.ForeignServerModules);
    }

    [Fact]
    public void A_community_mod_copied_in_as_a_real_folder_is_not_stock_and_not_in_the_catalog()
    {
        MakeServerPackage();
        MakeModule("ImprovedGarrisons", "ImprovedGarrisons");
        MakeModule("CoopModPatch", "CoopModPatch");

        var catalog = Scan();

        Assert.DoesNotContain(catalog.Modules, m => m.Id is "ImprovedGarrisons" or "CoopModPatch");
        Assert.Equal(new[] { "CoopModPatch", "ImprovedGarrisons" },
            catalog.ForeignServerModules.Select(m => m.Id).OrderBy(n => n, StringComparer.Ordinal));
    }

    [Fact]
    public void A_community_mod_in_the_server_folder_stays_out_of_the_server_load_order()
    {
        MakeServerPackage();
        MakeModule("ImprovedGarrisons", "ImprovedGarrisons");
        MakeModule("RaiseYourBanner", "RaiseYourBanner");

        var catalog = Scan();
        var order = LoadOrder.Compute(catalog.Modules.Where(m => m.IsStock).ToList(), []);

        Assert.DoesNotContain("ImprovedGarrisons", order.ModuleIds);
        Assert.DoesNotContain("RaiseYourBanner", order.ModuleIds);
        Assert.Contains("CoopNightly", order.ModuleIds);
    }

    [Fact]
    public void The_launch_note_names_the_folder_and_says_another_tool_put_it_there()
    {
        MakeServerPackage();
        MakeModule("ImprovedGarrisons", "ImprovedGarrisons");

        var note = ModuleCatalog.ForeignServerModuleNote(Scan().ForeignServerModules.Single());

        Assert.Contains("ImprovedGarrisons", note);
        Assert.Contains("another tool", note);
        Assert.Contains(Path.Combine(Server, "ImprovedGarrisons"), note);
    }

    [Theory]
    [InlineData("Native", true)]
    [InlineData("SandBoxCore", true)]
    [InlineData("Sandbox", true)]
    [InlineData("Multiplayer", true)]
    [InlineData("Coop", true)]
    [InlineData("CoopNightly", true)]
    [InlineData("DedicatedServer.Windows", true)]
    [InlineData("DedicatedServer.CoodszTools", true)]   // a partner launcher's server module; Coop's join check ignores DedicatedServer.*
    [InlineData("ImprovedGarrisons", false)]
    [InlineData("CoopModPatch", false)]                 // starts with "Coop" but is not Coop
    [InlineData("XorberaxLegacy", false)]
    public void Server_package_membership_is_decided_by_id(string id, bool expected) =>
        Assert.Equal(expected, ModuleCatalog.IsServerPackageModule(id));

    [Fact]
    public void A_manifest_claiming_official_does_not_make_a_community_mod_stock()
    {
        MakeServerPackage();
        var folder = Path.Combine(Server, "SettlementCultureDrift");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "SubModule.xml"), """
            <Module><Name value="SettlementCultureDrift"/><Id value="SettlementCultureDrift"/><Version value="v1.0.0"/>
            <ModuleType value="Official"/><SubModules/></Module>
            """);

        var catalog = Scan();

        Assert.DoesNotContain(catalog.Modules, m => m.Id == "SettlementCultureDrift");
        Assert.Single(catalog.ForeignServerModules);
    }

    // ---- the harness's check of the server's module list (ServerModuleListCheck) ----------------------------------

    private static readonly string[] PackageIds = ["Native", "SandBoxCore", "Sandbox", "CoopNightly", "DedicatedServer.Windows"];

    [Fact]
    public void A_server_list_of_the_package_and_the_profile_mods_passes()
    {
        var server = PackageIds.Concat(["ModderLords.Compat", "TAOM", "DedicatedServer.ModderLordsCompat"]);
        Assert.Null(ServerModuleListCheck.Problem(server, ["ModderLords.Compat", "TAOM"], []));
    }

    [Fact]
    public void A_module_the_profile_did_not_select_stops_the_run_and_says_another_tool_put_it_there()
    {
        // What the server loaded on 2026-11-18: the package, the profile's mods, and the other tool's copies.
        MakeServerPackage();
        MakeModule("ImprovedGarrisons", "ImprovedGarrisons");
        var foreign = Scan().ForeignServerModules;
        var server = PackageIds.Concat(["ModderLords.Compat", "ImprovedGarrisons", "Unknown.Mod"]);

        var problem = ServerModuleListCheck.Problem(server, ["ModderLords.Compat"], foreign);

        Assert.NotNull(problem);
        Assert.Contains("ImprovedGarrisons (a real folder in the server's Modules folder that another tool put there", problem);
        Assert.Contains(Path.Combine(Server, "ImprovedGarrisons"), problem);
        Assert.Contains("Unknown.Mod", problem);
        Assert.DoesNotContain("ModderLords.Compat", problem);
    }

    [Fact]
    public void A_copied_in_mod_the_profile_does_select_passes()
    {
        MakeServerPackage();
        MakeModule("ImprovedGarrisons", "ImprovedGarrisons");

        Assert.Null(ServerModuleListCheck.Problem(PackageIds.Append("ImprovedGarrisons"), ["improvedgarrisons"], Scan().ForeignServerModules));
    }
}
