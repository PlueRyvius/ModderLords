using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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

/// <summary>
/// The hosting half of the app: the dedicated server, its console, its saves and its gameplay settings, plus the
/// mod-list sync that makes this PC's own game join the server it is running.
///
/// It exists so none of that is constructed in Player mode. <c>ModderLords.Core</c> already cannot reach the coop
/// assemblies at all (the compiler enforces the Phase 2 split), so this is about not paying for the server: no
/// flush timer, no bounded queues, no resource sampler and no live-settings channel for someone who only wants to
/// launch their mods. Built on the first switch to Host mode and then kept, because throwing it away while a
/// server was running would orphan the engine process.
///
/// Everything shared with the mod list - the profile, the mod rows, Status, Messages - stays on
/// <see cref="MainViewModel"/> and is reached through <see cref="Main"/>.
/// </summary>
public partial class HostViewModel : ObservableObject
{
    /// <summary>The mod-list half of the app. Host mode is an addition to it, never a replacement.</summary>
    public MainViewModel Main { get; }

    public HostViewModel(MainViewModel main)
    {
        Main = main;
        ConsoleView = CollectionViewSource.GetDefaultView(Console);
        ConsoleView.Filter = FilterLine;
        _flushTimer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
        _flushTimer.Tick += (_, _) => FlushConsole();
        _flushTimer.Start();
    }

    // Passed through so the coop tabs, whose DataContext is this object, can still bind the profile and the
    // one-line status the whole window shares.
    public Profile Profile => Main.Profile;
    public string Status { get => Main.Status; set => Main.Status = value; }
    public ObservableCollection<string> Messages => Main.Messages;
    public string ClientManifestText => Main.ClientManifestText;

    public string[] Regions { get; } = ["EU", "NA", "SA", "AS", "OC", "AF"];
    public ServerVisibility[] Visibilities { get; } = [ServerVisibility.Public, ServerVisibility.FriendsOnly, ServerVisibility.None];

    private EngineProcess? _engine;
    /// <summary>
    /// Lines waiting to reach the console, capped. It used to be an unbounded queue draining at only
    /// 1500 lines per 250 ms tick, so any engine talking faster than 6000 lines/second grew it forever:
    /// one session with a Trace switch on reached 19.8 GB of working set and drove the machine into
    /// paging. The console only ever shows the newest lines, so discarding the oldest waiting ones is
    /// right - but never silently, hence the drop count reported in FlushConsole.
    /// </summary>
    private readonly BoundedQueue<ConsoleLine> _pending = new(100_000);
    /// <summary>The Performance tab's state. Fed on the console flush tick; see DrainPerf.</summary>
    public PerformanceViewModel Performance { get; } = new();
    private readonly System.Windows.Threading.DispatcherTimer _flushTimer;
    /// <summary>
    /// Perf samples waiting to be applied. Bounded like the console queue, though at one line per ten seconds this
    /// is a formality: it exists so a duplicated or runaway sampler can never grow memory the way v0.8.3 did.
    /// </summary>
    private readonly BoundedQueue<PerfSample> _pendingPerf = new(1000);
    private ProcessResourceSampler? _resources;
    private DateTime _lastPerfSave = DateTime.MinValue;
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

    public ObservableCollection<SaveRow> Saves { get; } = new();
    public BulkObservableCollection<ConsoleLine> Console { get; } = new();
    public ICollectionView ConsoleView { get; }
    /// <summary>Mod settings tab: the running server's MCM settings, edited live through the sync module.</summary>
    public LiveSettingsViewModel LiveSettings { get; } = new();

    [ObservableProperty] private bool _isRunning;
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
    /// <summary>Perf lines are data for the Performance tab, not prose; showing them every 10 s forever just crowds the console.</summary>
    [ObservableProperty] private bool _showPerf;
    /// <summary>One-line feedback under the command box: what was sent, and whether anything came back.</summary>
    [ObservableProperty] private string _commandStatus = "";
    [ObservableProperty] private SaveRow? _selectedSave;
    [ObservableProperty] private string _saveDiff = "";
    [ObservableProperty] private string _clientCheckText = "";

