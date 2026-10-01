using ModderLords.Core.Logs;

namespace ModderLords.Core.Smoke;

/// <summary>
/// What a smoke test has seen so far, built only from log lines. No files, processes or clocks of its own, so the
/// whole decision can be driven from recorded lines in a test.
/// </summary>
public sealed class SmokeObserver
{
    private const int KeepLines = 25;

    // ---- the server ----
    public bool Serving { get; private set; }
    public DateTimeOffset? ClientStartedAt { get; private set; }
    /// <summary>Players already on the server when the test's game started; the test's player is any other one.</summary>
    private HashSet<int>? _playersBefore;
    private IReadOnlyList<ServerPlayer> _lastPlayers = [];
    public ServerPlayer? TestPlayer { get; private set; }
    public string BestServerState { get; private set; } = "";
    public DateTimeOffset? ServerOnMapAt { get; private set; }
    /// <summary>The test's player was on the map and then left the server's list before the test closed the game.</summary>
    public bool PlayerDropped { get; private set; }
    public bool ServerAnsweredPing { get; private set; }
    public List<string> ServerWarningsAtStartup { get; } = new();
    public List<string> ServerWarningsDuringJoin { get; } = new();
    public List<string> ExpectedWarnings { get; } = new();
    public int ServerErrorsDuringJoin { get; private set; }

    // ---- campaign time, from the server's pulse line once the game is on the map ----
    public string? TimeRequestedAs { get; private set; }
    public (string Date, int Day)? FirstDateOnMap { get; private set; }
    public (string Date, int Day)? LastDate { get; private set; }
    public string? LastTimeMode { get; private set; }
    /// <summary>The server's answer to the time command, if it printed one.</summary>
    public string? TimeCommandReply { get; private set; }

    public int TimeAsks { get; private set; }
    public DateTimeOffset? TimeAskedAt { get; private set; }
    /// <summary>The Coop policy that held time back, from "Time control request ... limited to Pause by X".</summary>
    public string? TimeLimitedBy { get; private set; }

    /// <summary>How long to wait for the date to move before asking again, and how many times to ask at most.</summary>
    public static readonly TimeSpan TimeAskRetry = TimeSpan.FromSeconds(15);
    public const int MaxTimeAsks = 8;

