using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using ModularCoop.Core.Compat;
using ModularCoop.Core.Export;
using ModularCoop.Core.Launch;
using ModularCoop.Core.Live;
using ModularCoop.Core.Logs;
using ModularCoop.Core.Modules;
using ModularCoop.Core.Overlay;
using ModularCoop.Core.Profiles;
using ModularCoop.Core.Saves;

namespace ModularCoop.App.ViewModels;

public partial class ModRow : ObservableObject
{
    public required DiscoveredModule Module { get; init; }
    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private ServerRole _role;
    /// <summary>Layer 1: behaviours run on the server only; clients skip them (needs the shared module on both sides).</summary>
    [ObservableProperty] private bool _serverAuthoritative;
    /// <summary>Behaviours excluded from gating (kept on clients), edited in the Behaviours window.</summary>
    public List<string> ClientSideBehaviors { get; set; } = new();
    public ModularCoop.Core.Compat.ScanResult Scan => _scan ??= ModularCoop.Core.Compat.AssemblyScan.Scan(Module);
    public string Behaviors => (_scan ??= ModularCoop.Core.Compat.AssemblyScan.Scan(Module)) is { } s
        ? (s.CampaignBehaviors.Count + s.MissionBehaviors.Count == 0 ? "" : $"{s.CampaignBehaviors.Count} campaign, {s.MissionBehaviors.Count} mission")
        : "";
    public string Id => Module.Id;
    public string Version => Module.Version;
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

    private ModularCoop.Core.Compat.ScanResult? _scan;
    /// <summary>IL-metadata verdict: server-safe / guarded / needs review. Computed lazily, never executes mod code.</summary>
    public string ServerVerdict => (_scan ??= ModularCoop.Core.Compat.AssemblyScan.Scan(Module)).Summary;
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
    private EngineProcess? _engine;
    /// <summary>
    /// Lines waiting to reach the console, capped. It used to be an unbounded queue draining at only
    /// 1500 lines per 250 ms tick, so any engine talking faster than 6000 lines/second grew it forever:
    /// one session with a Trace switch on reached 19.8 GB of working set and drove the machine into
    /// paging. The console only ever shows the newest lines, so discarding the oldest waiting ones is
    /// right — but never silently, hence the drop count reported in FlushConsole.
    /// </summary>
    private readonly BoundedQueue<ConsoleLine> _pending = new(100_000);
    private readonly System.Windows.Threading.DispatcherTimer _flushTimer;
    /// <summary>Raised on the UI thread after a batch of console lines was added (the view scrolls once per batch).</summary>
    public event Action? ConsoleFlushed;
    private LaunchSession.Prepared? _prepared;
    /// <summary>
    /// Until when engine output is attributed to the last command the host sent. The engine offers no request/reply
    /// protocol on stdin, so this is a time window and nothing more: lines that arrive inside it and are not already
    /// errors, warnings or milestones get tagged CommandReply. Lines the server would have printed anyway can land
    /// in the window; the tagging is a reading aid, not a guarantee.
    /// </summary>
    private DateTime _replyWindowEnds = DateTime.MinValue;
    private string _lastCommand = "";
    private int _repliesSeen;
    private static readonly TimeSpan ReplyWindow = TimeSpan.FromSeconds(3);
    private CappedLogWriter? _launchLog;
    private long _totalDropped;

    public ObservableCollection<string> ProfileNames { get; } = new();
    public ObservableCollection<ModRow> Mods { get; } = new();
    public ObservableCollection<SaveRow> Saves { get; } = new();
    public BulkObservableCollection<ConsoleLine> Console { get; } = new();
    public ObservableCollection<string> Messages { get; } = new();
    public ObservableCollection<string> LoadOrderPreview { get; } = new();
    public ICollectionView ConsoleView { get; }
    /// <summary>Mod settings tab: the running server's MCM settings, edited live through the sync module.</summary>
    public LiveSettingsViewModel LiveSettings { get; } = new();

