using ModderLords.Coop.Launch;
using ModderLords.Core.Export;
using ModderLords.Core.Launch;
using ModderLords.Core.Modules;
using ModderLords.Core.Overlay;
using ModderLords.Core.Profiles;
using Xunit;

namespace ModderLords.Core.Tests;

/// <summary>
/// "Server only" folders: a game started through ModderLords is not shown them. Launched from its real folder a mod
/// is all or nothing, so leaving a folder out means the private view, and in it a real folder for that one mod. The
/// rest of the launch must not notice: other mods stay single junctions, nothing is copied out of a mod's folders,
/// the originals are never touched, and a bad name can only ever be reported.
/// </summary>
public sealed class ClientFolderExclusionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ModderLords-client-folders-" + Guid.NewGuid().ToString("N"));
    private readonly string _game;
    private readonly List<string> _views = [];

    public ClientFolderExclusionTests()
    {
        _game = Path.Combine(_root, "game");
        Directory.CreateDirectory(GamePaths.ClientBin(_game));
        File.WriteAllText(Path.Combine(GamePaths.ClientBin(_game), "Bannerlord.exe"), "fixture");
        Module("Native");
        var mod = Module("FolderMod", "bin", "ModuleData", "DsData");
        File.WriteAllText(Path.Combine(mod, "DsData", "server.xml"), "<server/>");
        File.WriteAllText(Path.Combine(mod, "ModuleData", "items.xml"), "<items/>");
        File.WriteAllText(Path.Combine(mod, "config.json"), "{}");
        Module("PlainMod", "ModuleData");
    }

    public void Dispose()
    {
        // Unlink before deleting, and never recurse through a link into the fixture's "installed" mods.
        if (OperatingSystem.IsWindows())
            foreach (var view in _views)
            {
                foreach (var dir in Directory.GetDirectories(view)) if (Junction.IsJunction(dir)) Junction.Remove(dir);
                foreach (var module in Directory.GetDirectories(Path.Combine(view, "Modules")))
                {
                    if (Junction.IsJunction(module)) { Junction.Remove(module); continue; }
                    foreach (var link in Directory.GetDirectories(module)) Junction.Remove(link);
                }
            }
        Directory.Delete(_root, true);
    }

    private string Module(string id, params string[] folders)
    {
        var folder = Path.Combine(_game, "Modules", id);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "SubModule.xml"), $"<Module><Id value='{id}'/><Name value='{id}'/><Version value='v1.0.0'/><SubModules/></Module>");
        foreach (var f in folders) Directory.CreateDirectory(Path.Combine(folder, f));
        return folder;
    }

    private string FolderOf(string id) => Path.Combine(_game, "Modules", id);

    private Profile ProfileWith(params string[]? serverOnlyFolders) => new()
    {
        Name = "client-folders", GameRoot = _game, ClientOfficialModules = ["Native"],
        Mods =
        [
            new ProfileMod { Id = "FolderMod", ClientExcludedFolders = serverOnlyFolders?.ToList() },
            new ProfileMod { Id = "PlainMod" },
        ],
    };

    private ClientLaunchSession.Prepared Prepare(Profile profile) =>
        ClientLaunchSession.Prepare(profile, (ModuleCatalog.Scan("", _game, [], []), _game));

    private string View(ClientLaunchPlan plan)
    {
        var view = ClientModuleView.Create(plan, Path.Combine(_root, "views"));
        _views.Add(view);
        return view;
    }

    [Fact]
    public void A_server_only_folder_forces_the_private_view_and_is_named_in_the_messages()
    {
        // Both mods sit in the game's own Modules folder under unique ids: nothing else here asks for a view.
        var plain = Prepare(ProfileWith());
        Assert.False(plain.Plan.RequiresIsolatedView);
        Assert.False(plain.Plan.UsesIsolatedView);
        Assert.Empty(plain.Plan.ExcludedFolders);
        Assert.DoesNotContain(plain.Messages, m => m.StartsWith("folders:"));

        var prepared = Prepare(ProfileWith("dsdata"));

        Assert.True(prepared.Plan.RequiresIsolatedView);
        // Named as the folder is on disk, whatever casing the profile used.
        Assert.Equal(["DsData"], prepared.Plan.ExcludedFolders["FolderMod"]);
        Assert.False(prepared.Plan.ExcludedFolders.ContainsKey("PlainMod"));
        Assert.Contains(prepared.Messages, m => m.Contains("FolderMod") && m.Contains(ClientFolderExclusions.LeftOutNote + "DsData"));
        Assert.Equal(["Native", "FolderMod", "PlainMod"], prepared.Plan.ModuleIds);
    }

    [Fact]
    public void Names_that_cannot_be_honoured_are_reported_and_change_nothing()
    {
        // A folder this copy does not have, the one folder that may never be left out, a path, and the manifest.
        var prepared = Prepare(ProfileWith("RuntimeDataCache", "bin", "..\\PlainMod", "SubModule.xml"));

        // Nothing real to leave out, so the launch is exactly the one it would have been: no view, no entry.
        Assert.False(prepared.Plan.RequiresIsolatedView);
        Assert.False(prepared.Plan.UsesIsolatedView);
        Assert.Empty(prepared.Plan.ExcludedFolders);
        Assert.Empty(prepared.Plan.MissingModules);
        Assert.Contains(prepared.Messages, m => m.Contains(ClientFolderExclusions.NotLeftOutNote + "RuntimeDataCache"));
        var ignored = Assert.Single(prepared.Messages, m => m.Contains("WARNING: ignored"));
        Assert.Contains("\"bin\"", ignored);
        Assert.Contains("the game loads the mod's code from bin", ignored);
        Assert.Contains("\"..\\PlainMod\"", ignored);
        Assert.Contains("\"SubModule.xml\"", ignored);

        // Alongside a real one they are still only reported.
        var mixed = Prepare(ProfileWith("bin", "DsData", "Nope"));
        Assert.Equal(["DsData"], mixed.Plan.ExcludedFolders["FolderMod"]);
        Assert.Contains(mixed.Messages, m => m.Contains(ClientFolderExclusions.NotLeftOutNote + "Nope"));
        Assert.Contains(mixed.Messages, m => m.Contains("WARNING: ignored \"bin\""));
    }

    [Fact]
    public void The_view_shows_the_mod_without_the_folder_and_everything_else_as_before()
    {
        if (!OperatingSystem.IsWindows()) return;
        var view = View(Prepare(ProfileWith("DsData")).Plan);
        var inView = Path.Combine(view, "Modules", "FolderMod");

        // A real folder of links, not a link to the mod.
        Assert.True(Directory.Exists(inView));
        Assert.False(Junction.IsJunction(inView));
        Assert.False(Directory.Exists(Path.Combine(inView, "DsData")));
        Assert.True(Junction.PointsTo(Path.Combine(inView, "ModuleData"), Path.Combine(FolderOf("FolderMod"), "ModuleData")));
        Assert.True(Junction.PointsTo(Path.Combine(inView, "bin"), Path.Combine(FolderOf("FolderMod"), "bin")));
        Assert.True(File.Exists(Path.Combine(inView, "ModuleData", "items.xml")));
        // The manifest and the mod's other top-level files are there as files.
        Assert.Equal(File.ReadAllText(Path.Combine(FolderOf("FolderMod"), "SubModule.xml")), File.ReadAllText(Path.Combine(inView, "SubModule.xml")));
        Assert.Equal("{}", File.ReadAllText(Path.Combine(inView, "config.json")));
        Assert.Equal(["bin", "ModuleData"], Directory.GetDirectories(inView).Select(Path.GetFileName).Order(StringComparer.OrdinalIgnoreCase));

        // A mod with nothing to leave out is still one junction to its folder, and so is the game's own module.
        Assert.True(Junction.PointsTo(Path.Combine(view, "Modules", "PlainMod"), FolderOf("PlainMod")));
        Assert.True(Junction.PointsTo(Path.Combine(view, "Modules", "Native"), FolderOf("Native")));

        // The engine's own discovery finds all three in the view, at the same versions.
        var visible = ModuleCatalog.Scan("", view, [], []);
        Assert.Equal(["FolderMod", "Native", "PlainMod"], visible.Modules.Select(m => m.Id).Order(StringComparer.OrdinalIgnoreCase));

        // The installed mod is untouched: the folder left out of the view is still there, contents and all.
        Assert.Equal("<server/>", File.ReadAllText(Path.Combine(FolderOf("FolderMod"), "DsData", "server.xml")));
        Assert.False(Junction.IsJunction(Path.Combine(FolderOf("FolderMod"), "ModuleData")));
    }

    /// <summary>
    /// A plan can be built by hand (the CLI and the smoke test both adjust one). The view must not trust it: a view
    /// without a mod's bin is a game that fails to start, and a path must never reach the file system as a name.
    /// </summary>
    [Fact]
    public void A_hand_built_plan_cannot_leave_out_bin_or_reach_outside_the_mod()
    {
        if (!OperatingSystem.IsWindows()) return;
        var plan = Prepare(ProfileWith()).Plan with
        {
            ExcludedFolders = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["FolderMod"] = ["bin", "..\\PlainMod"],
            },
        };
        // The entry alone asks for the view, even though nobody set the flag.
        Assert.False(plan.RequiresIsolatedView);
        Assert.True(plan.UsesIsolatedView);

        var view = View(plan);

        // Nothing valid to leave out, so the mod is the single junction it always was.
        Assert.True(Junction.PointsTo(Path.Combine(view, "Modules", "FolderMod"), FolderOf("FolderMod")));
        Assert.True(Directory.Exists(Path.Combine(view, "Modules", "FolderMod", "bin")));
    }

    /// <summary>
    /// Host mode's Launch client builds the game's mod list from what the server selected, one fresh entry per mod.
    /// The "server only" folders are the part of the host's profile entry that is about the game, so they come along;
    /// the entry gets its own copy so a launch can never edit the profile.
    /// </summary>
    [Fact]
    public void The_hosts_own_client_takes_the_server_only_folders_from_the_hosts_profile()
    {
        var catalog = ModuleCatalog.Scan("", _game, [], []);
        var host = ProfileWith("DsData");
        host.Mods[0].ServerExcludedFolders = ["RuntimeDataCache"];

        var entry = ServerMatchedClient.ClientEntry(catalog.Candidates("FolderMod").Single(), catalog, host);

        Assert.Equal("FolderMod", entry.Id);
        Assert.Equal(["DsData"], entry.ClientExcludedFolders);
        Assert.NotSame(host.Mods[0].ClientExcludedFolders, entry.ClientExcludedFolders);
        Assert.Equal(FolderOf("FolderMod"), entry.SourcePath);
        Assert.Equal("v1.0.0", entry.LastVersion);

        Assert.Null(ServerMatchedClient.ClientEntry(catalog.Candidates("PlainMod").Single(), catalog, host).ClientExcludedFolders);

        // And the launch built from such an entry leaves the folder out.
        var client = ProfileStore.Snapshot(host);
        client.Mods = [entry, new ProfileMod { Id = "PlainMod" }];
        Assert.Equal(["DsData"], Prepare(client).Plan.ExcludedFolders["FolderMod"]);
    }

    /// <summary>The list travels in the profile file and in a shared mod list, so whoever receives either gets it.</summary>
    [Fact]
    public void The_list_survives_the_profile_snapshot_and_an_exported_mod_list()
    {
        var profile = ProfileWith("DsData");
        Assert.Equal(["DsData"], ProfileStore.Snapshot(profile).Mods[0].ClientExcludedFolders);
        Assert.Null(ProfileStore.Snapshot(profile).Mods[1].ClientExcludedFolders);

        var exported = ModListFile.From(Prepare(profile).Modules, profile);

        Assert.Equal(["DsData"], exported.Mods.Single(m => m.Id == "FolderMod").ClientExcludedFolders);
        Assert.Null(exported.Mods.Single(m => m.Id == "PlainMod").ClientExcludedFolders);
    }
}
