using ModderLords.Coop.Launch;
using ModderLords.Coop.Saves;
using ModderLords.Core.Launch;
using ModderLords.Core.Overlay;
using ModderLords.Core.Profiles;
using ModderLords.Core.Testing;

namespace ModderLords.Coop.Testing;

/// <summary>What to run: the CLI's <c>test</c> options and the app's Tests tab both become one of these.</summary>
public sealed record InGameTestOptions
{
    /// <summary>The profile, by name: each launch loads it fresh, since a run changes it in memory (its save, its mods).</summary>
    public required string ProfileName { get; init; }
    /// <summary>The dedicated server's folders; null to resolve them from the profile, as the app does.</summary>
    public ServerPaths? Paths { get; init; }
    public bool SinglePlayer { get; init; }
    /// <summary>One suite (module/suite, a unique suite name, or a file path); null to pick suites.</summary>
    public string? Suite { get; init; }
    /// <summary>Only these test modules (plus core); null for every module that applies.</summary>
    public IReadOnlyCollection<string>? Modules { get; init; }
    public string? OnlyMod { get; init; }
    public bool WithOthers { get; init; }
    /// <summary>A test world (a name under test-worlds), copied fresh for every launch; null for the profile's save.</summary>
    public string? World { get; init; }
    public bool KeepClient { get; init; }
    public bool KeepOpenOnFail { get; init; }
    public bool KeepServer { get; init; }
    /// <summary>Where the launcher's compat folder is (module step DLLs); null for next to this process.</summary>
    public string? BundledRoot { get; init; }
}

/// <summary>How a run ended: the exit code the CLI returns, every launch's report and run folder, and why it stopped early.</summary>
public sealed record InGameTestResult(int ExitCode, IReadOnlyList<TestReport> Reports, IReadOnlyList<string> RunFolders, string? Problem)
{
    public bool Passed => ExitCode == 0;
}

/// <summary>
/// The in-game test harness's run (docs/INGAME-TEST-HARNESS.md): picks the suites, makes one plan per launch, starts
/// the game (and in Coop the server and its join), drives the steps through a run folder and writes the reports. The
/// CLI's <c>test</c> command and the app's Tests tab both call this, so they never differ.
/// </summary>
public static class InGameTestRun
{
    private static readonly HashSet<string> CoopIds = new(["Coop", "CoopNightly"], StringComparer.OrdinalIgnoreCase);

