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
using ModderLords.Coop.Config;

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
    private LaunchSession.Prepared? _runningPrepared;
    private Profile? _runningProfile;
    internal LaunchSession.Prepared? ClientTarget => _runningPrepared ?? _prepared;
    internal Profile ClientProfile => _runningProfile ?? Profile;
    internal void InvalidatePreview() => _prepared = null;

    internal void RecordRunningSession(LaunchSession.Prepared prepared, Profile profile)
    {
        _runningPrepared = prepared;
        _runningProfile = ProfileStore.Snapshot(profile);
        IsRunning = true;
        Main.UpdateShareText();
    }
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
        OnPropertyChanged(nameof(Profile));
        LiveSettings.Log ??= s => Application.Current.Dispatcher.BeginInvoke(() => AddLine(LogCategory.Tool, "[ModderLords] " + s));
        try { LiveSettings.OnProfileSelected(Profile.Name); }
        catch (Exception ex) { Messages.Add("mod settings: " + ex.Message); }
    }

    internal MainViewModel.PreviewResult PrepareServerPreview()
    {
        _prepared = null;
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
        RefreshClientSaves();
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
        // Added/removed modules and a version bump are not the same thing, and saying "with a warning" for both
        // understates the case that actually stalls a load. Same severity split the CLI reports.
        var r = SaveModuleCheck.Compare(SelectedSave.Header, LaunchSession.PlannedCommunityVersions(_prepared));
        if (r.IsClean) { SaveDiff = "Save and profile agree on every community module."; return; }

        var lines = new List<string>();
        if (r.IsSevere)
        {
            lines.Add("This world was not built with this module set. The engine will force the load and the campaign may stall while the world initialises.");
            if (r.Removed.Count > 0) lines.Add("  missing now: " + string.Join(", ", r.Removed));
            if (r.Added.Count > 0) lines.Add("  new in this profile: " + string.Join(", ", r.Added));
            lines.Add("  To host these mods, start a campaign in the game with them and import that save.");
        }
        foreach (var d in r.VersionChanges) lines.Add($"  {d.ModuleId}: version changed (save {d.SaveVersion ?? "-"}, now {d.CurrentVersion ?? "-"})");
        SaveDiff = string.Join("\n", lines);
    }

    /// <summary>The player's own saves, for seeding the server with a world built by the real game (see SavePreparer.ImportFrom).</summary>
    public ObservableCollection<SaveRow> ClientSaves { get; } = new();

    [ObservableProperty] private SaveRow? _selectedClientSave;

    [RelayCommand]
    internal void RefreshClientSaves()
    {
        ClientSaves.Clear();
        try { foreach (var h in SavePreparer.ClientSaves()) ClientSaves.Add(new SaveRow { Header = h }); }
        catch (Exception ex) { AddLine(LogCategory.Tool, "[ModderLords] could not read the game's saves: " + ex.Message); }
    }

    [RelayCommand]
    private void ImportClientSave()
    {
        if (SelectedClientSave is null) { Status = "Pick one of the game's saves to import."; return; }
        try
        {
            var paths = LaunchSession.ResolvePaths(Profile);
            var name = SelectedClientSave.Name;
            var exists = SavePreparer.Exists(paths, name);
            // Replacing a hosted world is not something to do on a single click without saying so.
            if (exists && MessageBox.Show(
                    $"The server already has a save called '{name}'. Replace it?\n\nThe existing one is kept alongside it, not deleted.",
                    "Import save", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            var r = SavePreparer.ImportFrom(SelectedClientSave.Header.Path, paths, name, overwrite: exists);
            foreach (var w in r.Warnings) AddLine(LogCategory.Warning, "[ModderLords] " + w);
            AddLine(LogCategory.Tool, $"[ModderLords] imported '{name}' into {paths.SavesDir}");
            RefreshSaves(paths);
            SelectedSave = Saves.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            Status = $"Imported save '{name}'";
        }
        catch (Exception ex)
        {
            AddLine(LogCategory.Error, "[ModderLords] import failed: " + ex.Message);
            Status = "Import failed: " + ex.Message;
        }
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
        Main.RefreshPreview();
        if (ClientTarget is not { } target) return;
        var checks = ClientManifest.CompareWithLauncherData(ClientManifest.From(target.Modules), ClientManifest.DefaultLauncherDataPath());
        ClientCheckText = checks.Count == 0 ? "No community modules to compare." :
            string.Join("\n", checks.Select(c => $"{(c.Verdict == "ok" ? "  ok " : "  !! ")}{c.Id,-30} server {c.ServerVersion ?? "-",-12} client {c.ClientVersion ?? "-",-12} {c.Verdict}"));
    }

    // ---- launch ------------------------------------------------------------------------------------

    [RelayCommand(CanExecute = nameof(CanLaunch))]
    private async Task Launch()
    {
        Main.CollectProfileFromRows();
        ProfileStore.Save(Profile);
        var launchProfile = ProfileStore.Snapshot(Profile);
        Console.Clear();
        Status = "Checking…";
        var autoTaomCreate = false;
        try
        {
            var profileModuleIds = launchProfile.EnabledMods.Select(m => m.Id).ToList();
            if (TaomLaunchPolicy.IsTaom(profileModuleIds))
            {
                if (string.IsNullOrWhiteSpace(launchProfile.SaveName))
                {
                    launchProfile.SaveName = "taom_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
                    Profile.SaveName = launchProfile.SaveName;
                    ProfileStore.Save(Profile);
                    AddLine(LogCategory.Tool, $"[ModderLords] no TAOM save was selected; creating '{launchProfile.SaveName}' automatically");
                }
                var taomPaths = LaunchSession.ResolvePaths(launchProfile);
                autoTaomCreate = !SavePreparer.Exists(taomPaths, launchProfile.SaveName);
            }
            var pre = await Task.Run(() =>
            {
                var paths = LaunchSession.ResolvePaths(launchProfile);
                return Preflight.Run(paths, launchProfile.Server.JoinPort, launchProfile.Server.EnginePort, launchProfile.EnabledMods.Any());
            });
            foreach (var p in pre) AddLine(p.Blocking ? LogCategory.Error : LogCategory.Warning, "[ModderLords] " + p.Message);
            if (pre.Any(p => p.Blocking))
            {
                Status = "Not launched: " + pre.First(p => p.Blocking).Message;
                IsRunning = false;
                return;
            }

            Status = "Preparing…";
            var prepared = await Task.Run(() => LaunchSession.Prepare(launchProfile, allowTaomWorldCreation: autoTaomCreate));
            _prepared = prepared;
            // Preparing can take long enough for the user to save more edits. Merge observations into
            // the latest saved document instead of overwriting it with the launch snapshot.
            var saved = ProfileStore.Load(launchProfile.Name);
            if (saved is not null)
            {
                ProfileStore.MergeLastVersions(saved, launchProfile);
                ProfileStore.Save(saved);
            }
            if (Profile.Name == launchProfile.Name) ProfileStore.MergeLastVersions(Profile, launchProfile);
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

            if (autoTaomCreate)
            {
                Status = $"Creating TAOM world '{launchProfile.SaveName}'…";
                AddLine(LogCategory.Milestone, $"[ModderLords] creating TAOM world '{launchProfile.SaveName}' before starting the server");
                // The creation phase must start with no configured save. Otherwise a stale server-config.json can
                // make the official host begin loading an older campaign before the creation hook gets control.
                ServerConfig.Write(prepared.Paths, "", launchProfile.Server);
                using var creation = EngineProcess.Start(LaunchSession.CreateWorldPlan(prepared, launchProfile.SaveName));
                creation.LineReceived += line =>
                {
                    if (line.Text.Contains("worldcreate:", StringComparison.OrdinalIgnoreCase) ||
                        line.Text.Contains("phase=", StringComparison.OrdinalIgnoreCase))
                        Application.Current.Dispatcher.BeginInvoke(() => AddLine(LogCategory.Tool, "[ModderLords] " + line.Text));
                };
                // The compat module normally exits at the same deadline. Keep a launcher-side watchdog as well so
                // a native hang cannot leave a half-started creation process behind indefinitely.
                var creationExit = creation.Exited;
                var watchdog = Task.Delay(TimeSpan.FromSeconds(LaunchSession.DefaultCreateWorldTimeoutSeconds + 30));
                var completed = await Task.WhenAny(creationExit, watchdog);
                var creationCode = completed == creationExit
                    ? await creationExit
                    : await creation.StopAsync(TimeSpan.FromSeconds(20));
                if (completed != creationExit)
                    AddLine(LogCategory.Error, "[ModderLords] TAOM world creation exceeded its 15-minute limit; the creation process was stopped");
                if (creationCode != 11 || !SavePreparer.Exists(prepared.Paths, launchProfile.SaveName))
                    throw new InvalidOperationException($"TAOM world creation stopped with exit code {creationCode}; no usable save was written.");
                AddLine(LogCategory.Milestone, $"[ModderLords] TAOM world '{launchProfile.SaveName}' saved; starting the server");
                prepared = await Task.Run(() => LaunchSession.Prepare(launchProfile));
                _prepared = prepared;
                foreach (var m in prepared.Messages) AddLine(LogCategory.Tool, "[ModderLords] " + m);
            }

            _engine = EngineProcess.Start(prepared.Plan);
            RecordRunningSession(prepared, launchProfile);
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
            // A load that repeats one state forever produces no error and no exit. Say so instead of looking healthy.
            // The quiet period is per profile: a heavy mod can load for hours without anything being wrong.
            var stall = new LoadStallDetector(_engine.StartedAt,
                launchProfile.StallWarningSeconds is { } secs ? TimeSpan.FromSeconds(secs) : null);
            LiveSettings.Log ??= s => Application.Current.Dispatcher.BeginInvoke(() => AddLine(LogCategory.Tool, "[ModderLords] " + s));
            LiveSettings.OnLaunched(prepared.Plan.ExtraEnvironment.TryGetValue(LiveProtocol.EnvVar, out var liveDir) ? liveDir : null, launchProfile.SettingsSync, launchProfile.Name);
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
                // LineReceived runs on the stream-reader thread, so this has to hop to the dispatcher like the
                // SERVING line above; AddLine touches an ObservableCollection a CollectionView is bound to. Fires at
                // most once per launch, so BeginInvoke here costs nothing and does not need the batched queue.
                if (stall.Observe(line.Text, line.At) is { } warning)
                    Application.Current.Dispatcher.BeginInvoke(() => AddLine(LogCategory.Warning, "[ModderLords] " + warning));
            };
            var code = await _engine.Exited;
            IsRunning = false;
            _resources?.Dispose(); _resources = null;
            if (Performance.WriteSessionSummary(launchProfile.Name) is { } summary)
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

    internal ClientLaunchSession.Prepared PrepareClientLaunch()
    {
        var target = ClientTarget ?? throw new InvalidOperationException("No valid server selection is available.");
        var client = ProfileStore.Snapshot(ClientProfile);
        client.Mods = target.Selections.Where(s => !ClientManifest.IsServerOnly(s.Module.Id)).Select(s => new ProfileMod
        {
            Id = s.Module.Id, Enabled = true, SourcePath = s.Module.FolderPath, LastVersion = s.Module.Version,
        }).ToList();
        var coop = target.Catalog.Modules.FirstOrDefault(m => m.IsStock && m.FolderName == "Coop");
        if (coop is not null) client.Mods.Add(new ProfileMod { Id = coop.Id, LastVersion = coop.Version });
        foreach (var missing in ClientProfile.EnabledMods.Where(pm => !client.Mods.Any(m => m.Id.Equals(pm.Id, StringComparison.OrdinalIgnoreCase)) &&
                     !ClientManifest.IsServerOnly(pm.Id) && !ClientManifest.CoopClientModuleIds.Contains(pm.Id)))
            client.Mods.Add(missing);
        var result = ClientLaunchSession.Prepare(client);
        var mismatched = client.Mods.Where(pm => pm.LastVersion is not null && result.Mods.Any(m =>
            m.Id.Equals(pm.Id, StringComparison.OrdinalIgnoreCase) && !SaveHeaderReader.VersionsEqual(m.Version, pm.LastVersion))).Select(pm => pm.Id).ToList();
        if (mismatched.Count > 0) throw new InvalidOperationException("Client versions differ from the server: " + string.Join(", ", mismatched) + ". Update matching copies or restart the server.");
        return result;
    }

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
        Main.RefreshPreview();
        if (ClientTarget is not { } target) { Status = "Nothing to compare yet: rescan the mods first."; return; }
        var path = ClientManifest.DefaultLauncherDataPath();
        var plan = LauncherDataSync.ComputePlan(ClientManifest.From(target.Modules), target.Order, path, Main.InstalledClientSide(), ClientProfile.ClientOfficialModules.ToHashSet(StringComparer.OrdinalIgnoreCase));
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
        var gameRoot = ClientLauncher.ResolveGameRoot(ClientProfile);
        var alreadyThere = gameRoot is not null && Directory.Exists(ClientModuleInstaller.TargetDir(gameRoot));
        if (!ClientProfile.SettingsSync && !alreadyThere) return;

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
            case ClientModuleInstaller.InstallOutcome.Unavailable when ClientProfile.SettingsSync:
                AddLine(LogCategory.Warning, "[ModderLords] " + r.Message);
                break;
        }
    }

    internal bool SyncLauncherData()
    {
        Main.RefreshPreview();
        var path = ClientManifest.DefaultLauncherDataPath();
        EnsureClientModule();
        if (ClientTarget is not { } target)
        {
            AddLine(LogCategory.Warning, "[ModderLords] Launch client: no valid server plan. Rescan before launching.");
            return false;
        }

        var plan = LauncherDataSync.ComputePlan(ClientManifest.From(target.Modules), target.Order, path, Main.InstalledClientSide(), ClientProfile.ClientOfficialModules.ToHashSet(StringComparer.OrdinalIgnoreCase));
        foreach (var b in plan.Blockers) AddLine(LogCategory.Warning, $"[ModderLords] mod list: {b.Id} — {b.Detail}");
        if (!plan.HasChanges)
        {
            AddLine(LogCategory.Tool, "[ModderLords] mod list already matches the server");
            return true;
        }

        var backupRoot = LauncherDataSync.DefaultBackupRoot();
        if (!ClientProfile.AutoSyncLauncherData)
        {
            var win = new LauncherSyncWindow(plan, path, backupRoot) { Owner = Application.Current.MainWindow };
            if (win.ShowDialog() != true)
            {
                Status = "Launch client cancelled.";
                return false;
            }
            if (win.DontAskAgain)
            {
                ClientProfile.AutoSyncLauncherData = true;
                var saved = ProfileStore.Load(ClientProfile.Name);
                if (saved is not null) { saved.AutoSyncLauncherData = true; ProfileStore.Save(saved); }
                if (Profile.Name == ClientProfile.Name) Profile.AutoSyncLauncherData = true;
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
        if (!value)
        {
            _runningPrepared = null;
            _runningProfile = null;
            LiveSettings.OnStopped();
            Main.RefreshPreview();
        }
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
            Performance.WriteSessionSummary(ClientProfile.Name);
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
