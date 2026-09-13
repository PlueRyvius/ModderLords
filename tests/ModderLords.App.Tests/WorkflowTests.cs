using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using ModderLords.App.ViewModels;
using ModderLords.Core.Launch;
using ModderLords.Core.Profiles;
using ModderLords.Core.Modules;
using ModderLords.Core.Overlay;
using ModderLords.Coop.Launch;
using Xunit;
using System.Windows.Controls;
using System.Windows.Data;

namespace ModderLords.App.Tests;

public class WorkflowTests
{
    [Fact]
    public void EmbeddedWindowIconDecodesAtEveryOriginalSize() => Sta(() =>
    {
        var resources = new System.Resources.ResourceManager("ModderLords.g", typeof(MainViewModel).Assembly);
        using var stream = resources.GetStream("appicon.ico");
        Assert.NotNull(stream);
        var icon = new System.Windows.Media.Imaging.IconBitmapDecoder(stream,
            System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat,
            System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
        Assert.Equal(new[] { 16, 24, 32, 48, 64, 128, 256 }, icon.Frames.Select(f => f.PixelWidth));
        foreach (var frame in icon.Frames)
        {
            var stride = (frame.PixelWidth * frame.Format.BitsPerPixel + 7) / 8;
            frame.CopyPixels(new byte[stride * frame.PixelHeight], stride, 0);
        }
    });

    [Fact]
    public void SaveKeepsTheProfileSelectedInTheBoundDropdown() => Sta(() =>
    {
        using var fixture = new Fixture();
        var vm = fixture.ViewModel();
        var profile = vm.Profile;
        var path = ProfileStore.PathFor(profile.Name);
        Assert.False(File.Exists(path)); // The test owns only this generated profile file.
        vm.ProfileNames.Add(profile.Name);
        vm.SelectedProfileName = profile.Name;
        var dropdown = new ComboBox { DataContext = vm };
        dropdown.SetBinding(ComboBox.ItemsSourceProperty, new Binding(nameof(vm.ProfileNames)));
        dropdown.SetBinding(ComboBox.SelectedItemProperty, new Binding(nameof(vm.SelectedProfileName)) { Mode = BindingMode.TwoWay });
        Assert.Equal(profile.Name, dropdown.SelectedItem);
        try
        {
            vm.SaveProfileCommand.Execute(null);
            Assert.True(File.Exists(path));
            Assert.Equal(profile.Name, vm.SelectedProfileName);
            Assert.Equal(profile.Name, dropdown.SelectedItem);
            Assert.Same(profile, vm.Profile);
            vm.SaveProfileCommand.Execute(null);
            Assert.Equal(profile.Name, dropdown.SelectedItem);
        }
        finally
        {
            BindingOperations.ClearAllBindings(dropdown);
            if (File.Exists(path)) File.Delete(path);
        }
    });

    private static void Sta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { failure = ex; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(45)), "UI test did not finish");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "ModderLords-ui-tests", Guid.NewGuid().ToString("N"));
        public Fixture()
        {
            Directory.CreateDirectory(GamePaths.ClientBin(Root));
            File.WriteAllText(Path.Combine(GamePaths.ClientBin(Root), "Bannerlord.exe"), "fixture only");
            Module("Native"); Module("SandBoxCore"); Module("Sandbox");
        }
        public void Module(string id)
        {
            var folder = Path.Combine(Root, "Modules", id);
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "SubModule.xml"), $"<Module><Name value='{id}'/><Id value='{id}'/><Version value='v1.0.0'/><SubModules/></Module>");
        }
        public MainViewModel ViewModel() => new(false)
        {
            Profile = new Profile { Name = "ui-test-" + Guid.NewGuid().ToString("N"), GameRoot = Root,
                DedicatedServerRoot = Path.Combine(Root, "no-server"), ClientOfficialModules = ["Native", "SandBoxCore", "Sandbox"] },
        };
        public void Dispose() => Directory.Delete(Root, true); // This fixture never creates links.
    }

    [Fact]
    public void PlayerHostPlayerUsesClientPreviewAndKeepsSelection() => Sta(() =>
    {
        using var fixture = new Fixture();
        fixture.Module("TestMod");
        var vm = fixture.ViewModel();
        vm.Rescan();
        vm.Mods.Single(m => m.Id == "TestMod").Enabled = true;
        vm.Mode = AppMode.Host;
        Assert.NotNull(vm.Host);
        vm.Mode = AppMode.Player;
        Assert.NotNull(vm.Host); // Retaining the object must not retain host routing.
        Assert.Contains("TestMod", vm.LoadOrderPreview);
        Assert.DoesNotContain("DedicatedServer.Windows", vm.LoadOrderPreview);
        Assert.True(vm.Mods.Single(m => m.Id == "TestMod").Enabled);
        Assert.Equal("", vm.ServerRoot);
    });

    [Fact]
    public void CoopCanBeEnabledAndIncludedInPlayerLaunch() => Sta(() =>
    {
        using var fixture = new Fixture(); fixture.Module("CoopNightly");
        var vm = fixture.ViewModel(); vm.Rescan();
        vm.Mods.Single(m => m.Id == "CoopNightly" && m.Folder.StartsWith(fixture.Root, StringComparison.OrdinalIgnoreCase)).Enabled = true;
        Assert.Contains("CoopNightly", vm.LoadOrderPreview);
        Assert.Contains("CoopNightly", ClientLaunchSession.Prepare(vm.Profile).Plan.ModuleIds);
    });

    [Fact]
    public void MissingImportRemainsVisibleAndBecomesInstalledAfterRescan() => Sta(() =>
    {
        using var fixture = new Fixture();
        var vm = fixture.ViewModel();
        vm.Profile.Mods.Add(new ProfileMod { Id = "PendingMod", LastVersion = "v1.0.0", DownloadUrl = "https://example.test/mod" });
        vm.Rescan();
        Assert.True(vm.Mods.Single(m => m.Id == "PendingMod").IsMissing);
        Assert.Contains(vm.Profile.EnabledMods, m => m.Id == "PendingMod");
        var plan = ClientLaunchSession.Prepare(vm.Profile).Plan;
        Assert.Contains("PendingMod", plan.MissingModules);
        Assert.Throws<InvalidOperationException>(() => ClientLaunchSession.Start(plan));
        fixture.Module("PendingMod"); vm.Rescan();
        var row = vm.Mods.Single(m => m.Id == "PendingMod");
        Assert.False(row.IsMissing); Assert.True(row.Enabled);
        Assert.Empty(ClientLaunchSession.Prepare(vm.Profile).Plan.MissingModules);
    });

    [Fact]
    public void OrdinaryCheckboxEditsRefreshShareTextImmediately() => Sta(() =>
    {
        using var fixture = new Fixture(); fixture.Module("TestMod");
        var vm = fixture.ViewModel(); vm.Rescan();
        var row = vm.Mods.Single(m => m.Id == "TestMod");
        row.Enabled = true;
        Assert.Contains("TestMod", vm.ClientManifestText);
        row.Enabled = false;
        Assert.DoesNotContain("TestMod", vm.ClientManifestText);
        Assert.DoesNotContain(vm.CurrentExport()!.Mods, m => m.Id == "TestMod");
        row.Enabled = true;
        Assert.Contains(vm.CurrentExport()!.Mods, m => m.Id == "TestMod");
    });

    [Fact]
    public void SharingAndClientLaunchKeepRunningSnapshotWhenEditorChanges() => Sta(() =>
    {
        using var fixture = new Fixture(); fixture.Module("RunningMod"); fixture.Module("NextMod");
        var vm = fixture.ViewModel(); vm.Rescan();
        vm.Mods.Single(m => m.Id == "RunningMod").Enabled = true;
        var client = ClientLaunchSession.Prepare(vm.Profile);
        var runningProfile = ProfileStore.Snapshot(vm.Profile);
        var paths = ServerPaths.Create(vm.Profile.DedicatedServerRoot!, fixture.Root, fixture.Root);
        var selections = client.Modules.Selections;
        var prepared = new LaunchSession.Prepared(paths, client.Catalog, selections, client.Order,
            OverlayPlanner.Plan(Path.Combine(fixture.Root, "overlay"), paths.ModulesRoot, selections),
            new LaunchPlan { Paths = paths, ModuleIds = client.Order.ModuleIds }, []);
        vm.Mode = AppMode.Host;
        vm.Host!.RecordRunningSession(prepared, runningProfile);
        vm.Profile = new Profile { Name = "next-profile", GameRoot = fixture.Root,
            DedicatedServerRoot = Path.Combine(fixture.Root, "missing-server"), Mods = [new ProfileMod { Id = "NextMod" }] };
        vm.Rescan(); // Invalid next server paths must not erase the live server's snapshot.
        var export = vm.CurrentExport()!;
        Assert.Equal(runningProfile.Name, export.Name);
        Assert.Contains(export.Mods, m => m.Id == "RunningMod");
        Assert.DoesNotContain(export.Mods, m => m.Id == "NextMod");
        Assert.Contains("RunningMod", vm.ClientManifestText);
        Assert.DoesNotContain("NextMod", vm.ClientManifestText);
        var launch = vm.Host.PrepareClientLaunch();
        Assert.Contains("RunningMod", launch.Plan.ModuleIds);
        Assert.DoesNotContain("NextMod", launch.Plan.ModuleIds);
    });

    [Fact]
    public void MissingDlcRequirementSurvivesRescanAndExport() => Sta(() =>
    {
        using var fixture = new Fixture();
        var vm = fixture.ViewModel(); vm.Profile.ClientOfficialModules.Add("NavalDLC"); vm.Rescan();
        Assert.True(vm.Mods.Single(m => m.Id == "NavalDLC").IsMissing);
        Assert.Contains("NavalDLC", vm.CurrentExport()!.ClientOfficialModules!);
        Assert.Contains("NavalDLC", ClientLaunchSession.Prepare(vm.Profile).Plan.MissingModules);
        vm.Mods.Single(m => m.Id == "NavalDLC").Enabled = false;
        Assert.DoesNotContain("NavalDLC", ClientLaunchSession.Prepare(vm.Profile).Plan.MissingModules);
    });

    [Fact]
    public void RemovingAMissingModTakesItOffTheProfileForGood() => Sta(() =>
    {
        using var fixture = new Fixture();
        var vm = fixture.ViewModel();
        vm.Profile.Mods.Add(new ProfileMod { Id = "DeletedMod", LastVersion = "v1.0.0" });
        vm.Rescan();
        vm.SelectedMod = vm.Mods.Single(m => m.Id == "DeletedMod");
        Assert.True(vm.RemoveModCommand.CanExecute(null));
        vm.RemoveModCommand.Execute(null);
        Assert.DoesNotContain(vm.Mods, m => m.Id == "DeletedMod");
        Assert.True(vm.IsDirty);
        // The two things that used to bring it back: collecting rows into the profile, and the next scan.
        vm.CollectProfileFromRows();
        Assert.DoesNotContain(vm.Profile.Mods, m => m.Id == "DeletedMod");
        vm.Rescan();
        Assert.DoesNotContain(vm.Mods, m => m.Id == "DeletedMod");
        Assert.Equal(0, vm.MissingCount);
        Assert.DoesNotContain(vm.Messages, m => m.Contains("DeletedMod"));
    });

    [Fact]
    public void AnInstalledModCannotBeRemovedOnlyUnticked() => Sta(() =>
    {
        using var fixture = new Fixture(); fixture.Module("TestMod");
        var vm = fixture.ViewModel(); vm.Rescan();
        vm.SelectedMod = vm.Mods.Single(m => m.Id == "TestMod");
        Assert.False(vm.RemoveModCommand.CanExecute(null));
    });

    [Fact]
    public void RemoveMissingClearsCommunityAndOfficialEntries() => Sta(() =>
    {
        using var fixture = new Fixture(); fixture.Module("KeptMod");
        var vm = fixture.ViewModel();
        vm.Profile.Mods.Add(new ProfileMod { Id = "GoneA" });
        vm.Profile.Mods.Add(new ProfileMod { Id = "GoneB" });
        vm.Profile.Mods.Add(new ProfileMod { Id = "KeptMod" });
        vm.Profile.ClientOfficialModules.Add("NavalDLC");
        vm.Rescan();
        Assert.Equal(3, vm.MissingCount);
        vm.RemoveMissingRows();
        Assert.Equal(0, vm.MissingCount);
        // Not an exact list: the catalogue also scans this PC's Steam Workshop, whose mods join the profile unticked.
        Assert.DoesNotContain(vm.Profile.Mods, m => m.Id is "GoneA" or "GoneB");
        Assert.Contains(vm.Profile.Mods, m => m.Id == "KeptMod");
        Assert.DoesNotContain("NavalDLC", vm.Profile.ClientOfficialModules);
        Assert.DoesNotContain("NavalDLC", vm.CurrentExport()!.ClientOfficialModules ?? []);
        Assert.Empty(ClientLaunchSession.Prepare(vm.Profile).Plan.MissingModules);
    });

    [Fact]
    public void RepeatedPreviewsDoNotRepeatMessages() => Sta(() =>
    {
        using var fixture = new Fixture();
        // A dependency that is not installed, so the order preview has something to say.
        var folder = Path.Combine(fixture.Root, "Modules", "NeedsDep");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "SubModule.xml"),
            "<Module><Name value='NeedsDep'/><Id value='NeedsDep'/><Version value='v1.0.0'/>" +
            "<DependedModules><DependedModule Id='AbsentDep'/></DependedModules><SubModules/></Module>");
        var vm = fixture.ViewModel(); vm.Rescan();
        var row = vm.Mods.Single(m => m.Id == "NeedsDep");
        for (var i = 0; i < 5; i++) { row.Enabled = !row.Enabled; vm.RefreshPreview(); }
        Assert.Equal(vm.Messages.Count, vm.Messages.Distinct().Count());
    });

    [Fact]
    public void PreviewReusesTheLastScanUntilRescan() => Sta(() =>
    {
        using var fixture = new Fixture();
        var vm = fixture.ViewModel(); vm.Rescan();
        // Installed after the scan: an edit must not notice it, because an edit must not walk the disk.
        fixture.Module("LateMod");
        vm.Profile.Mods.Add(new ProfileMod { Id = "LateMod" });
        vm.RefreshPreview();
        Assert.DoesNotContain("LateMod", vm.LoadOrderPreview);
        vm.Rescan();
        Assert.False(vm.Mods.Single(m => m.Id == "LateMod").IsMissing);
        Assert.Contains("LateMod", vm.LoadOrderPreview);
    });

    [Fact]
    public void UnsavedEditsAreTrackedAndCancelKeepsTheProfile() => Sta(() =>
    {
        using var fixture = new Fixture(); fixture.Module("TestMod");
        var vm = fixture.ViewModel(); vm.Rescan();
        var path = ProfileStore.PathFor(vm.Profile.Name);
        try
        {
            Assert.False(vm.IsDirty);
            vm.Mods.Single(m => m.Id == "TestMod").Enabled = true;
            Assert.True(vm.IsDirty);
            vm.SaveProfileCommand.Execute(null);
            Assert.False(vm.IsDirty);

            vm.Mods.Single(m => m.Id == "TestMod").Enabled = false;
            var asked = 0;
            vm.AskUnsaved = _ => { asked++; return System.Windows.MessageBoxResult.Cancel; };
            var name = vm.Profile.Name;
            vm.SelectedProfileName = "some-other-profile";
            Assert.Equal(1, asked);
            Assert.Equal(name, vm.Profile.Name);
            Assert.True(vm.IsDirty);
            Assert.False(vm.ResolveUnsavedChanges());

            vm.AskUnsaved = _ => System.Windows.MessageBoxResult.No;
            Assert.True(vm.ResolveUnsavedChanges());
            Assert.False(vm.IsDirty);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    });
}
