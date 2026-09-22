using ModderLords.Core.Export;
using ModderLords.Core.Launch;
using ModderLords.Core.Modules;
using ModderLords.Core.Overlay;
using ModderLords.Core.Profiles;

namespace ModderLords.Core.Tests;

public class ProfileWorkflowTests
{
    [Fact]
    public void IncompleteExportPreservesWhereMissingRequirementsBelong()
    {
        var profile = new Profile { Mods = [new() { Id = "Before" }, new() { Id = "Installed" }, new() { Id = "After" }] };
        var catalog = ModuleCatalog.Scan("", null, [], []);
        var module = new DiscoveredModule("Installed", "v1.0.0", "unused", ModuleSourceKind.Custom,
            new Bannerlord.ModuleManager.ModuleInfoExtended { Id = "Installed", Name = "Installed" });
        var prepared = new ModuleSelectionResult(catalog, [new ModSelection(module, ServerRole.Run)], new LoadOrder.Result(["Installed"], []));
        var exported = ModListFile.From(prepared, profile);
        Assert.Equal(["Before", "Installed", "After"], exported.ToProfile("copy").Mods.Select(m => m.Id));
    }

    [Fact]
    public void LaunchVersionObservationsDoNotOverwriteLaterProfileEdits()
    {
        var launched = new Profile { Mods = [new() { Id = "A", SourcePath = "old", LastVersion = "v2.0.0" }, new() { Id = "B", SourcePath = "same", LastVersion = "v3.0.0" }] };
        var edited = new Profile { SaveName = "new-save", Mods = [new() { Id = "A", SourcePath = "new", LastVersion = "v9.0.0" }, new() { Id = "B", SourcePath = "same", Enabled = false }] };
        ProfileStore.MergeLastVersions(edited, launched);
        Assert.Equal("new-save", edited.SaveName);
        Assert.Equal("v9.0.0", edited.Mods[0].LastVersion);
        Assert.False(edited.Mods[1].Enabled);
        Assert.Equal("v3.0.0", edited.Mods[1].LastVersion);
    }