    /// <summary>
    /// Builds the server plan for the Mods tab's order preview, keeping the copy this view model needs for the save
    /// diff, the mod-list sync and the launch itself. The client-mode counterpart lives on <see cref="MainViewModel"/>.
    /// </summary>
    /// <summary>Points the live-settings channel at the newly selected profile. Called by
    /// <see cref="MainViewModel.LoadProfile"/>, and only in Host mode: mod settings are a channel to a running
    /// dedicated server, so there is nothing for a player to be connected to.</summary>
    internal void OnProfileSelected()
    {
        LiveSettings.Log ??= s => Application.Current.Dispatcher.BeginInvoke(() => AddLine(LogCategory.Tool, "[ModderLords] " + s));
        try { LiveSettings.OnProfileSelected(Profile.Name); }
        catch (Exception ex) { Messages.Add("mod settings: " + ex.Message); }
    }

    internal MainViewModel.PreviewResult PrepareServerPreview()
    {
        var p = LaunchSession.Prepare(Profile, applySideEffects: false);
        _prepared = p;
        return new MainViewModel.PreviewResult(p.Catalog, p.Order, p.Modules, p.Messages);
    }

    // ---- saves ----------------------------------------------------------------------------------------

    internal void RefreshSaves(ServerPaths paths)
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

