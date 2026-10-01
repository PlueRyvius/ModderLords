using System.Diagnostics;
using System.Text;
using ModderLords.Core.Launch;

namespace ModderLords.Core.Smoke;

public sealed record SmokeOptions
{
    /// <summary>The game install whose client is launched (its Coop log sits in bin\Win64_Shipping_Client).</summary>
    public required string GameRoot { get; init; }
    public string JoinHost { get; init; } = "127.0.0.1";
    public required int JoinPort { get; init; }

    public TimeSpan ServerTimeout { get; init; } = TimeSpan.FromMinutes(15);
    /// <summary>From the game starting to Coop's first connection attempt (main menu load included).</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromMinutes(4);
    /// <summary>From the connection attempt to the campaign map (world transfer and load; heavy mods take minutes).</summary>
    public TimeSpan LoadTimeout { get; init; } = TimeSpan.FromMinutes(12);
    /// <summary>How long the game must then stay on the map, connected, before the test closes it.</summary>
    public TimeSpan SteadyFor { get; init; } = TimeSpan.FromSeconds(60);
    public bool CloseClientWhenDone { get; init; } = true;

    /// <summary>
    /// Sends one command to the server's console. When set, the test runs campaign time once the game is on the map
    /// (<see cref="TimeMode"/>), so the daily ticks, AI and mods actually run while it watches.
    /// </summary>
    public Func<string, Task>? SendServerCommand { get; init; }
    /// <summary>Coop's time mode for the watch: Play_1x or Play_2x.</summary>
    public string TimeMode { get; init; } = "Play_2x";

    /// <summary>Folder that gets one sub-folder per run (report + log slices).</summary>
    public required string ReportRoot { get; init; }
    public int KeepReports { get; init; } = 20;