    [Fact]
    public void OfficialSelectionRoundTripsIncludingEmptyAndLegacy()
    {
        var profile = new Profile { ClientOfficialModules = ["Native", "SandBoxCore", "Sandbox", "NavalDLC"] };
        var catalog = ModuleCatalog.Scan("", null, [], []);
        var selected = new ModuleSelectionResult(catalog, [], new LoadOrder.Result([], []));
        var export = ModListFile.From(selected, profile);
        var path = Path.GetTempFileName();
        try
        {
            ModListFile.Write(path, export);
            Assert.Equal(profile.ClientOfficialModules, ModListFile.Read(path).ToProfile("copy").ClientOfficialModules);
            Assert.Empty((export with { ClientOfficialModules = [] }).ToProfile("empty").ClientOfficialModules);
            Assert.Equal(new Profile().ClientOfficialModules, new ModListFile().ToProfile("legacy").ClientOfficialModules);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void MissingRequirementsAndDownloadLinksSurviveExport()
    {
        var profile = new Profile { Mods = [new ProfileMod { Id = "Pending", LastVersion = "v1.2.0", DownloadUrl = "https://example.test/download" }] };
        var catalog = ModuleCatalog.Scan("", null, [], []);
        var file = ModListFile.From(new ModuleSelectionResult(catalog, [], new LoadOrder.Result([], [])), profile);
        var imported = Assert.Single(file.ToProfile("copy").Mods);
        Assert.Equal("Pending", imported.Id);
        Assert.Equal("v1.2.0", imported.LastVersion);
        Assert.Equal("https://example.test/download", imported.DownloadUrl);
    }

    [Theory]
    [InlineData(@"C:\Game\Modules\HandInstalled", "https://steamcommunity.com/sharedfiles/filedetails/?id=3000000002", "https://steamcommunity.com/sharedfiles/filedetails/?id=3000000002")]
    [InlineData(@"C:\Steam\workshop\content\261550\2859188632", null, "https://steamcommunity.com/sharedfiles/filedetails/?id=2859188632")]
    [InlineData(@"C:\Steam\workshop\content\261550\2859188632", "https://example.test/mirror", "https://example.test/mirror")]
    [InlineData(@"C:\Game\Modules\HandInstalled", null, null)]
    public void InstalledModsExportTheProfileLinkBeforeTheFolderOne(string folder, string? profileLink, string? expected)
    {
        var profile = new Profile { Mods = [new ProfileMod { Id = "Linked", DownloadUrl = profileLink }] };
        var catalog = ModuleCatalog.Scan("", null, [], []);
        var module = new DiscoveredModule("Linked", "v1.0.0", folder, ModuleSourceKind.Custom,
            new Bannerlord.ModuleManager.ModuleInfoExtended { Id = "Linked", Name = "Linked" });
        var prepared = new ModuleSelectionResult(catalog, [new ModSelection(module, ServerRole.Run)], new LoadOrder.Result(["Linked"], []));
        Assert.Equal(expected, Assert.Single(ModListFile.From(prepared, profile).Mods).Source);
    }

    [Fact]
    public void SettingsSidecarsAreNotListedAsProfiles()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "alpha.json"), "{}");
            File.WriteAllText(Path.Combine(root, "alpha.settings.json"), "{}");
            File.WriteAllText(Path.Combine(root, "beta.SETTINGS.JSON"), "{}");
            Assert.Equal(["alpha"], ProfileStore.ListIn(root));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void RunningProfileSnapshotCannotBeMutatedThroughEditor()
    {
        var profile = new Profile { Mods = [new ProfileMod { Id = "A", ClientSideBehaviors = ["UI"] }] };
        profile.Server.TraceTick = true;
        var snapshot = ProfileStore.Snapshot(profile);
        profile.Mods[0].ClientSideBehaviors.Clear();
        profile.Mods.Clear();
        profile.ClientOfficialModules.Clear();
        Assert.Equal("A", Assert.Single(snapshot.Mods).Id);
        Assert.Equal(["UI"], snapshot.Mods[0].ClientSideBehaviors);
        Assert.NotEmpty(snapshot.ClientOfficialModules);
        Assert.True(snapshot.Server.TraceTick);
    }

