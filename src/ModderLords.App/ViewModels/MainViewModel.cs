using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using ModderLords.Core.Compat;
using ModderLords.Coop.Compat;
using ModderLords.Core.Export;
using ModderLords.Core.Launch;
using ModderLords.Coop.Launch;
using ModderLords.Coop.Live;
using ModderLords.Core.Logs;
using ModderLords.Core.Modules;
using ModderLords.Core.Overlay;
using ModderLords.Core.Perf;
using ModderLords.Core.Profiles;
using ModderLords.Core.Saves;
using ModderLords.Coop.Saves;

namespace ModderLords.App.ViewModels;

public partial class ModRow : ObservableObject
{
    [ObservableProperty] private string _operationSummary = "Not analyzed";
    public required DiscoveredModule Module { get; init; }
    public bool IsMissing { get; init; }
    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private ServerRole _role;
    /// <summary>Layer 1: behaviours run on the server only; clients skip them (needs the shared module on both sides).</summary>
    [ObservableProperty] private bool _serverAuthoritative;
    /// <summary>Behaviours excluded from gating (kept on clients), edited in the Behaviours window.</summary>
    public List<string> ClientSideBehaviors { get; set; } = new();
    /// <summary>The assembly scan, computed on demand. The grid's columns never call this: reading a mod's DLLs for
    /// every row on the UI thread froze the first draw, so Rescan fills them from a background pass instead.</summary>
    public ModderLords.Core.Compat.ScanResult Scan => _scan ??= ModderLords.Core.Compat.AssemblyScan.Scan(Module);

    /// <summary>Shown in the scan columns until the background pass reaches this row.</summary>
    private const string Scanning = "…";

    /// <summary>Hands this row the result of the background scan and tells the grid to redraw its scan columns.</summary>
    internal void ApplyScan(ModderLords.Core.Compat.ScanResult scan)
    {
        _scan = scan;
        OnPropertyChanged(nameof(Behaviors));
        OnPropertyChanged(nameof(ServerVerdict));
        OnPropertyChanged(nameof(ServerVerdictDetail));
        OnPropertyChanged(nameof(Settings));
        OnPropertyChanged(nameof(SettingsTip));
    }

    public string Behaviors => IsMissing ? "" : _scan is not { } s ? Scanning
        : s.CampaignBehaviors.Count + s.MissionBehaviors.Count == 0 ? "" : $"{s.CampaignBehaviors.Count} campaign, {s.MissionBehaviors.Count} mission";

    /// <summary>
    /// Only an entry whose mod is no longer installed can be taken off the list. An installed mod is simply unticked:
    /// it is still on disk, so the next scan would put it straight back.
    /// </summary>
    public bool CanRemove => IsMissing;
    public string Id => Module.Id;
    public string Version => Module.Version;

    /// <summary>True for a module TaleWorlds ship. Decided by id, never by the module's own ModuleType claim.</summary>
    public bool IsGameModule => OfficialModules.IsGameModule(Module.Id);

    /// <summary>
    /// A framework that the game's own modules load AFTER - Harmony, ButterLib, UIExtenderEx, MCM. Its manifest
    /// says so, and both the engine order and this list read the same fact, so they cannot disagree.
    /// </summary>
    public bool LoadsBeforeGame => !IsGameModule && LoadOrder.LoadsBeforeNative(Module);

    /// <summary>
    /// The three bands of the load order: frameworks, then the game's own modules, then everything else. A row can
    /// be dragged freely inside its band and never out of it, because which band it is in is decided by the
    /// manifests, not by preference - moving a mod across would simply be undone by the next sort.
    /// </summary>
    public int Band => LoadsBeforeGame ? 0 : IsGameModule ? 1 : 2;

    public string BandName
    {
        get
        {
            if (IsMissing) return "Not installed. This entry is kept from the profile. Download it and Rescan, untick it to launch without it, or right-click → Remove from profile if you deleted it.";
            var band = Band switch
            {
                0 => "loads before the game's own modules (its manifest asks for it)",
                1 => "part of the game; the engine places it",
                _ => "loads after the game's own modules",
            };
            return HasVersionSiblings
                ? band + $"\n\n{VersionCount} copies of {Id} are installed at different versions. Only one can be on: ticking this one unticks the others.\nThis copy: {Folder}"
                : band;
        }
    }

    /// <summary>The game will not start without Native, SandBoxCore or SandBox, so their checkbox is read-only.</summary>
    public bool IsLocked => OfficialModules.IsRequired(Module.Id);
    public bool CanToggle => !IsLocked && !IsCoopClientMarker;

    /// <summary>
    /// The Coop row as Host mode shows it: the server supplies Coop itself, so this row is not a choice about the
    /// server at all. It is there because its position IS a choice -- the one that decides where Coop loads on the
    /// players' machines, which the host is the one handing out. In Player mode Coop is an ordinary mod and this is
    /// false.
    /// </summary>
    public bool IsCoopClientMarker { get; init; }

    /// <summary>True for Coop in either mode, so the list can say what it is rather than calling it "Mod".</summary>
    public bool IsCoop => IsCoopClientMarker || ClientManifest.CoopClientModuleIds.Contains(Module.Id);

    /// <summary>Shown in its own column so a game module is never mistaken for a mod you installed.</summary>
    // Deliberately short: the column sits between Module and Version, and "required" is already obvious from the
    // checkbox being greyed out. The tooltip carries the explanation.
    public string Kind => IsMissing ? "Missing" : IsCoop ? "Coop" : IsGameModule ? (OfficialModules.IsDlc(Module.Id) ? "DLC" : "Game") : LoadsBeforeGame ? "Framework" : "Mod";

    public string KindTip => IsCoopClientMarker
        ? "The Coop mod. This row shows where it loads on players' machines — drag it to change that. On the server it is always last; the dedicated server pins it there and this list cannot change it.\n\nMods that patch Coop (CoopMarriage, CoopModPatch) must sit BELOW this row on a player's machine, or their game crashes at startup.\n\nAlways on: the server supplies Coop and every player needs it."
        : IsCoop
        ? "The Coop mod. Mods that patch it — CoopMarriage, CoopModPatch — must load after it, or their Harmony patches find nothing to patch and the game crashes at startup."
        : LoadsBeforeGame
        ? "A framework. Its own manifest says the game's modules load after it, so it sits above them - the TaleWorlds launcher does the same. Drag it among the other frameworks."
        : !IsGameModule ? "A mod. Enable it and drag it to place it in the load order."
        : IsLocked ? "Part of the base game. It cannot be turned off - the game will not start without it."
        : OfficialModules.IsDlc(Module.Id) ? "A paid expansion. Coop refuses to let a client join with a DLC enabled, so leave it off for coop sessions."
        : HostMode
            ? "Part of the base game, and safe to turn off. Coop does not work with Birth and Aging or Fast Mode enabled."
            : "Part of the base game, and safe to turn off.";

    /// <summary>Whether the row is being shown in Host mode. Only the wording depends on it: a player should not be
    /// told which game modules break coop.</summary>
    public bool HostMode { get; init; }

    /// <summary>
    /// How many copies of this module id are installed at different versions. More than one means this row is one
    /// of several and only one of them can be ticked, because the engine loads a module id once.
    /// </summary>
    public int VersionCount { get; set; } = 1;

    public bool HasVersionSiblings => VersionCount > 1;

    public string Source => IsMissing ? "MISSING — download then Rescan" : Module.Source.ToString();
    public string Folder => Module.FolderPath;
    public string Bins => (Module.HasServerBin ? "server" : "") + (Module.HasServerBin && Module.HasClientBin ? " + " : "") + (Module.HasClientBin ? "client" : "");
    public string Notes => IsMissing ? "Download then Rescan" : string.Join(", ", new[]
    {
        Module.HasUnparsableVersion ? "version the game cannot read" : null,
        Module.HasHeadlessExclusions ? "client-only tags" : null,
        Module.HasCode ? null : "data only",
        Module.HasServerBin ? null : "no server bin",
    }.Where(s => s is not null));
    public static ServerRole[] Roles { get; } = [ServerRole.Run, ServerRole.DependencyOnly, ServerRole.AsShipped];

    private ModderLords.Core.Compat.ScanResult? _scan;

    /// <summary>The scan if the background pass has already produced one. Never computes: reading a mod's DLLs on
    /// the UI thread is exactly what the background pass exists to avoid.</summary>
    internal ModderLords.Core.Compat.ScanResult? ScanIfDone => _scan;
    /// <summary>IL-metadata verdict: server-safe / guarded / needs review. Computed lazily, never executes mod code.</summary>
    public string ServerVerdict => IsMissing ? "not installed" : _scan?.Summary ?? Scanning;
    public string ServerVerdictDetail => _scan is null ? "" : string.Join("\n", _scan.UiAssemblies.Concat(_scan.StoryModeAssemblies).Concat(_scan.GuardedCalls).Concat(_scan.Notes));
    /// <summary>What the Mod settings tab will find for this mod (metadata scan): MCM, its own settings classes, or nothing.</summary>
    public string Settings => IsMissing ? "" : _scan?.SettingsSummary ?? Scanning;
    public string? SettingsTip => _scan is not { } s ? null
        : s.SettingsClasses.Count == 0
            ? (s.UsesMcm ? "Uses MCM; its settings appear in the Mod settings tab." : "No settings class found by the scan. If the mod does have one, add its type name to the compat record as a settings hint (SettingsTypes in compat-db.local.json).")
            : "Settings classes found (shown in the Mod settings tab once the server has created them):\n" + string.Join("\n", s.SettingsClasses) + (s.UsesMcm ? "\nPlus MCM settings." : "");

    /// <summary>Curated verdict from the compat database (bundled + local override); refreshed by Rescan and after Record….</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CompatText), nameof(CompatVerdictValue), nameof(CompatTip))]
    private CompatBadge _compat = CompatBadge.None;

    public string CompatText => Compat.Verdict switch
    {
        CompatVerdict.Works => "Works",
        CompatVerdict.NeedsRecipe => "Needs recipe",
        CompatVerdict.Broken => "Broken",
        _ => Compat.Source == CompatSource.None ? "" : "Unknown",
    } + (Compat.VersionUntested ? " · untested version" : "");

    /// <summary>
    /// The verdict itself, for the Mods grid to colour by. The colour used to be a hex string built here,
    /// which meant the Compat column ignored the theme entirely; the view now maps this to a theme brush.
    /// </summary>
    public CompatVerdict CompatVerdictValue => Compat.Verdict;

    public string CompatTip
    {
        get
        {
            var r = Compat.Record;
            if (r is null) return "No record in the compatibility database. Use Record… after testing this mod.";
            var lines = new List<string>();
            if (!string.IsNullOrWhiteSpace(r.Notes)) lines.Add(r.Notes);
            lines.Add(r.TestedVersions.Count == 0 ? "Tested versions: (none recorded)" : "Tested versions: " + string.Join(", ", r.TestedVersions) + (Compat.VersionUntested ? $" (this copy is {Version})" : ""));
            if (r.TestedCoopVersion is not null) lines.Add("Coop: " + r.TestedCoopVersion);
            if (r.DefaultRole is not null || r.ServerAuthoritative is not null)
                lines.Add($"Defaults: role {r.DefaultRole?.ToString() ?? "-"}, server-only logic {(r.ServerAuthoritative is true ? "on" : r.ServerAuthoritative is false ? "off" : "-")}" +
                          (r.ClientSideBehaviors.Count > 0 ? $", client-side: {string.Join(", ", r.ClientSideBehaviors)}" : ""));
            if (!string.IsNullOrWhiteSpace(r.Url)) lines.Add(r.Url);
            lines.Add("Source: " + (Compat.Source == CompatSource.Local ? "your local record (compat-db.local.json)" : "bundled with the launcher"));
            return string.Join("\n", lines);
        }
    }
}

