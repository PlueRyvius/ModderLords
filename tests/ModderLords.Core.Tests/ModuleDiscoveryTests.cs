using ModderLords.Core.Modules;
using ModderLords.Core.Profiles;

namespace ModderLords.Core.Tests;

/// <summary>
/// Discovery of modules buried inside a package folder, and the tie-break between a package's per-side trees.
///
/// Regression cover for COOP Family 1.4 (2026-09-19): it ships as a container holding Client/CoopMarriage and
/// Server/CoopMarriage, a person drops the whole thing into Modules exactly as downloaded, and the one-level scan
/// hid both halves with no error at all.
/// </summary>
public class ModuleDiscoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ml-disc-" + Guid.NewGuid().ToString("N"));

    public ModuleDiscoveryTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private string MakeModule(string relativePath, string id, string version = "v1.0.0")
    {
        var folder = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "SubModule.xml"), $"""
            <?xml version="1.0" encoding="utf-8"?>
            <Module>
              <Name value="{id}"/>
              <Id value="{id}"/>
              <Version value="{version}"/>
              <SubModules/>
              <Xmls/>
            </Module>
            """);
        return folder;
    }

    private ModuleCatalog ScanRootOnly() => ModuleCatalog.Scan("", null, [], [_root]);

    [Fact]
    public void A_package_shipping_client_and_server_trees_is_discovered()
    {
        MakeModule(Path.Combine("CoopMarriage 1.4 WORKING", "Client", "CoopMarriage"), "CoopMarriage", "v1.4.0");
        MakeModule(Path.Combine("CoopMarriage 1.4 WORKING", "Server", "CoopMarriage"), "CoopMarriage", "v1.4.0");

        var found = ScanRootOnly().Modules.Where(m => m.Id == "CoopMarriage").ToList();

        Assert.Equal(2, found.Count);
        Assert.Contains(found, m => m.FolderPath.Contains(Path.Combine("Client", "CoopMarriage")));
        Assert.Contains(found, m => m.FolderPath.Contains(Path.Combine("Server", "CoopMarriage")));
    }

    [Fact]
    public void A_plain_module_still_works_and_is_found_at_depth_zero()
    {
        MakeModule("PlainMod", "PlainMod");
        Assert.Single(ScanRootOnly().Modules, m => m.Id == "PlainMod");
    }

    [Fact]
    public void A_module_is_never_descended_into()
    {
        // TAOM.Dependencies really does carry nested folders with manifests of their own. Treating those as modules
        // would invent entries the game will never load.
        MakeModule("Outer", "Outer");
        MakeModule(Path.Combine("Outer", "Nested"), "NestedShouldNotAppear");

        var ids = ScanRootOnly().Modules.Select(m => m.Id).ToList();

        Assert.Contains("Outer", ids);
        Assert.DoesNotContain("NestedShouldNotAppear", ids);
    }

    [Fact]
    public void Nesting_deeper_than_the_bound_is_not_discovered()
    {
        MakeModule(Path.Combine("a", "b", "c", "d", "TooDeep"), "TooDeep");
        Assert.DoesNotContain(ScanRootOnly().Modules, m => m.Id == "TooDeep");
    }

    [Fact]
    public void Nesting_at_the_bound_is_discovered()
    {
        MakeModule(Path.Combine("a", "b", "AtBound"), "AtBound");
        Assert.Contains(ScanRootOnly().Modules, m => m.Id == "AtBound");
    }

    [Fact]
    public void Our_own_data_directory_is_never_scanned()
    {
        // A custom root pointing at (or above) %LOCALAPPDATA%\ModderLords would otherwise discover our own overlay
        // shadow copies as if they were installed mods.
        var ours = ProfileStore.RootDir;
        var catalog = ModuleCatalog.Scan("", null, [], [ours, Path.GetDirectoryName(ours.TrimEnd(Path.DirectorySeparatorChar))!]);

        Assert.DoesNotContain(catalog.Modules, m => m.FolderPath.StartsWith(ours, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_root_that_does_not_exist_is_not_an_error()
    {
        var catalog = ModuleCatalog.Scan("", null, [], [Path.Combine(_root, "nope")]);
        Assert.Empty(catalog.Modules);
        Assert.Empty(catalog.Problems);
    }

    [Fact]
    public void A_container_with_no_modules_under_it_contributes_nothing()
    {
        Directory.CreateDirectory(Path.Combine(_root, "junk", "more", "deeper"));
        File.WriteAllText(Path.Combine(_root, "junk", "readme.txt"), "not a module");

        var catalog = ScanRootOnly();
        Assert.Empty(catalog.Modules);
        Assert.Empty(catalog.Problems);
    }

    // ---- side preference -------------------------------------------------------------------------------------

    /// <summary>
    /// Two complete trees of one module, one per side, in two separate roots so the ORDER they reach the catalog is
    /// controlled by the test rather than by the filesystem. Both orderings are asserted: before this tie-break the
    /// winner was whichever the OS enumerated first, so a test that fixed only one ordering could pass by luck.
    /// </summary>
    private (ModuleCatalog catalog, Profile profile) TwoSidedPackage(bool clientRootFirst)
    {
        MakeModule(Path.Combine("rootA", "Client", "CoopMarriage"), "CoopMarriage", "v1.4.0");
        MakeModule(Path.Combine("rootB", "Server", "CoopMarriage"), "CoopMarriage", "v1.4.0");
        var a = Path.Combine(_root, "rootA");
        var b = Path.Combine(_root, "rootB");

        var catalog = ModuleCatalog.Scan("", null, [], clientRootFirst ? [a, b] : [b, a]);
        var profile = new Profile { Mods = { new ProfileMod { Id = "CoopMarriage", Enabled = true } } };
        return (catalog, profile);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_server_launch_prefers_the_server_tree(bool clientRootFirst)
    {
        var (catalog, profile) = TwoSidedPackage(clientRootFirst);
        var messages = new List<string>();

        var picked = Assert.Single(ModuleSelector.Select(profile, catalog, messages, ModuleSelector.ModuleSide.Server));

        Assert.Equal(Path.Combine(_root, "rootB", "Server", "CoopMarriage"), picked.Module.FolderPath);
        Assert.Contains(messages, m => m.Contains("2 copies found"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_client_launch_prefers_the_client_tree(bool clientRootFirst)
    {
        var (catalog, profile) = TwoSidedPackage(clientRootFirst);

        var picked = Assert.Single(ModuleSelector.Select(profile, catalog, [], ModuleSelector.ModuleSide.Client));

        Assert.Equal(Path.Combine(_root, "rootA", "Client", "CoopMarriage"), picked.Module.FolderPath);
    }

    [Fact]
    public void A_pinned_source_path_still_wins_over_the_side_preference()
    {
        // An explicit choice the user made is not a tie, so the tie-break must not touch it.
        var (catalog, profile) = TwoSidedPackage(clientRootFirst: true);
        profile.Mods[0].SourcePath = Path.Combine(_root, "rootA", "Client", "CoopMarriage");

        var picked = Assert.Single(ModuleSelector.Select(profile, catalog, [], ModuleSelector.ModuleSide.Server));

        Assert.Equal(profile.Mods[0].SourcePath, picked.Module.FolderPath);
    }

    // ---- copies at one version -------------------------------------------------------------------------------

    /// <summary>
    /// The 1.2.5 report: the full mod in one place for the host's own game, and the same mod at the same version
    /// minus RuntimeDataCache in another for the server. Nothing in either manifest tells them apart.
    /// </summary>
    private (string Full, string Stripped) FullAndStrippedCopies(string version = "v1.0.0", string strippedVersion = "v1.0.0")
    {
        var full = MakeModule(Path.Combine("game", "TwinMod"), "TwinMod", version);
        var stripped = MakeModule(Path.Combine("server", "TwinMod"), "TwinMod", strippedVersion);
        foreach (var dir in new[] { "ModuleData", "RuntimeDataCache" }) Directory.CreateDirectory(Path.Combine(full, dir));
        Directory.CreateDirectory(Path.Combine(stripped, "ModuleData"));
        return (full, stripped);
    }

    private ModuleCatalog ScanGameThenServer() =>
        ModuleCatalog.Scan("", null, [], [Path.Combine(_root, "game"), Path.Combine(_root, "server")]);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_pin_chooses_between_two_copies_at_the_same_version(bool pinStripped)
    {
        var (full, stripped) = FullAndStrippedCopies();
        var pin = pinStripped ? stripped : full;
        var profile = new Profile { Mods = { new ProfileMod { Id = "TwinMod", SourcePath = pin, LastVersion = "v1.0.0" } } };

        var picked = Assert.Single(ModuleSelector.Select(profile, ScanGameThenServer(), []));

        Assert.Equal(pin, picked.Module.FolderPath);
    }

    [Fact]
    public void A_pin_outside_every_scanned_folder_is_still_the_copy_that_loads()
    {
        var (full, stripped) = FullAndStrippedCopies();
        var profile = new Profile { Mods = { new ProfileMod { Id = "TwinMod", SourcePath = stripped } } };
        var gameOnly = ModuleCatalog.Scan("", null, [], [Path.Combine(_root, "game")]);

        var picked = Assert.Single(ModuleSelector.Select(profile, gameOnly, []));

        Assert.Equal(stripped, picked.Module.FolderPath);
        Assert.NotEqual(full, picked.Module.FolderPath);
    }

    [Fact]
    public void A_pinned_folder_that_now_holds_a_different_mod_is_not_loaded_in_its_place()
    {
        // Only reachable for a folder outside the catalog: inside it, candidates are already matched by id.
        var real = MakeModule(Path.Combine("game", "TwinMod"), "TwinMod");
        var squatter = MakeModule(Path.Combine("elsewhere", "TwinMod"), "SomethingElse");
        var profile = new Profile { Mods = { new ProfileMod { Id = "TwinMod", SourcePath = squatter } } };
        var gameOnly = ModuleCatalog.Scan("", null, [], [Path.Combine(_root, "game")]);

        var picked = Assert.Single(ModuleSelector.Select(profile, gameOnly, []));

        Assert.Equal("TwinMod", picked.Module.Id);
        Assert.Equal(real, picked.Module.FolderPath);
    }

    [Fact]
    public void Copies_at_one_version_with_different_top_level_folders_are_separate_choices()
    {
        var (full, stripped) = FullAndStrippedCopies();
        var copies = ScanGameThenServer().Candidates("TwinMod").ToList();

        Assert.Equal([full, stripped], ModuleCopies.Distinct(copies, "TwinMod").Select(m => m.FolderPath));
    }

    [Fact]
    public void Copies_at_one_version_with_the_same_top_level_folders_are_one_choice()
    {
        // The routine case: a mod in the game's Modules folder and in the workshop. It must not double every row.
        var (full, stripped) = FullAndStrippedCopies();
        Directory.CreateDirectory(Path.Combine(stripped, "RuntimeDataCache"));
        var copies = ScanGameThenServer().Candidates("TwinMod").ToList();

        Assert.Equal([full], ModuleCopies.Distinct(copies, "TwinMod").Select(m => m.FolderPath));
        // ...and the copy a profile pins is the one left standing, whichever was scanned first.
        Assert.Equal([stripped], ModuleCopies.Distinct(copies, "TwinMod", keep: copies[1]).Select(m => m.FolderPath));
    }

    [Fact]
    public void Different_versions_are_separate_choices_whatever_they_contain()
    {
        var (full, stripped) = FullAndStrippedCopies(strippedVersion: "v2.0.0");
        Directory.CreateDirectory(Path.Combine(stripped, "RuntimeDataCache"));
        var copies = ScanGameThenServer().Candidates("TwinMod").ToList();

        Assert.Equal([full, stripped], ModuleCopies.Distinct(copies, "TwinMod").Select(m => m.FolderPath));
    }

    // ---- the host's own game, launched against their server ---------------------------------------------------

    [Fact]
    public void The_hosts_game_is_not_handed_a_copy_cut_down_for_the_server()
    {
        var (full, stripped) = FullAndStrippedCopies();
        var catalog = ScanGameThenServer();
        var serverCopy = catalog.Candidates("TwinMod").Single(m => m.FolderPath == stripped);

        Assert.Equal(full, ModderLords.Coop.Launch.ServerMatchedClient.ClientCopy(serverCopy, catalog).FolderPath);
    }

    [Fact]
    public void The_hosts_game_keeps_the_servers_copy_when_no_other_is_known_to_be_fuller()
    {
        var (full, stripped) = FullAndStrippedCopies();
        var catalog = ScanGameThenServer();
        var copies = catalog.Candidates("TwinMod").ToList();

        // The server runs the full copy: the stripped one is never an improvement on it.
        Assert.Equal(full, ModderLords.Coop.Launch.ServerMatchedClient.ClientCopy(copies.Single(m => m.FolderPath == full), catalog).FolderPath);

        // Same folders on both sides: nothing says the server's choice is wrong for a player, so it stands.
        Directory.CreateDirectory(Path.Combine(stripped, "RuntimeDataCache"));
        Assert.Equal(stripped, ModderLords.Coop.Launch.ServerMatchedClient.ClientCopy(copies.Single(m => m.FolderPath == stripped), catalog).FolderPath);
    }

    [Fact]
    public void The_hosts_game_never_swaps_to_a_copy_at_another_version()
    {
        // Coop matches community modules on id AND version, so a fuller copy at another version is not a substitute.
        var (_, stripped) = FullAndStrippedCopies(version: "v2.0.0");
        var catalog = ScanGameThenServer();
        var serverCopy = catalog.Candidates("TwinMod").Single(m => m.FolderPath == stripped);

        Assert.Equal(stripped, ModderLords.Coop.Launch.ServerMatchedClient.ClientCopy(serverCopy, catalog).FolderPath);
    }

    [Fact]
    public void An_ordinary_single_copy_mod_is_unaffected_by_the_side_preference()
    {
        MakeModule("PlainMod", "PlainMod");
        var profile = new Profile { Mods = { new ProfileMod { Id = "PlainMod", Enabled = true } } };
        var messages = new List<string>();

        var picked = Assert.Single(ModuleSelector.Select(profile, ScanRootOnly(), messages, ModuleSelector.ModuleSide.Server));

        Assert.Equal(Path.Combine(_root, "PlainMod"), picked.Module.FolderPath);
        Assert.DoesNotContain(messages, m => m.Contains("copies found"));
    }
}