    public static async Task<InGameTestResult> RunAsync(InGameTestOptions options, Action<string> say, CancellationToken cancel)
    {
        var reports = new List<TestReport>();
        var folders = new List<string>();
        InGameTestResult Stop(string problem) { say("[ModderLords] " + problem); return new InGameTestResult(2, reports, folders, problem); }

        var profile = ProfileStore.Load(options.ProfileName);
        if (profile is null) return Stop("profile not found: " + options.ProfileName);
        if (ClientLauncher.IsClientRunning()) return Stop("Bannerlord is already running; close it first.");

        // One suite by name (as v1), or with none named the core suite plus every suite this profile's mods meet.
        IReadOnlyList<TestPlan>? namedSuite = null;
        if (options.Suite is { } suiteArg)
        {
            TestPlan testPlan;
            try { testPlan = TestSuites.Load(suiteArg); }
            catch (Exception ex) { return Stop("suite: " + ex.Message); }
            if (testPlan.Mode == SuiteMode.Both) testPlan = testPlan.ForMode(options.SinglePlayer);
            if (testPlan.Clients > 1) return Stop($"suite {testPlan.Name} needs {testPlan.Clients} players' games; this version starts one.");
            // An API v2 suite leaves the world and the wait for the map and Coop's join to the run's setup, so it
            // runs as the one picked suite below. Without that setup its steps started while the game was loading.
            if (testPlan.Api >= 2) namedSuite = [testPlan];
            else
            {
                // Steps all on "game": just the player's game, no server (decision 15).
                var one = testPlan.SinglePlayer
                    ? await RunSinglePlayer(profile, testPlan, options, say, cancel)
                    : await RunCoop(profile, testPlan, options, say, cancel);
                return Collect(one);
            }
        }

        var installed = InstalledMods(profile, options.SinglePlayer, say);
        if (installed is null) return Stop("the profile's mods could not be read");
        if (namedSuite is null && options.Modules is { } picked)
        {
            var unknown = picked.Where(m => TestSuites.Module(m) is null).ToList();
            if (unknown.Count > 0)
                return Stop($"no test module {string.Join(", ", unknown)} (there are: {string.Join(", ", TestSuites.Modules().Select(m => m.Id))}; test --list says more)");
            foreach (var m in picked.Select(TestSuites.Module))
                if (SuiteSelection.Unmet(m!.Requires, installed) is { Count: > 0 } unmet)
                    say($"[ModderLords] test module {m.Id} is unavailable here: needs {string.Join(", ", unmet.Select(r => SuiteSelection.Describe(r, installed)))}");
        }
        var left = new List<string>();
        IReadOnlyList<TestPlan> chosen;
        if (namedSuite is not null) chosen = namedSuite;
        else
        {
            try { chosen = SuiteSelection.Select(TestSuites.LoadAll(), installed, options.SinglePlayer, options.OnlyMod, options.WithOthers, left, options.Modules); }
            catch (Exception ex) { return Stop("suites: " + ex.Message); }
        }
        say($"[ModderLords] {(options.SinglePlayer ? "single-player" : "Coop")} suites for {profile.Name}: {string.Join(", ", chosen.Select(p => p.Module.Length > 0 ? p.Module + "/" + p.Name : p.Name))}");
        foreach (var why in left) say("[ModderLords]   left out " + why);

        var (loads, missingDll) = ModuleLoads(chosen, options);
        if (missingDll is not null) return Stop(missingDll);

        if (options.SinglePlayer)
        {
            // One launch: the run makes its world (or loads the test world) once, and loads it fresh before each suite
            // that needs a fresh world.
            string? spWorld = null;
            if (options.World is { } named)
            {
                var worldFile = TestSuites.WorldPath(named);
                if (!File.Exists(worldFile)) return Stop($"test world not found: {worldFile}");
                spWorld = "mltest-" + named;
                var saves = Path.Combine(GamePaths.UserDataDir(), "Game Saves");
                Directory.CreateDirectory(saves);
                File.Copy(worldFile, Path.Combine(saves, spWorld + ".sav"), overwrite: true);
                say($"[ModderLords] world {named} copied fresh -> {Path.Combine(saves, spWorld + ".sav")}");
            }
            var composed = SuiteSelection.Compose("selected", chosen, (suite, i) => SuiteSelection.SinglePlayerSetup(suite, i, spWorld)) with { Loads = loads };
            return Collect(await RunSinglePlayer(profile, composed, options, say, cancel));
        }

        // Coop: a fresh world costs a launch for now (the server restarts on the fresh copy); shared suites run in
        // core's launch.
        var launches = SuiteSelection.CoopLaunches(chosen);
        var exit = 0;
        for (int n = 0; n < launches.Count && !cancel.IsCancellationRequested; n++)
        {
            say($"[ModderLords] launch {n + 1}/{launches.Count}: {string.Join(", ", launches[n].Select(p => p.Name))}");
            var composed = SuiteSelection.Compose(launches.Count == 1 ? "selected" : $"selected-{n + 1}", launches[n], SuiteSelection.CoopSetup) with { Loads = loads };
            var fresh = ProfileStore.Load(options.ProfileName)!;
            var one = await RunCoop(fresh, composed, options, say, cancel);
            if (one.Report is not null) reports.Add(one.Report);
            if (one.Folder is not null) folders.Add(one.Folder);
            if (one.ExitCode != 0) exit = 1;
            if (ClientLauncher.IsClientRunning()) { say("[ModderLords] the game is still running (kept open?); the remaining launches are skipped."); return new InGameTestResult(1, reports, folders, "the game was kept open"); }
        }
        return new InGameTestResult(exit, reports, folders, null);

        InGameTestResult Collect((int ExitCode, TestReport? Report, string? Folder, string? Problem) one)
        {
            if (one.Report is not null) reports.Add(one.Report);
            if (one.Folder is not null) folders.Add(one.Folder);
            return new InGameTestResult(one.ExitCode, reports, folders, one.Problem);
        }
    }