public sealed record ConsoleLine(string Time, LogCategory Category, string Text);

public sealed class SaveRow
{
    public required SaveHeader Header { get; init; }
    public string Name => Header.Name;
    public string Character => Header.CharacterName;
    public string Level => Header.MainHeroLevel;
    public string Day => Header.DayLong.ToString("0");
    public string Written => Header.LastWriteUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    public string Mods => string.Join(", ", Header.CommunityModuleIds);
}

public partial class MainViewModel : ObservableObject
{

    public ObservableCollection<string> ProfileNames { get; } = new();
    public ObservableCollection<ModRow> Mods { get; } = new();
    public ObservableCollection<string> Messages { get; } = new();

    /// <summary>
    /// Before v0.9.0 the launcher's in-game modules were called ModularCoop.Compat and
    /// DedicatedServer.ModularCoopCompat. Upgrading leaves those folders behind in the game install, where they
    /// are dead weight and — because the old community module has the same shape as the new one — a confusing
    /// second entry in every mod list. Say so rather than deleting anything inside the player's game folder.
    /// </summary>
    private static IEnumerable<string> LegacyModuleNotice(string? gameRoot, ServerPaths? paths)
    {
        // Both roots. The client module lands in the game install, but DedicatedServer.ModularCoopCompat is a
        // SERVER module and lives in the dedicated server's engine\Modules -- so looking only at the game root
        // could never find the one it was most likely to find. Verified on a real install 2026-09-11: a 0.8.7
        // DedicatedServer.ModularCoopCompat junction had survived every upgrade there, unreported.
        foreach (var root in new[]
                 {
                     string.IsNullOrWhiteSpace(gameRoot) ? null : System.IO.Path.Combine(gameRoot, "Modules"),
                     paths?.ModulesRoot,
                 })
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            foreach (var id in new[] { "ModularCoop.Compat", "DedicatedServer.ModularCoopCompat" })
            {
                var dir = System.IO.Path.Combine(root, id);
                if (System.IO.Directory.Exists(dir))
                    yield return $"left over from the old name: the module folder {id} is no longer used and can be deleted ({dir})";
            }
        }
    }
    public ObservableCollection<string> LoadOrderPreview { get; } = new();

    [ObservableProperty] private Profile _profile = new();
    [ObservableProperty] private string _selectedProfileName = "default";
    [ObservableProperty] private string _status = "Idle";
    [ObservableProperty] private string _serverRoot = "";
    [ObservableProperty] private string _gameRoot = "";
    [ObservableProperty] private string _clientManifestText = "";
    [ObservableProperty] private ModRow? _selectedMod;


    /// <summary>
    /// Player or Host. Player mode is a mod loader: the Mods and Share tabs, and nothing that mentions a server.
    /// Setting this to Host builds <see cref="Host"/>; the coop view model — and the engine, console and sampler
    /// it owns — is never constructed for someone who only wants to play.
    /// </summary>
    [ObservableProperty] private AppMode _mode = AppMode.Player;

    /// <summary>
    /// The hosting half of the app, or null in Player mode. The coop tabs take this as their DataContext, so a
    /// null here leaves them bound to nothing — which is correct, because they are collapsed at the same time.
    /// </summary>
    [ObservableProperty] private HostViewModel? _host;

    /// <summary>The update banner. Set by the window once it exists; null in tests, where nothing checks for updates.</summary>
    [ObservableProperty] private UpdateViewModel? _update;

    public bool IsHost => Mode == AppMode.Host;

    /// <summary>
    /// App-wide, off by default: explicit legacy behavior gating and diagnostic tracing. It works
    /// for some behaviour-plus-settings mods only, so it is hidden and, when off, not applied at launch either.
    /// </summary>
    [ObservableProperty] private bool _experimentalCompat;

    /// <summary>Subscribe to an imported list's missing Workshop mods through Steam. Opt-in, remembered in UiState.</summary>
    [ObservableProperty] private bool _autoSubscribeWorkshop;

    /// <summary>Make sure the Bannerlord Coop Workshop item is subscribed whenever a list is imported. Remembered in UiState.</summary>
    [ObservableProperty] private bool _alwaysSubscribeCoop = true;

    /// <summary>Whether Steam has the Coop Workshop item on disk in any library.</summary>
    private static bool CoopOnWorkshop() => GamePaths.SteamLibraries().Any(lib => Directory.Exists(
        Path.Combine(lib, "steamapps", "workshop", "content", GamePaths.BannerlordAppId.ToString(), ServerPaths.CoopWorkshopItemId.ToString())));

    /// <summary>The experimental columns and buttons are shown only in Host mode with experimental compatibility on.</summary>
    public bool ShowExperimentalCompat => IsHost && ExperimentalCompat;

    partial void OnExperimentalCompatChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowExperimentalCompat));
        Host?.InvalidatePreview();
        RefreshPreview();
    }

    partial void OnModeChanged(AppMode value)
    {
        // Built once and kept: switching back to Host must not lose the console scrollback or, far worse, orphan a
        // running server. HostViewModel disposes nothing on the way out because nothing about it is per-session.
        if (value == AppMode.Host) Host ??= new HostViewModel(this);
        OnPropertyChanged(nameof(IsHost));
        OnPropertyChanged(nameof(ShowExperimentalCompat));
        Rescan();
        if (IsHost) Host?.OnProfileSelected();
    }

    /// <summary>
    /// Somewhere to put a log line in either mode. Host mode has the console; Player mode has the Messages list on
    /// the Mods tab, which is the only log surface a player is shown.
    /// </summary>
    public void Log(LogCategory category, string text)
    {
        if (IsHost && Host is not null) Host.AddLine(category, text);
        else if (category is LogCategory.Warning or LogCategory.Error or LogCategory.Tool) Messages.Add(text);
    }

    /// <summary>
    /// Puts the shared ModderLords.Compat module in the game's Modules folder once per app start, so it is already
    /// there whenever a server turns out to need it. This lives here rather than on HostViewModel because the player
    /// it exists for is in Player mode, where there is no HostViewModel at all.
    ///
    /// The folder alone changes nothing: the module only does anything when the Bannerlord launcher has it enabled,
    /// which is still the mod-list sync's job, and only when the server runs it too. What it buys is that "the server
    /// has a mod you do not have" stops being reachable for this one module. Failures are not worth a line at
    /// startup — the launch path reports them properly.
    /// </summary>
    public async Task EnsureClientModuleAtStartupAsync()
    {
        try
        {
            // Off the UI thread: resolving the game root can walk the Steam libraries and the install copies a folder,
            // and this runs on the first frame. The await returns here on the UI thread, where the log surfaces live.
            var profile = Profile;
            var r = await Task.Run(() => ClientModuleInstaller.Ensure(ClientLauncher.ResolveGameRoot(profile)));
            if (r.Outcome is ClientModuleInstaller.InstallOutcome.Installed or ClientModuleInstaller.InstallOutcome.Updated)
                Log(LogCategory.Tool, "[ModderLords] " + r.Message);
        }
        catch (Exception ex) { Log(LogCategory.Tool, "[ModderLords] client module check skipped: " + ex.Message); }
    }

    public MainViewModel() : this(true) { }

    internal MainViewModel(bool initialize)
    {
        // Every reorder - drag, Move up/down, Use engine order - moves an item; Rescan only ever adds and clears. So a
        // move is exactly "the user changed the order", in one place rather than at each of the three call sites.
        Mods.CollectionChanged += (_, e) =>
        {
            if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Move) IsDirty = true;
        };
        if (!initialize) return;
        LoadProfileList();
        LoadProfile(ProfileNames.FirstOrDefault() ?? "default");
    }

    /// <summary>Applies the saved mode at startup, before the first scan, so the app never briefly scans as the
    /// wrong half of itself.</summary>
    public void ApplyMode(AppMode mode)
    {
        if (mode == AppMode.Host) Host ??= new HostViewModel(this);
        var changed = Mode != mode;
        Mode = mode;
        OnPropertyChanged(nameof(IsHost));
        OnPropertyChanged(nameof(ShowExperimentalCompat));
        if (!changed)
        {
            Rescan();               // OnModeChanged did not fire, but the first scan still has to happen
            if (IsHost) Host?.OnProfileSelected();
        }
    }

    // ---- profiles ---------------------------------------------------------------------------------

    /// <summary>Re-reads the profiles folder without disturbing the current selection (called when the dropdown opens).</summary>
    public void RefreshProfileList()
    {
        var current = SelectedProfileName;
        LoadProfileList();
        if (current is not null && ProfileNames.Contains(current)) SelectedProfileName = current;
    }

    private void LoadProfileList()
    {
        ProfileNames.Clear();
        foreach (var n in ProfileStore.List()) ProfileNames.Add(n);
        if (ProfileNames.Count == 0) ProfileNames.Add("default");
    }

    /// <summary>
    /// True when the mod list has been edited since the profile was last loaded or saved. Ticks, drags, removals and
    /// the order switch set it; the window title shows it, and switching profile or closing asks before losing it.
    /// Before this, all of those silently threw the edits away.
    /// </summary>
    [ObservableProperty] private bool _isDirty;

    /// <summary>How the unsaved-changes question is asked. Replaceable so tests never block on a message box.</summary>
    internal Func<string, MessageBoxResult> AskUnsaved { get; set; } = profileName => MessageBox.Show(
        $"The mod list in profile '{profileName}' has unsaved changes. Save them?",
        "Unsaved changes", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

    /// <summary>
    /// Settles unsaved edits before they would be lost: Yes saves, No discards, Cancel stops. Returns false only on
    /// Cancel, meaning whatever was about to happen should not.
    /// </summary>
    public bool ResolveUnsavedChanges()
    {
        if (!IsDirty) return true;
        switch (AskUnsaved(Profile.Name))
        {
            case MessageBoxResult.Yes:
                CollectProfileFromRows();
                ProfileStore.Save(Profile);
                Status = $"Profile '{Profile.Name}' saved";
                break;
            case MessageBoxResult.No:
                break;
            default:
                return false;
        }
        IsDirty = false;
        return true;
    }

    partial void OnSelectedProfileNameChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value == Profile.Name) return;
        if (!ResolveUnsavedChanges())
        {
            // The dropdown is mid-update when this runs and ignores a change made now, so put the name back once
            // it has finished.
            var current = Profile.Name;
            System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(() => SelectedProfileName = current);
            return;
        }
        LoadProfile(value);
    }

    public void LoadProfile(string name)
    {
        Profile = ProfileStore.Load(name) ?? new Profile { Name = name };
        SelectedProfileName = Profile.Name;
        Rescan();
        if (IsHost) Host?.OnProfileSelected();
        IsDirty = false;
    }

    [RelayCommand]
    private void SaveProfile()
    {
        CollectProfileFromRows();
        ProfileStore.Save(Profile);
        LoadProfileList();
        // Clearing ItemsSource clears ComboBox.SelectedItem through its two-way binding.
        // Re-select the saved profile once its item exists again; do not reload its contents.
        SelectedProfileName = Profile.Name;
        IsDirty = false;
        Status = $"Profile '{Profile.Name}' saved";
    }

    [RelayCommand]
    private void NewProfile()
    {
        if (!ResolveUnsavedChanges()) return;
        var n = 1;
        string suggested;
        do { suggested = $"profile{n++}"; } while (ProfileNames.Contains(suggested));

        var ask = new NameProfileWindow("Name this profile", suggested) { Owner = Application.Current?.MainWindow };
        if (ask.ShowDialog() != true) return;
        var name = ask.ProfileName;

        Profile = new Profile { Name = name, SaveName = Profile.SaveName };
        ProfileStore.Save(Profile);
        LoadProfileList();
        SelectedProfileName = name;
        Rescan();
    }

    /// <summary>
    /// Renames this profile and the files named after it. Blocked while the profile is hosting: the running server
    /// holds paths under the overlay folder that the rename moves.
    /// </summary>
    [RelayCommand]
    private void RenameProfile()
    {
        if (Host?.IsRunning == true)
        {
            Status = "Stop the server before renaming this profile.";
            return;
        }
        var old = Profile.Name;
        var ask = new NameProfileWindow("Rename this profile", old, currentName: old) { Owner = Application.Current?.MainWindow };
        if (ask.ShowDialog() != true || ask.ProfileName == old) return;

        // Save first: a rename must not quietly discard edits made since the last save.
        CollectProfileFromRows();
        ProfileStore.Save(Profile);
        try { ProfileStore.Rename(old, ask.ProfileName); }
        catch (Exception ex) { Status = "Rename failed: " + ex.Message; return; }

        Profile.Name = ask.ProfileName;
        LoadProfileList();
        SelectedProfileName = Profile.Name;
        IsDirty = false;
        Status = $"Renamed “{old}” to “{Profile.Name}”";
    }

    [RelayCommand]
    private void DeleteProfile()
    {
        if (MessageBox.Show($"Delete profile '{Profile.Name}'? Junctions it created are removed too.", "Delete profile", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        string? junctionError = null;
        try { new OverlayApplier().RemoveAll(ProfileStore.OverlayDirFor(Profile.Name)); }
        catch (Exception ex) { junctionError = ex.Message; }
        var deleted = Profile.Name;
        ProfileStore.Delete(deleted);
        IsDirty = false;
        LoadProfileList();
        LoadProfile(ProfileNames.First());
        // After the reload, which clears Messages: said before it, this would vanish at once.
        if (junctionError is not null)
            Messages.Add($"profile '{deleted}' deleted, but some of its junctions could not be removed: {junctionError}");
    }

    // ---- folders ----------------------------------------------------------------------------------

    [RelayCommand]
    private void EditFolders()
    {
        var win = new FoldersWindow(Profile.GameRoot, Profile.DedicatedServerRoot, Profile.CustomModRoots, IsHost)
            { Owner = Application.Current.MainWindow };
        if (win.ShowDialog() != true) return;
        ApplyFolders(win.GameRoot, win.ServerRoot, win.ModRoots);
    }

    /// <summary>
    /// Pins (or un-pins, with null) the profile's folders and rescans, since every one of them changes what the
    /// catalogue contains. The current list is collected first so ticks made before opening the dialog survive the scan.
    /// </summary>
    internal void ApplyFolders(string? gameRoot, string? serverRoot, IReadOnlyList<string> modRoots)
    {
        static string? Clean(string? p) => string.IsNullOrWhiteSpace(p) ? null : p.Trim();
        var roots = modRoots.Select(r => r.Trim()).Where(r => r.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (string.Equals(Clean(gameRoot), Profile.GameRoot, StringComparison.OrdinalIgnoreCase)
            && string.Equals(Clean(serverRoot), Profile.DedicatedServerRoot, StringComparison.OrdinalIgnoreCase)
            && roots.SequenceEqual(Profile.CustomModRoots, StringComparer.OrdinalIgnoreCase))
            return;
        CollectProfileFromRows();
        Profile.GameRoot = Clean(gameRoot);
        Profile.DedicatedServerRoot = Clean(serverRoot);
        Profile.CustomModRoots = roots;
        Rescan();
        IsDirty = true;
    }

    /// <summary>
    /// Ids the CLIENT can load: the game's own Modules folder and the Steam workshop. Lets a mod-list sync tell a
    /// mod the Bannerlord launcher has simply never scanned from one that really is not installed.
    /// </summary>
    internal IReadOnlySet<string> InstalledClientSide() =>
        _preview is null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : _preview.Catalog.Modules
                .Where(m => m.Source is ModuleSourceKind.GameModules or ModuleSourceKind.Workshop)
                .Select(m => m.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

    internal IReadOnlySet<string> OfficialSelection()
    {
        CollectProfileFromRows();
        return new HashSet<string>(Profile.ClientOfficialModules, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Copies of one mod id, in the order they should be listed: the pinned one first, then the folder actually
    /// named after the mod, then newest version first. Same tie-breakers ModuleSelector uses when nothing is
    /// pinned, so the top row is the copy that would load if you never chose.
    /// </summary>
    private static List<DiscoveredModule> OrderCopies(List<DiscoveredModule> copies, string id, DiscoveredModule? pinned) =>
        copies.OrderByDescending(c => pinned is not null && ReferenceEquals(c, pinned))
              .ThenByDescending(c => c.FolderName.Equals(id, StringComparison.OrdinalIgnoreCase))
              .ThenByDescending(c => c.Version, StringComparer.OrdinalIgnoreCase)
              .ToList();

    /// <summary>Guards the untick cascade below against re-entering itself.</summary>
    private bool _syncingVersions;

    /// <summary>
    /// The engine loads a module id once, so ticking one copy unticks the others. Without this you could tick two
    /// versions of the same mod, and the launch would silently pick one of them - the list would be saying
    /// something the engine cannot do.
    /// </summary>
    private void ModRowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_syncingVersions || e.PropertyName is not (nameof(ModRow.Enabled) or nameof(ModRow.Role) or nameof(ModRow.ServerAuthoritative))) return;
        if (sender is not ModRow row) return;
        IsDirty = true;
        if (e.PropertyName != nameof(ModRow.Enabled) || !row.Enabled || row.IsGameModule || !row.HasVersionSiblings)
        {
            RefreshPreview();
            return;
        }
        _syncingVersions = true;
        try
        {
            foreach (var other in Mods)
                if (!ReferenceEquals(other, row) && !other.IsGameModule
                    && other.Id.Equals(row.Id, StringComparison.OrdinalIgnoreCase) && other.Enabled)
                {
                    other.Enabled = false;
                    Status = $"{row.Id}: using {row.Version} ({row.Source}); the other copy was switched off.";
                }
        }
        finally { _syncingVersions = false; }
        RefreshPreview();
    }

    internal void CollectProfileFromRows()
    {
        // Game modules are a separate list on the profile: they have no role, no source path and no place in the
        // mod order, and writing them into Profile.Mods would make every exported mod list mention Native.
        var officialRows = Mods.Where(r => r.IsGameModule).ToList();
        if (officialRows.Count > 0)
            Profile.ClientOfficialModules = officialRows.Where(r => r.Enabled || r.IsLocked).Select(r => r.Id).ToList();

        // A profile names each mod once, so the rows for one id collapse back to a single entry: the ticked copy
        // if there is one, otherwise the first. SourcePath is what carries the choice of copy to the launch.
        // GroupBy keeps first-appearance order, so the profile keeps the order shown in the list.
        var byId = Profile.Mods.ToDictionary(m => m.Id, StringComparer.OrdinalIgnoreCase);
        var ordered = new List<ProfileMod>();
        foreach (var g in Mods.Where(r => !r.IsGameModule).GroupBy(r => r.Id, StringComparer.OrdinalIgnoreCase))
        {
            var row = g.FirstOrDefault(r => r.Enabled) ?? g.First();
            if (!byId.TryGetValue(g.Key, out var pm)) pm = new ProfileMod { Id = g.Key };
            pm.Enabled = row.Enabled;
            pm.Role = row.Role;
            pm.ServerAuthoritative = row.ServerAuthoritative;
            pm.ClientSideBehaviors = row.ClientSideBehaviors.ToList();
            if (!row.IsMissing) pm.SourcePath = row.Folder;
            ordered.Add(pm);
        }
        // Entries with no row this session are requirements, not a request to delete them from the profile -- and
        // not a request to MOVE them either. Appending them used to silently walk a hidden module to the end of the
        // load order on every save, which is how CoopNightly ended up loading after the mods that patch it.
        Profile.Mods = MergeKeepingPosition(ordered, Profile.Mods);
    }

    /// <summary>
    /// Puts back the entries that had no row, each at the index it held before. A save made in a mode that cannot
    /// show a row for something must not be the thing that reorders it.
    /// </summary>
    internal static List<ProfileMod> MergeKeepingPosition(List<ProfileMod> ordered, IReadOnlyList<ProfileMod> previous)
    {
        var present = new HashSet<string>(ordered.Select(m => m.Id), StringComparer.OrdinalIgnoreCase);
        // Ascending, so several missing entries keep their order relative to each other as well.
        for (var i = 0; i < previous.Count; i++)
        {
            if (present.Contains(previous[i].Id)) continue;
            ordered.Insert(Math.Min(i, ordered.Count), previous[i]);
        }
        return ordered;
    }

    // ---- catalog / mods ----------------------------------------------------------------------------

    [RelayCommand]
    public void Rescan()
    {
        Mods.Clear();
        _preview = null;
        ScannedCatalog = null;
        _previewLines.Clear();
        _scanGeneration++;          // any background scan still running is for rows that no longer exist
        Host?.InvalidatePreview();
        LoadOrderPreview.Clear();
        try
        {
            // Player mode has no dedicated server to resolve, and asking for one throws when it is not installed —
            // which is the normal case for someone using this purely as a mod loader.
            ServerPaths? paths = null;
            ModuleCatalog catalog;
            string? gameRoot;
            if (IsHost && Host is not null)
            {
                paths = LaunchSession.ResolvePaths(Profile);
                ServerRoot = paths.DedicatedServerRoot;
                catalog = LaunchSession.Scan(Profile, paths, out gameRoot);
            }
            else
            {
                ServerRoot = "";
                catalog = ClientLaunchSession.Scan(Profile, out gameRoot);
            }
            GameRoot = gameRoot ?? "(game install not found)";
            ScannedCatalog = (catalog, gameRoot);
            Messages.Clear();
            _blockedFiles.Clear();
            BlockedFileCount = 0;       // the background scan fills this in again
            foreach (var p in catalog.Problems) Messages.Add("catalog: " + p);
            var db = CompatDb.Reload();
            foreach (var p in db.Problems) Messages.Add("compat db: " + p);
            foreach (var m in LegacyModuleNotice(gameRoot, paths)) Messages.Add(m);
            CoopVersion = catalog.Modules.FirstOrDefault(m => m.IsStock && m.FolderName == "Coop")?.Version;

            // One row per module id AND version. The same mod routinely exists in the game's Modules folder and in
            // the workshop at different versions, and which one loads changes what you are playing - so both are
            // shown and you pick. Two copies at the SAME version are still one row: there is nothing to choose
            // between them, and ModuleSelector would pick either.
            // The server supplies Coop in Host mode; in Player mode it is an ordinary selectable client mod.
            var stockIds = new HashSet<string>(catalog.Modules.Where(m => m.IsStock).Select(m => m.Id), StringComparer.OrdinalIgnoreCase);
            // Coop is the exception to "stock modules are not rows". In Host mode the server supplies it, so it is
            // stock AND usually installed from the workshop as well - and where the workshop copy loads on the
            // players' machines is a real choice, made here, by the host who hands the list out. Prefer the
            // workshop copy: that is the one that actually loads on a player. Never a version sibling: one row.
            var byId = catalog.Modules.Where(m => !m.IsStock && !OfficialModules.IsGameModule(m.Id)
                                                  && (!stockIds.Contains(m.Id) || ClientManifest.CoopClientModuleIds.Contains(m.Id)))
                .GroupBy(m => m.Id, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g
                    .GroupBy(m => m.Version ?? "", StringComparer.OrdinalIgnoreCase)
                    .Select(vg => vg.OrderByDescending(m => m.FolderName.Equals(g.Key, StringComparison.OrdinalIgnoreCase)).First())
                    .ToList(), StringComparer.OrdinalIgnoreCase);

            Mods.Clear();

            // The game's own modules come first and are shown as what they are. They were invisible before, which
            // meant a coop host had no way to turn off Birth and Aging or Fast Mode - both of which break coop -
            // without leaving the launcher for the TaleWorlds one.
            // Build every mod row first, in the profile's order, then place them. Which band a row belongs to is a
            // fact about its manifest, so the rows do not need to know about it while they are being made.
            var modRows = new List<ModRow>();
            foreach (var pm in Profile.Mods)
            {
                // Host mode: one Coop row, marking where Coop loads on the players' machines. The server's own copy
                // decides nothing here, so a version sibling would be a choice about nothing - show a single row.
                if (IsHost && ClientManifest.CoopClientModuleIds.Contains(pm.Id))
                {
                    var coop = (byId.TryGetValue(pm.Id, out var coopCopies) ? coopCopies.FirstOrDefault() : null)
                               ?? catalog.Modules.FirstOrDefault(m => m.IsStock && m.FolderName == "Coop");
                    byId.Remove(pm.Id);
                    if (coop is null) continue;      // no Coop anywhere: nothing to place
                    modRows.Add(new ModRow
                    {
                        Module = coop, Enabled = true, Role = ServerRole.AsShipped,
                        IsCoopClientMarker = true, HostMode = true,
                    });
                    continue;
                }
                if (!byId.TryGetValue(pm.Id, out var copies))
                {
                    if (IsHost && stockIds.Contains(pm.Id)) continue;
                    Messages.Add($"MISSING: {pm.Id} — download it, then Rescan, or right-click it → Remove from profile."
                                 + (string.IsNullOrWhiteSpace(pm.DownloadUrl) ? "" : " " + pm.DownloadUrl));
                    modRows.Add(new ModRow
                    {
                        Module = new DiscoveredModule(pm.Id, pm.LastVersion ?? "unknown", "", ModuleSourceKind.Custom,
                            new Bannerlord.ModuleManager.ModuleInfoExtended { Id = pm.Id, Name = pm.Id }),
                        IsMissing = true, Enabled = pm.Enabled, Role = pm.Role,
                        ServerAuthoritative = pm.ServerAuthoritative, ClientSideBehaviors = pm.ClientSideBehaviors.ToList(),
                    });
                    continue;
                }
                // The profile pins a folder, so that copy is the one that is on; the rest are shown alongside it,
                // newest first, and are off. This is the same choice ModuleSelector makes at launch.
                var pinned = pm.SourcePath is null ? null : copies.FirstOrDefault(c => Junction.PathsEqual(c.FolderPath, pm.SourcePath));
                pinned ??= pm.LastVersion is null ? null : copies.FirstOrDefault(c => SaveHeaderReader.VersionsEqual(c.Version, pm.LastVersion));
                var ordered = OrderCopies(copies, pm.Id, pinned);
                var chosen = pinned ?? ordered[0];
                foreach (var c in ordered)
                    modRows.Add(new ModRow
                    {
                        Module = c, Enabled = pm.Enabled && ReferenceEquals(c, chosen), Role = pm.Role,
                        ServerAuthoritative = pm.ServerAuthoritative, ClientSideBehaviors = pm.ClientSideBehaviors.ToList(),
                    });
                byId.Remove(pm.Id);
            }
            // Mods new to this profile take their defaults from the compat database; existing entries are never rewritten.
            foreach (var kv in byId.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            {
                // A host whose profile has never named Coop still needs the marker, so there is somewhere to say
                // where Coop loads on a player. Last is the honest default: it is where the server loads it.
                if (IsHost && ClientManifest.CoopClientModuleIds.Contains(kv.Key))
                {
                    modRows.Add(new ModRow
                    {
                        Module = kv.Value[0], Enabled = true, Role = ServerRole.AsShipped,
                        IsCoopClientMarker = true, HostMode = true,
                    });
                    continue;
                }
                var rec = db.Find(kv.Key);
                foreach (var c in OrderCopies(kv.Value, kv.Key, null))
                    modRows.Add(new ModRow
                    {
                        Module = c, Enabled = false, Role = db.DefaultRoleFor(kv.Key),
                        ServerAuthoritative = rec?.ServerAuthoritative ?? false,
                        ClientSideBehaviors = rec?.ClientSideBehaviors.ToList() ?? new List<string>(),
                    });
            }

            // Tell each row how many siblings it has, and say so once in Messages - a mod installed twice at two
            // versions is worth noticing even if you never open the tooltip.
            foreach (var g in modRows.GroupBy(r => r.Id, StringComparer.OrdinalIgnoreCase))
            {
                var n = g.Count();
                foreach (var r in g) r.VersionCount = n;
                if (n > 1) Messages.Add($"{g.Key}: {n} versions installed ({string.Join(", ", g.Select(r => r.Version))}); only the ticked one loads");
                foreach (var r in g.Where(r => !r.IsMissing && r.Module.HasUnparsableVersion))
                    Messages.Add($"{r.Id}: SubModule.xml says version “{r.Version}”, which Bannerlord cannot parse "
                               + "(a prefix letter then numbers only, e.g. v0.9.30). Everything that reads it, Coop's module check included, sees a0.0.0.");
            }

            // Band 0: frameworks. The list used to open with the game's own modules and say mods load after them,
            // which is not true of Harmony, ButterLib, UIExtenderEx or MCM - and the engine order panel next to it
            // said so. Same rule as LoadOrder.Compute, read from the same manifests.
            foreach (var row in modRows.Where(r => r.LoadsBeforeGame)) Mods.Add(row);

            // Band 1: the game's own modules. They were invisible before v0.8, which meant a coop host had no way to
            // turn off Birth and Aging or Fast Mode - both of which break coop - without leaving for the TaleWorlds
            // launcher.
            var wantedOfficials = new HashSet<string>(Profile.ClientOfficialModules, StringComparer.OrdinalIgnoreCase);
            foreach (var g in catalog.Modules
                         .Where(m => m.Source == ModuleSourceKind.GameModules && OfficialModules.IsGameModule(m.Id))
                         .GroupBy(m => m.Id, StringComparer.OrdinalIgnoreCase)
                         .OrderBy(g => OfficialModules.IsRequired(g.Key) ? 0 : OfficialModules.IsDlc(g.Key) ? 2 : 1)
                         .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
            {
                var m = g.First();
                var on = OfficialModules.IsRequired(m.Id)
                         || wantedOfficials.Contains(m.Id) || wantedOfficials.Contains(m.FolderName);
                Mods.Add(new ModRow { Module = m, Enabled = on, Role = ServerRole.AsShipped, HostMode = IsHost });
            }
            foreach (var id in Profile.ClientOfficialModules.Where(id => !Mods.Any(r => r.Id.Equals(id, StringComparison.OrdinalIgnoreCase))))
                Mods.Add(new ModRow
                {
                    Module = new DiscoveredModule(id, "not installed", "", ModuleSourceKind.GameModules,
                        new Bannerlord.ModuleManager.ModuleInfoExtended { Id = id, Name = id }),
                    IsMissing = true, Enabled = true, Role = ServerRole.AsShipped, HostMode = IsHost,
                });

            // Band 2: everything else.
            foreach (var row in modRows.Where(r => !r.LoadsBeforeGame)) Mods.Add(row);

            // Rows are rebuilt on every scan, so the old ones (and their handlers) go with them.
            foreach (var row in Mods) row.PropertyChanged += ModRowChanged;

            foreach (var row in Mods) row.Compat = db.For(row.Id, row.Version);
            NotifyMissingChanged();
            CheckCoopOrder();
            StartBackgroundScan();

            if (IsHost && Host is not null && paths is not null) Host.RefreshSaves(paths);
            RefreshPreview();
            UpdateModFolderWatch(gameRoot);
            OfferPendingLauncherApply();
            if (IsHost)
            {
                Host?.RefreshDrift();
                Host?.LoadGameplay();
            }
            Status = $"{modRows.Count(r => r.Enabled && !r.IsMissing)} installed mods enabled, "
                   + $"{Mods.Count(r => r.IsMissing && r.Enabled)} selected modules missing, "
                   + $"{Mods.Count(r => r.IsGameModule && r.Enabled && !r.IsMissing)} game modules";
        }
        catch (Exception ex)
        {
            Status = ex.Message;
            ClientManifestText = "Cannot scan this selection: " + ex.Message;
            if (IsHost && Host?.ClientTarget is not null) UpdateShareText();
            Messages.Add(ex.Message);
        }
    }

    [RelayCommand]
    private void EditBehaviors()
    {
        if (SelectedMod is null) { Status = "Select a mod first"; return; }
        var scan = SelectedMod.Scan;
        if (scan.CampaignBehaviors.Count + scan.MissionBehaviors.Count == 0) { Status = $"{SelectedMod.Id} has no campaign or mission behaviours to gate"; return; }
        var win = new BehaviorsWindow(SelectedMod.Id, scan, SelectedMod.ClientSideBehaviors) { Owner = Application.Current.MainWindow };
        if (win.ShowDialog() == true)
        {
            SelectedMod.ClientSideBehaviors = win.ClientSide;
            if (!SelectedMod.ServerAuthoritative && win.ClientSide.Count < scan.CampaignBehaviors.Count + scan.MissionBehaviors.Count) SelectedMod.ServerAuthoritative = true;
            Status = $"{SelectedMod.Id}: {scan.CampaignBehaviors.Count + scan.MissionBehaviors.Count - win.ClientSide.Count} behaviour(s) server-only, {win.ClientSide.Count} kept client-side";
        }
    }

    [RelayCommand]
    private void ShowAuthority()
    {
        if (SelectedMod is null) { Status = "Select a mod first"; return; }
        if (!SelectedMod.Module.HasCode) { Status = $"{SelectedMod.Id} has no code to analyse"; return; }
        new AuthorityWindow(SelectedMod.Module) { Owner = Application.Current.MainWindow }.Show();
    }

    // ---- compat database ---------------------------------------------------------------------------

    /// <summary>Coop's version from the last scan (recorded as TestedCoopVersion); null when the server was not found.</summary>
    [ObservableProperty] private string? _coopVersion;

    private void RefreshCompatBadges()
    {
        var db = CompatDb.Reload();
        foreach (var p in db.Problems) Messages.Add("compat db: " + p);
        foreach (var row in Mods) row.Compat = db.For(row.Id, row.Version);
    }

    [RelayCommand]
    private void RecordCompat()
    {
        if (SelectedMod is null) { Status = "Select a mod first"; return; }
        var win = new CompatRecordWindow(SelectedMod, CoopVersion) { Owner = Application.Current.MainWindow };
        if (win.ShowDialog() != true) return;
        try
        {
            if (win.RemoveLocal)
            {
                CompatDb.RemoveLocal(CompatDb.LocalPath, SelectedMod.Id);
                RefreshCompatBadges();
                Status = $"{SelectedMod.Id}: local record removed" + (SelectedMod.Compat.Source == CompatSource.Bundled ? ", showing the bundled one" : "");
            }
            else
            {
                CompatDb.SaveLocal(CompatDb.LocalPath, win.Result);
                RefreshCompatBadges();
                Status = $"{SelectedMod.Id}: recorded as {SelectedMod.CompatText} in {CompatDb.LocalPath}";
            }
        }
        catch (Exception ex) { Status = ex.Message; Messages.Add("compat db: " + ex.Message); }
    }

    [RelayCommand]
    private void ExportCompat()
    {
        var db = CompatDb.Current;
        var selected = SelectedMod is not null ? db.Find(SelectedMod.Id) : null;
        var records = selected is not null ? new[] { selected } : db.LocalRecords.ToArray();
        if (records.Length == 0) { Status = "Nothing to export: select a mod with a record, or record one first"; return; }
        var dlg = new SaveFileDialog
        {
            Title = selected is not null ? $"Export the {selected.Id} record" : "Export all local records",
            Filter = "Compat records (*.json)|*.json",
            FileName = selected is not null ? $"compat-{ProfileStore.Safe(selected.Id)}.json" : "compat-db.local.json",
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            CompatDb.WriteFile(dlg.FileName, records);
            Status = $"Exported {records.Length} record(s) to {dlg.FileName}";
        }
        catch (Exception ex) { Status = ex.Message; }
    }

    [RelayCommand]
    private void ImportCompat()
    {
        var dlg = new OpenFileDialog { Title = "Import compat records", Filter = "Compat records (*.json)|*.json|All files (*.*)|*.*" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var r = CompatDb.ImportLocal(CompatDb.LocalPath, File.ReadAllText(dlg.FileName));
            RefreshCompatBadges();
            if (r.Added.Count > 0) Messages.Add("compat import: added " + string.Join(", ", r.Added));
            if (r.Updated.Count > 0) Messages.Add("compat import: updated " + string.Join(", ", r.Updated));
            foreach (var id in r.Kept) Messages.Add($"compat import: kept your local {id} record (the file's copy is not newer)");
            Status = $"Import: {r.Added.Count} added, {r.Updated.Count} updated, {r.Kept.Count} kept";
        }
        catch (Exception ex) { Status = ex.Message; Messages.Add("compat import: " + ex.Message); }
    }

    [RelayCommand(CanExecute = nameof(CanMoveUp))]
    private void MoveUp() => Move(-1);

    [RelayCommand(CanExecute = nameof(CanMoveDown))]
    private void MoveDown() => Move(1);

    private bool CanMoveUp() => CanMove(-1);
    private bool CanMoveDown() => CanMove(1);

    /// <summary>
    /// Whether the selected row can move that way at all. The buttons are bound to this, so a row at the top or
    /// bottom of its band shows two greyed buttons instead of two that quietly do nothing - which is how the old
    /// ones behaved, and the reason the rules felt arbitrary.
    /// </summary>
    private bool CanMove(int delta)
    {
        if (SelectedMod is null || SelectedMod.IsGameModule) return false;
        var i = Mods.IndexOf(SelectedMod);
        var j = i + delta;
        return i >= 0 && j >= 0 && j < Mods.Count && Mods[j].Band == SelectedMod.Band;
    }

    partial void OnSelectedModChanged(ModRow? value)
    {
        NotifyMoveability();
        RemoveModCommand.NotifyCanExecuteChanged();
        OpenModFolderCommand.NotifyCanExecuteChanged();
        SetSourceLinkCommand.NotifyCanExecuteChanged();
    }

    // ---- removing mods that are gone ---------------------------------------------------------------

    /// <summary>
    /// The catalogue from the last Rescan, with the game root it found. Previews reuse it so an edit to the list never
    /// walks the disk; only Rescan (and a real launch, which ignores this) looks at the folders again. Null until a
    /// scan has succeeded, and after one that failed.
    /// </summary>
    internal (ModuleCatalog Catalog, string? GameRoot)? ScannedCatalog { get; private set; }

    /// <summary>Rows kept from the profile whose mod is no longer installed. Drives the "Remove missing" button.</summary>
    public int MissingCount => Mods.Count(r => r.IsMissing);

    [RelayCommand(CanExecute = nameof(CanRemoveMod))]
    private void RemoveMod()
    {
        if (SelectedMod is not { CanRemove: true } row) return;
        RemoveRows([row]);
        Status = $"{row.Id} removed from profile '{Profile.Name}'. Save to keep the change.";
    }

    private bool CanRemoveMod() => SelectedMod is { CanRemove: true };

    [RelayCommand(CanExecute = nameof(HasMissing))]
    private void RemoveAllMissing()
    {
        var ids = Mods.Where(r => r.IsMissing).Select(r => r.Id).ToList();
        if (ids.Count == 0) return;
        if (MessageBox.Show($"Take these {ids.Count} mod(s) that are no longer installed off profile '{Profile.Name}'?\n\n{string.Join("\n", ids)}",
                "Remove missing mods", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        RemoveMissingRows();
    }

    private bool HasMissing() => MissingCount > 0;

    /// <summary>Subscribes to the missing mods on the Workshop (or, with auto-subscribe off, offers their pages).</summary>
    [RelayCommand(CanExecute = nameof(HasMissing))]
    private void SubscribeMissing() => SubscribeToMissing(auto: AutoSubscribeWorkshop || AskToSubscribe(),
        includeCoop: AlwaysSubscribeCoop && !CoopOnWorkshop());

    private bool AskToSubscribe() => MessageBox.Show(
        "Subscribe to the missing mods through Steam? Steam shows you as playing Bannerlord for as long as it takes.\n\n"
        + "No opens their Workshop pages instead, so you can subscribe to each yourself.",
        "Subscribe to missing mods", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    /// <summary>
    /// The profile's missing mods, split into those with a Workshop id (from the link the list carried) and those
    /// nothing here can fetch.
    /// </summary>
    internal (List<(ulong WorkshopId, string ModId)> Workshop, List<(string ModId, string? Link)> Manual) MissingBySource(IEnumerable<string> missingIds)
    {
        var workshop = new List<(ulong, string)>();
        var manual = new List<(string, string?)>();
        foreach (var id in missingIds.Where(id => !OfficialModules.IsGameModule(id)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var link = Profile.Mods.FirstOrDefault(m => m.Id.Equals(id, StringComparison.OrdinalIgnoreCase))?.DownloadUrl;
            if (Core.Workshop.WorkshopEvent.ParseWorkshopId(link) is { } wid) workshop.Add((wid, id));
            else manual.Add((id, link));
        }
        return (workshop, manual);
    }

    /// <param name="includeCoop">Add the Bannerlord Coop Workshop item, which no list names as a missing mod.</param>
    /// <param name="onlyCoop">Subscribe to Coop and nothing else (the Coop toggle is on, general auto-subscribe is off).</param>
    private void SubscribeToMissing(bool auto, bool includeCoop = false, bool onlyCoop = false)
    {
        var ids = onlyCoop ? [] : Mods.Where(r => r.IsMissing && r.Enabled).Select(r => r.Id)
            .Concat(Profile.PendingLauncherApply ? ModListFile.AsListed(Profile).NotInstalled(InstalledClientSide()) : []);
        var (workshop, manual) = MissingBySource(ids);
        if ((includeCoop || onlyCoop) && !workshop.Any(w => w.WorkshopId == (ulong)ServerPaths.CoopWorkshopItemId))
            workshop.Insert(0, ((ulong)ServerPaths.CoopWorkshopItemId, "Bannerlord Coop"));
        if (workshop.Count == 0 && manual.Count == 0) return;
        var win = new WorkshopSubscribeWindow(workshop, manual, ClientLauncher.ResolveGameRoot(Profile), auto)
        {
            Owner = Application.Current.MainWindow,
        };
        win.ShowDialog();
        // The folder watch would get there too, a few seconds later; a rescan now shows the result as the window closes.
        if (!win.AnyInstalled || IsDirty) return;
        Rescan();
        ReportVersionMismatches(workshop.Select(w => w.ModId));
    }

    /// <summary>
    /// The Workshop only ever serves an item's latest version, so a freshly downloaded mod can be newer than the one
    /// the list was made with, and a server running the older one will turn the player away. Say so rather than let
    /// "installed" read as "ready".
    /// </summary>
    private void ReportVersionMismatches(IEnumerable<string> ids)
    {
        var mismatches = VersionMismatches(ids);
        if (mismatches.Count == 0) return;
        foreach (var m in mismatches)
            Log(LogCategory.Warning, $"[ModderLords] {m.Id}: the list wants {m.Wanted}, the Workshop gave {m.Installed}");
        MessageBox.Show(
            "These mods downloaded at a different version from the one in the list. The Workshop only serves the latest, "
            + "so the host may need to update too, or send you the version they run:\n\n"
            + string.Join("\n", mismatches.Select(m => $"{m.Id}: list {m.Wanted}, installed {m.Installed}")),
            "Version mismatch", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    /// <summary>Mods in <paramref name="ids"/> installed at a version other than the one the profile recorded.</summary>
    internal List<(string Id, string Wanted, string Installed)> VersionMismatches(IEnumerable<string> ids)
    {
        var found = new List<(string, string, string)>();
        foreach (var id in ids.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var wanted = Profile.Mods.FirstOrDefault(m => m.Id.Equals(id, StringComparison.OrdinalIgnoreCase))?.LastVersion;
            if (string.IsNullOrWhiteSpace(wanted)) continue;
            var installed = Mods.Where(r => !r.IsMissing && r.Id.Equals(id, StringComparison.OrdinalIgnoreCase)).ToList();
            // Any installed copy at the wanted version will do: it is the one the launch can pin.
            if (installed.Count == 0 || installed.Any(r => SaveHeaderReader.VersionsEqual(r.Version, wanted))) continue;
            found.Add((id, wanted, installed[0].Version));
        }
        return found;
    }

    /// <summary>Records where the selected mod can be downloaded, so exported lists can tell importers.</summary>
    [RelayCommand(CanExecute = nameof(CanSetSourceLink))]
    private void SetSourceLink()
    {
        if (SelectedMod is not { IsGameModule: false } row) return;
        CollectProfileFromRows();
        var pm = Profile.Mods.FirstOrDefault(m => m.Id.Equals(row.Id, StringComparison.OrdinalIgnoreCase));
        if (pm is null) return;
        var win = new SourceLinkWindow(row.Id, pm.DownloadUrl ?? ClientManifest.WorkshopUrl(row.Folder)) { Owner = Application.Current.MainWindow };
        if (win.ShowDialog() != true) return;
        pm.DownloadUrl = win.Link.Length == 0 ? null : win.Link;
        IsDirty = true;
        Status = $"{row.Id}: download link {(pm.DownloadUrl is null ? "removed" : "set")}. Save to keep it.";
    }

    private bool CanSetSourceLink() => SelectedMod is { IsGameModule: false };

    /// <summary>Removes every missing row without asking. The command asks first; tests call this directly.</summary>
    internal void RemoveMissingRows()
    {
        var rows = Mods.Where(r => r.IsMissing).ToList();
        if (rows.Count == 0) return;
        RemoveRows(rows);
        Status = $"Removed {rows.Count} missing mod(s) from profile '{Profile.Name}'. Save to keep the change.";
    }

    /// <summary>
    /// Takes rows off the list AND out of the profile. Deleting the row alone is not enough: CollectProfileFromRows
    /// deliberately keeps profile entries that have no row (a shared list's requirements must survive), so the entry
    /// would simply come back on the next scan.
    /// </summary>
    private void RemoveRows(IReadOnlyList<ModRow> rows)
    {
        foreach (var row in rows)
        {
            row.PropertyChanged -= ModRowChanged;
            Mods.Remove(row);
            if (row.IsGameModule)
                Profile.ClientOfficialModules.RemoveAll(id => id.Equals(row.Id, StringComparison.OrdinalIgnoreCase));
            // Another installed copy of the same id keeps the entry; only the last row for an id takes it away.
            else if (!Mods.Any(r => !r.IsGameModule && r.Id.Equals(row.Id, StringComparison.OrdinalIgnoreCase)))
                Profile.Mods.RemoveAll(m => m.Id.Equals(row.Id, StringComparison.OrdinalIgnoreCase));
            foreach (var m in Messages.Where(m => m.StartsWith($"MISSING: {row.Id} ", StringComparison.OrdinalIgnoreCase)).ToList())
                Messages.Remove(m);
        }
        if (rows.Contains(SelectedMod)) SelectedMod = null;
        IsDirty = true;
        NotifyMissingChanged();
        RefreshPreview();
    }

    private void NotifyMissingChanged()
    {
        OnPropertyChanged(nameof(MissingCount));
        RemoveModCommand.NotifyCanExecuteChanged();
        RemoveAllMissingCommand.NotifyCanExecuteChanged();
        SubscribeMissingCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanOpenModFolder))]
    private void OpenModFolder()
    {
        if (SelectedMod is not { IsMissing: false } row || !Directory.Exists(row.Folder)) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(row.Folder) { UseShellExecute = true }); }
        catch (Exception ex) { Status = "Could not open the folder: " + ex.Message; }
    }

    private bool CanOpenModFolder() => SelectedMod is { IsMissing: false };

    /// <summary>Re-asks both buttons whether they are still available. Needed after a move as well as after a
    /// selection change: moving a row to the end of its band is what disables the button you just pressed.</summary>
    public void NotifyMoveability()
    {
        MoveUpCommand.NotifyCanExecuteChanged();
        MoveDownCommand.NotifyCanExecuteChanged();
    }

    private void Move(int delta)
    {
        if (SelectedMod is null) return;
        var i = Mods.IndexOf(SelectedMod);
        var j = i + delta;
        if (i < 0 || j < 0 || j >= Mods.Count) return;
        if (!TryMove(i, j)) return;
        RefreshPreview();
    }

    /// <summary>
    /// The half-open range of rows sharing a band, so a drag can be clamped to it. Bands are contiguous by
    /// construction (Rescan builds them in order and no move can cross one), so a scan outwards is enough.
    /// </summary>
    public (int Start, int End) BandRange(int band)
    {
        var start = 0;
        while (start < Mods.Count && Mods[start].Band < band) start++;
        var end = start;
        while (end < Mods.Count && Mods[end].Band == band) end++;
        return (start, end);
    }

    /// <summary>
    /// Moves a row to an insertion point - the gap BEFORE index <paramref name="insertAt"/> - clamped into its own
    /// band. Clamping rather than refusing is deliberate: dragging past the end of a band parks the row at the end
    /// of the band, which is what the insertion line was showing, instead of silently doing nothing.
    /// </summary>
    /// <summary>
    /// The crashing arrangement, if this profile holds it: a mod that patches Coop placed before Coop. Bound to the
    /// Fix load order button's visibility, and reported in Messages by Rescan.
    /// </summary>
    [ObservableProperty] private string? _coopOrderWarning;

    private void CheckCoopOrder()
    {
        var finding = CoopOrderCheck.Inspect(Mods.Where(r => !r.IsGameModule)
            .Select(r => new ProfileMod { Id = r.Id, Enabled = r.Enabled || r.IsCoopClientMarker }).ToList(),
            CompatDb.Current.ClientFollowsCoop());
        CoopOrderWarning = finding?.Message(Profile.ManualLoadOrder);
        // Runs again once the background scan lands, so each line is said once rather than once per pass.
        void Say(string m) { if (!Messages.Contains(m)) Messages.Add(m); }
        if (CoopOrderWarning is not null) Say(CoopOrderWarning);

        // Mods nothing has placed, whose own submodule binds to Coop. The scan is filled in by the background pass,
        // so rows it has not reached yet simply do not report -- the next Rescan catches them.
        var binders = CoopOrderCheck.UnplacedCoopBinders(
            Mods.Where(r => !r.IsGameModule).Select(r => new ProfileMod { Id = r.Id, Enabled = r.Enabled || r.IsCoopClientMarker }).ToList(),
            Mods.Where(r => !r.IsMissing && r.ScanIfDone is { CoopAssemblyReferences.Count: > 0 }).Select(r => r.Id).ToList(),
            CompatDb.Current.ClientFollowsCoop(),
            id => Mods.FirstOrDefault(r => r.Id == id) is { IsMissing: false } row
                  && LoadOrder.LoadsAfterCoop(row.Module, ClientManifest.CoopClientModuleIds));
        foreach (var id in binders)
            Say($"{id} is listed before Coop and its own submodule references Coop's assemblies, which usually means it "
                       + "must load after Coop. Nothing records that, so it has not been moved — if the game crashes at startup, "
                       + "drag it below Coop and add a compatibility record.");
    }

    /// <summary>
    /// Moves Coop above the mods that patch it. Deliberately a button and not something Rescan does on its own:
    /// the user sees the move in the list and can drag it back before saving.
    /// </summary>
    [RelayCommand]
    private void FixCoopOrder()
    {
        var coop = Mods.FirstOrDefault(r => r.IsCoop);
        if (coop is null) return;
        var follows = CompatDb.Current.ClientFollowsCoop();
        var first = Mods.FirstOrDefault(r => follows.Contains(r.Id, StringComparer.OrdinalIgnoreCase));
        if (first is null || Mods.IndexOf(first) > Mods.IndexOf(coop)) return;
        if (!TryMoveTo(coop, Mods.IndexOf(first))) return;
        IsDirty = true;
        CheckCoopOrder();
        Status = $"Moved {coop.Id} above the mods that patch it — save the profile to keep it";
    }

    public bool TryMoveTo(ModRow row, int insertAt)
    {
        var from = Mods.IndexOf(row);
        if (from < 0 || row.IsGameModule) return false;
        var (start, end) = BandRange(row.Band);
        insertAt = Math.Clamp(insertAt, start, end);
        var to = from < insertAt ? insertAt - 1 : insertAt;
        if (to == from) return false;
        Mods.Move(from, to);
        NotifyMoveability();
        RefreshPreview();
        return true;
    }

    /// <summary>
    /// Moves a row, refusing any move that would take it out of its band. Shared by the buttons and by dragging.
    /// Returns false (having said why) when the move is not one the engine would honour.
    /// </summary>
    public bool TryMove(int from, int to)
    {
        if (from < 0 || from >= Mods.Count || to < 0 || to >= Mods.Count || from == to) return false;
        var row = Mods[from];
        if (row.IsGameModule)
        {
            Status = $"{row.Id} is part of the game; the engine places it, not you.";
            return false;
        }
        if (Mods[to].Band != row.Band)
        {
            Status = row.LoadsBeforeGame
                ? $"{row.Id} is a framework: the game's own modules load after it because its manifest says so, so it stays above them."
                : $"{row.Id} loads after the game's own modules. Only frameworks that ask for it (Harmony, ButterLib, UIExtenderEx, MCM) sit above them.";
            return false;
        }
        Mods.Move(from, to);
        NotifyMoveability();
        return true;
    }

    /// <summary>
    /// Sorts the mod list into the order the engine actually uses. Your own order is only a preference: when it
    /// would load a mod before something it depends on, the whole preference is discarded and the dependency sort
    /// wins — which leaves the list on the left disagreeing with the engine order on the right, with no obvious way
    /// to reconcile them. This is that way.
    /// </summary>
    [RelayCommand]
    private void UseEngineOrder()
    {
        if (_preview is null) RefreshPreview();
        if (_preview is null) { Status = "Nothing to order yet: rescan the mods first."; return; }

        var position = _preview.Order.ModuleIds
            .Select((id, i) => (id, i))
            .ToDictionary(x => x.id, x => x.i, StringComparer.OrdinalIgnoreCase);
        // Band first: a disabled mod is in no engine order at all, so sorting on position alone dropped every
        // disabled row to the bottom - including disabled frameworks, which would then sit below the game.
        var sorted = Mods.OrderBy(m => m.Band)
                         .ThenBy(m => position.TryGetValue(m.Id, out var i) ? i : int.MaxValue)
                         .ThenBy(m => m.Id, StringComparer.OrdinalIgnoreCase).ToList();
        for (var target = 0; target < sorted.Count; target++)
        {
            var from = Mods.IndexOf(sorted[target]);
            if (from != target) Mods.Move(from, target);
        }
        RefreshPreview();
        Status = "Mod list sorted into the engine's load order.";
    }

    /// <summary>
    /// What the next launch would load, for the Mods tab's order preview and the shared mod list. The two modes
    /// prepare through different sessions — the server plan builds an overlay and a save diff, the client plan
    /// builds neither — but both hand back the same module selection and order, which is all this needs.
    /// </summary>
    public sealed record PreviewResult(ModuleCatalog Catalog, LoadOrder.Result Order, ModuleSelectionResult Modules, IReadOnlyList<string> Messages);

    private PreviewResult? _preview;
    private long _previewRevision;
    [RelayCommand]
    private async Task AnalyzeOperations(CancellationToken cancellationToken)
    {
        try
        {
            CollectProfileFromRows();
            var snapshot = ProfileStore.Snapshot(Profile);
            var revision = _previewRevision;
            var request = IsHost ? OperationPreparation.Resolve(snapshot) : CreateClientAnalysisRequest(snapshot);
            Status = "Analyzing selected operations…";
            var report = await Task.Run(() => OperationAnalysisService.Analyze(request, cancellationToken), cancellationToken);
            // Details remain tied to the captured request. Do not annotate a newly selected profile with old results.
            if (Profile.Name == snapshot.Name && revision == _previewRevision)
                foreach (var row in Mods.Where(r => request.Modules.Any(m => m.Id == r.Module.Id && m.Folder == r.Module.FolderPath)))
                {
                    var contracts = report.Plan.Contracts.Where(c => c.Contract.Module == row.Module.Id).ToArray();
                    row.OperationSummary = contracts.Length > 0 ? string.Join(", ", contracts.Select(c => c.Decision).Distinct())
                        : $"{report.Operations.Count(o => o.Module == row.Module.Id)} operations; coverage unverified";
                }
            Status = report.Summary;
            new OperationAnalysisWindow(report) { Owner = Application.Current.MainWindow }.Show();
        }
        catch (OperationCanceledException) { Status = "Analysis cancelled"; }
        catch (Exception ex) { Status = "Operation analysis failed: " + ex.Message; }
    }
    private static ModderLords.Analysis.AnalysisRequest CreateClientAnalysisRequest(Profile profile)
    {
        var prepared = ClientLaunchSession.Prepare(profile);
        return OperationAnalysisService.CreateRequest(profile, prepared.Modules, prepared.GameRoot);
    }

    /// <summary>
    /// Whether this list is the load order or merely a request. Wraps the profile flag so toggling it re-runs the
    /// preview immediately — the whole point is to see the order change.
    /// </summary>
    public bool ManualLoadOrder
    {
        get => Profile.ManualLoadOrder;
        set
        {
            if (Profile.ManualLoadOrder == value) return;
            Profile.ManualLoadOrder = value;
            IsDirty = true;
            OnPropertyChanged();
            Host?.InvalidatePreview();
            RefreshPreview();
        }
    }

    /// <summary>Switching profile changes the flag without anything assigning to it.</summary>
    partial void OnProfileChanged(Profile value)
    {
        OnPropertyChanged(nameof(ManualLoadOrder));
    }

    [RelayCommand]
    public void RefreshPreview()
    {
        _previewRevision++;
        foreach (var row in Mods) row.OperationSummary = "Not analyzed";
        try
        {
            CollectProfileFromRows();
            _preview = IsHost && Host is not null ? Host.PrepareServerPreview() : PrepareClientPreview();
            var p = _preview;
            LoadOrderPreview.Clear();
            foreach (var id in p.Order.ModuleIds) LoadOrderPreview.Add(id);
            SetPreviewMessages(p.Messages.Where(m => m.StartsWith("order:")));
            UpdateShareText();
            if (IsHost) Host?.UpdateSaveDiff();
        }
        catch (Exception ex)
        {
            _preview = null;
            LoadOrderPreview.Clear();
            ClientManifestText = "Cannot prepare this selection: " + ex.Message;
            if (IsHost && Host?.ClientTarget is not null) UpdateShareText();
            SetPreviewMessages(["preview: " + ex.Message]);
        }
    }

    /// <summary>The lines the last preview put in Messages, so the next preview can take them back out.</summary>
    private readonly List<string> _previewLines = new();

    /// <summary>
    /// Replaces the previous preview's lines rather than adding to them. The preview runs on every tick and every
    /// drag, and it used to append each time, so one ordering warning filled the Messages list after a few clicks.
    /// </summary>
    private void SetPreviewMessages(IEnumerable<string> lines)
    {
        foreach (var old in _previewLines) Messages.Remove(old);
        _previewLines.Clear();
        foreach (var line in lines.Distinct())
        {
            if (Messages.Contains(line)) continue;
            Messages.Add(line);
            _previewLines.Add(line);
        }
    }

    private PreviewResult PrepareClientPreview()
    {
        var c = ClientLaunchSession.Prepare(Profile, ScannedCatalog);
        return new PreviewResult(c.Catalog, c.Order, c.Modules, c.Messages);
    }

    /// <summary>Bumped by every Rescan, so a background scan that outlives its rows stops instead of feeding them.</summary>
    private int _scanGeneration;

    /// <summary>
    /// Reads each installed mod's assemblies off the UI thread and hands the result to its row. The Behaviours,
    /// Settings and Server verdict columns show "…" until then; computing them while the grid drew froze the window
    /// on a large mod list.
    /// </summary>
    private void StartBackgroundScan()
    {
        var rows = Mods.Where(r => !r.IsMissing).ToList();
        if (rows.Count == 0) return;
        var generation = _scanGeneration;
        var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        Task.Run(() =>
        {
            foreach (var row in rows)
            {
                if (generation != _scanGeneration) return;
                ModderLords.Core.Compat.ScanResult scan;
                try { scan = ModderLords.Core.Compat.AssemblyScan.Scan(row.Module); }
                catch { continue; }     // a mod removed mid-scan; the next Rescan drops its row anyway
                dispatcher.BeginInvoke(() => { if (generation == _scanGeneration) row.ApplyScan(scan); });
            }
            // Only the enabled rows: a blocked assembly in a mod nobody has ticked is not a problem anyone has.
            var blocked = rows.Where(r => r.Enabled)
                .Select(r => (Row: r, Files: BlockedFiles.Find(r.Module.FolderPath)))
                .Where(x => x.Files.Count > 0)
                .ToList();
            if (generation != _scanGeneration) return;
            dispatcher.BeginInvoke(() =>
            {
                if (generation != _scanGeneration) return;
                ReportBlocked(blocked);
                CheckCoopOrder();   // the coop-binding check reads the scans, so it only has an answer once they are in
            });
        });
    }

    /// <summary>
    /// Says, in the Messages list, which enabled mods Windows has blocked, and arms the button that clears them.
    /// This is worth its own notice rather than a line per mod: a player whose zip was blocked usually has EVERY
    /// mod from it blocked at once, and the symptom they came here with is that none of them do anything.
    /// </summary>
    private void ReportBlocked(IReadOnlyList<(ModRow Row, IReadOnlyList<string> Files)> blocked)
    {
        _blockedFiles = blocked.SelectMany(b => b.Files).ToList();
        BlockedFileCount = _blockedFiles.Count;
        if (blocked.Count == 0) return;
        Messages.Add($"BLOCKED: Windows is blocking {_blockedFiles.Count} file(s) in {blocked.Count} enabled mod(s), so the "
                   + "game loads the mod's folder but none of its code — the usual cause is extracting a download without "
                   + "unblocking the zip first. Use “Unblock them” to clear it, then restart the game.");
        foreach (var b in blocked.OrderBy(b => b.Row.Id, StringComparer.OrdinalIgnoreCase))
            Messages.Add($"  blocked: {b.Row.Id} — {b.Files.Count} file(s)");
    }

    /// <summary>Files the last scan found blocked, kept so the command does not have to walk the disk again.</summary>
    private List<string> _blockedFiles = new();

    [ObservableProperty] private int _blockedFileCount;

    public bool HasBlockedFiles => BlockedFileCount > 0;

    partial void OnBlockedFileCountChanged(int value) => OnPropertyChanged(nameof(HasBlockedFiles));

    /// <summary>Clears the mark-of-the-web the scan found. Nothing is undone by it and nothing needs elevating.</summary>
    [RelayCommand]
    private void UnblockMods()
    {
        var files = _blockedFiles.ToList();
        if (files.Count == 0) return;
        var cleared = BlockedFiles.UnblockAll(files);
        var failed = files.Count - cleared;
        Log(LogCategory.Tool, $"[ModderLords] unblocked {cleared} file(s)"
            + (failed > 0 ? $"; {failed} refused — close the game and the Bannerlord launcher, then try again" : ""));
        if (failed == 0) Messages.Add("Unblocked. Restart Bannerlord for the mods to load.");
        _blockedFiles = files.Where(BlockedFiles.IsBlocked).ToList();
        BlockedFileCount = _blockedFiles.Count;
    }

    internal void UpdateShareText()
    {
        var shared = IsHost ? Host?.ClientTarget : null;
        var modules = shared?.Modules ?? _preview?.Modules;
        if (modules is null) return;
        var entries = ClientManifest.From(modules);
        if (!IsHost) ClientManifestText = ClientManifest.ToPlayerText(entries);
        else
        {
            var coop = modules.Catalog.Modules.FirstOrDefault(m => m.IsStock && m.FolderName == "Coop");
            ClientManifestText = ClientManifest.ToText(entries, coop?.Id ?? "Coop", coop?.Version ?? "");
            if (Host?.IsRunning == true) ClientManifestText = "Running server — " + Host.ClientProfile.Name + "\n" + ClientManifestText;
        }
        if (Host?.IsRunning != true || !IsHost)
        {
            var missing = Mods.Where(m => m.Enabled && m.IsMissing).Select(m => m.Id).ToList();
            if (missing.Count > 0) ClientManifestText = "INCOMPLETE — download these selected mods before launching: " + string.Join(", ", missing) + "\n\n" + ClientManifestText;
        }
    }

    internal ModListFile? CurrentExport()
    {
        RefreshPreview();
        var shared = IsHost ? Host?.ClientTarget : null;
        var modules = shared?.Modules ?? _preview?.Modules;
        return modules is null ? null : ModListFile.From(modules, IsHost ? Host?.ClientProfile ?? Profile : Profile, "ModderLords");
    }

    // ---- export ------------------------------------------------------------------------------------

    [RelayCommand]
    private void CopyManifest()
    {
        RefreshPreview();
        if (_preview is not null || (IsHost && Host?.ClientTarget is not null)) Clipboard.SetText(ClientManifestText);
    }

    /// <summary>Writes the current mod list, versions and load order to a file another player or host can import.</summary>
    [RelayCommand]
    private void ExportList()
    {
        var file = CurrentExport();
        if (file is null) { Status = "Nothing to export yet: rescan the mods first."; return; }
        var dlg = new SaveFileDialog
        {
            Title = "Export this mod list",
            Filter = "Mod list (*.json)|*.json",
            FileName = $"modlist-{ProfileStore.Safe(file.Name ?? Profile.Name)}.json",
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            ModListFile.Write(dlg.FileName, file);
            Status = $"Exported {file.Mods.Count} mods to {dlg.FileName}";
        }
        catch (Exception ex) { Status = ex.Message; }
    }

    /// <summary>
    /// Reads a shared mod list and offers the two things it is good for: setting this PC's Bannerlord launcher up to
    /// match, and creating a server profile that runs the same set in the same order.
    /// </summary>
    [RelayCommand]
    private void ImportList()
    {
        var dlg = new OpenFileDialog { Title = "Import a shared mod list", Filter = "Mod list (*.json)|*.json|All files (*.*)|*.*" };
        if (dlg.ShowDialog() != true) return;
        ModListFile file;
        try { file = ModListFile.Read(dlg.FileName); }
        catch (Exception ex) { Status = ex.Message; Messages.Add("import: " + ex.Message); return; }

        var win = new ImportListWindow(file, dlg.FileName, ProfileStore.List().ToList(), AutoSubscribeWorkshop, AlwaysSubscribeCoop) { Owner = Application.Current.MainWindow };
        if (win.ShowDialog() != true) return;
        AutoSubscribeWorkshop = win.AutoSubscribe;
        AlwaysSubscribeCoop = win.AlwaysSubscribeCoop;

        try
        {
            // Mods the launcher cannot be given yet. Without a profile to hold them they would only be logged and
            // then forgotten, and downloading them would mean importing the file all over again.
            var missing = file.NotInstalled(InstalledClientSide());
            if (win.CreateProfile || (win.ApplyToLauncher && missing.Count > 0))
            {
                var name = win.ProfileNameText.Length > 0 ? win.ProfileNameText : "shared";
                var imported = file.ToProfile(name);
                imported.PendingLauncherApply = win.ApplyToLauncher && missing.Count > 0;
                ProfileStore.Save(imported);
                LoadProfileList();
                SelectedProfileName = imported.Name;
                Log(LogCategory.Tool, $"[ModderLords] imported profile “{imported.Name}” with {file.Mods.Count} mods");
                if (missing.Count > 0)
                    Log(LogCategory.Warning, $"[ModderLords] {missing.Count} mods from the shared list are not installed: {string.Join(", ", missing)}. "
                                           + $"Profile “{imported.Name}” keeps them; once they are downloaded you will be offered the launcher update again.");
            }
            // Before the launcher update, so whatever downloads while the window is open goes into LauncherData.xml too.
            var coop = AlwaysSubscribeCoop && !CoopOnWorkshop();
            if (AutoSubscribeWorkshop && (missing.Count > 0 || coop)) SubscribeToMissing(auto: true, includeCoop: coop);
            else if (coop) SubscribeToMissing(auto: true, onlyCoop: true);
            if (win.ApplyToLauncher) ApplyListToLauncher(file, "the shared file");
            Status = missing.Count > 0 ? $"Import done — {missing.Count} mods still to download." : "Import done.";
        }
        catch (Exception ex) { Status = ex.Message; Log(LogCategory.Error, "[ModderLords] import: " + ex); }
    }

    /// <summary>
    /// Shows what bringing LauncherData.xml in line with <paramref name="file"/> would change and, on confirmation,
    /// does it. Returns whether the launcher list was written.
    /// </summary>
    private bool ApplyListToLauncher(ModListFile file, string what)
    {
        var path = ClientManifest.DefaultLauncherDataPath();
        // A format 2 list knows where Coop loads on a player, so the sync may place it. Format 1 does not.
        var plan = LauncherDataSync.ComputePlan(file.ToClientEntries(), file.ToOrder(), path, InstalledClientSide(),
            file.ClientOfficialModules?.ToHashSet(StringComparer.OrdinalIgnoreCase),
            orderIncludesCoopPosition: file.FormatVersion >= 2);
        if (file.CoopPositionUnknown)
            Log(LogCategory.Warning, "[ModderLords] shared list: written by an older ModderLords and does not record where Coop loads. "
                                   + "Coop has been left where it is; if you use mods that patch Coop, drag it above them.");
        foreach (var b in plan.Blockers) Log(LogCategory.Warning, $"[ModderLords] shared list: {b.Id} — {b.Detail}");
        var confirm = new LauncherSyncWindow(plan, path, LauncherDataSync.DefaultBackupRoot()) { Owner = Application.Current.MainWindow };
        if (confirm.ShowDialog() != true) return false;
        var applied = LauncherDataSync.Apply(plan, path, LauncherDataSync.DefaultBackupRoot());
        if (applied is null) return false;
        Log(LogCategory.Tool, $"[ModderLords] launcher mod list set from {what} (backup: {applied.BackupPath})");
        return true;
    }

    /// <summary>Missing-mod count last seen per pending profile, so the offer is made when it drops, not on every scan.</summary>
    private readonly Dictionary<string, int> _pendingMissingSeen = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// After a scan: if this profile holds an imported list the launcher could not fully take, and some of what was
    /// missing has since been installed, offer to apply it again. Runs after the scan returns, never inside it.
    /// </summary>
    private void OfferPendingLauncherApply()
    {
        if (!Profile.PendingLauncherApply || Application.Current is null) return;
        var file = ModListFile.AsListed(Profile);
        var missing = file.NotInstalled(InstalledClientSide());
        var name = Profile.Name;
        var seen = _pendingMissingSeen.TryGetValue(name, out var s) ? s : (int?)null;
        _pendingMissingSeen[name] = missing.Count;
        if (missing.Count > 0 && (seen is null || missing.Count >= seen)) return;

        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            if (!Profile.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return;
            var text = missing.Count == 0
                ? $"Every mod from the list imported into “{name}” is now installed. Set up the Bannerlord launcher to match?"
                : $"Some mods from the list imported into “{name}” are now installed ({missing.Count} still missing). Update the Bannerlord launcher with what is here so far?";
            if (MessageBox.Show(text, "Imported mod list", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            try
            {
                if (!ApplyListToLauncher(file, $"profile “{name}”") || missing.Count > 0) return;
                // Cleared on the stored copy too, without saving any unsaved edits in the list along with it.
                var stored = ProfileStore.Load(name);
                if (stored is not null) { stored.PendingLauncherApply = false; ProfileStore.Save(stored); }
                Profile.PendingLauncherApply = false;
                _pendingMissingSeen.Remove(name);
                Status = $"Launcher set up from “{name}” — nothing left to download.";
            }
            catch (Exception ex) { Status = ex.Message; Log(LogCategory.Error, "[ModderLords] pending import: " + ex); }
        });
    }

    // ---- watching for downloads -------------------------------------------------------------------

    private readonly List<FileSystemWatcher> _modFolderWatchers = new();
    private System.Windows.Threading.DispatcherTimer? _modFolderDebounce;

    /// <summary>
    /// While the list has mods that are not installed, watch the Workshop content folder and the game's Modules so a
    /// finished download is picked up without anyone pressing Rescan. Stopped as soon as nothing is missing.
    /// </summary>
    private void UpdateModFolderWatch(string? gameRoot)
    {
        foreach (var w in _modFolderWatchers) w.Dispose();
        _modFolderWatchers.Clear();
        if (Application.Current is null || !(Profile.PendingLauncherApply || Mods.Any(r => r.IsMissing && r.Enabled))) return;

        var folders = GamePaths.SteamLibraries()
            .Select(lib => Path.Combine(lib, "steamapps", "workshop", "content", GamePaths.BannerlordAppId.ToString()))
            .Append(gameRoot is null ? "" : Path.Combine(gameRoot, "Modules"))
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in folders)
        {
            try
            {
                var w = new FileSystemWatcher(folder) { NotifyFilter = NotifyFilters.DirectoryName, IncludeSubdirectories = false };
                w.Created += OnModFolderChanged;
                w.Renamed += OnModFolderChanged;
                w.EnableRaisingEvents = true;
                _modFolderWatchers.Add(w);
            }
            catch (Exception ex) { Log(LogCategory.Warning, $"[ModderLords] cannot watch {folder} for downloads: {ex.Message}"); }
        }
    }

    private void OnModFolderChanged(object sender, FileSystemEventArgs e) =>
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            // Steam creates the item folder as the download lands; wait for it to go quiet before scanning.
            _modFolderDebounce ??= new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _modFolderDebounce.Tick -= ModFolderSettled;
            _modFolderDebounce.Tick += ModFolderSettled;
            _modFolderDebounce.Stop();
            _modFolderDebounce.Start();
        });

    private void ModFolderSettled(object? sender, EventArgs e)
    {
        _modFolderDebounce?.Stop();
        // A rescan rebuilds the rows from the saved profile, so it would throw away unsaved ticks and drags.
        if (IsDirty) { Status = "New mods were downloaded — save, then Rescan to pick them up."; return; }
        Rescan();
    }

    /// <summary>
    /// Starts the player's own Bannerlord with exactly the mods this profile enables.
    ///
    /// Player mode uses the editable profile; Host mode uses the running server snapshot when available.
    /// Both launch an explicit module token. Ambiguous copies use a private launch view; invalid selections
    /// report an error instead of silently falling back to an unrelated launcher selection.
    /// </summary>
    [RelayCommand]
    private void LaunchClient()
    {
        var clientLog = BeginClientLaunchLog();
        try
        {
            if (ClientLauncher.IsClientRunning())
            {
                Status = "Bannerlord is already running.";
                return;
            }

            // Host mode joins the server this app is running, so the server's mod list is the one that must be
            // matched; the client plan would happily launch a set the server rejects.
            if (IsHost && Host is not null)
            {
                if (!Host.SyncLauncherData()) return;
                var client = Host.PrepareClientLaunch();
                AppendClientLaunchLog(clientLog, client.Plan.Describe());
                foreach (var message in client.Messages) AppendClientLaunchLog(clientLog, message);
                var process = ClientLaunchSession.Start(client.Plan);
                Status = $"Client started for server profile '{Host.ClientProfile.Name}' (pid {process.Id}).";
                AppendClientLaunchLog(clientLog, $"started pid={process.Id}");
                return;
            }

            // Saved first, as Host mode's Launch does: what you played with is what the profile holds next time.
            CollectProfileFromRows();
            ProfileStore.Save(Profile);
            IsDirty = false;
            var prepared = ClientLaunchSession.Prepare(Profile);

            foreach (var m in prepared.Messages) Log(LogCategory.Tool, "[ModderLords] " + m);
            AppendClientLaunchLog(clientLog, prepared.Plan.Describe());
            foreach (var message in prepared.Messages) AppendClientLaunchLog(clientLog, message);
            var p = ClientLaunchSession.Start(prepared.Plan);
            Status = $"Bannerlord started with {prepared.Order.ModuleIds.Count} modules (pid {p.Id}).";
            Log(LogCategory.Tool, $"[ModderLords] Play: {prepared.Plan.Exe} (pid {p.Id})");
            AppendClientLaunchLog(clientLog, $"started pid={p.Id}");
        }
        catch (Exception ex)
        {
            Status = "Play: " + ex.Message;
            Log(LogCategory.Error, "[ModderLords] Play: " + ex);
            AppendClientLaunchLog(clientLog, "FAILED\n" + ex);
        }
    }

    private string BeginClientLaunchLog()
    {
        var directory = Path.Combine(ProfileStore.RootDir, "logs");
        Directory.CreateDirectory(directory);
        Preflight.RotateLogs(directory, "client-launch-*.log", keep: 20, maxTotalBytes: 64L * 1024 * 1024);
        var path = Path.Combine(directory, $"client-launch-{DateTime.Now:yyyyMMdd-HHmmss-fff}.log");
        AppendClientLaunchLog(path, $"{DateTimeOffset.Now:O} mode={Mode} profile={Profile.Name}");
        return path;
    }

    private static void AppendClientLaunchLog(string path, string text)
    {
        try { File.AppendAllText(path, text + Environment.NewLine); } catch { }
    }

}

public partial class GameplayRow : ObservableObject
{
    public required string Path { get; init; }
    public required System.Text.Json.JsonValueKind Kind { get; init; }
    public required string Original { get; init; }
    public string[]? Choices { get; init; }
    public bool HasChoices => Choices is not null;
    [ObservableProperty] private string _value = "";
    public string Section => Path.Contains('.') ? Path[..Path.IndexOf('.')] : "";
    public string Key => Path.Contains('.') ? Path[(Path.IndexOf('.') + 1)..] : Path;
}

/// <summary>
/// The width of the Share tab's second column: a star in Host mode, zero in Player mode. A collapsed child does
/// not shrink its grid column, so without this the mod list would sit in half a tab with nothing beside it.
/// </summary>
public sealed class HostColumnWidthConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
        value is true ? new System.Windows.GridLength(1, System.Windows.GridUnitType.Star) : new System.Windows.GridLength(0);
    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => throw new NotSupportedException();
}

public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => value is true ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => throw new NotSupportedException();
}
