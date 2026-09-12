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
    public required DiscoveredModule Module { get; init; }
    public bool IsMissing { get; init; }
    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private ServerRole _role;
    /// <summary>Layer 1: behaviours run on the server only; clients skip them (needs the shared module on both sides).</summary>
    [ObservableProperty] private bool _serverAuthoritative;
    /// <summary>Behaviours excluded from gating (kept on clients), edited in the Behaviours window.</summary>
    public List<string> ClientSideBehaviors { get; set; } = new();
    public ModderLords.Core.Compat.ScanResult Scan => _scan ??= ModderLords.Core.Compat.AssemblyScan.Scan(Module);
    public string Behaviors => (_scan ??= ModderLords.Core.Compat.AssemblyScan.Scan(Module)) is { } s
        ? (s.CampaignBehaviors.Count + s.MissionBehaviors.Count == 0 ? "" : $"{s.CampaignBehaviors.Count} campaign, {s.MissionBehaviors.Count} mission")
        : "";
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
            if (IsMissing) return "Not installed. This entry is kept from the profile. Download it and Rescan, or untick it to launch without it.";
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
    public bool CanToggle => !IsLocked;

    /// <summary>Shown in its own column so a game module is never mistaken for a mod you installed.</summary>
    // Deliberately short: the column sits between Module and Version, and "required" is already obvious from the
    // checkbox being greyed out. The tooltip carries the explanation.
    public string Kind => IsMissing ? "Missing" : IsGameModule ? (OfficialModules.IsDlc(Module.Id) ? "DLC" : "Game") : LoadsBeforeGame ? "Framework" : "Mod";

    public string KindTip => LoadsBeforeGame
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
    /// <summary>IL-metadata verdict: server-safe / guarded / needs review. Computed lazily, never executes mod code.</summary>
    public string ServerVerdict => IsMissing ? "not installed" : (_scan ??= ModderLords.Core.Compat.AssemblyScan.Scan(Module)).Summary;
    public string ServerVerdictDetail => _scan is null ? "" : string.Join("\n", _scan.UiAssemblies.Concat(_scan.StoryModeAssemblies).Concat(_scan.GuardedCalls).Concat(_scan.Notes));
    /// <summary>What the Mod settings tab will find for this mod (metadata scan): MCM, its own settings classes, or nothing.</summary>
    public string Settings => Scan.SettingsSummary;
    public string SettingsTip => Scan.SettingsClasses.Count == 0
        ? (Scan.UsesMcm ? "Uses MCM; its settings appear in the Mod settings tab." : "No settings class found by the scan. If the mod does have one, add its type name to the compat record as a settings hint (SettingsTypes in compat-db.local.json).")
        : "Settings classes found (shown in the Mod settings tab once the server has created them):\n" + string.Join("\n", Scan.SettingsClasses) + (Scan.UsesMcm ? "\nPlus MCM settings." : "");

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

    public bool IsHost => Mode == AppMode.Host;

    partial void OnModeChanged(AppMode value)
    {
        // Built once and kept: switching back to Host must not lose the console scrollback or, far worse, orphan a
        // running server. HostViewModel disposes nothing on the way out because nothing about it is per-session.
        if (value == AppMode.Host) Host ??= new HostViewModel(this);
        OnPropertyChanged(nameof(IsHost));
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

    public MainViewModel() : this(true) { }

    internal MainViewModel(bool initialize)
    {
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

    partial void OnSelectedProfileNameChanged(string value)
    {
        if (!string.IsNullOrWhiteSpace(value) && value != Profile.Name) LoadProfile(value);
    }

    public void LoadProfile(string name)
    {
        Profile = ProfileStore.Load(name) ?? new Profile { Name = name };
        SelectedProfileName = Profile.Name;
        Rescan();
        if (IsHost) Host?.OnProfileSelected();
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
        Status = $"Profile '{Profile.Name}' saved";
    }

    [RelayCommand]
    private void NewProfile()
    {
        var n = 1;
        string name;
        do { name = $"profile{n++}"; } while (ProfileNames.Contains(name));
        Profile = new Profile { Name = name, SaveName = Profile.SaveName };
        ProfileStore.Save(Profile);
        LoadProfileList();
        SelectedProfileName = name;
        Rescan();
    }

    [RelayCommand]
    private void DeleteProfile()
    {
        if (MessageBox.Show($"Delete profile '{Profile.Name}'? Junctions it created are removed too.", "Delete profile", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        try { new OverlayApplier().RemoveAll(ProfileStore.OverlayDirFor(Profile.Name)); } catch { }
        ProfileStore.Delete(Profile.Name);
        LoadProfileList();
        LoadProfile(ProfileNames.First());
    }

    /// <summary>The game modules this profile wants switched on, for reconciling the player's launcher list.</summary>
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
        // Host mode hides its stock Coop module. Missing or temporarily hidden entries are requirements,
        // not a request to delete them from the profile.
        ordered.AddRange(Profile.Mods.Where(pm => !ordered.Any(m => m.Id.Equals(pm.Id, StringComparison.OrdinalIgnoreCase))));
        Profile.Mods = ordered;
    }

    // ---- catalog / mods ----------------------------------------------------------------------------

    [RelayCommand]
    public void Rescan()
    {
        Mods.Clear();
        _preview = null;
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
            Messages.Clear();
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
            var byId = catalog.Modules.Where(m => !m.IsStock && !OfficialModules.IsGameModule(m.Id) && !stockIds.Contains(m.Id)
                                                  && (!IsHost || !ClientManifest.CoopClientModuleIds.Contains(m.Id)))
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
                if (!byId.TryGetValue(pm.Id, out var copies))
                {
                    if (IsHost && (stockIds.Contains(pm.Id) || ClientManifest.CoopClientModuleIds.Contains(pm.Id))) continue;
                    Messages.Add($"MISSING: {pm.Id} — download it, then Rescan. {pm.DownloadUrl}");
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

            if (IsHost && Host is not null && paths is not null) Host.RefreshSaves(paths);
            RefreshPreview();
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

    partial void OnSelectedModChanged(ModRow? value) => NotifyMoveability();

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
            OnPropertyChanged();
            Host?.InvalidatePreview();
            RefreshPreview();
        }
    }

    /// <summary>Switching profile changes the flag without anything assigning to it.</summary>
    partial void OnProfileChanged(Profile value) => OnPropertyChanged(nameof(ManualLoadOrder));

    [RelayCommand]
    public void RefreshPreview()
    {
        try
        {
            CollectProfileFromRows();
            _preview = IsHost && Host is not null ? Host.PrepareServerPreview() : PrepareClientPreview();
            var p = _preview;
            LoadOrderPreview.Clear();
            foreach (var id in p.Order.ModuleIds) LoadOrderPreview.Add(id);
            foreach (var m in p.Messages.Where(m => m.StartsWith("order:"))) Messages.Add(m);
            UpdateShareText();
            if (IsHost) Host?.UpdateSaveDiff();
        }
        catch (Exception ex)
        {
            _preview = null;
            LoadOrderPreview.Clear();
            ClientManifestText = "Cannot prepare this selection: " + ex.Message;
            if (IsHost && Host?.ClientTarget is not null) UpdateShareText();
            Messages.Add("preview: " + ex.Message);
        }
    }

    private PreviewResult PrepareClientPreview()
    {
        var c = ClientLaunchSession.Prepare(Profile);
        return new PreviewResult(c.Catalog, c.Order, c.Modules, c.Messages);
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

        var win = new ImportListWindow(file, dlg.FileName, ProfileStore.List().ToList()) { Owner = Application.Current.MainWindow };
        if (win.ShowDialog() != true) return;

        try
        {
            if (win.CreateProfile)
            {
                var imported = file.ToProfile(win.ProfileNameText);
                ProfileStore.Save(imported);
                LoadProfileList();
                SelectedProfileName = imported.Name;
                Log(LogCategory.Tool, $"[ModderLords] imported profile “{imported.Name}” with {file.Mods.Count} mods");
            }
            if (win.ApplyToLauncher)
            {
                var path = ClientManifest.DefaultLauncherDataPath();
                var plan = LauncherDataSync.ComputePlan(file.ToClientEntries(), file.ToOrder(), path, InstalledClientSide(),
                    file.ClientOfficialModules?.ToHashSet(StringComparer.OrdinalIgnoreCase));
                foreach (var b in plan.Blockers) Log(LogCategory.Warning, $"[ModderLords] shared list: {b.Id} — {b.Detail}");
                var confirm = new LauncherSyncWindow(plan, path, LauncherDataSync.DefaultBackupRoot()) { Owner = Application.Current.MainWindow };
                if (confirm.ShowDialog() == true)
                {
                    var applied = LauncherDataSync.Apply(plan, path, LauncherDataSync.DefaultBackupRoot());
                    if (applied is not null)
                        Log(LogCategory.Tool, $"[ModderLords] launcher mod list set from the shared file (backup: {applied.BackupPath})");
                }
            }
            Status = "Import done.";
        }
        catch (Exception ex) { Status = ex.Message; Log(LogCategory.Error, "[ModderLords] import: " + ex); }
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
                var process = ClientLaunchSession.Start(client.Plan);
                Status = $"Client started for server profile '{Host.ClientProfile.Name}' (pid {process.Id}).";
                return;
            }

            CollectProfileFromRows();
            var prepared = ClientLaunchSession.Prepare(Profile);

            foreach (var m in prepared.Messages) Log(LogCategory.Tool, "[ModderLords] " + m);
            var p = ClientLaunchSession.Start(prepared.Plan);
            Status = $"Bannerlord started with {prepared.Order.ModuleIds.Count} modules (pid {p.Id}).";
            Log(LogCategory.Tool, $"[ModderLords] Play: {prepared.Plan.Exe} (pid {p.Id})");
        }
        catch (Exception ex)
        {
            Status = "Play: " + ex.Message;
            Log(LogCategory.Error, "[ModderLords] Play: " + ex);
        }
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