    /// <summary>
    /// The profile's enabled mods with the versions that would load, for suite requirements; null (said) when the game
    /// cannot be found. Single player leaves Coop out, as its run does.
    /// </summary>
    /// <param name="scanned">A catalogue already scanned for this profile (the app's last Rescan), to skip a fresh scan.</param>
    public static Dictionary<string, string?>? InstalledMods(Profile profile, bool singlePlayer, Action<string> say,
        (Core.Modules.ModuleCatalog Catalog, string? GameRoot)? scanned = null)
    {
        try
        {
            var client = ClientLaunchSession.Prepare(profile, scanned);
            var mods = client.Mods.ToDictionary(m => m.Id, m => (string?)m.Version, StringComparer.OrdinalIgnoreCase);
            if (singlePlayer) foreach (var id in CoopIds) mods.Remove(id);
            return mods;
        }
        catch (Exception ex) { say("[ModderLords] mods: " + ex.Message); return null; }
    }

    /// <summary>The test worlds kept under test-worlds, by name.</summary>
    public static IReadOnlyList<string> TestWorlds() =>
        Directory.Exists(TestSuites.WorldsDir)
            ? Directory.EnumerateFiles(TestSuites.WorldsDir, "*.sav").Select(Path.GetFileNameWithoutExtension).OfType<string>().OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList()
            : [];

    /// <summary>
    /// A Coop test run: the smoke test's server and join, then the plan's steps inside both games, driven through a run
    /// folder, with a pass/fail per step.
    /// </summary>
    /// <summary>
    /// The suites' modules' own step DLLs, from the compat folder next to this launcher (decision 30); a problem when
    /// one is missing.
    /// </summary>
    private static (List<ModuleLoad> Loads, string? Problem) ModuleLoads(IEnumerable<TestPlan> plans, InGameTestOptions options)
    {
        var loads = new List<ModuleLoad>();
        foreach (var m in plans.Select(p => TestSuites.Module(p.Module)).Where(m => m?.StepsDll is not null).Distinct())
        {
            var dll = Path.Combine(options.BundledRoot ?? LaunchSession.BundledRoot ?? AppContext.BaseDirectory, "compat", m!.StepsDll!);
            if (!File.Exists(dll)) return (loads, $"test module {m.Id}: {dll} is missing (is it built into the compat folder?)");
            loads.Add(new ModuleLoad(m.Id, dll, m.StepsType!, m.Requires.Select(r => r.Id).ToList()));
        }
        return (loads, null);
    }

