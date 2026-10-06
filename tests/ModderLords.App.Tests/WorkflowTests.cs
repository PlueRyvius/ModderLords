using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using ModderLords.App.ViewModels;
using ModderLords.Core.Compat;
using ModderLords.Core.Launch;
using ModderLords.Core.Profiles;
using ModderLords.Core.Modules;
using ModderLords.Core.Overlay;
using ModderLords.Core.Support;
using ModderLords.Coop.Launch;
using Xunit;
using System.Windows.Controls;
using System.Windows.Data;

namespace ModderLords.App.Tests;

public class WorkflowTests
{
    [Fact]
    public void Support_destination_opens_the_issue_then_selects_the_generated_bundle()
    {
        var launcher = new FakeSupportLauncher();
        var result = new ModderLords.Core.Support.SupportBundleResult("id", @"C:\Reports\bundle.zip", null,
            123, null, [], []);

        SupportDestination.Open(result, launcher);

        Assert.Equal(["issue:" + SupportReportService.IssueUrl, @"file:C:\Reports\bundle.zip"], launcher.Actions);
    }

    private sealed class FakeSupportLauncher : ISupportDestinationLauncher
    {
        public List<string> Actions { get; } = new();
        public void SelectFile(string path) => Actions.Add("file:" + path);
        public void OpenIssueForm(string url) => Actions.Add("issue:" + url);
    }

    [Fact]
    public void Support_dialog_defaults_to_no_save_and_disables_save_attachments_while_hosting() => Sta(() =>
    {
        var save = new SupportSaveInfo(@"C:\Saves\campaign.sav", "campaign", "v1", ["Native"],
            new Dictionary<string, string>(), 1, DateTime.UtcNow, 10);
        SupportReportContext Context(bool running) => new()
        {
            Profile = new Profile(),
            Environment = new SupportEnvironment("Host", "1", "1", "1", "Windows", "X64", ".NET"),
            CatalogModules = [],
            ModuleStates = new Dictionary<string, (bool, bool, string?)>(),
            LoadOrder = [], Sources = [], Generated = [], Warnings = [],
            PathTokens = new Dictionary<string, string>(), KnownSecrets = [],
            SaveChoices = [new SupportSaveChoice("None (recommended)", null), new SupportSaveChoice("campaign", save)],
            Categories = [new SupportCategoryEstimate("Configuration", "Sanitized", 1)],
            ServerRunning = running,
        };

        var stopped = new SupportReportWindow(Context(false));
        Assert.Null(stopped.SelectedSave.Save);
        Assert.True(stopped.SaveBox.IsEnabled);
        Assert.Contains("No save is included by default", stopped.SaveNote.Text);

        var running = new SupportReportWindow(Context(true));
        Assert.Null(running.SelectedSave.Save);
        Assert.False(running.SaveBox.IsEnabled);
        Assert.Contains("Stop the server", running.SaveNote.Text);
    });