    [Fact]
    public void IsolatedViewExposesOnlySelectedCopyAndLeavesOriginalsUntouched()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "ModderLords-view-test-" + Guid.NewGuid().ToString("N"));
        var game = Path.Combine(root, "game");
        var chosen = Path.Combine(root, "workshop", "TestMod");
        var other = Path.Combine(game, "Modules", "TestMod");
        Directory.CreateDirectory(GamePaths.ClientBin(game));
        File.WriteAllText(Path.Combine(GamePaths.ClientBin(game), "Bannerlord.exe"), "fixture");
        foreach (var (folder, version) in new[] { (chosen, "v2.0.0"), (other, "v1.0.0") })
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "SubModule.xml"), $"<Module><Id value='TestMod'/><Name value='TestMod'/><Version value='{version}'/><SubModules/></Module>");
        }
        string? view = null;
        try
        {
            var mod = ModuleCatalog.TryParse(chosen, ModuleSourceKind.Workshop, out _)!;
            var plan = new ClientLaunchPlan { GameRoot = game, ModuleIds = ["TestMod"], SelectedModules = [mod], RequiresIsolatedView = true };
            view = ClientModuleView.Create(plan, Path.Combine(root, "views"));
            var visible = ModuleCatalog.Scan("", view, [], []);
            Assert.Equal("v2.0.0", Assert.Single(visible.Modules).Version);
            Assert.True(Junction.PointsTo(Path.Combine(view, "Modules", "TestMod"), chosen));
            Assert.Contains("v1.0.0", File.ReadAllText(Path.Combine(other, "SubModule.xml")));
            Assert.True(File.Exists(Path.Combine(GamePaths.ClientBin(view), "Bannerlord.exe")));
        }
        finally
        {
            // Unlink explicitly before removing the owned fixture; never recurse through game/module junctions.
            if (view is not null)
            {
                foreach (var link in Directory.GetDirectories(Path.Combine(view, "Modules"))) Junction.Remove(link);
                foreach (var dir in Directory.GetDirectories(view)) if (Junction.IsJunction(dir)) Junction.Remove(dir);
            }
            Directory.Delete(root, true);
        }
    }

    // ---- naming ------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("", "Enter a name.")]
    [InlineData("   ", "Enter a name.")]
    [InlineData("///", "cannot contain")]
    [InlineData("a:b", "cannot contain")]
    public void A_name_that_cannot_be_a_file_is_refused(string name, string expected)
        => Assert.Contains(expected, ProfileStore.NameProblem(name));

    [Fact]
    public void An_ordinary_name_is_accepted() => Assert.Null(ProfileStore.NameProblem("Nicks coop night"));

    [Fact]
    public void A_name_longer_than_the_limit_is_refused()
        => Assert.Contains("too long", ProfileStore.NameProblem(new string('x', 65)));

    /// <summary>The profile, its settings overrides, its settings cache and its overlay folder all move together.</summary>
    [Fact]
    public void Renaming_moves_the_profile_and_everything_named_after_it()
    {
        var from = "mbc-rename-" + Guid.NewGuid().ToString("N");
        var to = "mbc-renamed-" + Guid.NewGuid().ToString("N");
        try
        {
            ProfileStore.Save(new Profile { Name = from, Mods = [new() { Id = "CoopNightly" }] });
            Directory.CreateDirectory(Path.GetDirectoryName(ProfileStore.SettingsOverridesPath(from))!);
            File.WriteAllText(ProfileStore.SettingsOverridesPath(from), "{}");
            Directory.CreateDirectory(ProfileStore.OverlayDirFor(from));

            Assert.Contains("already exists", ProfileStore.NameProblem(from));   // a name in use is refused
            ProfileStore.Rename(from, to);

            Assert.False(ProfileStore.Exists(from));
            Assert.True(ProfileStore.Exists(to));
            Assert.Equal(to, ProfileStore.Load(to)!.Name);
            Assert.Equal(["CoopNightly"], ProfileStore.Load(to)!.Mods.Select(m => m.Id));
            Assert.True(File.Exists(ProfileStore.SettingsOverridesPath(to)));
            Assert.False(File.Exists(ProfileStore.SettingsOverridesPath(from)));
            Assert.True(Directory.Exists(ProfileStore.OverlayDirFor(to)));
            Assert.False(Directory.Exists(ProfileStore.OverlayDirFor(from)));
        }
        finally
        {
            foreach (var n in new[] { from, to })
            {
                ProfileStore.Delete(n);
                if (Directory.Exists(ProfileStore.OverlayDirFor(n))) Directory.Delete(ProfileStore.OverlayDirFor(n), true);
            }
        }
    }

    /// <summary>Changing only the capitalisation is a rename, not a collision with itself.</summary>
    [Fact]
    public void Renaming_to_a_different_capitalisation_is_allowed()
    {
        var name = "mbc-Case-" + Guid.NewGuid().ToString("N");
        try
        {
            ProfileStore.Save(new Profile { Name = name });
            Assert.Null(ProfileStore.NameProblem(name.ToUpperInvariant(), currentName: name));
            ProfileStore.Rename(name, name.ToUpperInvariant());
            Assert.Equal(name.ToUpperInvariant(), ProfileStore.Load(name)!.Name);
        }
        finally { ProfileStore.Delete(name); }
    }

    [Fact]
    public void Renaming_something_that_is_not_there_says_so()
        => Assert.Throws<InvalidOperationException>(() => ProfileStore.Rename("mbc-absent-" + Guid.NewGuid().ToString("N"), "mbc-x"));
}
