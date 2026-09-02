using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
    private readonly System.Collections.Concurrent.ConcurrentQueue<ConsoleLine> _pending = new();
    private readonly System.Windows.Threading.DispatcherTimer _flushTimer;
    /// <summary>Raised on the UI thread after a batch of console lines was added (the view scrolls once per batch).</summary>
    public event Action? ConsoleFlushed;
    private LaunchSession.Prepared? _prepared;
    private StreamWriter? _launchLog;

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
            foreach (var m in best.Values.OrderBy(m => m.Id))
                Mods.Add(new ModRow { Module = m, Enabled = false, Role = Profile.DefaultRoleFor(m.Id) });

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
            Preflight.RotateLogs(logDir, "launch-*.log", keep: 20);
            _launchLog = new StreamWriter(Path.Combine(logDir, $"launch-{DateTime.Now:yyyyMMdd-HHmmss}.log")) { AutoFlush = true };

            _engine = EngineProcess.Start(prepared.Plan);
            IsRunning = true;
            Status = $"Engine pid {_engine.ProcessId}, loading…";
            LiveSettings.Log ??= s => Application.Current.Dispatcher.BeginInvoke(() => AddLine(LogCategory.Tool, "[ModularCoop] " + s));
            LiveSettings.OnLaunched(prepared.Plan.ExtraEnvironment.TryGetValue(LiveProtocol.EnvVar, out var liveDir) ? liveDir : null, Profile.SettingsSync);
            _engine.LineReceived += line =>
            {
                var c = LogClassifier.Classify(line.Text);
                _launchLog?.WriteLine($"{line.At:HH:mm:ss.fff} {c.Category,-10} {line.Text}");
                // Never touch the UI per line: the engine prints thousands during load. Queue and flush on a timer.
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
        try { await _engine.SendCommandAsync(CommandText); AddLine(LogCategory.Tool, "> " + CommandText); CommandText = ""; }
        catch (Exception ex) { AddLine(LogCategory.Error, "[ModularCoop] " + ex.Message); }
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
        // Engine chatter is the bulk of the output; drop it first so module-load, probe, server and error lines survive a whole campaign load.
        if (Console.Count > 60000) Console.TrimTo(50000, l => l.Category is LogCategory.Engine);
        ConsoleFlushed?.Invoke();
    }

    private bool FilterLine(object o)
    {
        if (o is not ConsoleLine l) return false;
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
        return string.IsNullOrEmpty(ConsoleFilter) || l.Text.Contains(ConsoleFilter, StringComparison.OrdinalIgnoreCase);
    }

    partial void OnShowEngineChanged(bool value) => ConsoleView.Refresh();
    partial void OnShowModuleLoadChanged(bool value) => ConsoleView.Refresh();
    partial void OnShowServerChanged(bool value) => ConsoleView.Refresh();
    partial void OnShowCoopChanged(bool value) => ConsoleView.Refresh();
    partial void OnShowWarningsChanged(bool value) => ConsoleView.Refresh();
    partial void OnShowProbesChanged(bool value) => ConsoleView.Refresh();
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