    [Fact]
    public void ExperimentalSwitchPreservesSettingsAndManualChoices() => Sta(() =>
    {
        using var fixture = new Fixture(); fixture.Module("TestMod");
        var vm = fixture.ViewModel(); vm.Rescan(); vm.Mode = AppMode.Host;
        vm.Profile.SettingsSync = true;
        var row = vm.Mods.Single(m => m.Id == "TestMod");
        row.ServerAuthoritative = true;
        Assert.False(vm.ExperimentalCompat);
        Assert.False(vm.ShowExperimentalCompat);
        Assert.True(vm.Host!.ClientProfile.SettingsSync);
        vm.ExperimentalCompat = true;
        Assert.True(vm.ShowExperimentalCompat);
        vm.ExperimentalCompat = false;
        Assert.True(vm.Profile.SettingsSync);
        Assert.True(row.ServerAuthoritative);
    });

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
            // Without this the scan walks this machine's real Steam Workshop, so a fixture that means to describe
            // three modules describes however many the developer happens to have installed — and the same test
            // passes here and fails on a build agent.
            GamePaths.SteamLibrariesOverride = () => [Path.Combine(Root, "no-steam")];
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
        /// <summary>A copy of a mod anywhere under the fixture, with the given top-level folders inside it.</summary>
        public string Copy(string relativeFolder, string id, params string[] folders)
        {
            var folder = Path.Combine(Root, relativeFolder);
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "SubModule.xml"), $"<Module><Name value='{id}'/><Id value='{id}'/><Version value='v1.0.0'/><SubModules/></Module>");
            foreach (var f in folders) Directory.CreateDirectory(Path.Combine(folder, f));
            return folder;
        }
        public MainViewModel ViewModel() => new(false)
        {
            Profile = new Profile { Name = "ui-test-" + Guid.NewGuid().ToString("N"), GameRoot = Root,
                DedicatedServerRoot = Path.Combine(Root, "no-server"), ClientOfficialModules = ["Native", "SandBoxCore", "Sandbox"] },
        };
        public void Dispose()
        {
            GamePaths.SteamLibrariesOverride = null;
            Directory.Delete(Root, true); // This fixture never creates links.
        }
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

    /// <summary>
    /// The crash of 2026-09-18. Host mode has no row for Coop's server copy, and a save used to append every
    /// row-less entry to the end of the profile — walking CoopNightly past the mods that patch it. The next client
    /// launch then loaded CoopMarriage first, its Harmony patch found nothing to patch, and Bannerlord died at
    /// startup before the main menu.
    /// </summary>
    [Fact]
    public void HostModeSaveKeepsCoopsIndex() => Sta(() =>
    {
        using var fixture = new Fixture();
        fixture.Module("Bannerlord.Harmony"); fixture.Module("CoopNightly"); fixture.Module("CoopMarriage");
        var vm = fixture.ViewModel();
        vm.Profile.Mods =
        [
            new ProfileMod { Id = "CoopNightly", Enabled = true },
            new ProfileMod { Id = "CoopMarriage", Enabled = true },
        ];
        vm.Rescan();
        Assert.Equal(0, vm.Profile.Mods.FindIndex(m => m.Id == "CoopNightly"));
        var before = 0;

        // What Host mode does to the list: the server supplies Coop, so its row is not among the rows being saved.
        foreach (var row in vm.Mods.Where(r => r.Id == "CoopNightly").ToList()) vm.Mods.Remove(row);
        vm.CollectProfileFromRows();

        Assert.Equal(before, vm.Profile.Mods.FindIndex(m => m.Id == "CoopNightly"));
        Assert.True(vm.Profile.Mods.FindIndex(m => m.Id == "CoopNightly")
                  < vm.Profile.Mods.FindIndex(m => m.Id == "CoopMarriage"),
            "Coop must still load before the mod that patches it: " + string.Join(", ", vm.Profile.Mods.Select(m => m.Id)));
    });

    /// <summary>
    /// Launching the client from the Server panel builds a client list from the server's selections. Coop used to be
    /// appended to it, so a host handed their own game the server's arrangement — Coop after every mod — which is
    /// the token the 2026-09-18 crash ran with.
    /// </summary>
    [Fact]
    public void TheHostsClientLaunchPutsCoopWhereTheProfileWantsIt()
    {
        List<ProfileMod> Profile(params string[] ids) => ids.Select(id => new ProfileMod { Id = id }).ToList();

        // Profile order: Harmony, Coop, CoopMarriage. The synthesized list has the two mods; Coop goes between them.
        var mods = Profile("Bannerlord.Harmony", "CoopMarriage");
        Assert.Equal(1, HostViewModel.CoopInsertIndex(mods, Profile("Bannerlord.Harmony", "CoopNightly", "CoopMarriage"), "CoopNightly"));

        // Profile puts Coop first: nothing precedes it.
        Assert.Equal(0, HostViewModel.CoopInsertIndex(mods, Profile("CoopNightly", "Bannerlord.Harmony", "CoopMarriage"), "CoopNightly"));

        // Profile puts Coop last: so does the launch.
        Assert.Equal(2, HostViewModel.CoopInsertIndex(mods, Profile("Bannerlord.Harmony", "CoopMarriage", "CoopNightly"), "CoopNightly"));

        // A profile that never mentions Coop has said nothing, so the server's own convention stands.
        Assert.Equal(2, HostViewModel.CoopInsertIndex(mods, Profile("Bannerlord.Harmony", "CoopMarriage"), "CoopNightly"));
    }

    /// <summary>
    /// The mods list and the engine load order are computed separately, and this bug lived in the gap between them:
    /// the list said one thing and the launch token said another, with nothing asserting they agree. Every enabled
    /// row must appear in the preview, in the same relative order, or the list is lying about what will load.
    /// </summary>
    [Fact]
    public void TheModsListAndTheEnginePreviewAgree() => Sta(() =>
    {
        using var fixture = new Fixture();
        fixture.Module("CoopNightly"); fixture.Module("CoopMarriage"); fixture.Module("ZebraMod");
        var vm = fixture.ViewModel();
        vm.Profile.Mods =
        [
            new ProfileMod { Id = "CoopNightly", Enabled = true },
            new ProfileMod { Id = "CoopMarriage", Enabled = true },
            new ProfileMod { Id = "ZebraMod", Enabled = true },
        ];
        vm.Rescan();

        var preview = vm.LoadOrderPreview.ToList();
        var listed = vm.Mods.Where(r => r.Enabled && !r.IsMissing && !r.IsGameModule).Select(r => r.Id).Distinct().ToList();

        foreach (var id in listed)
            Assert.True(preview.Contains(id), $"{id} is ticked but never loads. Preview: {string.Join(", ", preview)}");

        // Same relative order, ignoring anything the preview places that the list does not show.
        var listedInPreview = preview.Where(id => listed.Contains(id, StringComparer.OrdinalIgnoreCase)).ToList();
        Assert.Equal(listed.Where(id => listedInPreview.Contains(id, StringComparer.OrdinalIgnoreCase)), listedInPreview);
    });

    [Fact]
    public void AnEntryWithNoRowKeepsItsIndex()
    {
        var previous = new List<ProfileMod>
        {
            new() { Id = "A" }, new() { Id = "Hidden" }, new() { Id = "B" }, new() { Id = "C" },
        };
        var rows = new List<ProfileMod> { previous[0], previous[2], previous[3] };

        var merged = MainViewModel.MergeKeepingPosition(rows, previous);

        Assert.Equal(["A", "Hidden", "B", "C"], merged.Select(m => m.Id));
    }

    [Fact]
    public void SeveralHiddenEntriesKeepTheirOrderAmongThemselves()
    {
        var previous = new List<ProfileMod>
        {
            new() { Id = "H1" }, new() { Id = "A" }, new() { Id = "H2" },
        };

        var merged = MainViewModel.MergeKeepingPosition([previous[1]], previous);

        Assert.Equal(["H1", "A", "H2"], merged.Select(m => m.Id));
    }

    /// <summary>Coop's position on a player is a choice, so the launcher offers the fix rather than making it.</summary>
    [Fact]
    public void AModThatPatchesCoopPlacedFirstIsReportedAndFixable() => Sta(() =>
    {
        using var fixture = new Fixture();
        fixture.Module("CoopMarriage"); fixture.Module("CoopNightly");
        var vm = fixture.ViewModel();
        vm.Profile.Mods =
        [
            new ProfileMod { Id = "CoopMarriage", Enabled = true },
            new ProfileMod { Id = "CoopNightly", Enabled = true },
        ];
        vm.Rescan();

        Assert.NotNull(vm.CoopOrderWarning);
        Assert.Contains("CoopMarriage", vm.CoopOrderWarning);

        vm.FixCoopOrderCommand.Execute(null);

        Assert.Null(vm.CoopOrderWarning);
        Assert.True(vm.Mods.IndexOf(vm.Mods.First(r => r.Id == "CoopNightly"))
                  < vm.Mods.IndexOf(vm.Mods.First(r => r.Id == "CoopMarriage")));
    });

    [Fact]
    public void CoopInTheRightPlaceIsNotComplainedAbout() => Sta(() =>
    {
        using var fixture = new Fixture();
        fixture.Module("CoopMarriage"); fixture.Module("CoopNightly");
        var vm = fixture.ViewModel();
        vm.Profile.Mods =
        [
            new ProfileMod { Id = "CoopNightly", Enabled = true },
            new ProfileMod { Id = "CoopMarriage", Enabled = true },
        ];
        vm.Rescan();

        Assert.Null(vm.CoopOrderWarning);
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

    /// <summary>
    /// Rows are rebuilt from the profile on every Rescan and the profile is rebuilt from the rows on every save, so
    /// a per-mod option that lives in only one of them is lost on the first round trip. The two folder lists must
    /// survive both directions, and the server's list in all three of its states.
    /// </summary>
    [Fact]
    public void FolderChoicesSurviveCollectAndRescan() => Sta(() =>
    {
        using var fixture = new Fixture();
        fixture.Module("Named"); fixture.Module("Nothing"); fixture.Module("NoOpinion");
        var vm = fixture.ViewModel(); vm.Rescan();
        Assert.False(vm.IsDirty);

        // None of the three is in the profile yet: setting the lists has to create the entry, not drop the edit.
        vm.SetModFolders(vm.Mods.Single(m => m.Id == "Named"), ["RuntimeDataCache", "AssetSources"], ["DsData"]);
        Assert.True(vm.IsDirty);
        Assert.Contains("RuntimeDataCache", vm.Status);
        Assert.Contains("DsData", vm.Status);
        vm.SetModFolders(vm.Mods.Single(m => m.Id == "Nothing"), [], []);
        vm.SetModFolders(vm.Mods.Single(m => m.Id == "NoOpinion"), null, null);

        void AssertKept(Profile profile)
        {
            Assert.Equal(["RuntimeDataCache", "AssetSources"], profile.Mods.Single(m => m.Id == "Named").ServerExcludedFolders);
            Assert.Equal(["DsData"], profile.Mods.Single(m => m.Id == "Named").ClientExcludedFolders);
            Assert.Empty(profile.Mods.Single(m => m.Id == "Nothing").ServerExcludedFolders!);
            // "Server only" has no record behind it, so an empty list says nothing null does not and is not stored.
            Assert.Null(profile.Mods.Single(m => m.Id == "Nothing").ClientExcludedFolders);
            Assert.Null(profile.Mods.Single(m => m.Id == "NoOpinion").ServerExcludedFolders);
            Assert.Null(profile.Mods.Single(m => m.Id == "NoOpinion").ClientExcludedFolders);
        }

        AssertKept(vm.Profile);
        vm.CollectProfileFromRows(); AssertKept(vm.Profile);
        vm.Rescan(); AssertKept(vm.Profile);
        // An unrelated edit to the same row, then the save path's collect: the lists must not be rewritten from the row.
        vm.Mods.Single(m => m.Id == "Named").Enabled = true;
        vm.CollectProfileFromRows(); AssertKept(vm.Profile);
        vm.Rescan(); vm.CollectProfileFromRows(); AssertKept(vm.Profile);
        // And through the file format the launch reads.
        AssertKept(ProfileStore.Snapshot(vm.Profile));

        // Clearing them again is an edit like any other.
        vm.SetModFolders(vm.Mods.Single(m => m.Id == "Named"), null, null);
        vm.Rescan();
        Assert.Null(vm.Profile.Mods.Single(m => m.Id == "Named").ServerExcludedFolders);
        Assert.Null(vm.Profile.Mods.Single(m => m.Id == "Named").ClientExcludedFolders);
    });

    /// <summary>Set where a server is set up: offered for a mod in Host mode, never in Player mode or for the game's own modules.</summary>
    [Fact]
    public void FolderChoicesAreAHostModeChoiceForMods() => Sta(() =>
    {
        using var fixture = new Fixture(); fixture.Module("TestMod");
        var vm = fixture.ViewModel(); vm.Rescan();
        vm.SelectedMod = vm.Mods.Single(m => m.Id == "TestMod");
        Assert.False(vm.EditModFoldersCommand.CanExecute(null));

        // Setting it for a game module is refused outright rather than writing Native into Profile.Mods.
        vm.SetModFolders(vm.Mods.Single(m => m.Id == "Native"), ["ModuleData"], ["GUI"]);
        Assert.DoesNotContain(vm.Profile.Mods, m => m.Id == "Native");
    });

    /// <summary>
    /// The dialog lists the folders of the copy the row shows, and what it stores is what the launch reads. Opening
    /// it and pressing OK without touching anything is not an edit.
    /// </summary>
    [Fact]
    public void TheFoldersDialogReadsTheModsOwnFoldersAndStoresTheChoices() => Sta(() =>
    {
        using var fixture = new Fixture();
        fixture.Copy(Path.Combine("Modules", "FolderMod"), "FolderMod", "bin", "ModuleData", "RuntimeDataCache", "DsData");
        var vm = fixture.ViewModel(); vm.Rescan();
        var row = vm.Mods.Single(m => m.Id == "FolderMod");

        var untouched = vm.ModFolderChoicesFor(row);
        Assert.Equal(["bin", "DsData", "ModuleData", "RuntimeDataCache"], untouched.Rows.Select(r => r.Name));
        Assert.All(untouched.Rows, r => Assert.Equal(FolderSide.Both, r.Side));
        vm.SetModFolders(row, untouched.ServerExcluded, untouched.ClientExcluded);
        Assert.False(vm.IsDirty);

        var edited = vm.ModFolderChoicesFor(row);
        edited.Rows.Single(r => r.Name == "RuntimeDataCache").Choice = ModFolderRow.ClientOnlyLabel;
        edited.Rows.Single(r => r.Name == "DsData").Choice = ModFolderRow.ServerOnlyLabel;
        vm.SetModFolders(row, edited.ServerExcluded, edited.ClientExcluded);
        Assert.True(vm.IsDirty);
        var stored = vm.Profile.Mods.Single(m => m.Id == "FolderMod");
        Assert.Equal(["RuntimeDataCache"], stored.ServerExcludedFolders);
        Assert.Equal(["DsData"], stored.ClientExcludedFolders);

        // Opened again, the dialog shows what was stored.
        var reopened = vm.ModFolderChoicesFor(vm.Mods.Single(m => m.Id == "FolderMod"));
        Assert.Equal(FolderSide.ClientOnly, reopened.Rows.Single(r => r.Name == "RuntimeDataCache").Side);
        Assert.Equal(FolderSide.ServerOnly, reopened.Rows.Single(r => r.Name == "DsData").Side);
        Assert.Equal(FolderSide.Both, reopened.Rows.Single(r => r.Name == "ModuleData").Side);
    });

    [Fact]
    public void FolderRowsStartFromTheListsAndTheCompatRecord()
    {
        // The profile has no opinion about the server's list, so the record's applies.
        var choices = new ModFolderChoices(
            ["SceneObj", "bin", "AssetPackages", "RuntimeDataCache", "DsData", "ModuleData"],
            profileServerExcluded: null, recordServerExcluded: ["runtimedatacache"], profileClientExcluded: ["DsData", "Gone"]);

        Assert.Equal(["AssetPackages", "bin", "DsData", "Gone", "ModuleData", "RuntimeDataCache", "SceneObj"], choices.Rows.Select(r => r.Name));
        ModFolderRow Row(string name) => choices.Rows.Single(r => r.Name == name);
        Assert.Equal(FolderSide.ClientOnly, Row("RuntimeDataCache").Side);
        Assert.Equal(FolderSide.ServerOnly, Row("DsData").Side);
        Assert.Equal(FolderSide.Both, Row("ModuleData").Side);
        Assert.Equal(FolderSide.Both, Row("SceneObj").Side);

        // bin and AssetPackages are listed, but their side is not a choice, and the row says why.
        Assert.True(Row("bin").IsLocked);
        Assert.False(Row("bin").CanChange);
        Assert.Equal(ModFolderRow.BothLabel, Row("bin").Choice);
        Assert.Contains("code", Row("bin").Note);
        Assert.True(Row("AssetPackages").IsLocked);
        Assert.Equal(ModFolderRow.ClientOnlyLabel, Row("AssetPackages").Choice);
        Assert.Contains("never shown AssetPackages", Row("AssetPackages").Note);

        // A stored name this copy has no folder for is still a row, so a host can see it and clear it.
        Assert.False(Row("Gone").IsPresent);
        Assert.Equal(FolderSide.ServerOnly, Row("Gone").Side);
        Assert.Contains("Not present in this copy", Row("Gone").Note);
        Assert.True(Row("Gone").CanChange);
        Assert.True(Row("DsData").IsPresent);
        Assert.Equal("", Row("DsData").Note);

        // Untouched, the profile keeps following the database: null, not a copy of the record's list.
        Assert.Null(choices.ServerExcluded);
        Assert.Equal(["DsData", "Gone"], choices.ClientExcluded);

        Row("Gone").Choice = ModFolderRow.BothLabel;
        Assert.Equal(["DsData"], choices.ClientExcluded);
    }

    [Fact]
    public void FolderRowsTurnBackIntoTheRightProfileValues()
    {
        // A record list and no opinion: overruling it is an explicit list, and "nothing" is an EMPTY list, not null.
        var following = new ModFolderChoices(["RuntimeDataCache", "ModuleData"], null, ["RuntimeDataCache"], null);
        following.Rows.Single(r => r.Name == "RuntimeDataCache").Choice = ModFolderRow.BothLabel;
        Assert.NotNull(following.ServerExcluded);
        Assert.Empty(following.ServerExcluded!);
        following.Rows.Single(r => r.Name == "ModuleData").Choice = ModFolderRow.ClientOnlyLabel;
        Assert.Equal(["ModuleData"], following.ServerExcluded);
        // Back to exactly what the record says is "no opinion" again.
        following.Rows.Single(r => r.Name == "ModuleData").Choice = ModFolderRow.BothLabel;
        following.Rows.Single(r => r.Name == "RuntimeDataCache").Choice = ModFolderRow.ClientOnlyLabel;
        Assert.Null(following.ServerExcluded);
        Assert.Null(following.ClientExcluded);

        // The profile already overrules the record with "nothing": that survives an untouched OK.
        var nothing = new ModFolderChoices(["RuntimeDataCache"], [], ["RuntimeDataCache"], null);
        Assert.Equal(FolderSide.Both, nothing.Rows.Single().Side);
        Assert.Empty(nothing.ServerExcluded!);

        // No record list: nothing chosen is null, never a pinned empty list.
        Assert.Null(new ModFolderChoices(["ModuleData"], null, [], null).ServerExcluded);
        Assert.Null(new ModFolderChoices(["ModuleData"], [], [], []).ServerExcluded);
        Assert.Null(new ModFolderChoices(["ModuleData"], [], [], []).ClientExcluded);
        Assert.Empty(new ModFolderChoices([], null, [], null).Rows);

        // One choice per folder: picking "Server only" for a client-only folder takes it off the server's list.
        var own = new ModFolderChoices(["RuntimeDataCache", "AssetSources"], ["RuntimeDataCache", "AssetSources"], [], null);
        Assert.Equal(["AssetSources", "RuntimeDataCache"], own.ServerExcluded);
        own.Rows.Single(r => r.Name == "AssetSources").Choice = ModFolderRow.ServerOnlyLabel;
        Assert.Equal(["RuntimeDataCache"], own.ServerExcluded);
        Assert.Equal(["AssetSources"], own.ClientExcluded);
        // A label the drop-down never offers changes nothing.
        own.Rows.Single(r => r.Name == "AssetSources").Choice = "Nobody";
        Assert.Equal(FolderSide.ServerOnly, own.Rows.Single(r => r.Name == "AssetSources").Side);
        Assert.Equal(["Server + Client", "Server only", "Client only"], ModFolderRow.Labels);
    }

    /// <summary>
    /// A locked row is not a choice, so it must never read as an edit: whatever the server's list said about bin or
    /// AssetPackages comes back unchanged (the launch ignores both), and neither can be made "Server only".
    /// </summary>
    [Fact]
    public void LockedFolderRowsNeverChangeWhatIsStored()
    {
        var record = new ModFolderChoices(["bin", "AssetPackages", "ModuleData"], null, ["AssetPackages"], ["bin", "AssetPackages"]);
        Assert.Null(record.ServerExcluded);
        Assert.Null(record.ClientExcluded);

        var profile = new ModFolderChoices(["bin", "AssetPackages", "ModuleData"], ["AssetPackages", "ModuleData"], [], null);
        Assert.Equal(["AssetPackages", "ModuleData"], profile.ServerExcluded);

        // A name the launch would refuse is shown with the reason and can be cleared like any other.
        var invalid = new ModFolderChoices(["ModuleData"], ["..\\Other"], [], null);
        var row = invalid.Rows.Single(r => r.Name == "..\\Other");
        Assert.Contains("Ignored when launching", row.Note);
        row.Choice = ModFolderRow.BothLabel;
        Assert.Null(invalid.ServerExcluded);
    }

    /// <summary>The dialog itself, never shown: its XAML loads, it shows the model's rows and says what the choices mean.</summary>
    [Fact]
    public void TheFoldersDialogShowsTheRowsAndExplainsTheChoices() => Sta(() =>
    {
        var choices = new ModFolderChoices(["bin", "RuntimeDataCache"], null, ["RuntimeDataCache"], null);
        var win = new ModFoldersWindow("Mod", choices);
        Assert.Same(choices.Rows, win.Grid.ItemsSource);
        Assert.Equal(System.Windows.Visibility.Visible, win.Grid.Visibility);
        Assert.Contains("Mod", win.Header.Text);
        foreach (var label in ModFolderRow.Labels) Assert.Contains(label, win.IntroText.Text);
        Assert.Contains("never changed", win.IntroText.Text);
        Assert.Contains("Steam", win.IntroText.Text);

        var empty = new ModFoldersWindow("Mod", new ModFolderChoices([], null, [], null));
        Assert.Equal(System.Windows.Visibility.Collapsed, empty.Grid.Visibility);
        Assert.Equal(System.Windows.Visibility.Visible, empty.EmptyText.Visibility);
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

    [Fact]
    public void FoldersPinAnExtraModFolderAndRescan() => Sta(() =>
    {
        using var fixture = new Fixture();
        var mod = Path.Combine(fixture.Root, "ExtraMods", "SideMod");
        Directory.CreateDirectory(mod);
        File.WriteAllText(Path.Combine(mod, "SubModule.xml"),
            "<Module><Name value='SideMod'/><Id value='SideMod'/><Version value='v1.0.0'/><SubModules/></Module>");
        var vm = fixture.ViewModel(); vm.Rescan();
        Assert.DoesNotContain(vm.Mods, m => m.Id == "SideMod");
        // Picking the mod itself must add the folder it sits in: the catalogue scans an extra folder's children.
        vm.ApplyFolders(fixture.Root, null, [FolderChecks.ModRootFor(mod)]);
        Assert.Equal([Path.Combine(fixture.Root, "ExtraMods")], vm.Profile.CustomModRoots);
        Assert.Contains(vm.Mods, m => m.Id == "SideMod" && m.Source == "Custom");
        Assert.True(vm.IsDirty);
    });

    // ---- two copies of one mod at one version (the pinned server copy that "reverted", 1.2.5) -------------------

    /// <summary>The host's layout: the full mod in the game's Modules, the same version minus RuntimeDataCache in
    /// a folder of their own for the server.</summary>
    private static (string Game, string Server) FullAndServerCopies(Fixture fixture, bool sameFolders = false)
    {
        var game = fixture.Copy(Path.Combine("Modules", "TwinMod"), "TwinMod", "ModuleData", "RuntimeDataCache");
        var server = sameFolders
            ? fixture.Copy(Path.Combine("ServerMods", "TwinMod"), "TwinMod", "ModuleData", "RuntimeDataCache")
            : fixture.Copy(Path.Combine("ServerMods", "TwinMod"), "TwinMod", "ModuleData");
        return (game, server);
    }

    private static MainViewModel PinnedTo(Fixture fixture, string? pin, bool scanServerFolder = true)
    {
        var vm = fixture.ViewModel();
        if (scanServerFolder) vm.Profile.CustomModRoots = [Path.Combine(fixture.Root, "ServerMods")];
        vm.Profile.Mods = [new ProfileMod { Id = "TwinMod", Enabled = true, SourcePath = pin, LastVersion = "v1.0.0" }];
        return vm;
    }

    private static string? PinOf(Profile profile) => profile.Mods.Single(m => m.Id == "TwinMod").SourcePath;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void APinOnEitherOfTwoSameVersionCopiesSurvivesRescanSaveAndLoad(bool pinServerCopy) => Sta(() =>
    {
        using var fixture = new Fixture();
        var (game, server) = FullAndServerCopies(fixture);
        var pin = pinServerCopy ? server : game;
        var vm = PinnedTo(fixture, pin);
        var path = ProfileStore.PathFor(vm.Profile.Name);
        try
        {
            vm.Rescan();

            // Both copies are listed, told apart by folder, and the pinned one is the one that is on.
            var rows = vm.Mods.Where(r => r.Id == "TwinMod").ToList();
            Assert.Equal(new[] { game, server }.Order(), rows.Select(r => r.Folder).Order());
            Assert.Equal(pin, rows.Single(r => r.Enabled).Folder);
            Assert.All(rows, r => Assert.Contains(r.Folder, r.BandName));
            Assert.Contains(vm.Messages, m => m.Contains("TwinMod: 2 copies installed") && m.Contains(game) && m.Contains(server));

            vm.CollectProfileFromRows();
            Assert.Equal(pin, PinOf(vm.Profile));

            vm.SaveProfileCommand.Execute(null);
            var loaded = ProfileStore.Load(vm.Profile.Name)!;
            Assert.Equal(pin, PinOf(loaded));

            vm.Profile = loaded;
            vm.Rescan();
            vm.CollectProfileFromRows();
            Assert.Equal(pin, PinOf(vm.Profile));
            Assert.Equal(pin, vm.Mods.Single(r => r.Id == "TwinMod" && r.Enabled).Folder);
            // The list and the launch agree on the copy.
            Assert.Equal(pin, ClientLaunchSession.Prepare(vm.Profile).Mods.Single(m => m.Id == "TwinMod").FolderPath);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    });

    [Fact]
    public void IdenticalSameVersionCopiesAreStillOneRow() => Sta(() =>
    {
        using var fixture = new Fixture();
        var (game, server) = FullAndServerCopies(fixture, sameFolders: true);

        // Nothing pinned: one row, the copy the launch would pick.
        var vm = PinnedTo(fixture, null);
        vm.Rescan();
        Assert.Equal(game, vm.Mods.Single(r => r.Id == "TwinMod").Folder);
        Assert.DoesNotContain(vm.Messages, m => m.Contains("TwinMod: 2"));

        // Pinned to the one that would have been folded away: still one row, and it is the pinned copy.
        vm = PinnedTo(fixture, server);
        vm.Rescan();
        Assert.Equal(server, vm.Mods.Single(r => r.Id == "TwinMod").Folder);
        vm.CollectProfileFromRows();
        Assert.Equal(server, PinOf(vm.Profile));
    });

    [Fact]
    public void APinOutsideEveryScannedFolderSurvives() => Sta(() =>
    {
        using var fixture = new Fixture();
        var (game, server) = FullAndServerCopies(fixture);
        var vm = PinnedTo(fixture, server, scanServerFolder: false);
        vm.Rescan();

        var rows = vm.Mods.Where(r => r.Id == "TwinMod").ToList();
        Assert.Equal(2, rows.Count);
        Assert.Equal(server, rows.Single(r => r.Enabled).Folder);
        Assert.DoesNotContain(rows, r => r.IsMissing);
        vm.CollectProfileFromRows();
        Assert.Equal(server, PinOf(vm.Profile));
        vm.Rescan();
        vm.CollectProfileFromRows();
        Assert.Equal(server, PinOf(vm.Profile));
        Assert.Equal(server, ClientLaunchSession.Prepare(vm.Profile).Mods.Single(m => m.Id == "TwinMod").FolderPath);
        Assert.Equal(game, rows.Single(r => !r.Enabled).Folder);
    });

    [Fact]
    public void APinnedCopyThatIsTheOnlyOneInstalledIsNotReportedMissing() => Sta(() =>
    {
        using var fixture = new Fixture();
        var server = fixture.Copy(Path.Combine("ServerMods", "TwinMod"), "TwinMod", "ModuleData");
        var vm = PinnedTo(fixture, server, scanServerFolder: false);
        vm.Rescan();

        var row = vm.Mods.Single(r => r.Id == "TwinMod");
        Assert.False(row.IsMissing);
        Assert.True(row.Enabled);
        Assert.Equal(server, row.Folder);
        Assert.Equal(0, vm.MissingCount);
    });

    [Fact]
    public void TickingTheOtherCopyMovesThePin() => Sta(() =>
    {
        using var fixture = new Fixture();
        var (game, server) = FullAndServerCopies(fixture);
        var vm = PinnedTo(fixture, server);
        vm.Rescan();

        vm.Mods.Single(r => r.Id == "TwinMod" && r.Folder == game).Enabled = true;

        Assert.False(vm.Mods.Single(r => r.Id == "TwinMod" && r.Folder == server).Enabled);
        Assert.Contains(game, vm.Status);
        vm.CollectProfileFromRows();
        Assert.Equal(game, PinOf(vm.Profile));
        vm.Rescan();
        Assert.Equal(game, vm.Mods.Single(r => r.Id == "TwinMod" && r.Enabled).Folder);
    });

    [Fact]
    public void UntickingAModDoesNotMoveItsPin() => Sta(() =>
    {
        using var fixture = new Fixture();
        var (game, server) = FullAndServerCopies(fixture);
        var vm = PinnedTo(fixture, server);
        vm.Rescan();

        // Off, and with the other copy dragged above it: "the first row" is now the copy that was NOT chosen.
        var pinned = vm.Mods.Single(r => r.Id == "TwinMod" && r.Folder == server);
        pinned.Enabled = false;
        vm.Mods.Move(vm.Mods.IndexOf(vm.Mods.Single(r => r.Id == "TwinMod" && r.Folder == game)), vm.Mods.IndexOf(pinned));
        Assert.Equal(game, vm.Mods.First(r => r.Id == "TwinMod").Folder);

        vm.CollectProfileFromRows();

        Assert.False(vm.Profile.Mods.Single(m => m.Id == "TwinMod").Enabled);
        Assert.Equal(server, PinOf(vm.Profile));
    });

    /// <summary>
    /// Launch client builds the game's mod list from the SERVER's selections. Once the server's pin holds, that
    /// would point the host's own game at the copy cut down for the server.
    /// </summary>
    [Fact]
    public void TheHostsClientLaunchUsesTheFullCopyWhenTheServerRunsACutDownOne() => Sta(() =>
    {
        using var fixture = new Fixture();
        var (game, server) = FullAndServerCopies(fixture);
        var vm = PinnedTo(fixture, server);
        vm.Rescan();
        var selected = ClientLaunchSession.Prepare(vm.Profile);
        Assert.Equal(server, selected.Mods.Single(m => m.Id == "TwinMod").FolderPath);   // what the server links
        var paths = ServerPaths.Create(vm.Profile.DedicatedServerRoot!, fixture.Root, fixture.Root);
        var selections = selected.Modules.Selections;
        var prepared = new LaunchSession.Prepared(paths, selected.Catalog, selections, selected.Order,
            OverlayPlanner.Plan(Path.Combine(fixture.Root, "overlay"), paths.ModulesRoot, selections),
            new LaunchPlan { Paths = paths, ModuleIds = selected.Order.ModuleIds }, []);
        vm.Mode = AppMode.Host;
        vm.Host!.RecordRunningSession(prepared, ProfileStore.Snapshot(vm.Profile));

        var launch = vm.Host.PrepareClientLaunch();

        Assert.Equal(game, launch.Mods.Single(m => m.Id == "TwinMod").FolderPath);
    });

    [Fact]
    public void FolderChecksTellTheGameFolderFromTheWrongOne()
    {
        using var fixture = new Fixture();
        Assert.True(FolderChecks.Game(fixture.Root, null).Ok);
        Assert.False(FolderChecks.Game(Path.Combine(fixture.Root, "Modules"), null).Ok);
        Assert.False(FolderChecks.Game(Path.Combine(fixture.Root, "absent"), null).Ok);
        Assert.True(FolderChecks.Game(null, fixture.Root).Ok);
        Assert.False(FolderChecks.Game(null, null).Ok);
    }

    /// <summary>
    /// Submit… with the browser, the clipboard and the question replaced, against a database built in the fixture
    /// folder: the real one lives in this machine's profile folder and a test must not read meaning into it.
    /// </summary>
    private sealed class SubmitHarness
    {
        public List<string> Opened { get; } = new();
        public List<string> Copied { get; } = new();
        public List<string> Asked { get; } = new();
        public bool Answer { get; set; } = true;
        public bool ClipboardWorks { get; set; } = true;
        public MainViewModel Vm { get; }
        private readonly string _dir;

        public SubmitHarness(Fixture fixture)
        {
            _dir = Path.Combine(fixture.Root, "compat");
            fixture.Module("TestMod"); fixture.Module("BundledMod"); fixture.Module("PlainMod");
            Vm = fixture.ViewModel(); Vm.Rescan();
            Vm.ConfirmCompatSubmit = message => { Asked.Add(message); return Answer; };
            Vm.OpenCompatSubmitPage = Opened.Add;
            Vm.CopyCompatSubmitText = text => { Copied.Add(text); return ClipboardWorks; };
        }

        public void Submit(string? modId, params CompatRecord[] local)
        {
            var bundled = Path.Combine(_dir, CompatDb.BundledFileName);
            var mine = Path.Combine(_dir, CompatDb.LocalFileName);
            CompatDb.WriteFile(bundled, [new CompatRecord { Id = "BundledMod", Verdict = CompatVerdict.Works }]);
            CompatDb.WriteFile(mine, local);
            Vm.SelectedMod = modId is null ? null : Vm.Mods.Single(m => m.Id == modId);
            Vm.SubmitCompatFrom(CompatDb.Load(bundled, mine));
        }
    }

    [Fact]
    public void SubmitOpensThePrefilledIssueOnlyAfterTheUserAgrees() => Sta(() =>
    {
        using var fixture = new Fixture();
        var h = new SubmitHarness(fixture);
        var record = new CompatRecord { Id = "TestMod", Verdict = CompatVerdict.Broken, Notes = "dies & never recovers" };

        h.Answer = false;
        h.Submit("TestMod", record);
        Assert.Contains("public issue", Assert.Single(h.Asked));
        Assert.Contains("your own GitHub account", h.Asked[0]);
        Assert.Contains("Nothing is sent by pressing OK", h.Asked[0]);
        Assert.Empty(h.Opened);

        h.Answer = true;
        h.Submit("TestMod", record);
        var url = Assert.Single(h.Opened);
        Assert.Equal(CompatSubmission.Build(record, "v1.0.0", null).Url, url);
        Assert.Contains("&mod=TestMod&mod-version=v1.0.0&record=", url);
        Assert.Empty(h.Copied);
        Assert.Contains("Nothing is posted until you press Submit", h.Vm.Status);
        // The privacy property, end to end: the fixture's folder, profile and other mods are nowhere in the link.
        var sent = Uri.UnescapeDataString(url);
        Assert.DoesNotContain(Path.GetFileName(fixture.Root), sent);
        Assert.DoesNotContain(h.Vm.Profile.Name, sent);
        Assert.DoesNotContain("PlainMod", sent);
    });

    [Fact]
    public void SubmitHasNothingToSendWithoutALocalRecord() => Sta(() =>
    {
        using var fixture = new Fixture();
        var h = new SubmitHarness(fixture);

        h.Submit(null);
        Assert.Equal("Select a mod first", h.Vm.Status);

        h.Submit("PlainMod");
        Assert.Contains("press Record… first", h.Vm.Status);

        // The maintainer wrote the bundled record; sending it back says nothing.
        h.Submit("BundledMod");
        Assert.Contains("bundled record", h.Vm.Status);

        Assert.Empty(h.Asked);
        Assert.Empty(h.Opened);
        Assert.Empty(h.Copied);
    });

    [Fact]
    public void SubmitCopiesARecordTooLargeForALinkAndSaysSo() => Sta(() =>
    {
        using var fixture = new Fixture();
        var h = new SubmitHarness(fixture);
        var record = new CompatRecord { Id = "TestMod", Verdict = CompatVerdict.Broken, Notes = new string('x', CompatSubmission.MaxUrlLength) };

        h.Submit("TestMod", record);
        Assert.Contains("copied to the clipboard", Assert.Single(h.Asked));
        Assert.Equal(CompatSubmission.RecordJson(record), Assert.Single(h.Copied));
        var url = Assert.Single(h.Opened);
        Assert.True(url.Length <= CompatSubmission.MaxUrlLength);
        Assert.Contains("&mod=TestMod&", url);
        Assert.Contains("copied to the clipboard", h.Vm.Status);
        Assert.Contains("paste", h.Vm.Status);

        // A clipboard that refuses must not leave the user at a form with nothing to paste.
        h.ClipboardWorks = false;
        h.Submit("TestMod", record);
        Assert.Single(h.Opened);
        Assert.Contains("Export…", h.Vm.Status);
    });

    // ---- the Role cell: plain-language choices over the stored role and Server-only logic tick ------------------

    private static ModRow RoleRow(ServerRole role, bool tick, bool experimental) => new()
    {
        Module = new DiscoveredModule("RoleMod", "v1.0.0", "", ModuleSourceKind.Custom,
            new Bannerlord.ModuleManager.ModuleInfoExtended { Id = "RoleMod", Name = "RoleMod" }),
        Role = role, ServerAuthoritative = tick, ShowExperimental = experimental,
    };

    /// <summary>Every stored state, with the switch off and on, and what the Role cell says for it.</summary>
    [Theory]
    [InlineData(ServerRole.Run, false, false, RoleChoice.ServerAndClient)]
    [InlineData(ServerRole.Run, true, false, RoleChoice.ServerAndClient)]      // the tick is hidden and a launch ignores it
    [InlineData(ServerRole.DependencyOnly, false, false, RoleChoice.ClientOnly)]
    [InlineData(ServerRole.DependencyOnly, true, false, RoleChoice.ClientOnly)]
    [InlineData(ServerRole.AsShipped, false, false, RoleChoice.ModDecides)]
    [InlineData(ServerRole.AsShipped, true, false, RoleChoice.ModDecides)]
    [InlineData(ServerRole.Run, false, true, RoleChoice.ServerAndClient)]
    [InlineData(ServerRole.Run, true, true, RoleChoice.ServerOnly)]
    [InlineData(ServerRole.DependencyOnly, false, true, RoleChoice.ClientOnly)]
    [InlineData(ServerRole.DependencyOnly, true, true, RoleChoice.ClientOnly)]  // a tick does not change what the role is
    [InlineData(ServerRole.AsShipped, false, true, RoleChoice.ModDecides)]
    [InlineData(ServerRole.AsShipped, true, true, RoleChoice.ModDecides)]
    public void TheRoleCellShowsTheStoredRoleAndTick(ServerRole role, bool tick, bool experimental, RoleChoice shown)
    {
        Assert.Equal(shown, RoleRow(role, tick, experimental).RoleChoice);
        Assert.Equal(shown, RoleLabels.Choice(role, tick, experimental));
    }

    /// <summary>
    /// Every choice, made from every stored state, with the switch off and on. The role always follows the choice.
    /// The tick moves only for the two choices that are about it, and only while its column is on screen.
    /// </summary>
    [Fact]
    public void ChoosingARoleWritesTheStoredRoleAndOnlyTouchesAVisibleTick()
    {
        foreach (var experimental in new[] { false, true })
        foreach (var role in Enum.GetValues<ServerRole>())
        foreach (var tick in new[] { false, true })
        foreach (var choice in Enum.GetValues<RoleChoice>())
        {
            var row = RoleRow(role, tick, experimental);
            row.RoleChoice = choice;
            var what = $"{role}, tick {tick}, switch {experimental}, chose {choice}";

            if (choice == RoleChoice.ServerOnly && !experimental)
            {
                // Not in the drop-down with the switch off, so it changes nothing.
                Assert.True(row.Role == role && row.ServerAuthoritative == tick, what);
                continue;
            }
            var expectedRole = choice switch
            {
                RoleChoice.ClientOnly => ServerRole.DependencyOnly,
                RoleChoice.ModDecides => ServerRole.AsShipped,
                _ => ServerRole.Run,
            };
            var expectedTick = !experimental ? tick
                : choice == RoleChoice.ServerOnly || (choice != RoleChoice.ServerAndClient && tick);
            Assert.True(row.Role == expectedRole, what + $": role is {row.Role}");
            Assert.True(row.ServerAuthoritative == expectedTick, what + $": tick is {row.ServerAuthoritative}");
            // With the switch on the cell then shows exactly what was chosen.
            if (experimental) Assert.True(row.RoleChoice == choice, what + $": cell shows {row.RoleChoice}");
        }
    }

    /// <summary>
    /// Switch off: a tick made earlier is still in the profile but its column is hidden and a launch ignores it. The
    /// cell says what the launch will do, and picking roles must not quietly throw the tick away.
    /// </summary>
    [Fact]
    public void AHiddenTickReadsAsServerAndClientAndSurvivesRoleChanges()
    {
        var row = RoleRow(ServerRole.Run, tick: true, experimental: false);
        Assert.Equal(RoleChoice.ServerAndClient, row.RoleChoice);

        row.RoleChoice = RoleChoice.ClientOnly;
        row.RoleChoice = RoleChoice.ModDecides;
        row.RoleChoice = RoleChoice.ServerAndClient;

        Assert.Equal(ServerRole.Run, row.Role);
        Assert.True(row.ServerAuthoritative);
        Assert.Equal(RoleChoice.ServerAndClient, row.RoleChoice);
        // And it is what it always was once the switch is back on.
        row.ShowExperimental = true;
        Assert.Equal(RoleChoice.ServerOnly, row.RoleChoice);
    }

    /// <summary>
    /// Switch on: the Role cell and the Server-only logic checkbox are two views of one row, and each must hear
    /// about a change made through the other or they disagree on screen.
    /// </summary>
    [Fact]
    public void TheRoleCellAndTheServerOnlyCheckboxStayInStep()
    {
        var row = RoleRow(ServerRole.Run, tick: false, experimental: true);
        var raised = new List<string?>();
        row.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        // The checkbox moves the cell.
        row.ServerAuthoritative = true;
        Assert.Equal(RoleChoice.ServerOnly, row.RoleChoice);
        Assert.Contains(nameof(ModRow.RoleChoice), raised);
        raised.Clear();
        row.ServerAuthoritative = false;
        Assert.Equal(RoleChoice.ServerAndClient, row.RoleChoice);
        Assert.Contains(nameof(ModRow.RoleChoice), raised);

        // The cell moves the checkbox.
        raised.Clear();
        row.RoleChoice = RoleChoice.ServerOnly;
        Assert.True(row.ServerAuthoritative);
        Assert.Contains(nameof(ModRow.ServerAuthoritative), raised);
        Assert.Contains(nameof(ModRow.RoleChoice), raised);
        raised.Clear();
        row.RoleChoice = RoleChoice.ServerAndClient;
        Assert.False(row.ServerAuthoritative);
        Assert.Contains(nameof(ModRow.ServerAuthoritative), raised);
        Assert.Contains(nameof(ModRow.RoleChoice), raised);

        // Anything that sets Role directly (a rescan, a record's defaults) moves the cell too.
        raised.Clear();
        row.Role = ServerRole.DependencyOnly;
        Assert.Equal(RoleChoice.ClientOnly, row.RoleChoice);
        Assert.Contains(nameof(ModRow.RoleChoice), raised);

        // Ticking the checkbox on a row that is not Run is stored and leaves the role alone: the tick only moves
        // the cell between Server + Client and Server only.
        row.ServerAuthoritative = true;
        Assert.Equal(ServerRole.DependencyOnly, row.Role);
        Assert.Equal(RoleChoice.ClientOnly, row.RoleChoice);
        row.Role = ServerRole.AsShipped;
        Assert.True(row.ServerAuthoritative);
        Assert.Equal(RoleChoice.ModDecides, row.RoleChoice);

        // Server only from Client only: one visible step, never a flash of a third choice the host did not pick.
        row.Role = ServerRole.DependencyOnly; row.ServerAuthoritative = false;
        var seen = new List<RoleChoice>();
        row.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ModRow.RoleChoice)) seen.Add(row.RoleChoice); };
        row.RoleChoice = RoleChoice.ServerOnly;
        Assert.Equal([RoleChoice.ClientOnly, RoleChoice.ServerOnly], seen.Distinct());
    }

    /// <summary>
    /// Server only is offered only with the experimental columns, and the list and the rows change in the order that
    /// never leaves a cell holding a choice its drop-down does not list (which a ComboBox shows as blank).
    /// </summary>
    [Fact]
    public void ServerOnlyIsOfferedOnlyWithExperimentalCompatibility() => Sta(() =>
    {
        using var fixture = new Fixture(); fixture.Module("TestMod");
        var vm = fixture.ViewModel(); vm.Rescan(); vm.Mode = AppMode.Host;
        var row = vm.Mods.Single(m => m.Id == "TestMod");
        row.Role = ServerRole.Run; row.ServerAuthoritative = true;

        RoleChoice[] offered() => vm.RoleChoices.Select(c => c.Choice).ToArray();
        Assert.Equal([RoleChoice.ServerAndClient, RoleChoice.ClientOnly, RoleChoice.ModDecides], offered());
        Assert.Equal(["Server + Client", "Client only", "Mod decides"], vm.RoleChoices.Select(c => c.Label));
        Assert.Equal(RoleChoice.ServerAndClient, row.RoleChoice);

        // What a bound cell would see at each notification: its own choice must be in the list at that moment.
        var orphaned = new List<string>();
        void Check(string at) { if (!offered().Contains(row.RoleChoice)) orphaned.Add($"{at}: {row.RoleChoice}"); }
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(MainViewModel.RoleChoices)) Check("list changed"); };
        row.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ModRow.RoleChoice)) Check("row changed"); };

        vm.ExperimentalCompat = true;
        Assert.Equal([RoleChoice.ServerAndClient, RoleChoice.ClientOnly, RoleChoice.ServerOnly, RoleChoice.ModDecides], offered());
        Assert.Equal("Server only", vm.RoleChoices.Single(c => c.Choice == RoleChoice.ServerOnly).Label);
        Assert.Equal(RoleChoice.ServerOnly, row.RoleChoice);

        vm.ExperimentalCompat = false;
        Assert.DoesNotContain(RoleChoice.ServerOnly, offered());
        Assert.Equal(RoleChoice.ServerAndClient, row.RoleChoice);
        Assert.True(row.ServerAuthoritative);
        Assert.Empty(orphaned);

        // Rows built by a later scan are told too, and Player mode never offers it whatever the switch says.
        vm.ExperimentalCompat = true;
        vm.Rescan();
        Assert.All(vm.Mods, r => Assert.True(r.ShowExperimental));
        vm.Mode = AppMode.Player;
        Assert.DoesNotContain(RoleChoice.ServerOnly, offered());
        Assert.All(vm.Mods, r => Assert.False(r.ShowExperimental));
    });

    /// <summary>
    /// The Role column's own wiring, on a ComboBox set up the way the column sets up its cells: label shown, choice
    /// stored, and the list swapped by the window when the view model says so. A cell must keep its selection
    /// through both swaps, and a pick must reach the row.
    /// </summary>
    [Fact]
    public void ABoundRoleCellKeepsItsSelectionWhenTheListChanges() => Sta(() =>
    {
        using var fixture = new Fixture(); fixture.Module("TestMod");
        var vm = fixture.ViewModel(); vm.Rescan(); vm.Mode = AppMode.Host;
        var row = vm.Mods.Single(m => m.Id == "TestMod");
        row.Role = ServerRole.Run; row.ServerAuthoritative = true;

        var cell = new ComboBox { DataContext = row, DisplayMemberPath = "Label", SelectedValuePath = "Choice", ItemsSource = vm.RoleChoices };
        cell.SetBinding(ComboBox.SelectedValueProperty,
            new Binding(nameof(ModRow.RoleChoice)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(MainViewModel.RoleChoices)) cell.ItemsSource = vm.RoleChoices; };
        string? Shown() => (cell.SelectedItem as RoleChoiceItem)?.Label;
        try
        {
            Assert.Equal("Server + Client", Shown());

            vm.ExperimentalCompat = true;
            Assert.Equal("Server only", Shown());
            cell.SelectedValue = RoleChoice.ServerAndClient;      // the host picks from the drop-down
            Assert.False(row.ServerAuthoritative);
            Assert.Equal("Server + Client", Shown());
            cell.SelectedValue = RoleChoice.ServerOnly;
            Assert.True(row.ServerAuthoritative);
            Assert.Equal(ServerRole.Run, row.Role);
            row.ServerAuthoritative = false;                      // the checkbox in the next column
            Assert.Equal("Server + Client", Shown());
            row.ServerAuthoritative = true;
            Assert.Equal("Server only", Shown());

            vm.ExperimentalCompat = false;
            Assert.Equal("Server + Client", Shown());
            Assert.True(row.ServerAuthoritative);
            cell.SelectedValue = RoleChoice.ClientOnly;
            Assert.Equal(ServerRole.DependencyOnly, row.Role);
            Assert.True(row.ServerAuthoritative);
            Assert.Equal("Client only", Shown());
        }
        finally { BindingOperations.ClearAllBindings(cell); }
    });

    /// <summary>
    /// The labels are a way of showing the row, not a new thing to store. A profile and a shared list written after
    /// choosing from the drop-down hold the same role names and the same separate tick they always did.
    /// </summary>
    [Fact]
    public void ChoicesMadeInTheRoleCellAreStoredAsTheRoleAndTheTick() => Sta(() =>
    {
        using var fixture = new Fixture();
        fixture.Module("Both"); fixture.Module("ClientSide"); fixture.Module("ServerSide"); fixture.Module("Decides");
        var vm = fixture.ViewModel(); vm.Rescan(); vm.Mode = AppMode.Host; vm.ExperimentalCompat = true;
        var path = ProfileStore.PathFor(vm.Profile.Name);
        ModRow Row(string id) => vm.Mods.Single(m => m.Id == id);
        try
        {
            Assert.False(vm.IsDirty);
            foreach (var id in new[] { "Both", "ClientSide", "ServerSide", "Decides" }) Row(id).Enabled = true;
            Row("Both").RoleChoice = RoleChoice.ServerAndClient;
            Row("ClientSide").RoleChoice = RoleChoice.ClientOnly;
            Row("ServerSide").RoleChoice = RoleChoice.ServerOnly;
            Row("Decides").RoleChoice = RoleChoice.ModDecides;
            Assert.True(vm.IsDirty);

            vm.SaveProfileCommand.Execute(null);

            void AssertStored(Profile profile)
            {
                (ServerRole, bool) Stored(string id) { var m = profile.Mods.Single(m => m.Id == id); return (m.Role, m.ServerAuthoritative); }
                Assert.Equal((ServerRole.Run, false), Stored("Both"));
                Assert.Equal((ServerRole.DependencyOnly, false), Stored("ClientSide"));
                Assert.Equal((ServerRole.Run, true), Stored("ServerSide"));
                Assert.Equal((ServerRole.AsShipped, false), Stored("Decides"));
            }
            AssertStored(vm.Profile);
            AssertStored(ProfileStore.Load(vm.Profile.Name)!);

            // The file itself: the stored names, and none of the labels or the display-only property.
            var json = File.ReadAllText(path);
            Assert.Contains("\"DependencyOnly\"", json);
            Assert.Contains("\"AsShipped\"", json);
            Assert.Contains("\"Run\"", json);
            foreach (var label in new[] { "RoleChoice", "ServerOnly", "ClientOnly", "ModDecides", "Server only", "Client only", "Mod decides", "Server + Client" })
                Assert.DoesNotContain(label, json, StringComparison.OrdinalIgnoreCase);

            // The shared list a player imports says the same.
            var export = ModderLords.Core.Export.ModListFile.From(ClientLaunchSession.Prepare(vm.Profile).Modules, vm.Profile, "ModderLords");
            Assert.Contains(export.Mods, m => m.Id == "ServerSide" && m.Role == ServerRole.Run && m.ServerAuthoritative);
            Assert.Contains(export.Mods, m => m.Id == "ClientSide" && m.Role == ServerRole.DependencyOnly && !m.ServerAuthoritative);

            // And reading it back shows the choices that were made.
            vm.Profile = ProfileStore.Load(vm.Profile.Name)!;
            vm.Rescan();
            Assert.Equal(RoleChoice.ServerAndClient, Row("Both").RoleChoice);
            Assert.Equal(RoleChoice.ClientOnly, Row("ClientSide").RoleChoice);
            Assert.Equal(RoleChoice.ServerOnly, Row("ServerSide").RoleChoice);
            Assert.Equal(RoleChoice.ModDecides, Row("Decides").RoleChoice);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    });

    /// <summary>The game's own modules and Host mode's Coop marker are built As-shipped; the cell must go on saying so.</summary>
    [Fact]
    public void GameModulesAndTheCoopMarkerKeepTheRoleTheyAreBuiltWith() => Sta(() =>
    {
        using var fixture = new Fixture(); fixture.Module("CoopNightly");
        var vm = fixture.ViewModel(); vm.Rescan(); vm.Mode = AppMode.Host;
        foreach (var experimental in new[] { false, true })
        {
            vm.ExperimentalCompat = experimental;
            var fixed_ = vm.Mods.Where(r => r.IsGameModule || r.IsCoopClientMarker).ToList();
            Assert.Contains(fixed_, r => r.IsGameModule);
            Assert.Contains(fixed_, r => r.IsCoopClientMarker);
            Assert.All(fixed_, r => Assert.Equal(RoleChoice.ModDecides, r.RoleChoice));
        }
    });

    /// <summary>The places a host reads a stored default: the Compat tooltip and the Record dialog use the tab's words.</summary>
    [Fact]
    public void StoredDefaultsAreDescribedInTheModsTabsWords()
    {
        Assert.Equal("Server only", RoleLabels.Describe(ServerRole.Run, true, "-"));
        Assert.Equal("Server + Client, server-only logic off", RoleLabels.Describe(ServerRole.Run, false, "-"));
        Assert.Equal("Client only, server-only logic -", RoleLabels.Describe(ServerRole.DependencyOnly, null, "-"));
        Assert.Equal("Mod decides, server-only logic on", RoleLabels.Describe(ServerRole.AsShipped, true, "-"));
        Assert.Equal("(unset), server-only logic on", RoleLabels.Describe(null, true, "(unset)"));
        Assert.Equal("DependencyOnly (\"Client only\" on the Mods tab)", RoleLabels.InLog(ServerRole.DependencyOnly));

        var row = RoleRow(ServerRole.Run, tick: false, experimental: false);
        row.Compat = new CompatBadge(CompatVerdict.NeedsRecipe, false, CompatSource.Bundled,
            new CompatRecord { Id = "RoleMod", DefaultRole = ServerRole.DependencyOnly, ServerAuthoritative = false });
        Assert.Contains("Defaults: role Client only, server-only logic off", row.CompatTip);
        Assert.DoesNotContain("DependencyOnly", row.CompatTip);
    }
}