    private static async Task<(int ExitCode, TestReport? Report, string? Folder, string? Problem)> RunCoop(Profile profile, TestPlan testPlan,
        InGameTestOptions options, Action<string> say, CancellationToken cancel)
    {
        (int, TestReport?, string?, string?) Stop(string problem) { say("[ModderLords] " + problem); return (2, null, null, problem); }
        if (profile.Server.Password.Length > 0) return Stop("This server has a password, and Coop's automatic join cannot send one. Clear it for the test.");
        var paths = options.Paths ?? LaunchSession.ResolvePaths(profile);

        // Every run starts from the same world, copied fresh over the last run's copy.
        var world = options.World ?? testPlan.World;
        if (world is not null)
        {
            var worldFile = TestSuites.WorldPath(world);
            if (!File.Exists(worldFile)) return Stop($"test world not found: {worldFile} (put a .sav there, with its co-op session file beside it)");
            try
            {
                var imported = SavePreparer.ImportFrom(worldFile, paths, "mltest-" + world, overwrite: true);
                foreach (var w in imported.Warnings) say("[ModderLords] WARNING " + w);
                say($"[ModderLords] world {world} copied fresh -> {imported.SavePath}");
            }
            catch (Exception ex) { return Stop("could not copy the test world: " + ex.Message); }
            profile.SaveName = "mltest-" + world;
        }

        var channel = FileTestRunChannel.Create(Path.Combine(ProfileStore.RootDir, "tests"), testPlan, DateTimeOffset.Now);
        say($"[ModderLords] test run {channel.RunId} -> {channel.Folder}");
        var prepared = LaunchSession.Prepare(profile);
        foreach (var m in prepared.Messages) say("[ModderLords] " + m);
        if (!prepared.Selections.Any(sel => sel.Module.Id.Equals(LaunchSession.SyncModuleId, StringComparison.OrdinalIgnoreCase)))
            return Stop($"the test agent lives in {LaunchSession.SyncModuleId}, which this profile does not load.");
        say("[ModderLords] " + ClientModuleInstaller.Ensure(ClientLauncher.ResolveGameRoot(profile)).Message);
        ClientLaunchSession.Prepared client;
        try { client = ServerMatchedClient.Prepare(prepared, profile); }
        catch (Exception ex) { return Stop("client plan: " + ex.Message); }
        // The copies picked for each side, hashed now, for mods.fingerprint to check the loaded copies against.
        foreach (var line in PickedCopies.Write(channel.Folder, "server", prepared.Selections.Select(s => s.Module))
            .Concat(PickedCopies.Write(channel.Folder, "client:1", client.Plan.SelectedModules, client.Plan.ExcludedFolders)))
            say("[ModderLords] picked " + line);
        // The switch reaches the server through its own launch plan only, never this process's environment, so the
        // player's game (started from here) cannot inherit it and no ordinary Host launch can carry it.
        var serverPlan = prepared.Plan.WithEnvironment(FileTestRunChannel.ServerVariable, channel.SwitchValue("server"));

        string? gameGone = null;
        var sequencer = new TestSequencer(channel) { Abort = () => gameGone };
        sequencer.Progress += line => say("[test] " + line);
        Task<TestReport>? testRun = null;
        var runner = new Core.Smoke.SmokeRunner(new Core.Smoke.SmokeOptions
        {
            GameRoot = client.GameRoot,
            JoinPort = profile.Server.JoinPort,
            SteadyFor = TimeSpan.Zero,
            // Still wait for the server to list the player on the map, but not the 15 s guess after it: the run's setup
            // waits for Coop's own join-finished signal on the player's game.
            JoinSettle = TimeSpan.Zero,
            CloseClientWhenDone = !options.KeepClient,
            ReportRoot = channel.Folder,
            // No time command: the steps pause the campaign themselves (game.pause), so the numbers they read hold still.
            SendServerCommand = null,
            ExtraLaunchArguments = [FileTestRunChannel.ClientArgument, channel.SwitchValue("client:1")],
            WhileOnMap = token => testRun = sequencer.RunAsync(testPlan, channel.RunId, token),
        });
        runner.Progress += line => say("[smoke] " + line);

        using var testLogWriter = new StreamWriter(Path.Combine(channel.Folder, "server-console.log")) { AutoFlush = true };
        using var engine = EngineProcess.Start(serverPlan);
        engine.LineReceived += line =>
        {
            runner.ServerLine(line.Text);
            lock (testLogWriter) testLogWriter.WriteLine($"{line.At:HH:mm:ss.fff} {line.Text}");
        };
        _ = engine.Exited.ContinueWith(t => runner.ServerExited(t.Result), TaskScheduler.Default);

        var smokeReport = await runner.RunAsync(args => ClientLaunchSession.Start(client.Plan with { ExtraArguments = args }, note => say("[ModderLords] " + note)), cancel);
        // The game or server may have gone before the steps finished; the step waiting is then reported as such.
        gameGone = "the game or the server exited before the step finished";
        var testReport = testRun is null ? null : await testRun;
        say("");
        say(smokeReport.ToText());
        if (testReport is null) say("[ModderLords] the game never reached the campaign map, so no step ran; see the smoke report above.");
        else
        {
            await File.WriteAllTextAsync(Path.Combine(channel.Folder, "report.json"), testReport.ToJson(), CancellationToken.None);
            await File.WriteAllTextAsync(Path.Combine(channel.Folder, "report.txt"), testReport.ToText(), CancellationToken.None);
            say(testReport.ToText());
        }
        say($"[ModderLords] evidence and logs: {channel.Folder}");
        if (!options.KeepServer && !engine.Exited.IsCompleted)
        {
            say("[ModderLords] stopping the server…");
            await engine.StopAsync(TimeSpan.FromSeconds(60));
        }
        // Both reports count: steps that pass in a game that then crashed, or where Coop logged a fatal error, still fail.
        var passed = testReport?.Passed == true && smokeReport.Overall != Core.Smoke.SmokeVerdict.Fail;
        return (passed ? 0 : 1, testReport, channel.Folder, testReport is null ? "the game never reached the campaign map" : null);
    }