    public string CoopClientLog => Path.Combine(ClientLauncher.ClientBin(GameRoot), "Coop_client.log");
    public string CompatClientLog { get; init; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Mount and Blade II Bannerlord", "Configs", "ModLogs", "ModderLords.Compat-client.log");
    public string EngineLogsDir { get; init; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Mount and Blade II Bannerlord", "logs");
    public string CrashesDir { get; init; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Mount and Blade II Bannerlord", "crashes");
    public string EngineConfigPath { get; init; } = EngineConfigGuard.DefaultPath;

    /// <summary>
    /// The game's extra arguments: Coop's <c>/autoconnect host:port</c> joins by itself, and <c>/modderlords-smoke</c>
    /// lets ModderLords.Compat create a character at random when the world has none for this player.
    /// </summary>
    public IReadOnlyList<string> LaunchArguments => ["/autoconnect", $"{JoinHost}:{JoinPort}", SmokeSignals.SmokeFlag];
}

/// <summary>
/// The client smoke test: starts the player's game against a running dedicated server, lets Coop join by itself, and
/// watches both sides until the game has stayed on the campaign map for a while, then closes it and reports.
/// <para>
/// The server is the caller's: it feeds the server's console lines in through <see cref="ServerLine"/> (from the Server
/// tab's engine, or the CLI's), so the test sees exactly what the host sees. The game is started through a delegate,
/// so it runs the same client plan the Launch client button runs, plus <see cref="SmokeOptions.LaunchArguments"/>.
/// </para>
/// </summary>
public sealed class SmokeRunner
{
    private readonly SmokeOptions _options;
    private readonly object _gate = new();
    private readonly SmokeObserver _observer = new();
    private readonly StringBuilder _serverLines = new();
    private const int ServerCaptureLimit = 16 * 1024 * 1024;
    private int? _serverExitCode;

    public SmokeRunner(SmokeOptions options) => _options = options;

    /// <summary>One human line per step, for a live view.</summary>
    public event Action<string>? Progress;

    /// <summary>Any thread: one line of the server's console output.</summary>
    public void ServerLine(string text)
    {
        lock (_gate)
        {
            _observer.ObserveServer(text, DateTimeOffset.Now);
            if (_serverLines.Length < ServerCaptureLimit) _serverLines.AppendLine($"{DateTime.Now:HH:mm:ss.fff} {text}");
        }
    }

    /// <summary>The server was already serving when the test started (its SERVING line went by before we listened).</summary>
    public void ServerAlreadyServing() { lock (_gate) _observer.ServingNow(); }

    public void ServerExited(int code) { lock (_gate) _serverExitCode = code; }

    /// <param name="startClient">Starts the game with these extra arguments appended to its client plan.</param>
    public async Task<SmokeReport> RunAsync(Func<IReadOnlyList<string>, Process> startClient, CancellationToken cancel)
    {
        var started = DateTimeOffset.Now;
        var notes = new List<string>();
        var facts = new SmokeRunFacts { SteadyTarget = _options.SteadyFor };

        if (ClientLauncher.IsClientRunning())
            return await Finish(started, facts with { ClientAlreadyRunning = true }, notes, null, null, null);

        // 1. The server.
        Say("Waiting for the server to reach SERVING…");
        var serverDeadline = DateTimeOffset.Now + _options.ServerTimeout;
        while (!Locked(() => _observer.Serving) && Locked(() => _serverExitCode) is null && DateTimeOffset.Now < serverDeadline && !cancel.IsCancellationRequested)
            await Delay(1000, cancel);
        if (!Locked(() => _observer.Serving))
        {
            facts = facts with { ServerExitCode = Locked(() => _serverExitCode), Cancelled = cancel.IsCancellationRequested, TimedOutWaitingFor = "the server" };
            return await Finish(started, facts, notes, null, null, null);
        }

        // 2. The game.
        var crashesBefore = CrashFolders();
        var coopLog = new LogTail(_options.CoopClientLog);
        var compatLog = new LogTail(_options.CompatClientLog);
        var config = new EngineConfigGuard(_options.EngineConfigPath);
        Process client;
        try
        {
            Say("Starting the game; Coop joins by itself…");
            client = startClient(_options.LaunchArguments);
        }
        catch (Exception ex)
        {
            notes.Add(config.Restore());
            return await Finish(started, facts with { ClientStartError = ex.Message }, notes, coopLog, compatLog, null);
        }
        var clientStarted = DateTimeOffset.Now;
        Locked(() => _observer.ClientStarted(clientStarted));
        facts = facts with { ClientPid = client.Id };

        // 3. Watch.
        ClientStage lastStage = ClientStage.Started;
        DateTimeOffset? onMapAt = null;
        while (!cancel.IsCancellationRequested)
        {
            await Delay(1000, cancel);
            var now = DateTimeOffset.Now;
            foreach (var line in coopLog.ReadNew()) Locked(() => _observer.ObserveCoopClient(line, now));
            foreach (var line in compatLog.ReadNew()) Locked(() => _observer.ObserveCompatClient(line, now));
            var stage = Locked(() => _observer.Stage);
            if (stage != lastStage) { Say(Describe(stage)); lastStage = stage; }

            if (client.HasExited && Relaunched(clientStarted) is { } relaunched)
            {
                // The starter handed over to a new process (a Steam relaunch); follow the game, not the starter.
                client = relaunched;
                facts = facts with { ClientPid = client.Id };
            }
            if (client.HasExited)
            {
                facts = facts with { ClientExitedEarly = true, ClientExitCode = SafeExitCode(client) };
                Say($"The game closed by itself (exit code {facts.ClientExitCode}).");
                break;
            }
            if (Locked(() => _serverExitCode) is { } serverCode)
            {
                facts = facts with { ServerExitCode = serverCode };
                Say($"The server exited with {serverCode}.");
                break;
            }
            if (stage == ClientStage.OnMap)
            {
                // Asked again while the date has not moved: Coop holds time back until it counts a just-arrived player
                // (a character created on the spot) as connected, and a single early request was simply refused.
                if (_options.SendServerCommand is { } send && Locked(() => _observer.TimeAskDue(now)))
                {
                    var again = Locked(() => _observer.TimeAsks) > 0;
                    Say(again ? $"Campaign time has not started; asking again ({_options.TimeMode})…" : $"Running campaign time ({_options.TimeMode})…");
                    try
                    {
                        await send($"{SmokeSignals.RunTimeCommand} {_options.TimeMode}");
                        Locked(() => _observer.TimeRequested(_options.TimeMode, now));
                    }
                    catch (Exception ex) { Say("Could not send the time command: " + ex.Message); }
                }
                onMapAt ??= now;
                facts = facts with { SteadyAchieved = now - onMapAt.Value };
                if (Locked(() => _observer.PlayerDropped)) { Say("The server dropped the player."); break; }
                if (now - onMapAt.Value >= _options.SteadyFor) { Say("Stayed on the map; closing the game."); break; }
                continue;
            }
            if (stage < ClientStage.Connecting && now - clientStarted > _options.ConnectTimeout)
            {
                facts = facts with { TimedOutWaitingFor = "connecting" };
                Say("The game never tried to connect.");
                break;
            }
            var connectedAt = Locked(() => _observer.StageAt.TryGetValue(ClientStage.Connecting, out var at) ? at : (DateTimeOffset?)null);
            if (stage >= ClientStage.Connecting && connectedAt is { } c && now - c > _options.LoadTimeout)
            {
                facts = facts with { TimedOutWaitingFor = stage < ClientStage.WorldReceived ? "receiving the world" : "loading" };
                Say("Gave up waiting for the campaign map.");
                break;
            }
        }
        facts = facts with { Cancelled = cancel.IsCancellationRequested };

        // 4. Close the game, then put engine_config.txt back (it is read-only while Coop's auto-join run lasts).
        if (!client.HasExited && _options.CloseClientWhenDone)
        {
            Say("Closing the game…");
            try { client.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
            try { await client.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token); } catch (OperationCanceledException) { }
        }
        else if (!client.HasExited) notes.Add("The game was left running, as asked; engine_config.txt stays read-only until it closes (Coop's auto-join does that).");
        if (client.HasExited) notes.Add(config.Restore());
        await Task.Delay(1500, CancellationToken.None);
        foreach (var line in coopLog.ReadNew()) Locked(() => _observer.ObserveCoopClient(line, DateTimeOffset.Now));
        foreach (var line in compatLog.ReadNew()) Locked(() => _observer.ObserveCompatClient(line, DateTimeOffset.Now));

        var engineErrorsFile = Path.Combine(_options.EngineLogsDir, $"rgl_log_errors_{client.Id}.txt");
        facts = facts with
        {
            NewCrashFolders = CrashFolders().Except(crashesBefore).ToList(),
            EngineErrors = ReadLines(engineErrorsFile),
            EngineErrorsFile = engineErrorsFile,
            ServerExitCode = facts.ServerExitCode ?? Locked(() => _serverExitCode),
        };
        return await Finish(started, facts, notes, coopLog, compatLog, engineErrorsFile);
    }

    private async Task<SmokeReport> Finish(DateTimeOffset started, SmokeRunFacts facts, List<string> notes,
        LogTail? coopLog, LogTail? compatLog, string? engineErrorsFile)
    {
        var checks = Locked(() => SmokeEvaluator.Evaluate(_observer, facts));
        if (facts.Cancelled) notes.Add("The test was cancelled.");
        var report = new SmokeReport(started, DateTimeOffset.Now - started, SmokeReport.OverallOf(checks), checks, notes);
        try
        {
            var folder = Path.Combine(_options.ReportRoot, started.ToString("yyyyMMdd-HHmmss"));
            Directory.CreateDirectory(folder);
            report = report with { Folder = folder };
            await File.WriteAllTextAsync(Path.Combine(folder, "report.txt"), report.ToText());
            await File.WriteAllTextAsync(Path.Combine(folder, "report.json"), report.ToJson());
            await File.WriteAllTextAsync(Path.Combine(folder, "server.log"), Locked(() => _serverLines.ToString()));
            if (coopLog is not null) await File.WriteAllTextAsync(Path.Combine(folder, "Coop_client.log"), coopLog.Captured.ToString());
            if (compatLog is not null) await File.WriteAllTextAsync(Path.Combine(folder, "ModderLords.Compat-client.log"), compatLog.Captured.ToString());
            if (engineErrorsFile is not null && File.Exists(engineErrorsFile)) File.Copy(engineErrorsFile, Path.Combine(folder, Path.GetFileName(engineErrorsFile)), overwrite: true);
            Prune();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            report = report with { Notes = [.. report.Notes, "The report could not be saved: " + ex.Message] };
        }
        Say($"Smoke test {report.Headline}.");
        return report;
    }

    private void Prune()
    {
        var runs = new DirectoryInfo(_options.ReportRoot).GetDirectories().OrderByDescending(d => d.Name).Skip(_options.KeepReports);
        foreach (var run in runs)
            try { run.Delete(recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private HashSet<string> CrashFolders()
    {
        try { return Directory.Exists(_options.CrashesDir) ? Directory.GetDirectories(_options.CrashesDir).ToHashSet(StringComparer.OrdinalIgnoreCase) : []; }
        catch (IOException) { return []; }
    }

    private static IReadOnlyList<string> ReadLines(string path)
    {
        try
        {
            if (!File.Exists(path)) return [];
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd().Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Trim().Length > 0).ToList();
        }
        catch (IOException) { return []; }
    }

    /// <summary>A Bannerlord process started after the test's own, still running: the game the starter handed over to.</summary>
    private static Process? Relaunched(DateTimeOffset since)
    {
        foreach (var p in Process.GetProcessesByName("Bannerlord"))
        {
            try { if (!p.HasExited && p.StartTime >= since.LocalDateTime.AddSeconds(-1)) return p; }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
        return null;
    }

    private static int? SafeExitCode(Process p)
    {
        try { return p.ExitCode; } catch (InvalidOperationException) { return null; }
    }

    private static string Describe(ClientStage stage) => stage switch
    {
        ClientStage.Connecting => "The game is connecting to the server…",
        ClientStage.ReceivingWorld => "Receiving the world from the server…",
        ClientStage.WorldReceived => "World received; loading…",
        ClientStage.Loading => "Loading the campaign…",
        ClientStage.CharacterCreation => "No character in this world yet: ModderLords is creating one at random…",
        ClientStage.OnMap => "On the campaign map; watching the connection…",
        _ => stage.ToString(),
    };

    private void Say(string line) => Progress?.Invoke(line);

    private T Locked<T>(Func<T> read) { lock (_gate) return read(); }
    private void Locked(Action act) { lock (_gate) act(); }

    private static async Task Delay(int ms, CancellationToken cancel)
    {
        try { await Task.Delay(ms, cancel); } catch (OperationCanceledException) { }
    }
}