    private static readonly System.Text.RegularExpressions.Regex TimeLimitedRx =
        new(@"Time control request \S+ limited to \S+ by ""(?<by>[^""]+)""", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static System.Text.RegularExpressions.Regex TimeLimitedByRx() => TimeLimitedRx;

    public void TimeRequested(string mode) => TimeRequested(mode, DateTimeOffset.Now);

    public void TimeRequested(string mode, DateTimeOffset at)
    {
        TimeRequestedAs = mode;
        TimeAskedAt = at;
        TimeAsks++;
    }

    /// <summary>
    /// Whether to ask the server (again) to run time. Coop holds time back while it does not yet count a just-arrived player
    /// as connected (DisconnectedPlayersServerHandler.PlayersConnectedPolicy): live, a character created on the spot
    /// reached the map, the one request was "limited to Pause", and the date never moved; asking again 40 s later ran it.
    /// So: not asked yet, or the date has not moved, the server still reports Stop, and the last ask is old enough.
    /// </summary>
    public bool TimeAskDue(DateTimeOffset now)
    {
        if (TimeAskedAt is not { } asked) return true;
        if (TimeAsks >= MaxTimeAsks || TimeRan) return false;
        return string.Equals(LastTimeMode, "Stop", StringComparison.Ordinal) && now - asked >= TimeAskRetry;
    }

    public bool TimeRan => FirstDateOnMap is { } first && LastDate is { } last && last.Day > first.Day;
    public List<string> ServerErrorSamples { get; } = new();

    // ---- the player's game: Coop's log ----
    public ClientStage Stage { get; private set; } = ClientStage.NotStarted;
    public Dictionary<ClientStage, DateTimeOffset> StageAt { get; } = new();
    public string? WorldTransfer { get; private set; }
    public List<double> FpsOnMap { get; } = new();
    public Dictionary<string, int> CoopErrorsBySource { get; } = new(StringComparer.Ordinal);
    public List<string> CoopFatal { get; } = new();
    /// <summary>Coop's own connection-side warnings and errors (not its auto-sync noise), first few.</summary>
    public List<string> CoopConnectionNotes { get; } = new();
    /// <summary>Coop wrote its client log at all (it does only when the game exits normally).</summary>
    public bool CoopLogSeen { get; private set; }

    // ---- the player's game: ModderLords.Compat's log ----
    public bool CompatSeen { get; private set; }
    public DateTimeOffset? ReadyAt { get; private set; }
    public int? PingMs { get; private set; }
    public List<string> ClientWarnings { get; } = new();
    /// <summary>The compat module started creating a character (the world had none for this player).</summary>
    public bool CreatingCharacter { get; private set; }
    /// <summary>"Smoke 4821 (Vlandia)" once it finished.</summary>
    public string? CreatedCharacter { get; private set; }

    public void ServingNow() => Serving = true;

    public void ClientStarted(DateTimeOffset at)
    {
        ClientStartedAt = at;
        Stage = ClientStage.Started;
        StageAt[ClientStage.Started] = at;
        _playersBefore = _lastPlayers.Select(p => p.Id).ToHashSet();
    }

    public void ObserveServer(string line, DateTimeOffset at)
    {
        if (!Serving && SmokeSignals.IsServing(line)) Serving = true;
        var joining = ClientStartedAt is not null;

        if (SmokeSignals.ParsePulse(line) is { } pulse)
        {
            LastTimeMode = pulse.TimeMode;
            if (Stage == ClientStage.OnMap)
            {
                FirstDateOnMap ??= (pulse.Date, pulse.Day);
                LastDate = (pulse.Date, pulse.Day);
            }
            return;
        }
        if (TimeRequestedAs is not null && TimeCommandReply is null && line.Contains("Time control set to", StringComparison.Ordinal))
            TimeCommandReply = line.Trim();
        if (TimeRequestedAs is not null && TimeLimitedByRx().Match(line) is { Success: true } limited)
            TimeLimitedBy = limited.Groups["by"].Value;

        if (SmokeSignals.ParsePlayers(line) is { } players)
        {
            _lastPlayers = players;
            if (!joining) return;
            var mine = players.FirstOrDefault(p => _playersBefore?.Contains(p.Id) != true);
            if (mine is not null)
            {
                TestPlayer = mine;
                if (Rank(mine.State) > Rank(BestServerState)) BestServerState = mine.State;
                if (mine.State == SmokeSignals.OnMapState) ServerOnMapAt ??= at;
                // The server's view is live; Coop's client log is not (see ObserveCoopClient).
                Advance(mine.State switch
                {
                    "handshake" => ClientStage.Connecting,
                    "loading" => ClientStage.Loading,
                    SmokeSignals.OnMapState => ClientStage.OnMap,
                    _ => ClientStage.Connecting,
                }, at);
            }
            else if (ServerOnMapAt is not null && TestPlayer is not null && players.All(p => p.Id != TestPlayer.Id))
                PlayerDropped = true;
            return;
        }

        if (SmokeSignals.CompatMessage(line) is { } message)
        {
            if (message.StartsWith(SmokeSignals.ServerAnsweredLine, StringComparison.Ordinal)) ServerAnsweredPing = true;
            if (SmokeSignals.IsWarning(message)) AddWarning(message, joining ? ServerWarningsDuringJoin : ServerWarningsAtStartup);
            return;
        }
        // Anywhere in the line: a saved console log puts a timestamp and a category in front.
        var warning = line.IndexOf("[ModderLords] WARNING", StringComparison.Ordinal);
        if (warning >= 0)
        {
            AddWarning(line[(warning + "[ModderLords] ".Length)..].Trim(), joining ? ServerWarningsDuringJoin : ServerWarningsAtStartup);
            return;
        }
        if (joining && LogClassifier.Classify(line).Category == LogCategory.Error)
        {
            ServerErrorsDuringJoin++;
            Keep(ServerErrorSamples, line.Trim());
        }
    }

    /// <summary>
    /// Coop's own client log. It is buffered in memory until the game exits normally (0 bytes on disk while it runs, and
    /// still 0 after the game is killed), so it is a bonus for the report when it exists, never the only signal.
    /// </summary>
    public void ObserveCoopClient(string raw, DateTimeOffset at)
    {
        if (SmokeSignals.ParseCoopLine(raw) is not { } line) return;
        CoopLogSeen = true;
        if (SmokeSignals.StageOf(line) is { } stage)
        {
            Advance(stage, at);
            if (stage == ClientStage.WorldReceived) WorldTransfer ??= line.Message;
        }
        if (Stage == ClientStage.OnMap && SmokeSignals.FpsAverage(line) is { } fps) FpsOnMap.Add(fps);
        if (SmokeSignals.IsCoopFatal(line)) Keep(CoopFatal, $"{line.Time} {line.Level} {line.Source}: {line.Message}");
        if (line.Level is "ERR" or "FTL")
            CoopErrorsBySource[line.Source] = CoopErrorsBySource.GetValueOrDefault(line.Source) + 1;
        if (line.Level is "WRN" or "ERR" && line.Source.StartsWith("Coop.Core", StringComparison.Ordinal))
            Keep(CoopConnectionNotes, $"{line.Time} {line.Level} {line.Source}: {line.Message}");
    }

    public void ObserveCompatClient(string raw, DateTimeOffset at)
    {
        if (SmokeSignals.CompatMessage(raw) is not { } message) return;
        CompatSeen = true;
        if (message.StartsWith(SmokeSignals.ReadyLine, StringComparison.Ordinal))
        {
            ReadyAt ??= at;
            Advance(ClientStage.OnMap, at);
        }
        if (SmokeSignals.PingMs(message) is { } ms) PingMs ??= ms;
        if (message.StartsWith(SmokeSignals.CreationStartedLine, StringComparison.Ordinal))
        {
            CreatingCharacter = true;
            Advance(ClientStage.CharacterCreation, at);
        }
        if (message.StartsWith(SmokeSignals.CreationDoneLine, StringComparison.Ordinal))
            CreatedCharacter ??= message[SmokeSignals.CreationDoneLine.Length..];
        if (SmokeSignals.IsWarning(message)) AddWarning(message, ClientWarnings);
    }

    /// <summary>Records a stage; the furthest one wins. CharacterCreation is a dead end, so OnMap always outranks it.</summary>
    private void Advance(ClientStage stage, DateTimeOffset at)
    {
        StageAt.TryAdd(stage, at);
        // Anything from here on means the connection happened; the load timeout counts from the first sign of it.
        if (stage >= ClientStage.Connecting) StageAt.TryAdd(ClientStage.Connecting, at);
        if (stage > Stage) Stage = stage;
    }

    private void AddWarning(string message, List<string> into)
    {
        if (SmokeSignals.IsExpectedWarning(message)) Keep(ExpectedWarnings, message);
        else Keep(into, message);
    }

    private static void Keep(List<string> list, string line)
    {
        if (list.Count < KeepLines && !list.Contains(line)) list.Add(line);
    }

    private static int Rank(string state) => state switch
    {
        "" => 0,
        "handshake" => 1,
        "loading" => 2,
        SmokeSignals.OnMapState => 3,
        _ => 1,
    };
}

/// <summary>What only the runner knows: processes, files and time.</summary>
public sealed record SmokeRunFacts
{
    public string? ClientStartError { get; init; }
    public int? ClientPid { get; init; }
    /// <summary>Set when the game ended before the test closed it.</summary>
    public int? ClientExitCode { get; init; }
    public bool ClientExitedEarly { get; init; }
    public int? ServerExitCode { get; init; }
    public IReadOnlyList<string> NewCrashFolders { get; init; } = [];
    public IReadOnlyList<string> EngineErrors { get; init; } = [];
    public string? EngineErrorsFile { get; init; }
    public TimeSpan SteadyTarget { get; init; }
    public TimeSpan SteadyAchieved { get; init; }
    /// <summary>Which wait ran out, if one did ("connecting", "loading", ...).</summary>
    public string? TimedOutWaitingFor { get; init; }
    public bool Cancelled { get; init; }
    public bool ClientAlreadyRunning { get; init; }
}

/// <summary>Turns observations and run facts into the checklist. Pure.</summary>
public static class SmokeEvaluator
{
    public static IReadOnlyList<SmokeCheck> Evaluate(SmokeObserver o, SmokeRunFacts f)
    {
        var checks = new List<SmokeCheck>();
        var skip = (string name) => new SmokeCheck(name, SmokeVerdict.Skipped, "not reached");

        if (f.ClientAlreadyRunning)
            return [new SmokeCheck("Game started", SmokeVerdict.Fail, "Bannerlord is already running on this PC. Close it, then run the test again.")];

        checks.Add(o.Serving
            ? new SmokeCheck("Server is serving", SmokeVerdict.Pass, "the dedicated server reached SERVING")
            : new SmokeCheck("Server is serving", SmokeVerdict.Fail, f.ServerExitCode is { } sc
                ? $"the server exited with {sc} before SERVING" : "the server never reached SERVING"));

        if (f.ClientStartError is { } startError)
        {
            checks.Add(new SmokeCheck("Game started", SmokeVerdict.Fail, startError));
            return checks;
        }
        if (f.ClientPid is null)
        {
            checks.Add(skip("Game started"));
            return checks;
        }
        checks.Add(new SmokeCheck("Game started", SmokeVerdict.Pass, $"Bannerlord pid {f.ClientPid}, joining automatically"));

        // Joined: the game tried, and the server saw a new peer.
        if (o.Stage >= ClientStage.Connecting && o.TestPlayer is not null)
            checks.Add(new SmokeCheck("Joined the server", SmokeVerdict.Pass, $"{Took(o, ClientStage.Connecting)}; the server lists the player"));
        else if (o.Stage >= ClientStage.Connecting)
            checks.Add(new SmokeCheck("Joined the server", SmokeVerdict.Fail, "the game tried to connect, but the server never listed a new player", o.CoopConnectionNotes));
        else
            checks.Add(new SmokeCheck("Joined the server", SmokeVerdict.Fail, f.ClientExitedEarly
                ? $"the game closed (exit code {f.ClientExitCode}) before it tried to connect"
                : "the game never tried to connect: Coop did not start its automatic join (is Coop in the client's mod list?)", o.CoopConnectionNotes));

        if (o.Stage >= ClientStage.WorldReceived)
            checks.Add(new SmokeCheck("World received", SmokeVerdict.Pass, o.WorldTransfer ?? "the server sent the world and the game loaded it"));
        else
            checks.Add(o.Stage >= ClientStage.Connecting
                ? new SmokeCheck("World received", SmokeVerdict.Fail, "the server's save never arrived", o.CoopConnectionNotes)
                : skip("World received"));

        var onMap = o.Stage == ClientStage.OnMap;
        if (onMap)
            checks.Add(new SmokeCheck("On the campaign map", SmokeVerdict.Pass, Took(o, ClientStage.OnMap)
                + (o.CreatingCharacter ? ", after creating a character automatically" : "")));
        else if (o.CreatingCharacter)
            checks.Add(new SmokeCheck("On the campaign map", SmokeVerdict.Fail,
                "the world had no character for you; creating one automatically started but the game never reached the map", o.ClientWarnings));
        else if (o.Stage == ClientStage.CharacterCreation)
            checks.Add(new SmokeCheck("On the campaign map", SmokeVerdict.Warn,
                "the game stopped at character creation (this world has no character for you), and its ModderLords.Compat did not create one. Join once by hand, or use a profile with Settings sync."));
        else if (o.Stage >= ClientStage.Connecting)
            checks.Add(new SmokeCheck("On the campaign map", SmokeVerdict.Fail,
                (f.TimedOutWaitingFor is { } w ? $"still {w} when the test gave up" : $"never got past {o.Stage}")
                + (o.BestServerState.Length > 0 ? $"; the server's furthest state for the player: \"{o.BestServerState}\"" : "")));
        else checks.Add(skip("On the campaign map"));

        checks.Add(o.BestServerState == SmokeSignals.OnMapState
            ? new SmokeCheck("Server sees the player on the map", SmokeVerdict.Pass, $"{o.TestPlayer?.Name} is \"{SmokeSignals.OnMapState}\" on the server")
            : onMap ? new SmokeCheck("Server sees the player on the map", SmokeVerdict.Fail,
                $"the game is on the map, but the server's furthest state for it is \"{(o.BestServerState.Length == 0 ? "none" : o.BestServerState)}\"")
            : skip("Server sees the player on the map"));

        // ModderLords' own channel, both directions.
        if (!onMap) checks.Add(skip("ModderLords channel round trip"));
        else if (!o.CompatSeen)
            checks.Add(new SmokeCheck("ModderLords channel round trip", SmokeVerdict.Skipped,
                "ModderLords.Compat is not loaded in the game (the profile does not use Settings sync), so there is no channel to test"));
        else if (o.PingMs is { } ms)
            checks.Add(new SmokeCheck("ModderLords channel round trip", SmokeVerdict.Pass,
                $"the server answered the game's ping in {ms} ms{(o.ServerAnsweredPing ? "" : " (its own log line was not seen)")}"));
        else if (o.ReadyAt is null)
            checks.Add(new SmokeCheck("ModderLords channel round trip", SmokeVerdict.Fail,
                "ModderLords.Compat runs in the game but never reported the joined campaign ready (an older module, or its Coop adapter did not load)"));
        else
            checks.Add(new SmokeCheck("ModderLords channel round trip", SmokeVerdict.Fail,
                o.ServerAnsweredPing ? "the server answered, but the answer never reached the game" : "the game asked, and the server never answered"));

        // Stayed connected.
        if (!onMap) checks.Add(skip("Stayed connected"));
        else if (f.ClientExitedEarly)
            checks.Add(new SmokeCheck("Stayed connected", SmokeVerdict.Fail, $"the game closed by itself after {f.SteadyAchieved.TotalSeconds:0} s on the map (exit code {f.ClientExitCode})"));
        else if (o.PlayerDropped)
            checks.Add(new SmokeCheck("Stayed connected", SmokeVerdict.Fail, "the server dropped the player while the game was on the map", o.CoopConnectionNotes));
        else if (f.SteadyAchieved < f.SteadyTarget && !f.Cancelled && f.ServerExitCode is null)
            checks.Add(new SmokeCheck("Stayed connected", SmokeVerdict.Fail, $"only {f.SteadyAchieved.TotalSeconds:0} of {f.SteadyTarget.TotalSeconds:0} s"));
        else if (f.ServerExitCode is { } code)
            checks.Add(new SmokeCheck("Stayed connected", SmokeVerdict.Fail, $"the server exited with {code} while the game was on the map"));
        else
            checks.Add(new SmokeCheck("Stayed connected", f.Cancelled ? SmokeVerdict.Skipped : SmokeVerdict.Pass,
                f.Cancelled ? "cancelled" : $"{f.SteadyAchieved.TotalSeconds:0} s on the map without a drop"));

        // Campaign time: a paused map runs no daily ticks, AI or battles, which is where most errors come from.
        if (!onMap) checks.Add(skip("Campaign time ran"));
        else if (o.TimeRequestedAs is null)
            checks.Add(new SmokeCheck("Campaign time ran", SmokeVerdict.Skipped,
                "the test had no server console to ask (attach mode without --server-commands), so the map stayed paused"));
        else if (o.FirstDateOnMap is { } first && o.LastDate is { } last && last.Day > first.Day)
            checks.Add(new SmokeCheck("Campaign time ran", SmokeVerdict.Pass,
                $"{last.Day - first.Day} in-game day(s) passed at {o.TimeRequestedAs} ({first.Date} to {last.Date})"
                + (o.TimeAsks > 1 ? $"; asked {o.TimeAsks} times" + (o.TimeLimitedBy is { } by ? $", Coop held it back at first ({by})" : "") : "")));
        else
            checks.Add(new SmokeCheck("Campaign time ran", SmokeVerdict.Fail,
                $"asked the server {o.TimeAsks} time(s) to run time at {o.TimeRequestedAs}, but the campaign date did not move"
                + (o.LastDate is { } d ? $" (still {d.Date}, mode {o.LastTimeMode})" : " (no server pulse seen)")
                + (o.TimeLimitedBy is { } held ? $"; Coop limited it to Pause ({held})" : ""),
                o.TimeCommandReply is { } reply ? [reply] : []));

        // Crashes.
        var crash = new List<string>();
        if (f.ClientExitedEarly) crash.Add($"the game ended by itself (exit code {f.ClientExitCode})");
        if (f.ServerExitCode is { } serverCode) crash.Add($"the server exited with {serverCode}");
        crash.AddRange(f.NewCrashFolders.Select(c => "new crash report: " + c));
        crash.AddRange(o.CoopFatal.Select(c => "Coop: " + c));
        checks.Add(crash.Count == 0
            ? new SmokeCheck("No crash", SmokeVerdict.Pass, "no crash report, no fatal Coop error, both processes ran until the test closed them")
            : new SmokeCheck("No crash", SmokeVerdict.Fail, crash[0], crash.Skip(1).ToList()));

        // ModderLords' own warnings: the class of failure (#151) a server-only test cannot see.
        var warnings = o.ClientWarnings.Select(w => "game: " + w).Concat(o.ServerWarningsDuringJoin.Select(w => "server: " + w)).ToList();
        checks.Add(warnings.Count == 0
            ? new SmokeCheck("No ModderLords warnings", SmokeVerdict.Pass,
                o.CompatSeen ? "neither the game's nor the server's ModderLords.Compat warned" : "the server's ModderLords.Compat did not warn during the join")
            : new SmokeCheck("No ModderLords warnings", SmokeVerdict.Fail, $"{warnings.Count} warning(s) while joining", warnings));

        // Measurements.
        if (o.CreatedCharacter is { } created)
            checks.Add(new SmokeCheck("Character created automatically", SmokeVerdict.Info,
                $"{created}: the world had no character for you, so one was made at random; it stays in this world"));
        if (o.ServerWarningsAtStartup.Count > 0)
            checks.Add(new SmokeCheck("Server warnings before the join", SmokeVerdict.Info, $"{o.ServerWarningsAtStartup.Count} (printed while the server loaded)", o.ServerWarningsAtStartup));
        if (o.ExpectedWarnings.Count > 0)
            checks.Add(new SmokeCheck("Expected warnings", SmokeVerdict.Info, "true by design in this run, not counted", o.ExpectedWarnings));
        if (!o.CoopLogSeen)
            checks.Add(new SmokeCheck("Coop's client log", SmokeVerdict.Info,
                "empty: Coop writes it only when the game exits normally, and the test closes the game; the stages above come from the server and ModderLords"));
        var coopErrors = o.CoopErrorsBySource.Values.Sum();
        if (coopErrors > 0)
            checks.Add(new SmokeCheck("Coop errors in the game's log", SmokeVerdict.Info,
                $"{coopErrors:N0} (Coop logs many in a healthy session; listed by source)",
                o.CoopErrorsBySource.OrderByDescending(kv => kv.Value).Take(6).Select(kv => $"{kv.Value,7:N0}  {kv.Key}").ToList()));
        if (o.ServerErrorsDuringJoin > 0)
            checks.Add(new SmokeCheck("Server error lines during the join", SmokeVerdict.Info, $"{o.ServerErrorsDuringJoin:N0}", o.ServerErrorSamples));
        if (f.EngineErrors.Count > 0)
            checks.Add(new SmokeCheck("Engine errors in the game", SmokeVerdict.Info, $"{f.EngineErrors.Count} line(s) in {Path.GetFileName(f.EngineErrorsFile)}", f.EngineErrors.Take(10).ToList()));
        if (o.FpsOnMap.Count > 0)
            checks.Add(new SmokeCheck("Frame rate on the map", SmokeVerdict.Info, $"{o.FpsOnMap.Average():0} fps average over {o.FpsOnMap.Count} sample(s) (Coop's own count)"));
        return checks;
    }

    private static string Took(SmokeObserver o, ClientStage stage) =>
        o.ClientStartedAt is { } start && o.StageAt.TryGetValue(stage, out var at)
            ? $"{(at - start).TotalSeconds:0} s after the game started" : "reached";
}