    /// <summary>
    /// A single-player test run: the profile's game with ModderLords.Compat (where the test agent lives) and without Coop,
    /// started with the test switch on side "game". The run's own steps load or create the world from the main menu.
    /// </summary>
    private static async Task<(int ExitCode, TestReport? Report, string? Folder, string? Problem)> RunSinglePlayer(Profile profile, TestPlan testPlan,
        InGameTestOptions options, Action<string> say, CancellationToken cancel)
    {
        (int, TestReport?, string?, string?) Stop(string problem) { say("[ModderLords] " + problem); return (2, null, null, problem); }
        // In memory only: the profile on disk is not changed.
        foreach (var mod in profile.Mods.Where(m => CoopIds.Contains(m.Id) && m.Enabled))
        {
            mod.Enabled = false;
            say($"[ModderLords] single player: {mod.Id} is left out of this run");
        }
        if (profile.Mods.FirstOrDefault(m => m.Id.Equals(LaunchSession.SyncModuleId, StringComparison.OrdinalIgnoreCase)) is { } sync) sync.Enabled = true;
        else profile.Mods.Add(new ProfileMod { Id = LaunchSession.SyncModuleId, Role = ServerRole.AsShipped });
        say("[ModderLords] " + ClientModuleInstaller.Ensure(ClientLauncher.ResolveGameRoot(profile)).Message);

        ClientLaunchSession.Prepared client;
        try { client = ClientLaunchSession.Prepare(profile); }
        catch (Exception ex) { return Stop("client plan: " + ex.Message); }
        foreach (var m in client.Messages) say("[ModderLords] " + m);

        var channel = FileTestRunChannel.Create(Path.Combine(ProfileStore.RootDir, "tests"), testPlan, DateTimeOffset.Now);
        say($"[ModderLords] single-player test run {channel.RunId} -> {channel.Folder}");
        foreach (var line in PickedCopies.Write(channel.Folder, TestPlan.GameSide, client.Plan.SelectedModules, client.Plan.ExcludedFolders))
            say("[ModderLords] picked " + line);
        var plan = client.Plan with { ExtraArguments = [FileTestRunChannel.ClientArgument, channel.SwitchValue(TestPlan.GameSide)] };
        say("[ModderLords] " + plan.Describe());

        System.Diagnostics.Process game;
        try { game = ClientLaunchSession.Start(plan, note => say("[ModderLords] " + note)); }
        catch (Exception ex) { return Stop("could not start the game: " + ex.Message); }

        // The run starts at launch here, not on the map as in Coop, so the hello waits out the game's start-up; a game
        // that exits on the way ends the wait sooner.
        var sequencer = new TestSequencer(channel, new TestSequencerOptions { HelloTimeout = TimeSpan.FromMinutes(10) })
        {
            Abort = () => game.HasExited ? $"the game exited (code {game.ExitCode})" : null,
        };
        sequencer.Progress += line => say("[test] " + line);
        var testReport = await sequencer.RunAsync(testPlan, channel.RunId, cancel);

        await File.WriteAllTextAsync(Path.Combine(channel.Folder, "report.json"), testReport.ToJson(), CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(channel.Folder, "report.txt"), testReport.ToText(), CancellationToken.None);
        say("");
        say(testReport.ToText());
        say($"[ModderLords] evidence and logs: {channel.Folder}");

        var keep = options.KeepClient || (options.KeepOpenOnFail && !testReport.Passed);
        if (keep && !game.HasExited) say("[ModderLords] the game was left running, as asked");
        else if (!game.HasExited)
        {
            say("[ModderLords] closing the game…");
            try { game.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
            try { await game.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token); } catch (OperationCanceledException) { }
        }
        return (testReport.Passed ? 0 : 1, testReport, channel.Folder, null);
    }
}
