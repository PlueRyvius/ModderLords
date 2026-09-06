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

    /// <summary>The game will not start without Native, SandBoxCore or SandBox, so their checkbox is read-only.</summary>
    public bool IsLocked => OfficialModules.IsRequired(Module.Id);
    public bool CanToggle => !IsLocked;

    /// <summary>Shown in its own column so a game module is never mistaken for a mod you installed.</summary>
    // Deliberately short: the column sits between Module and Version, and "required" is already obvious from the
    // checkbox being greyed out. The tooltip carries the explanation.
    public string Kind => !IsGameModule ? "Mod" : OfficialModules.IsDlc(Module.Id) ? "DLC" : "Game";

    public string KindTip => !IsGameModule ? "A mod. Enable it and drag it to place it in the load order."
        : IsLocked ? "Part of the base game. It cannot be turned off - the game will not start without it."
        : OfficialModules.IsDlc(Module.Id) ? "A paid expansion. Coop refuses to let a client join with a DLC enabled, so leave it off for coop sessions."
        : HostMode
            ? "Part of the base game, and safe to turn off. Coop does not work with Birth and Aging or Fast Mode enabled."
            : "Part of the base game, and safe to turn off.";

    /// <summary>Whether the row is being shown in Host mode. Only the wording depends on it: a player should not be
    /// told which game modules break coop.</summary>
    public bool HostMode { get; init; }

    public string Source => Module.Source.ToString();
    public string Folder => Module.FolderPath;
    public string Bins => (Module.HasServerBin ? "server" : "") + (Module.HasServerBin && Module.HasClientBin ? " + " : "") + (Module.HasClientBin ? "client" : "");
    public string Notes => string.Join(", ", new[]
    {
        Module.HasHeadlessExclusions ? "client-only tags" : null,
        Module.HasCode ? null : "data only",
        Module.HasServerBin ? null : "no server bin",
    }.Where(s => s is not null));
    public static ServerRole[] Roles { get; } = [ServerRole.Run, ServerRole.DependencyOnly, ServerRole.AsShipped];

    private ModderLords.Core.Compat.ScanResult? _scan;
    /// <summary>IL-metadata verdict: server-safe / guarded / needs review. Computed lazily, never executes mod code.</summary>
    public string ServerVerdict => (_scan ??= ModderLords.Core.Compat.AssemblyScan.Scan(Module)).Summary;
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
    private static IEnumerable<string> LegacyModuleNotice(string? gameRoot)
    {
        if (string.IsNullOrWhiteSpace(gameRoot)) yield break;
        foreach (var id in new[] { "ModularCoop.Compat", "DedicatedServer.ModularCoopCompat" })
        {
            var dir = System.IO.Path.Combine(gameRoot, "Modules", id);
            if (System.IO.Directory.Exists(dir))
                yield return $"left over from the old name: the module folder {id} is no longer used and can be deleted ({dir})";
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
        Host?.OnProfileSelected();
    }

    /// <summary>
    /// Somewhere to put a log line in either mode. Host mode has the console; Player mode has the Messages list on
    /// the Mods tab, which is the only log surface a player is shown.
    /// </summary>
    public void Log(LogCategory category, string text)
    {
        if (Host is not null) Host.AddLine(category, text);
        else if (category is LogCategory.Warning or LogCategory.Error or LogCategory.Tool) Messages.Add(text);
    }

    public MainViewModel()
    {
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
            Host?.OnProfileSelected();
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
        Host?.OnProfileSelected();
    }

    [RelayCommand]
    private void SaveProfile()
    {
        CollectProfileFromRows();
        ProfileStore.Save(Profile);
        LoadProfileList();
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

    internal void CollectProfileFromRows()
    {
        // Game modules are a separate list on the profile: they have no role, no source path and no place in the
        // mod order, and writing them into Profile.Mods would make every exported mod list mention Native.
        var officialRows = Mods.Where(r => r.IsGameModule).ToList();
        if (officialRows.Count > 0)
            Profile.ClientOfficialModules = officialRows.Where(r => r.Enabled || r.IsLocked).Select(r => r.Id).ToList();

        var byId = Profile.Mods.ToDictionary(m => m.Id, StringComparer.OrdinalIgnoreCase);
        var ordered = new List<ProfileMod>();
        foreach (var row in Mods)
        {
            if (row.IsGameModule) continue;
            if (!byId.TryGetValue(row.Id, out var pm)) pm = new ProfileMod { Id = row.Id };
            pm.Enabled = row.Enabled;
            pm.Role = row.Role;
            pm.ServerAuthoritative = row.ServerAuthoritative;
            pm.ClientSideBehaviors = row.ClientSideBehaviors.ToList();
            pm.SourcePath = row.Folder;
            ordered.Add(pm);
        }
        Profile.Mods = ordered;
    }

    // ---- catalog / mods ----------------------------------------------------------------------------

    [RelayCommand]
    public void Rescan()
    {
        try
        {
            // Player mode has no dedicated server to resolve, and asking for one throws when it is not installed —
            // which is the normal case for someone using this purely as a mod loader.
            ServerPaths? paths = null;
            ModuleCatalog catalog;
            string? gameRoot;
            if (Host is not null)
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
            foreach (var m in LegacyModuleNotice(gameRoot)) Messages.Add(m);
            CoopVersion = catalog.Modules.FirstOrDefault(m => m.IsStock && m.FolderName == "Coop")?.Version;

            // One row per module id (best copy), profile order first, then the rest alphabetically.
            // Coop itself (any build id) and the stock modules are never user-selectable.
            var stockIds = new HashSet<string>(catalog.Modules.Where(m => m.IsStock).Select(m => m.Id), StringComparer.OrdinalIgnoreCase);
            var best = catalog.Modules.Where(m => !m.IsStock && !OfficialModules.IsGameModule(m.Id) && !stockIds.Contains(m.Id)
                                                  && !m.Id.Equals("Coop", StringComparison.OrdinalIgnoreCase) && !m.Id.Equals("CoopNightly", StringComparison.OrdinalIgnoreCase))
                .GroupBy(m => m.Id, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g =>
                {
                    var pinned = Profile.Mods.FirstOrDefault(pm => pm.Id.Equals(g.Key, StringComparison.OrdinalIgnoreCase))?.SourcePath;
                    return g.OrderByDescending(m => pinned is not null && Junction.PathsEqual(m.FolderPath, pinned))
                            .ThenByDescending(m => m.FolderName.Equals(g.Key, StringComparison.OrdinalIgnoreCase))
                            .ThenByDescending(m => m.Version).First();
                }, StringComparer.OrdinalIgnoreCase);

            Mods.Clear();

            // The game's own modules come first and are shown as what they are. They were invisible before, which
            // meant a coop host had no way to turn off Birth and Aging or Fast Mode - both of which break coop -
            // without leaving the launcher for the TaleWorlds one.
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
                Mods.Add(new ModRow { Module = m, Enabled = on, Role = ServerRole.AsShipped, HostMode = Host is not null });
            }

            foreach (var pm in Profile.Mods)
                if (best.TryGetValue(pm.Id, out var m)) { Mods.Add(new ModRow { Module = m, Enabled = pm.Enabled, Role = pm.Role, ServerAuthoritative = pm.ServerAuthoritative, ClientSideBehaviors = pm.ClientSideBehaviors.ToList() }); best.Remove(pm.Id); }
                else Messages.Add($"{pm.Id}: in the profile but not installed anywhere");
            // Mods new to this profile take their defaults from the compat database; existing entries are never rewritten.
            foreach (var m in best.Values.OrderBy(m => m.Id))
            {
                var rec = db.Find(m.Id);
                Mods.Add(new ModRow
                {
                    Module = m, Enabled = false, Role = db.DefaultRoleFor(m.Id),
                    ServerAuthoritative = rec?.ServerAuthoritative ?? false,
                    ClientSideBehaviors = rec?.ClientSideBehaviors.ToList() ?? new List<string>(),
                });
            }
            foreach (var row in Mods) row.Compat = db.For(row.Id, row.Version);

            if (Host is not null && paths is not null) Host.RefreshSaves(paths);
            RefreshPreview();
            Host?.RefreshDrift();
            Host?.LoadGameplay();
            var modRows = Mods.Where(r => !r.IsGameModule).ToList();
            Status = $"{modRows.Count(r => r.Enabled)} of {modRows.Count} mods enabled, "
                   + $"{Mods.Count(r => r.IsGameModule && r.Enabled)} game modules";
        }
        catch (Exception ex)
        {
            Status = ex.Message;
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

    [RelayCommand]
    private void MoveUp() => Move(-1);

    [RelayCommand]
    private void MoveDown() => Move(1);

    private void Move(int delta)
    {
        if (SelectedMod is null) return;
        if (SelectedMod.IsGameModule) { Status = $"{SelectedMod.Id} is part of the game; the engine places it, not you."; return; }
        var i = Mods.IndexOf(SelectedMod);
        var j = i + delta;
        if (i < 0 || j < 0 || j >= Mods.Count) return;
        if (Mods[j].IsGameModule) { Status = "Mods load after the game's own modules."; return; }
        Mods.Move(i, j);
        RefreshPreview();
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
        var sorted = Mods.OrderBy(m => position.TryGetValue(m.Id, out var i) ? i : int.MaxValue).ToList();
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

    [RelayCommand]
    public void RefreshPreview()
    {
        try
        {
            CollectProfileFromRows();
            _preview = Host is not null ? Host.PrepareServerPreview() : PrepareClientPreview();
            var p = _preview;
            LoadOrderPreview.Clear();
            foreach (var id in p.Order.ModuleIds) LoadOrderPreview.Add(id);
            foreach (var m in p.Messages.Where(m => m.StartsWith("order:"))) Messages.Add(m);
            var entries = ClientManifest.From(p.Modules);
            if (Host is null)
            {
                ClientManifestText = ClientManifest.ToPlayerText(entries);
            }
            else
            {
                var coop = p.Catalog.Modules.FirstOrDefault(m => m.IsStock && m.FolderName == "Coop");
                ClientManifestText = ClientManifest.ToText(entries, coop?.Id ?? "Coop", coop?.Version ?? "");
            }
            OnPropertyChanged(nameof(ClientManifestText));
            Host?.UpdateSaveDiff();
        }
        catch (Exception ex) { Messages.Add("preview: " + ex.Message); }
    }

    private PreviewResult PrepareClientPreview()
    {
        var c = ClientLaunchSession.Prepare(Profile);
        return new PreviewResult(c.Catalog, c.Order, c.Modules, c.Messages);
    }

    // ---- export ------------------------------------------------------------------------------------

    [RelayCommand]
    private void CopyManifest() => Clipboard.SetText(ClientManifestText);

    /// <summary>Writes the current mod list, versions and load order to a file another player or host can import.</summary>
    [RelayCommand]
    private void ExportList()
    {
        if (_preview is null) RefreshPreview();
        if (_preview is null) { Status = "Nothing to export yet: rescan the mods first."; return; }
        var dlg = new SaveFileDialog
        {
            Title = "Export this mod list",
            Filter = "Mod list (*.json)|*.json",
            FileName = $"modlist-{ProfileStore.Safe(Profile.Name)}.json",
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var file = ModListFile.From(_preview.Modules, Profile, "ModderLords");
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
                var plan = LauncherDataSync.ComputePlan(file.ToClientEntries(), file.ToOrder(), path, InstalledClientSide());   // an imported list says nothing about game modules
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
    /// The module token on the command line completely replaces LauncherData.xml, and the client resolves Workshop
    /// ids unaided, so the normal path touches nothing on disk: no overlay, no compat module, no rewritten mod list.
    /// That is <see cref="ClientLaunchSession"/>.
    ///
    /// The old route through <see cref="ClientLauncher"/> — start the exe bare and make LauncherData.xml match the
    /// SERVER's plan first — is kept as a fallback for Host mode, where the point is to join the server you are
    /// running and the server's plan is the authority. It is also what runs if the client plan cannot be built.
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
            if (Host is not null)
            {
                LaunchClientViaLauncherData();
                return;
            }

            CollectProfileFromRows();
            ClientLaunchSession.Prepared prepared;
            try { prepared = ClientLaunchSession.Prepare(Profile); }
            catch (Exception ex)
            {
                Log(LogCategory.Warning, "[ModderLords] Play: " + ex.Message + " — falling back to starting the game as it is configured");
                LaunchClientViaLauncherData();
                return;
            }

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

    /// <summary>The pre-0.9 route: start the exe with no arguments, having first written the mod list the game will
    /// read. Only reachable in Host mode or when the client plan could not be built.</summary>
    private void LaunchClientViaLauncherData()
    {
        var gameRoot = ClientLauncher.ResolveGameRoot(Profile);
        var exe = ClientLauncher.FindExe(gameRoot);
        if (exe is null)
        {
            Status = gameRoot is null
                ? "Game install not found. Set the game folder in the profile."
                : $"No client exe under {ClientLauncher.ClientBin(gameRoot)}.";
            Log(LogCategory.Error, "[ModderLords] Launch client: " + Status);
            return;
        }
        if (Host is not null && !Host.SyncLauncherData()) return;   // cancelled at the confirmation
        var p = ClientLauncher.Start(exe);
        Status = $"Client started (pid {p.Id}).";
        Log(LogCategory.Tool, $"[ModderLords] Launch client: started {exe} (pid {p.Id})");
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