    [ObservableProperty] private Profile _profile = new();
    [ObservableProperty] private string _selectedProfileName = "default";
    [ObservableProperty] private string _status = "Idle";
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string _serverRoot = "";
    [ObservableProperty] private string _gameRoot = "";
    [ObservableProperty] private string _commandText = "";
    [ObservableProperty] private string _consoleFilter = "";
    [ObservableProperty] private bool _showEngine;
    [ObservableProperty] private bool _showModuleLoad = true;
    [ObservableProperty] private bool _showServer = true;
    [ObservableProperty] private bool _showCoop = true;
    [ObservableProperty] private bool _showWarnings = true;
    [ObservableProperty] private bool _errorsOnly;
    [ObservableProperty] private bool _autoScroll = true;
    [ObservableProperty] private bool _showProbes;
    /// <summary>Console tab: show the commands the host typed and whatever the server said back.</summary>
    [ObservableProperty] private bool _showConsoleIo = true;
    /// <summary>One-line feedback under the command box: what was sent, and whether anything came back.</summary>
    [ObservableProperty] private string _commandStatus = "";
    [ObservableProperty] private SaveRow? _selectedSave;
    [ObservableProperty] private string _saveDiff = "";
    [ObservableProperty] private string _clientManifestText = "";
    [ObservableProperty] private string _clientCheckText = "";
    [ObservableProperty] private ModRow? _selectedMod;

    public string[] Regions { get; } = ["EU", "NA", "SA", "AS", "OC", "AF"];
    public ServerVisibility[] Visibilities { get; } = [ServerVisibility.Public, ServerVisibility.FriendsOnly, ServerVisibility.None];