    internal void UpdateSaveDiff()
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
            Main.CollectProfileFromRows();
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
            foreach (var s in Coop.Config.ModConfig.Read(paths))
                Gameplay.Add(new GameplayRow { Path = s.Path, Kind = s.Kind, Original = s.RawValue, Value = s.Display,
                    Choices = Coop.Config.ModConfig.Choices.TryGetValue(s.Path, out var c) ? c : (s.Kind is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False ? ["true", "false"] : null) });
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
                var raw = Coop.Config.ModConfig.Encode(row.Kind, row.Value);
                if (raw != row.Original) changes.Add((row.Path, raw));
            }
            var n = Coop.Config.ModConfig.Apply(paths, changes);
            Status = n == 0 ? "Gameplay settings unchanged" : $"Saved {n} gameplay setting(s) to mod-config.json" + (IsRunning ? " (applies on next server start)" : "");
            LoadGameplay();
        }
        catch (Exception ex) { Status = "mod-config.json: " + ex.Message; }
    }

    [RelayCommand]
    private void CheckMyClient()
    {
        if (_prepared is null) Main.RefreshPreview();
        if (_prepared is null) return;
        var checks = ClientManifest.CompareWithLauncherData(ClientManifest.From(_prepared.Modules), ClientManifest.DefaultLauncherDataPath());
        ClientCheckText = checks.Count == 0 ? "No community modules to compare." :
            string.Join("\n", checks.Select(c => $"{(c.Verdict == "ok" ? "  ok " : "  !! ")}{c.Id,-30} server {c.ServerVersion ?? "-",-12} client {c.ClientVersion ?? "-",-12} {c.Verdict}"));
    }

    // ---- launch ------------------------------------------------------------------------------------

    [RelayCommand(CanExecute = nameof(CanLaunch))]
    private async Task Launch()
    {
        Main.CollectProfileFromRows();
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
            foreach (var p in pre) AddLine(p.Blocking ? LogCategory.Error : LogCategory.Warning, "[ModderLords] " + p.Message);
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
            foreach (var m in prepared.Messages) AddLine(LogCategory.Tool, "[ModderLords] " + m);
            foreach (var l in prepared.Plan.Describe().Split('\n', StringSplitOptions.RemoveEmptyEntries)) AddLine(LogCategory.Tool, "[ModderLords] " + l.TrimEnd());

            var logDir = Path.Combine(ProfileStore.RootDir, "logs");
            Directory.CreateDirectory(logDir);
            var rotated = Preflight.RotateLogs(logDir, "launch-*.log", keep: 20);
            if (rotated > 0) AddLine(LogCategory.Tool, $"[ModderLords] removed {rotated} old launch log(s)");
            _launchLog = new CappedLogWriter(Path.Combine(logDir, $"launch-{DateTime.Now:yyyyMMdd-HHmmss}.log"));
            _pending.Clear();
            _totalDropped = 0;

            _engine = EngineProcess.Start(prepared.Plan);
            IsRunning = true;
            Performance.SessionStarted();
            // CPU and working set come from the launcher watching the process, so they cost the server nothing and
            // work even when the compat module is not loaded. Its own timer; never the dispatcher.
            if (_engine.ProcessId is { } pid)
            {
                _resources = new ProcessResourceSampler(pid);
                _resources.SampleReady += r => Application.Current.Dispatcher.BeginInvoke(() => Performance.Apply(r));
                _resources.Start();
            }
            Status = $"Engine pid {_engine.ProcessId}, loading…";
            LiveSettings.Log ??= s => Application.Current.Dispatcher.BeginInvoke(() => AddLine(LogCategory.Tool, "[ModderLords] " + s));
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
                // Parse here (cheap, off the UI thread) but apply on the flush tick, so a perf line is no more
                // able to touch the UI per line than any other.
                if (c.Category == LogCategory.Perf && PerfLineParser.TryParse(line.Text, line.At) is { } sample)
                    _pendingPerf.Enqueue(sample);
                if (c.Category == LogCategory.Milestone && line.Text.Contains("SERVING"))
                    Application.Current.Dispatcher.BeginInvoke(() => Status = "SERVING, waiting for clients");
            };
            var code = await _engine.Exited;
            IsRunning = false;
            _resources?.Dispose(); _resources = null;
            if (Performance.WriteSessionSummary(Profile.Name) is { } summary)
                AddLine(LogCategory.Tool, "[ModderLords] performance summary written to " + summary);
            Status = $"Engine exited with {code}: {ExitCodeExplainer.Explain(code)}";
            AddLine(LogCategory.Milestone, "[ModderLords] " + Status);
            _launchLog?.Dispose(); _launchLog = null;
            _engine.Dispose(); _engine = null;
        }
        catch (Exception ex)
        {
            IsRunning = false;
            Status = ex.Message;
            AddLine(LogCategory.Error, "[ModderLords] " + ex);
        }
        LaunchCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
    }

    private bool CanLaunch() => !IsRunning;

    /// <summary>
    /// Brings this PC's LauncherData.xml in line with the server before the client starts, so the join is not
    /// refused over a mod list. Returns false only when the player cancels the confirmation; anything the sync
    /// cannot fix is reported and we launch anyway, since Coop's own rejection message says more than we could.
    /// </summary>
    /// <summary>
    /// Shows the mod-list diff on demand, including when there is nothing to change — Launch client only opens it
    /// when there are edits, so this is how you check what it would do without launching anything.
    /// </summary>
    [RelayCommand]
    private void MatchServer()
    {
        if (_prepared is null) Main.RefreshPreview();
        if (_prepared is null) { Status = "Nothing to compare yet: rescan the mods first."; return; }
        var path = ClientManifest.DefaultLauncherDataPath();
        var plan = LauncherDataSync.ComputePlan(ClientManifest.From(_prepared.Modules), _prepared.Order, path, Main.InstalledClientSide(), Main.OfficialSelection());
        var win = new LauncherSyncWindow(plan, path, LauncherDataSync.DefaultBackupRoot(), launching: false) { Owner = Application.Current.MainWindow };
        if (win.ShowDialog() != true) return;
        try
        {
            var result = LauncherDataSync.Apply(plan, path, LauncherDataSync.DefaultBackupRoot());
            Status = result is null
                ? "Your mod list already matches the server."
                : $"Mod list synced ({result.Enabled} on, {result.Added} added, {result.Disabled} off, {result.Moved} moved, {result.DuplicatesRemoved} duplicates removed).";
            if (result is not null) AddLine(LogCategory.Tool, $"[ModderLords] mod list synced (backup: {result.BackupPath})");
        }
        catch (Exception ex) { Status = ex.Message; }
    }

    /// <summary>
    /// Installs or updates the client's copy of the shared sync module before we launch, so the player never has to
    /// copy it out of the release zip and it cannot fall behind the launcher's build. Only when the profile actually
    /// uses it, or when an older copy is already there and would otherwise drift.
    /// </summary>
    private void EnsureClientModule()
    {
        var gameRoot = ClientLauncher.ResolveGameRoot(Profile);
        var alreadyThere = gameRoot is not null && Directory.Exists(ClientModuleInstaller.TargetDir(gameRoot));
        if (!Profile.SettingsSync && !alreadyThere) return;

        var r = ClientModuleInstaller.Ensure(gameRoot);
        switch (r.Outcome)
        {
            case ClientModuleInstaller.InstallOutcome.Installed:
            case ClientModuleInstaller.InstallOutcome.Updated:
                AddLine(LogCategory.Tool, "[ModderLords] " + r.Message);
                break;
            case ClientModuleInstaller.InstallOutcome.Failed:
                AddLine(LogCategory.Error, "[ModderLords] " + r.Message);
                break;
            case ClientModuleInstaller.InstallOutcome.Unavailable when Profile.SettingsSync:
                AddLine(LogCategory.Warning, "[ModderLords] " + r.Message);
                break;
        }
    }

    internal bool SyncLauncherData()
    {
        var path = ClientManifest.DefaultLauncherDataPath();
        EnsureClientModule();
        if (_prepared is null) Main.RefreshPreview();
        if (_prepared is null)
        {
            AddLine(LogCategory.Warning, "[ModderLords] Launch client: no server plan yet, leaving the mod list alone");
            return true;
        }

        var plan = LauncherDataSync.ComputePlan(ClientManifest.From(_prepared.Modules), _prepared.Order, path, Main.InstalledClientSide(), Main.OfficialSelection());
        foreach (var b in plan.Blockers) AddLine(LogCategory.Warning, $"[ModderLords] mod list: {b.Id} — {b.Detail}");
        if (!plan.HasChanges)
        {
            AddLine(LogCategory.Tool, "[ModderLords] mod list already matches the server");
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
            AddLine(LogCategory.Tool, $"[ModderLords] mod list synced: {result.Enabled} enabled, {result.Added} added, {result.Disabled} disabled, {result.Moved} reordered, {result.DuplicatesRemoved} duplicates removed (backup: {result.BackupPath})");
            Status = $"Mod list synced ({result.Enabled} on, {result.Added} added, {result.Disabled} off, {result.Moved} moved, {result.DuplicatesRemoved} duplicates removed).";
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
            AddLine(LogCategory.Error, "[ModderLords] " + ex.Message);
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

    internal void AddLine(LogCategory c, string text)
    {
        Console.Add(new ConsoleLine(DateTime.Now.ToString("HH:mm:ss"), c, text));
        ConsoleFlushed?.Invoke();
    }

    private void FlushConsole()
    {
        DrainPerf();
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
                $"[ModderLords] console overloaded: dropped {dropped:N0} lines ({_totalDropped:N0} total). " +
                "The engine is printing faster than this window can show. If a Trace switch is on under the Server tab, turn it off."));
        }

        // The launch log buffers rather than flushing per line; push it to disk on the same tick.
        _launchLog?.Flush();
        // Engine chatter is the bulk of the output; drop it first so module-load, probe, server and error lines survive a whole campaign load.
        if (Console.Count > 60000) Console.TrimTo(50000, l => l.Category is LogCategory.Engine);
        ConsoleFlushed?.Invoke();
    }

    /// <summary>
    /// Applies whatever perf samples arrived since the last tick. At one line per ten seconds this is almost always
    /// nothing; it rides the console timer so there is no second timer and no per-line UI work.
    /// </summary>
    private void DrainPerf()
    {
        while (_pendingPerf.TryDequeue(out var sample)) Performance.Apply(sample);
        _pendingPerf.TakeDropped();
        // Keep a summary on disk even if the session ends badly; every five minutes is cheap and loses little.
        if (IsRunning && DateTime.Now - _lastPerfSave > TimeSpan.FromMinutes(5))
        {
            _lastPerfSave = DateTime.Now;
            Performance.WriteSessionSummary(Profile.Name);
        }
    }

    private bool FilterLine(object o)
    {
        if (o is not ConsoleLine l) return false;
        // Console I/O is deliberately checked before ErrorsOnly: when you are driving the server by hand you want
        // your own commands and their replies visible even while filtered down to errors.
        if (l.Category is LogCategory.Command or LogCategory.CommandReply) return ShowConsoleIo && MatchesFilter(l);
        if (l.Category is LogCategory.Perf) return ShowPerf && MatchesFilter(l);
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
    partial void OnShowPerfChanged(bool value) => ConsoleView.Refresh();
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

