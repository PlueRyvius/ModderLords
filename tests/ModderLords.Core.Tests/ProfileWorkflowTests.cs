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
}