    public MainViewModel()
    {
        ConsoleView = CollectionViewSource.GetDefaultView(Console);
        ConsoleView.Filter = FilterLine;
        _flushTimer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
        _flushTimer.Tick += (_, _) => FlushConsole();
        _flushTimer.Start();
        LoadProfileList();
        LoadProfile(ProfileNames.FirstOrDefault() ?? "default");
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
        LiveSettings.Log ??= s => Application.Current.Dispatcher.BeginInvoke(() => AddLine(LogCategory.Tool, "[ModularCoop] " + s));
        try { LiveSettings.OnProfileSelected(Profile.Name); }
        catch (Exception ex) { Messages.Add("mod settings: " + ex.Message); }
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

    private void CollectProfileFromRows()
    {
        var byId = Profile.Mods.ToDictionary(m => m.Id, StringComparer.OrdinalIgnoreCase);
        var ordered = new List<ProfileMod>();
        foreach (var row in Mods)
        {
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
            var paths = LaunchSession.ResolvePaths(Profile);
            ServerRoot = paths.DedicatedServerRoot;
            var catalog = LaunchSession.Scan(Profile, paths, out var gameRoot);
            GameRoot = gameRoot ?? "(game install not found)";
            Messages.Clear();
            foreach (var p in catalog.Problems) Messages.Add("catalog: " + p);
            var db = CompatDb.Reload();
            foreach (var p in db.Problems) Messages.Add("compat db: " + p);
            CoopVersion = catalog.Modules.FirstOrDefault(m => m.IsStock && m.FolderName == "Coop")?.Version;

            // One row per module id (best copy), profile order first, then the rest alphabetically.
            // Coop itself (any build id) and the stock modules are never user-selectable.
            var stockIds = new HashSet<string>(catalog.Modules.Where(m => m.IsStock).Select(m => m.Id), StringComparer.OrdinalIgnoreCase);
            var best = catalog.Modules.Where(m => !m.IsStock && !m.IsOfficial && !stockIds.Contains(m.Id)
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

            RefreshSaves(paths);
            RefreshPreview();
            RefreshDrift();
            LoadGameplay();
            Status = $"{Mods.Count(r => r.Enabled)} of {Mods.Count} mods enabled";
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
        var i = Mods.IndexOf(SelectedMod);
        var j = i + delta;
        if (i < 0 || j < 0 || j >= Mods.Count) return;
        Mods.Move(i, j);
        RefreshPreview();
    }

    [RelayCommand]
    public void RefreshPreview()
    {
        try
        {
            CollectProfileFromRows();
            var p = LaunchSession.Prepare(Profile, applySideEffects: false);
            _prepared = p;
            LoadOrderPreview.Clear();
            foreach (var id in p.Order.ModuleIds) LoadOrderPreview.Add(id);
            foreach (var m in p.Messages.Where(m => m.StartsWith("order:"))) Messages.Add(m);
            var entries = ClientManifest.From(p);
            var coop = p.Catalog.Modules.FirstOrDefault(m => m.IsStock && m.FolderName == "Coop");
            ClientManifestText = ClientManifest.ToText(entries, coop?.Id ?? "Coop", coop?.Version ?? "");
            UpdateSaveDiff();
        }
        catch (Exception ex) { Messages.Add("preview: " + ex.Message); }
    }

    // ---- saves ----------------------------------------------------------------------------------------

    private void RefreshSaves(ServerPaths paths)
    {
        Saves.Clear();
        foreach (var h in SaveHeaderReader.ReadAll(paths.SavesDir)) Saves.Add(new SaveRow { Header = h });
        SelectedSave = Saves.FirstOrDefault(s => s.Name.Equals(Profile.SaveName, StringComparison.OrdinalIgnoreCase));
    }

    partial void OnSelectedSaveChanged(SaveRow? value)
    {
        if (value is not null) Profile.SaveName = value.Name;
        OnPropertyChanged(nameof(Profile));
        UpdateSaveDiff();
    }

    private void UpdateSaveDiff()
    {
        if (SelectedSave is null || _prepared is null) { SaveDiff = SelectedSave is null ? "Pick a save, or type a new name to start a fresh world." : ""; return; }
        var diffs = SaveHeaderReader.Compare(SelectedSave.Header, LaunchSession.PlannedCommunityVersions(_prepared));
        SaveDiff = diffs.Count == 0
            ? "Save and profile agree on every community module."
            : "The engine will load this save anyway, with a warning:\n" + string.Join("\n", diffs.Select(d => $"  {d.ModuleId}: {d.Kind} (save {d.SaveVersion ?? "-"}, now {d.CurrentVersion ?? "-"})"));
    }

    // ---- drift / resync / gameplay --------------------------------------------------------------------

    [ObservableProperty] private string _driftText = "";
    public ObservableCollection<GameplayRow> Gameplay { get; } = new();

    public void RefreshDrift()
    {
        try
        {
            var paths = LaunchSession.ResolvePaths(Profile);
            var catalog = LaunchSession.Scan(Profile, paths, out _);
            var drift = LaunchSession.DetectDrift(Profile, catalog);
            DriftText = drift.Count == 0 ? "" :
                "Mod versions changed since the last launch: " + string.Join(", ", drift.Select(d => $"{d.ModuleId} {d.LastVersion} → {d.CurrentVersion}"))
                + ". Players must update to match" + (IsRunning ? "; restart the server to pick them up." : ".");
        }
        catch { DriftText = ""; }
    }

    [RelayCommand]
    private void Resync()
    {
        try
        {
            CollectProfileFromRows();
            var r = LaunchSession.Resync(Profile);
            Status = $"Re-synced {r.Applied.Count} junction(s), removed {r.Removed.Count}" + (r.Warnings.Count > 0 ? $", {r.Warnings.Count} warning(s)" : "");
            foreach (var w in r.Warnings) Messages.Add("WARNING " + w);
            if (IsRunning) Status += " (running server keeps the old files until restart)";
        }
        catch (Exception ex) { Status = "Re-sync failed: " + ex.Message; }
    }

    [RelayCommand]
    public void LoadGameplay()
    {
        try
        {
            var paths = LaunchSession.ResolvePaths(Profile);
            Gameplay.Clear();
            foreach (var s in Core.Config.ModConfig.Read(paths))
                Gameplay.Add(new GameplayRow { Path = s.Path, Kind = s.Kind, Original = s.RawValue, Value = s.Display,
                    Choices = Core.Config.ModConfig.Choices.TryGetValue(s.Path, out var c) ? c : (s.Kind is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False ? ["true", "false"] : null) });
        }
        catch (Exception ex) { Status = "mod-config.json: " + ex.Message; }
    }

    [RelayCommand]
    private void SaveGameplay()
    {
        try
        {
            var paths = LaunchSession.ResolvePaths(Profile);
            var changes = new List<(string, string)>();
            foreach (var row in Gameplay)
            {
                var raw = Core.Config.ModConfig.Encode(row.Kind, row.Value);
                if (raw != row.Original) changes.Add((row.Path, raw));
            }
            var n = Core.Config.ModConfig.Apply(paths, changes);
            Status = n == 0 ? "Gameplay settings unchanged" : $"Saved {n} gameplay setting(s) to mod-config.json" + (IsRunning ? " (applies on next server start)" : "");
            LoadGameplay();
        }
        catch (Exception ex) { Status = "mod-config.json: " + ex.Message; }
    }

    // ---- export ------------------------------------------------------------------------------------

    [RelayCommand]
    private void CopyManifest() => Clipboard.SetText(ClientManifestText);

    [RelayCommand]
    private void CheckMyClient()
    {
        if (_prepared is null) RefreshPreview();
        if (_prepared is null) return;
        var checks = ClientManifest.CompareWithLauncherData(ClientManifest.From(_prepared), ClientManifest.DefaultLauncherDataPath());
        ClientCheckText = checks.Count == 0 ? "No community modules to compare." :
            string.Join("\n", checks.Select(c => $"{(c.Verdict == "ok" ? "  ok " : "  !! ")}{c.Id,-30} server {c.ServerVersion ?? "-",-12} client {c.ClientVersion ?? "-",-12} {c.Verdict}"));
    }

    // ---- launch ------------------------------------------------------------------------------------

    [RelayCommand(CanExecute = nameof(CanLaunch))]
    private async Task Launch()
    {
        CollectProfileFromRows();
        ProfileStore.Save(Profile);
        Console.Clear();
        Status = "Checking…";
        try
        {
            var pre = await Task.Run(() =>
            {
                var paths = LaunchSession.ResolvePaths(Profile);
                return Preflight.Run(paths, Profile.Server.JoinPort, Profile.Server.EnginePort, Profile.EnabledMods.Any());
            });
            foreach (var p in pre) AddLine(p.Blocking ? LogCategory.Error : LogCategory.Warning, "[ModularCoop] " + p.Message);
            if (pre.Any(p => p.Blocking))
            {
                Status = "Not launched: " + pre.First(p => p.Blocking).Message;
                IsRunning = false;
                return;
            }

            Status = "Preparing…";
            var prepared = await Task.Run(() => LaunchSession.Prepare(Profile));
            _prepared = prepared;
            ProfileStore.Save(Profile); // LastVersion updated by Prepare
            DriftText = "";
            foreach (var m in prepared.Messages) AddLine(LogCategory.Tool, "[ModularCoop] " + m);
            foreach (var l in prepared.Plan.Describe().Split('\n', StringSplitOptions.RemoveEmptyEntries)) AddLine(LogCategory.Tool, "[ModularCoop] " + l.TrimEnd());

            var logDir = Path.Combine(ProfileStore.RootDir, "logs");
            Directory.CreateDirectory(logDir);
            var rotated = Preflight.RotateLogs(logDir, "launch-*.log", keep: 20);
            if (rotated > 0) AddLine(LogCategory.Tool, $"[ModularCoop] removed {rotated} old launch log(s)");
            _launchLog = new CappedLogWriter(Path.Combine(logDir, $"launch-{DateTime.Now:yyyyMMdd-HHmmss}.log"));
            _pending.Clear();
            _totalDropped = 0;

            _engine = EngineProcess.Start(prepared.Plan);
            IsRunning = true;
            Status = $"Engine pid {_engine.ProcessId}, loading…";
            LiveSettings.Log ??= s => Application.Current.Dispatcher.BeginInvoke(() => AddLine(LogCategory.Tool, "[ModularCoop] " + s));
            LiveSettings.OnLaunched(prepared.Plan.ExtraEnvironment.TryGetValue(LiveProtocol.EnvVar, out var liveDir) ? liveDir : null, Profile.SettingsSync, Profile.Name);
            _engine.LineReceived += line =>
            {
                var c = LogClassifier.Classify(line.Text);
                c = AttributeToCommand(c);
                _launchLog?.WriteLine($"{line.At:HH:mm:ss.fff} {c.Category,-10} {line.Text}");
                // Never touch the UI per line: the engine prints thousands during load. Queue and flush on a timer.
                // Bounded: if the engine outruns the flush timer the oldest waiting lines are dropped and
                // counted, rather than the queue growing without limit.
                _pending.Enqueue(new ConsoleLine(line.At.ToString("HH:mm:ss"), c.Category, line.Text));
                if (c.Category == LogCategory.Milestone && line.Text.Contains("SERVING"))
                    Application.Current.Dispatcher.BeginInvoke(() => Status = "SERVING, waiting for clients");
            };
            var code = await _engine.Exited;
            IsRunning = false;
            Status = $"Engine exited with {code}: {ExitCodeExplainer.Explain(code)}";
            AddLine(LogCategory.Milestone, "[ModularCoop] " + Status);
            _launchLog?.Dispose(); _launchLog = null;
            _engine.Dispose(); _engine = null;
        }
        catch (Exception ex)
        {
            IsRunning = false;
            Status = ex.Message;
            AddLine(LogCategory.Error, "[ModularCoop] " + ex);
        }
        LaunchCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
    }

    private bool CanLaunch() => !IsRunning;

    /// <summary>
    /// Starts the Bannerlord client from the game install, so a host running the server on this machine can
    /// still get into the game: Steam refuses its own Play button while the coop server is logged on as a
    /// Steam game server, but the exe started directly is signed in by Steam as usual.
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
            var gameRoot = ClientLauncher.ResolveGameRoot(Profile);
            var exe = ClientLauncher.FindExe(gameRoot);
            if (exe is null)
            {
                Status = gameRoot is null
                    ? "Game install not found. Set the game folder in the profile."
                    : $"No client exe under {ClientLauncher.ClientBin(gameRoot)}.";
                AddLine(LogCategory.Error, "[ModularCoop] Launch client: " + Status);
                return;
            }
            if (!SyncLauncherData()) return;   // cancelled at the confirmation
            var p = ClientLauncher.Start(exe);
            Status = $"Client started (pid {p.Id}).";
            AddLine(LogCategory.Tool, $"[ModularCoop] Launch client: started {exe} (pid {p.Id})");
        }
        catch (Exception ex)
        {
            Status = "Launch client: " + ex.Message;
            AddLine(LogCategory.Error, "[ModularCoop] Launch client: " + ex);
        }
    }

    /// <summary>
    /// Brings this PC's LauncherData.xml in line with the server before the client starts, so the join is not
    /// refused over a mod list. Returns false only when the player cancels the confirmation; anything the sync
    /// cannot fix is reported and we launch anyway, since Coop's own rejection message says more than we could.
    /// </summary>
    private bool SyncLauncherData()
    {
        var path = ClientManifest.DefaultLauncherDataPath();
        if (_prepared is null) RefreshPreview();
        if (_prepared is null)
        {
            AddLine(LogCategory.Warning, "[ModularCoop] Launch client: no server plan yet, leaving the mod list alone");
            return true;
        }

        var plan = LauncherDataSync.ComputePlan(ClientManifest.From(_prepared), _prepared.Order, path);
        foreach (var b in plan.Blockers) AddLine(LogCategory.Warning, $"[ModularCoop] mod list: {b.Id} — {b.Detail}");
        if (!plan.HasChanges)
        {
            AddLine(LogCategory.Tool, "[ModularCoop] mod list already matches the server");
            return true;
        }

        var backupRoot = LauncherDataSync.DefaultBackupRoot();
        if (!Profile.AutoSyncLauncherData)
        {
            var win = new LauncherSyncWindow(plan, path, backupRoot) { Owner = Application.Current.MainWindow };
            if (win.ShowDialog() != true)
            {
                Status = "Launch client cancelled.";
                return false;
            }
            if (win.DontAskAgain)
            {
                Profile.AutoSyncLauncherData = true;
                ProfileStore.Save(Profile);
            }
        }

        var result = LauncherDataSync.Apply(plan, path, backupRoot);
        if (result is not null)
        {
            AddLine(LogCategory.Tool, $"[ModularCoop] mod list synced: {result.Enabled} enabled, {result.Disabled} disabled, {result.Moved} reordered (backup: {result.BackupPath})");
            Status = $"Mod list synced ({result.Enabled} on, {result.Disabled} off, {result.Moved} moved).";
        }
        return true;
    }

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private async Task Stop()
    {
        if (_engine is null) return;
        Status = "Stopping…";
        await _engine.StopAsync(TimeSpan.FromSeconds(30));
    }

    [RelayCommand]
    private async Task SendCommand()
    {
        if (_engine is null || string.IsNullOrWhiteSpace(CommandText)) return;
        var sent = CommandText.Trim();
        try
        {
            await _engine.SendCommandAsync(sent);
            _lastCommand = sent;
            _repliesSeen = 0;
            _replyWindowEnds = DateTime.Now + ReplyWindow;
            AddLine(LogCategory.Command, "> " + sent);
            CommandText = "";
            CommandStatus = $"Sent \u201c{sent}\u201d \u2014 waiting for the server\u2026";
            // Report what came back, so a command that the engine silently ignores is visibly different from one
            // that answered. The engine does not acknowledge commands, so silence is a real and common outcome.
            await Task.Delay(ReplyWindow);
            CommandStatus = _repliesSeen > 0
                ? $"\u201c{_lastCommand}\u201d \u2014 {_repliesSeen} line(s) back from the server"
                : $"\u201c{_lastCommand}\u201d \u2014 sent, but the server said nothing (many commands answer in the game log, not here)";
        }
        catch (Exception ex)
        {
            AddLine(LogCategory.Error, "[ModularCoop] " + ex.Message);
            CommandStatus = $"\u201c{sent}\u201d could not be sent: {ex.Message}";
        }
    }

    /// <summary>
    /// Re-tags a line as CommandReply when it lands inside the window opened by the last command sent. Errors,
    /// warnings and milestones keep their own category: those matter more than what prompted them.
    /// </summary>
    private ClassifiedLine AttributeToCommand(ClassifiedLine c)
    {
        if (DateTime.Now > _replyWindowEnds) return c;
        if (c.Category is LogCategory.Error or LogCategory.Warning or LogCategory.Milestone or LogCategory.Probe) return c;
        _repliesSeen++;
        return c with { Category = LogCategory.CommandReply };
    }

    partial void OnIsRunningChanged(bool value)
    {
        if (!value) LiveSettings.OnStopped();
        LaunchCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
    }

    // ---- console -------------------------------------------------------------------------------------

    private void AddLine(LogCategory c, string text)
    {
        Console.Add(new ConsoleLine(DateTime.Now.ToString("HH:mm:ss"), c, text));
        ConsoleFlushed?.Invoke();
    }

    private void FlushConsole()
    {
        if (_pending.IsEmpty) return;
        // No DeferRefresh here: a ListCollectionView throws if its source changes while a refresh is deferred.
        var n = 0;
        while (n < 1500 && _pending.TryDequeue(out var l)) { Console.Add(l); n++; }

        // Tell the user when output is being discarded; a console that silently skips lines is worse
        // than one that admits it. Reported once per tick, only when something was actually dropped.
        var dropped = _pending.TakeDropped();
        if (dropped > 0)
        {
            _totalDropped += dropped;
            Console.Add(new ConsoleLine(DateTime.Now.ToString("HH:mm:ss"), LogCategory.Warning,
                $"[ModularCoop] console overloaded: dropped {dropped:N0} lines ({_totalDropped:N0} total). " +
                "The engine is printing faster than this window can show. If a Trace switch is on under the Server tab, turn it off."));
        }

        // The launch log buffers rather than flushing per line; push it to disk on the same tick.
        _launchLog?.Flush();
        // Engine chatter is the bulk of the output; drop it first so module-load, probe, server and error lines survive a whole campaign load.
        if (Console.Count > 60000) Console.TrimTo(50000, l => l.Category is LogCategory.Engine);
        ConsoleFlushed?.Invoke();
    }

    private bool FilterLine(object o)
    {
        if (o is not ConsoleLine l) return false;
        // Console I/O is deliberately checked before ErrorsOnly: when you are driving the server by hand you want
        // your own commands and their replies visible even while filtered down to errors.
        if (l.Category is LogCategory.Command or LogCategory.CommandReply) return ShowConsoleIo && MatchesFilter(l);
        if (ErrorsOnly && l.Category is not (LogCategory.Error or LogCategory.Milestone)) return false;
        var visible = l.Category switch
        {
            LogCategory.Engine => ShowEngine,
            LogCategory.ModuleLoad => ShowModuleLoad,
            LogCategory.Server => ShowServer,
            LogCategory.Coop => ShowCoop,
            LogCategory.Warning => ShowWarnings,
            LogCategory.Probe => ShowProbes,
            _ => true,
        };
        if (!visible) return false;
        return MatchesFilter(l);
    }

    /// <summary>The Find box, applied on its own so every category path uses the same rule.</summary>
    private bool MatchesFilter(ConsoleLine l) =>
        string.IsNullOrEmpty(ConsoleFilter) || l.Text.Contains(ConsoleFilter, StringComparison.OrdinalIgnoreCase);

    partial void OnShowEngineChanged(bool value) => ConsoleView.Refresh();
    partial void OnShowModuleLoadChanged(bool value) => ConsoleView.Refresh();
    partial void OnShowServerChanged(bool value) => ConsoleView.Refresh();
    partial void OnShowCoopChanged(bool value) => ConsoleView.Refresh();
    partial void OnShowWarningsChanged(bool value) => ConsoleView.Refresh();
    partial void OnShowProbesChanged(bool value) => ConsoleView.Refresh();
    partial void OnShowConsoleIoChanged(bool value) => ConsoleView.Refresh();
    partial void OnErrorsOnlyChanged(bool value) => ConsoleView.Refresh();
    partial void OnConsoleFilterChanged(string value) => ConsoleView.Refresh();

    [RelayCommand]
    private void OpenLogsFolder()
    {
        var d = Path.Combine(ProfileStore.RootDir, "logs");
        Directory.CreateDirectory(d);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(d) { UseShellExecute = true });
    }

    public async Task OnClosingAsync()
    {
        if (_engine is { IsRunning: true }) await _engine.StopAsync(TimeSpan.FromSeconds(20));
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

public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => value is true ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => throw new NotSupportedException();
}
