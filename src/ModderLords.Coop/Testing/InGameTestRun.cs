using ModderLords.Coop.Launch;
using ModderLords.Coop.Saves;
using ModderLords.Core.Launch;
using ModderLords.Core.Modules;
using ModderLords.Core.Overlay;
using ModderLords.Core.Profiles;
using ModderLords.Core.Smoke;
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
    /// <summary>Generate the profile's test world again even when the kept one still fits (ProfileTestWorld).</summary>
    public bool NewWorld { get; init; }
    public bool KeepClient { get; init; }
    public bool KeepOpenOnFail { get; init; }
    public bool KeepServer { get; init; }
    /// <summary>Minimize the game's window for the run instead of keeping it behind other windows.</summary>
    public bool Minimized { get; init; }
    /// <summary>Launch without checking the mods' calls into the game first (ApiCheck), to see a break fail in the game.</summary>
    public bool SkipApiCheck { get; init; }
    /// <summary>The game's Configs folder, whose two settings files the run changes and puts back; null for the player's own.</summary>
    public string? GameConfigsDir { get; init; }
    /// <summary>Where the launcher's compat folder is (module step DLLs); null for next to this process.</summary>
    public string? BundledRoot { get; init; }
    /// <summary>
    /// A mod's release zip (decision 27): checked (PackageCheck), unpacked into ModderLords' own folder and pinned for
    /// this run in memory, never written over the player's copy. With no suite or modules named, the run picks the
    /// test modules that require that mod.
    /// </summary>
    public string? Package { get; init; }
    /// <summary>The version the package's SubModule.xml must say; null for any.</summary>
    public string? PackageVersion { get; init; }
    /// <summary>Only check the package; start no game.</summary>
    public bool PackageCheckOnly { get; init; }
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
        if (ClientLauncher.IsClientRunning())
        {
            say("[ModderLords] Bannerlord is already running; close it first.");
            return new InGameTestResult(2, [], [], "Bannerlord is already running; close it first.");
        }
        // The game opens in a small window and keeps running unfocused, so the PC stays usable; the player's settings
        // files are put back byte for byte at the end, however the run ends.
        foreach (var note in TestRunRetention.Prune(Path.Combine(ProfileStore.RootDir, "tests"), DateTimeOffset.Now)) say("[ModderLords] " + note);
        var settings = TestRunGameSettings.Apply(options.GameConfigsDir ?? TestRunGameSettings.DefaultConfigsDir, out var applied);
        foreach (var note in applied) say("[ModderLords] " + note);
        try { return await Run(options, say, cancel); }
        finally
        {
            // A game just closed can still be listed for a moment.
            for (var i = 0; i < 20 && ClientLauncher.IsClientRunning(); i++) await Task.Delay(500, CancellationToken.None);
            foreach (var note in settings.Restore()) say("[ModderLords] " + note);
            if (ClientLauncher.IsClientRunning())
            {
                // A game kept open saves its settings (the test window) when it closes. The app is still running then
                // and puts them back again; the CLI has exited by then.
                say("[ModderLords] the game is still open and may save the test window size when it closes; if it opens windowed next time, set Display mode back in its options.");
                _ = Task.Run(async () =>
                {
                    foreach (var game in System.Diagnostics.Process.GetProcessesByName("Bannerlord")) await game.WaitForExitAsync();
                    settings.Restore();
                });
            }
        }
    }

    private static async Task<InGameTestResult> Run(InGameTestOptions options, Action<string> say, CancellationToken cancel)
    {
        var reports = new List<TestReport>();
        var folders = new List<string>();
        InGameTestResult Stop(string problem) { say("[ModderLords] " + problem); return new InGameTestResult(2, reports, folders, problem); }

        var profile = ProfileStore.Load(options.ProfileName);
        if (profile is null) return Stop("profile not found: " + options.ProfileName);
        if (options.Package is { } zip)
        {
            if (PreparePackage(zip, profile, ref options, say) is { } packageProblem) return Stop(packageProblem);
            if (options.PackageCheckOnly) return new InGameTestResult(0, reports, folders, null);
        }
        if (ClientLauncher.IsClientRunning()) return Stop("Bannerlord is already running; close it first.");

        // One suite by name (as v1), or with none named the core suite plus every suite this profile's mods meet.
        IReadOnlyList<TestPlan>? namedSuite = null;
        // An upgrade world saved on a newer game than this one is left out: the older game crashes loading it.
        var installedGame = ClientLauncher.ResolveGameRoot(profile) is { } gameRoot ? CoopGameSupport.GameVersion(gameRoot) : null;
        string? SavedOn(string world) => WorldSavedOn(TestSuites.WorldsDir, world);
        var left = new List<string>();
        if (options.Suite is { } suiteArg)
        {
            TestPlan testPlan;
            try { testPlan = TestSuites.Load(suiteArg); }
            catch (Exception ex) { return Stop("suite: " + ex.Message); }
            if (testPlan.Mode == SuiteMode.Both) testPlan = testPlan.ForMode(options.SinglePlayer);
            if (testPlan.Clients > 1) return Stop($"suite {testPlan.Name} needs {testPlan.Clients} players' games; this version starts one.");
            if (testPlan.After is { } first)
                return Stop($"suite {testPlan.Name} continues the world suite {first} keeps; run its module (--modules) so {first} runs first");
            // An API v2 suite leaves the world and the wait for the map and Coop's join to the run's setup, so it
            // runs as the one picked suite below. Without that setup its steps started while the game was loading.
            if (testPlan.Api >= 2)
            {
                namedSuite = [testPlan];
                // A named upgrade suite runs on each kept upgrade world, as when picked.
                if (testPlan.OnUpgradeWorlds)
                {
                    namedSuite = SuiteSelection.UpgradeWorlds(testPlan, TestWorlds(), SavedOn, installedGame, left);
                    if (namedSuite.Count == 0 && left.Count > 0)
                        return Stop($"suite {testPlan.Name} has no upgrade world this game can load: " + string.Join("; ", left));
                    if (namedSuite.Count == 0)
                        return Stop($"suite {testPlan.Name} runs on upgrade worlds, and none is kept for {string.Join(" or ", testPlan.Requires.Select(r => r.Id))} in {TestSuites.WorldsDir}");
                }
            }
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
        IReadOnlyList<TestPlan> chosen;
        if (namedSuite is not null) chosen = namedSuite;
        else
        {
            try { chosen = SuiteSelection.Select(TestSuites.LoadAll(), installed, options.SinglePlayer, options.OnlyMod, options.WithOthers, left, options.Modules, TestWorlds(), SavedOn, installedGame); }
            catch (Exception ex) { return Stop("suites: " + ex.Message); }
            // With nothing picked, a module whose step DLL this build lacks is left out, not failed: a release
            // packaged on a PC without that mod has no DLL for it. A module picked by name still stops below.
            if (options.Modules is null)
                chosen = chosen.Where(p =>
                {
                    if (TestSuites.Module(p.Module) is not { } m || MissingStepsDll(m, options.BundledRoot) is not { } dll) return true;
                    left.Add($"{p.Module}/{p.Name}: this build has no {Path.GetFileName(dll)}");
                    return false;
                }).ToList();
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
            var saves = Path.Combine(GamePaths.UserDataDir(), "Game Saves");
            // Every test world the run loads (--world, and each suite's own: a named world or an upgrade world) is
            // copied fresh into the game's saves, so no run sees what an earlier one left.
            var testWorlds = chosen.Select(p => p.NamedWorld).OfType<string>().ToList();
            if (options.World is { } named) testWorlds.Insert(0, named);
            foreach (var w in testWorlds.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var worldFile = TestSuites.WorldPath(w);
                if (!File.Exists(worldFile)) return Stop($"test world not found: {worldFile}");
                Directory.CreateDirectory(saves);
                File.Copy(worldFile, Path.Combine(saves, SuiteSelection.SaveFor(w) + ".sav"), overwrite: true);
                say($"[ModderLords] world {w} copied fresh -> {Path.Combine(saves, SuiteSelection.SaveFor(w) + ".sav")}");
            }
            if (options.World is { } runWorld) spWorld = SuiteSelection.SaveFor(runWorld);
            var composed = SuiteSelection.Compose("selected", chosen, (suite, i) => SuiteSelection.SinglePlayerSetup(suite, i, spWorld)) with { Loads = loads };
            return Collect(await RunSinglePlayer(profile, composed, options, say, cancel));
        }

        // Coop: each fresh world restarts the server on a fresh copy, and the player's game, started once, goes back to
        // the main menu and joins it again (docs/INGAME-TEST-HARNESS.md, "One launch, many suites"); shared suites run
        // with the suite before them. A game that cannot rejoin is closed and that suite started again from scratch.
        var launches = SuiteSelection.CoopLaunches(chosen);
        var carry = new CoopClientCarry();
        try { return await RunCoopLaunches(); }
        finally
        {
            // A game kept for a suite that never ran (cancelled, or a launch that stopped before its server started).
            if (carry.Game is { HasExited: false } waiting && !options.KeepClient)
            {
                say("[ModderLords] closing the game kept for the next suite…");
                await CloseGame(waiting);
            }
            // Not while a game kept open (--keep-client) runs: Coop's auto-join keeps the file read-only until it closes.
            if (carry.Config is { } config && !ClientLauncher.IsClientRunning()) say("[ModderLords] " + config.Restore());
        }

        async Task<InGameTestResult> RunCoopLaunches()
        {
        var exit = 0;
        // What each suite's launch kept (suite → world) and reported, for the suites that continue its world.
        var keptBy = new Dictionary<string, string>(StringComparer.Ordinal);
        var valuesBy = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
        for (int n = 0; n < launches.Count && !cancel.IsCancellationRequested; n++)
        {
            say($"[ModderLords] launch {n + 1}/{launches.Count}: {string.Join(", ", launches[n].Select(p => p.Name))}");
            var composed = SuiteSelection.Compose(launches.Count == 1 ? "selected" : $"selected-{n + 1}", launches[n], SuiteSelection.CoopSetup) with { Loads = loads };
            var launchOptions = options;
            if (launches[n][0].After is { } after)
            {
                // The server starts on the world the suite before kept in this run, and the steps can read its values.
                if (!keptBy.TryGetValue(after, out var kept))
                {
                    say($"[ModderLords] {launches[n][0].Name} continues {after}'s world, and {after} kept none in this run (it failed before coop.saveWorld?); skipped");
                    exit = 1;
                    continue;
                }
                launchOptions = options with { World = kept, NewWorld = false };
                composed = composed with { Seed = valuesBy[after] };
            }
            var fresh = ProfileStore.Load(options.ProfileName)!;
            var keptNow = new Dictionary<string, string>(StringComparer.Ordinal);
            carry.NextName = n + 1 < launches.Count ? $"selected-{n + 2}" : null;
            var rejoining = carry.Game is { HasExited: false };
            var one = await RunCoop(fresh, composed, launchOptions, say, cancel, keptNow, carry);
            if (rejoining && carry.RejoinFailed && !cancel.IsCancellationRequested)
            {
                // The kept game could not join the restarted server: this suite starts again with a game of its own.
                carry.RejoinFailed = false;
                if (one.Folder is not null) say($"[ModderLords] the kept game did not rejoin (evidence: {one.Folder}); starting the game again for {launches[n][0].Name}");
                keptNow.Clear();
                one = await RunCoop(ProfileStore.Load(options.ProfileName)!, composed, launchOptions, say, cancel, keptNow, carry);
            }
            if (one.Report is not null)
            {
                reports.Add(one.Report);
                foreach (var suite in launches[n])
                {
                    valuesBy[suite.Name] = one.Report.Values;
                    if (keptNow.TryGetValue(suite.Name, out var world)) keptBy[suite.Name] = world;
                }
            }
            if (one.Folder is not null) folders.Add(one.Folder);
            if (one.ExitCode != 0) exit = 1;
            if (carry.Game is null && ClientLauncher.IsClientRunning()) { say("[ModderLords] the game is still running (kept open?); the remaining launches are skipped."); return new InGameTestResult(1, reports, folders, "the game was kept open"); }
        }
        return new InGameTestResult(exit, reports, folders, null);
        }

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

    /// <summary>
    /// Before a launch: the calls into the game that each community mod in <paramref name="plan"/> (ModderLords.Compat
    /// included) and each test module DLL in <paramref name="extraDlls"/> makes, against the installed game's
    /// assemblies (its bin folder and the bin folders of the modules the launch loads). Null when nothing is missing or
    /// the check is skipped; otherwise why the run stops, naming each DLL and what it calls that the game lacks.
    /// </summary>
    private static string? CheckApi(ClientLaunchPlan plan, IEnumerable<string> extraDlls, InGameTestOptions options, Action<string> say)
    {
        if (options.SkipApiCheck) { say("[ModderLords] game API check skipped (--skip-api-check)"); return null; }
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var found = ApiProblems(plan, extraDlls, out var checkedCount);
        say($"[ModderLords] game API check: {checkedCount} DLL(s) against the installed game, {found.Count} with missing calls ({clock.ElapsedMilliseconds} ms)");
        if (found.Count == 0) return null;
        foreach (var line in found) say("[ModderLords]   " + line);
        return "a mod calls into the game what this game version no longer has, so it would fail or crash in the game: "
               + string.Join(" | ", found) + " (update the mod, or run with --skip-api-check to see it fail in the game)";
    }

    /// <summary>
    /// What <see cref="CheckApi"/> finds, one line per DLL with something missing: "Mod\bin\...\X.dll: method T::M (2
    /// parameters); ...". <paramref name="checkedCount"/> is how many DLLs were checked.
    /// </summary>
    public static IReadOnlyList<string> ApiProblems(ClientLaunchPlan plan, IEnumerable<string> extraDlls, out int checkedCount)
    {
        var game = ApiIndex.Of(ApiIndex.DllsIn([Path.Combine(plan.GameRoot, "bin", "Win64_Shipping_Client"), .. plan.SelectedModules.Select(m => m.ClientBin)]));
        // Only what the game loads: the DLLs each SubModule.xml names, and what they reference beside them. Framework
        // mods ship a DLL per game version (MCM's Bannerlord.MBOptionScreen.v1.2.12.dll, ...) and load the one that
        // fits at runtime; the others would all be reported.
        var mods = plan.SelectedModules.Where(m => !m.IsOfficial).ToList();
        var dlls = mods
            .SelectMany(m => ApiCheck.LoadedDlls(m.ClientBin, m.Info.SubModules.Select(s => s.DLLName)))
            .Concat(extraDlls.Where(File.Exists)).ToList();
        // A mod's Coop adapter (ModderLords.CompatSync.Coop, Retinues.Coop, CoopWarehouses.Coop) is loaded by name only
        // once Coop is there, so nothing the SubModule.xml names references it. With Coop in the launch it runs too.
        if (plan.SelectedModules.Any(m => CoopIds.Contains(m.Id)))
            dlls.AddRange(mods.SelectMany(m => CoopAdapters(m.ClientBin)).Where(a => !dlls.Contains(a, StringComparer.OrdinalIgnoreCase)));
        checkedCount = dlls.Count;
        return ApiCheck.CheckAll(dlls, game)
            .Select(f => $"{Shorten(f.Dll, plan.SelectedModules)}: {string.Join("; ", f.Missing.Take(8))}{(f.Missing.Count > 8 ? $"; and {f.Missing.Count - 8} more" : "")}")
            .ToList();
    }

    /// <summary>The DLLs in <paramref name="bin"/> named like a Coop adapter, "*.Coop.dll".</summary>
    public static IEnumerable<string> CoopAdapters(string bin) =>
        Directory.Exists(bin) ? Directory.EnumerateFiles(bin, "*.Coop.dll") : [];

    /// <summary>"ModId\X.dll" for a DLL in a selected module's folder; the full path otherwise.</summary>
    private static string Shorten(string dll, IEnumerable<Core.Modules.DiscoveredModule> modules) =>
        modules.FirstOrDefault(m => dll.StartsWith(Path.GetFullPath(m.FolderPath), StringComparison.OrdinalIgnoreCase)) is { } owner
            ? owner.Id + "\\" + Path.GetFileName(dll)
            : dll;

    /// <summary>
    /// The game API check alone, for <c>test --api-check</c>: the profile's client launch as a single-player test run
    /// would make it (ModderLords.Compat added, Coop left out unless <paramref name="singlePlayer"/> is false), and
    /// every test module DLL this launcher has. Returns the lines found; empty when nothing is missing.
    /// </summary>
    public static IReadOnlyList<string> ApiProblemsFor(Profile profile, bool singlePlayer, Action<string> say, string? bundledRoot = null)
    {
        if (singlePlayer)
            foreach (var mod in profile.Mods.Where(m => CoopIds.Contains(m.Id) && m.Enabled)) mod.Enabled = false;
        if (!profile.Mods.Any(m => m.Id.Equals(LaunchSession.SyncModuleId, StringComparison.OrdinalIgnoreCase)))
            profile.Mods.Add(new ProfileMod { Id = LaunchSession.SyncModuleId, Role = ServerRole.AsShipped });
        var client = ClientLaunchSession.Prepare(profile);
        var stepDlls = TestSuites.Modules().Where(m => m.StepsDll is not null)
            .Select(m => Path.Combine(bundledRoot ?? LaunchSession.BundledRoot ?? AppContext.BaseDirectory, "compat", m.StepsDll!));
        var found = ApiProblems(client.Plan, stepDlls, out var count);
        say($"[ModderLords] game API check: {count} DLL(s) of {profile.Name} against {client.Plan.GameRoot}");
        return found;
    }

    /// <summary>
    /// Copies each save a passed <c>game.keepWorld</c> step reported (values <c>keptSave</c> and <c>keptWorld</c>)
    /// from the game's saves into test-worlds, with a note beside it saying which run made it. An upgrade world is
    /// made this way on a mod's released version (decision 27).
    /// </summary>
    public static IReadOnlyList<string> KeepWorlds(TestReport report, string gameSaves, string worldsDir)
    {
        var said = new List<string>();
        foreach (var step in report.Steps.Where(s => s.Outcome == StepOutcome.Pass))
        {
            if (!step.Values.TryGetValue("keptSave", out var save) || !step.Values.TryGetValue("keptWorld", out var world)) continue;
            if (world.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || save.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                said.Add($"not keeping world \"{world}\": not a file name");
                continue;
            }
            var from = Path.Combine(gameSaves, save + ".sav");
            if (!File.Exists(from)) { said.Add($"not keeping world {world}: {from} is missing"); continue; }
            Directory.CreateDirectory(worldsDir);
            var to = Path.Combine(worldsDir, world + ".sav");
            var replaced = File.Exists(to);
            File.Copy(from, to, overwrite: true);
            var note = new System.Text.Json.Nodes.JsonObject
            {
                ["world"] = world,
                ["madeBy"] = $"{report.RunId} {step.Suite}/{step.Step}".Trim(),
                ["at"] = DateTimeOffset.Now.ToString("o", System.Globalization.CultureInfo.InvariantCulture),
                ["versions"] = System.Text.Json.Nodes.JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(report.Versions)),
            };
            File.WriteAllText(Path.Combine(worldsDir, world + ".json"), note.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            said.Add($"kept world {world}{(replaced ? " (replaced the one there)" : "")} -> {to}");
        }
        return said;
    }

    /// <summary>
    /// Keeps the world a passed <c>coop.saveWorld</c> step asked for (value <c>keptWorld</c>): the server's shutdown save
    /// <paramref name="serverSave"/>, copied into test-worlds with Coop's session file beside it as <c>&lt;world&gt;.json</c>
    /// (what <see cref="SavePreparer.ImportFrom"/> copies back) and a note as <c>&lt;world&gt;.kept.json</c>. Call it once
    /// the server has stopped.
    /// </summary>
    public static IReadOnlyList<string> KeepCoopWorlds(TestReport report, string serverSaves, string serverSave, string worldsDir, Dictionary<string, string>? kept = null)
    {
        var said = new List<string>();
        foreach (var step in report.Steps.Where(s => s.Outcome == StepOutcome.Pass && s.Step == "coop.saveWorld"))
        {
            if (!step.Values.TryGetValue("keptWorld", out var world)) continue;
            if (world.Length == 0 || world.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                said.Add($"not keeping world \"{world}\": not a file name");
                continue;
            }
            var from = Path.Combine(serverSaves, serverSave + ".sav");
            var session = SavePreparer.SessionPath(from);
            if (!File.Exists(from) || !File.Exists(session)) { said.Add($"not keeping world {world}: {(File.Exists(from) ? session : from)} is missing"); continue; }
            Directory.CreateDirectory(worldsDir);
            var to = Path.Combine(worldsDir, world + ".sav");
            File.Copy(from, to, overwrite: true);
            File.Copy(session, SavePreparer.SessionPath(to), overwrite: true);
            var note = new System.Text.Json.Nodes.JsonObject
            {
                ["world"] = world,
                ["madeBy"] = $"{report.RunId} {step.Suite}/{step.Step}".Trim(),
                ["from"] = from,
                ["at"] = DateTimeOffset.Now.ToString("o", System.Globalization.CultureInfo.InvariantCulture),
                ["versions"] = System.Text.Json.Nodes.JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(report.Versions)),
            };
            File.WriteAllText(Path.Combine(worldsDir, world + ".kept.json"), note.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            if (kept is not null && step.Suite.Length > 0) kept[step.Suite] = world;
            said.Add($"kept the server's shutdown save {serverSave} with its Coop session as test world {world} -> {to}");
        }
        return said;
    }

    /// <summary>
    /// <c>--package</c>: checks the release zip; if it is fit to publish, unpacks it into
    /// %LOCALAPPDATA%\ModderLords\package-tests\&lt;Mod&gt;-&lt;version&gt;-&lt;hash&gt; and pins that copy in the profile, in memory.
    /// With no suite or modules named, the run then picks the test modules that require the mod. Null when ready; the
    /// reason to stop otherwise.
    /// </summary>
    private static string? PreparePackage(string zip, Profile profile, ref InGameTestOptions options, Action<string> say)
    {
        if (!File.Exists(zip)) return "package not found: " + zip;
        var modId = PackageCheck.ModIdOf(zip);
        var modules = modId is null ? [] : TestSuites.Modules().Where(m => m.Requires.Any(r => r.Id.Equals(modId, StringComparison.OrdinalIgnoreCase))).ToList();
        var atLeast = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, n) in modules.SelectMany(m => m.PackageAtLeast)) atLeast[path] = Math.Max(n, atLeast.GetValueOrDefault(path));
        var report = PackageCheck.Inspect(zip, modId, options.PackageVersion, atLeast);
        foreach (var line in report.ToText().Split('\n')) say("[ModderLords] " + line.TrimEnd());
        if (!report.Ok) return "the release zip is not fit to publish: " + string.Join("; ", report.Problems);
        if (options.PackageCheckOnly) return null;

        var parent = Path.Combine(ProfileStore.RootDir, "package-tests", $"{report.ModId}-{report.Version}-{report.Sha256[..8]}");
        string folder;
        try { folder = PackageCheck.Unpack(zip, report.ModId!, parent); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { return "could not unpack the release zip: " + ex.Message; }
        var mod = profile.Mods.FirstOrDefault(m => m.Id.Equals(report.ModId, StringComparison.OrdinalIgnoreCase));
        if (mod is null) profile.Mods.Add(mod = new ProfileMod { Id = report.ModId!, Role = ServerRole.AsShipped });
        mod.Enabled = true;
        mod.SourcePath = folder;   // in memory: the profile on disk is not changed
        say($"[ModderLords] package unpacked to {folder}; this run loads {report.ModId} from there");
        if (options.Suite is null && options.Modules is null)
        {
            if (modules.Count == 0) return $"no test module requires {report.ModId}, so there is nothing to run the package against (name --modules or --suite)";
            options = options with { Modules = modules.Select(m => m.Id).ToList() };
            say("[ModderLords] testing it with " + string.Join(", ", modules.Select(m => m.Id)));
        }
        return null;
    }

    /// <summary>
    /// The game version a kept test world was saved on: the save's own header ("v1.5.7.125802"), else the version
    /// its note recorded when it was kept (versions.game.game); null when neither says.
    /// </summary>
    public static string? WorldSavedOn(string worldsDir, string world)
    {
        if (Core.Saves.SaveHeaderReader.TryRead(Path.Combine(worldsDir, world + ".sav"), out _) is { ApplicationVersion: { Length: > 0 } fromHeader })
            return fromHeader;
        try
        {
            var note = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(worldsDir, world + ".json")));
            return note?["versions"]?["game"]?["game"]?.GetValue<string>() is { Length: > 0 } fromNote ? fromNote : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException) { return null; }
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
            if (MissingStepsDll(m!, options.BundledRoot) is { } missing)
                return (loads, $"test module {m!.Id}: {missing} is missing (is it built into the compat folder?)");
            loads.Add(new ModuleLoad(m!.Id, StepsDllPath(m, options.BundledRoot), m.StepsType!, m.Requires.Select(r => r.Id).ToList()));
        }
        return (loads, null);
    }

    private static string StepsDllPath(TestModule m, string? bundledRoot) =>
        Path.Combine(bundledRoot ?? LaunchSession.BundledRoot ?? AppContext.BaseDirectory, "compat", m.StepsDll!);

    /// <summary>
    /// Where a module's step DLL should be, when this build lacks it; null when it is there or the module has none.
    /// Such a DLL is built only on a PC with its mod installed (Directory.Build.props), so a release can lack it.
    /// </summary>
    public static string? MissingStepsDll(TestModule m, string? bundledRoot = null) =>
        m.StepsDll is null || File.Exists(StepsDllPath(m, bundledRoot)) ? null : StepsDllPath(m, bundledRoot);

    /// <summary>
    /// The player's game carried from one launch of a Coop run to the next: the server restarts on each fresh world,
    /// the game stays open at the main menu and joins it again.
    /// </summary>
    private sealed class CoopClientCarry
    {
        /// <summary>The next launch's plan name, so this launch hands its game over to it; null on the last launch.</summary>
        public string? NextName { get; set; }
        /// <summary>The game kept at the main menu, already told to go on in <see cref="Next"/>'s run folder.</summary>
        public System.Diagnostics.Process? Game { get; set; }
        public FileTestRunChannel.Reservation? Next { get; set; }
        /// <summary>engine_config.txt before the run's first game started; put back once the last game closes.</summary>
        public EngineConfigGuard? Config { get; set; }
        /// <summary>The kept game never got back onto the map: the caller starts this launch again with a new game.</summary>
        public bool RejoinFailed { get; set; }
    }

    /// <summary>How long a kept game may take to answer the next run's hello once the launcher asks it to join.</summary>
    private static readonly TimeSpan JoinHelloTimeout = TimeSpan.FromMinutes(2);

    /// <summary>How long the player's game may take to leave to the main menu between suites.</summary>
    private static readonly TimeSpan LeaveTimeout = TimeSpan.FromMinutes(4);

    private static async Task CloseGame(System.Diagnostics.Process game)
    {
        try { game.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
        try { await game.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token); } catch (OperationCanceledException) { }
    }

    /// <param name="kept">Filled with each suite's kept world (suite → test world) when its <c>coop.saveWorld</c> passed.</param>
    /// <param name="carry">
    /// The run's player's game between launches: reused when one is kept, handed on at the end when there is a next
    /// launch; null for a run of one launch.
    /// </param>
    private static async Task<(int ExitCode, TestReport? Report, string? Folder, string? Problem)> RunCoop(Profile profile, TestPlan testPlan,
        InGameTestOptions options, Action<string> say, CancellationToken cancel, Dictionary<string, string>? kept = null, CoopClientCarry? carry = null)
    {
        (int, TestReport?, string?, string?) Stop(string problem)
        {
            say("[ModderLords] " + problem);
            // A game kept for this launch was told to run here: it cannot serve the next one.
            if (carry?.Game is { } waiting)
            {
                if (!waiting.HasExited) CloseGame(waiting).GetAwaiter().GetResult();
                carry.Game = null;
                carry.Next = null;
                if (carry.Config is { } config) say("[ModderLords] " + config.Restore());
            }
            return (2, null, null, problem);
        }
        async Task CloseAndRestore(System.Diagnostics.Process? game)
        {
            if (game is { HasExited: false }) await CloseGame(game);
            if (carry?.Config is { } config) say("[ModderLords] " + config.Restore());
        }
        if (profile.Server.Password.Length > 0) return Stop("This server has a password, and Coop's automatic join cannot send one. Clear it for the test.");
        var paths = options.Paths ?? LaunchSession.ResolvePaths(profile);

        // Every run starts from the same world, copied fresh over the last run's copy.
        var world = options.World ?? testPlan.World;

        // A profile whose world is generated (TAOM, or the Host tab's tick box) gets a test world of its own, generated
        // once with the Host tab's code and kept under test-worlds; from then on it is a named world like any other.
        // The profile's own save is left alone.
        if (world is null && WorldCreationRun.Generates(profile))
        {
            world = ProfileTestWorld.Name(profile.Name);
            var mods = profile.EnabledMods.Select(m => m.Id).ToList();
            if (ProfileTestWorld.Stale(TestSuites.WorldsDir, world, mods, options.NewWorld) is { } why)
            {
                var generated = "mltest-" + world;
                say($"[ModderLords] {why}: generating '{world}' with this profile's mods, as the Host tab does; a large mod set takes several minutes");
                try
                {
                    // Creation only runs when the save is missing; this name is the harness's own copy.
                    foreach (var file in new[] { SavePreparer.SavePath(paths, generated), SavePreparer.SessionPath(SavePreparer.SavePath(paths, generated)) })
                        if (File.Exists(file)) File.Delete(file);
                    profile.SaveName = generated;
                    var creating = LaunchSession.Prepare(profile, allowWorldCreation: true);
                    foreach (var m in creating.Messages) say("[ModderLords] " + m);
                    await WorldCreationRun.RunAsync(creating, generated, profile.Server, Path.Combine(ProfileStore.RootDir, "logs"),
                        m => say("[ModderLords] " + m),
                        new WorldCreationRun.Hooks
                        {
                            Line = line =>
                            {
                                if (line.Text.Contains("worldcreate:", StringComparison.OrdinalIgnoreCase)) say("[ModderLords] " + line.Text.Trim());
                            },
                        }, cancel);
                    var keptAt = ProfileTestWorld.Keep(SavePreparer.SavePath(paths, generated), TestSuites.WorldsDir, world, mods);
                    say($"[ModderLords] test world kept -> {keptAt}");
                }
                catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
                {
                    return Stop("could not generate the test world: " + ex.Message);
                }
            }
        }

        if (world is not null)
        {
            var worldFile = TestSuites.WorldPath(world);
            if (!File.Exists(worldFile)) return Stop($"test world not found: {worldFile} (put a .sav there, with its co-op session file beside it)");
            try
            {
                // The last run's copy is only a copy: replaced, not kept beside it.
                var imported = SavePreparer.ImportFrom(worldFile, paths, "mltest-" + world, overwrite: true, testCopy: true);
                foreach (var w in imported.Warnings) say("[ModderLords] WARNING " + w);
                say($"[ModderLords] world {world} copied fresh -> {imported.SavePath}");
            }
            catch (Exception ex) { return Stop("could not copy the test world: " + ex.Message); }
            profile.SaveName = "mltest-" + world;
        }

        LaunchSession.Prepared prepared;
        try { prepared = LaunchSession.Prepare(profile); }
        catch (InvalidOperationException ex) { return Stop(ex.Message); }
        if (ServerModuleListCheck.Problem(prepared.Plan.ModuleIds, prepared.Selections.Select(s => s.Module.Id),
                prepared.Catalog.ForeignServerModules) is { } unrequested)
        {
            foreach (var m in prepared.Messages) say("[ModderLords] " + m);
            return Stop(unrequested);
        }
        // A game kept from the launch before waits at the main menu for this run's folder, which it was told about.
        var keptGame = carry?.Game is { HasExited: false } carried && carry.Next is not null ? carried : null;
        var testsRoot = Path.Combine(ProfileStore.RootDir, "tests");
        var channel = FileTestRunChannel.Create(testsRoot, testPlan, DateTimeOffset.Now, keptGame is not null ? carry!.Next : null);
        say($"[ModderLords] test run {channel.RunId} -> {channel.Folder}");
        foreach (var m in prepared.Messages) say("[ModderLords] " + m);
        if (!prepared.Selections.Any(sel => sel.Module.Id.Equals(LaunchSession.SyncModuleId, StringComparison.OrdinalIgnoreCase)))
            return Stop($"the test agent lives in {LaunchSession.SyncModuleId}, which this profile does not load.");
        // The kept game has it loaded: its files are in use, and the first launch installed them.
        if (keptGame is null) say("[ModderLords] " + ClientModuleInstaller.Ensure(ClientLauncher.ResolveGameRoot(profile)).Message);
        if (carry is not null) carry.Config ??= new EngineConfigGuard(EngineConfigGuard.DefaultPath);
        var handOver = carry?.NextName is not null;
        ClientLaunchSession.Prepared client;
        try { client = ServerMatchedClient.Prepare(prepared, profile); }
        catch (Exception ex) { return Stop("client plan: " + ex.Message); }
        if (CheckApi(client.Plan, testPlan.Loads.Select(l => l.Dll), options, say) is { } broken) return Stop(broken);
        // The copies picked for each side, hashed now, for mods.fingerprint to check the loaded copies against.
        foreach (var line in PickedCopies.Write(channel.Folder, "server", prepared.Selections.Select(s => s.Module))
            .Concat(PickedCopies.Write(channel.Folder, "client:1", client.Plan.SelectedModules, client.Plan.ExcludedFolders)))
            say("[ModderLords] picked " + line);
        // The switch reaches the server through its own launch plan only, never this process's environment, so the
        // player's game (started from here) cannot inherit it and no ordinary Host launch can carry it.
        var serverPlan = prepared.Plan.WithEnvironment(FileTestRunChannel.ServerVariable, channel.SwitchValue("server"));
        var serverDump = new ServerDump(channel.Folder);
        foreach (var (name, value) in serverDump.Environment) serverPlan = serverPlan.WithEnvironment(name, value);

        string? gameGone = null;
        EngineProcess? engine = null;
        // Rejoining: Coop's join failing ends the wait for the map at once, rather than at the smoke test's timeouts.
        using var joinFailed = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        // The server and the player's game write crash reports to the same folder; either one crashing ends the run.
        var crash = new CrashWatch(ServerPaths.CrashesDir)
        {
            WhoIs = pid => engine?.ProcessId?.ToString(System.Globalization.CultureInfo.InvariantCulture) == pid ? "the server" : "the game",
        };
        // The server's own exit names its cause (an access violation, say); its watchdog log may say nothing yet.
        string? ServerGone() => engine is { Exited.IsCompleted: true } e
            ? $"the server exited during the step: {ExitCodeExplainer.Explain(e.Exited.Result)}"
            : null;
        // A crashed server is kept alive while it writes its dump, so it has not exited yet: say so at once rather than
        // waiting out the step as a game that "may have crashed or hung".
        string? ServerDumping() => serverDump.Writing ? $"the server crashed (its dump is being written to {serverDump.Folder})" : null;
        var sequencer = new TestSequencer(channel) { Abort = () => gameGone ?? ServerGone() ?? ServerDumping() ?? crash.Check() };
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
            CloseClientWhenDone = !options.KeepClient && !handOver,
            ClientAlreadyRunning = keptGame is not null,
            EngineConfig = carry?.Config,
            ReportRoot = channel.Folder,
            // No time command: the steps pause the campaign themselves (game.pause), so the numbers they read hold still.
            SendServerCommand = null,
            ExtraLaunchArguments = [FileTestRunChannel.ClientArgument, channel.SwitchValue("client:1")],
            // Handing over, the run is ended after the game has left (the sequencer's End below).
            WhileOnMap = token => testRun = sequencer.RunAsync(testPlan, channel.RunId, cancel, end: !handOver),
        });
        runner.Progress += line => say("[smoke] " + line);

        using var testLogWriter = new StreamWriter(Path.Combine(channel.Folder, "server-console.log")) { AutoFlush = true };
        using var server = EngineProcess.Start(serverPlan);
        engine = server;
        server.LineReceived += line =>
        {
            runner.ServerLine(line.Text);
            serverDump.Observe(line.Text);
            lock (testLogWriter) testLogWriter.WriteLine($"{line.At:HH:mm:ss.fff} {line.Text}");
        };
        _ = server.Exited.ContinueWith(t => runner.ServerExited(t.Result), TaskScheduler.Default);

        using var windowWatch = new CancellationTokenSource();
        var responder = CrashResponder.RunAsync(crash, () => serverDump.Writing, say, windowWatch.Token);
        Task? joinWatch = null;
        if (carry is not null) { carry.Game = null; carry.Next = null; }
        var smokeReport = await runner.RunAsync(args =>
        {
            if (keptGame is not null)
            {
                // The server is serving: the kept game joins it as Coop's own automatic join would at start.
                say("[ModderLords] the game kept from the last suite joins the restarted server");
                channel.Send(new System.Text.Json.Nodes.JsonObject
                {
                    ["op"] = "step", ["seq"] = 0, ["side"] = "client:1", ["step"] = "coop.join",
                    ["args"] = new System.Text.Json.Nodes.JsonObject(), ["timeoutMs"] = (long)TimeSpan.FromMinutes(10).TotalMilliseconds,
                }.ToJsonString());
                joinWatch = WatchJoin(channel.Folder, say, joinFailed, windowWatch.Token);
                _ = TestGameWindow.Watch(keptGame, options.Minimized, note => say("[ModderLords] " + note), windowWatch.Token);
                return keptGame;
            }
            var started = ClientLaunchSession.Start(client.Plan with { ExtraArguments = args }, note => say("[ModderLords] " + note));
            _ = TestGameWindow.Watch(started, options.Minimized, note => say("[ModderLords] " + note), windowWatch.Token);
            return started;
        }, joinFailed.Token);
        // The game or server may have gone before the steps finished; the step waiting is then reported as such.
        gameGone = "the game or the server exited before the step finished";
        var testReport = testRun is null ? null : await testRun;
        // A game kept open is the player's from here.
        windowWatch.Cancel();
        if (joinWatch is not null) await joinWatch;
        var game = runner.Client ?? keptGame;
        if (keptGame is not null && testReport is null && carry is not null)
        {
            // The kept game never got back onto the map: no step of this suite ran, so the caller runs it again with a
            // game of its own.
            carry.RejoinFailed = true;
            sequencer.End();
            await CloseAndRestore(game);
        }
        else if (handOver)
        {
            // Between suites: the game leaves to the main menu while the server still runs, then is told where the next
            // suite's run will be. Anything that went wrong closes it instead, and the next suite starts its own.
            gameGone = null;
            var fit = testReport is not null && game is { HasExited: false } && crash.CrashedSides().Count == 0 && !server.Exited.IsCompleted;
            var leave = fit ? await sequencer.RunStepAsync("client:1", "coop.leave", LeaveTimeout, cancel) : null;
            if (leave?.Outcome == StepOutcome.Pass)
            {
                var next = FileTestRunChannel.Reserve(testsRoot, carry!.NextName!, DateTimeOffset.Now);
                sequencer.End("client:1", next);
                carry.Game = game;
                carry.Next = next;
                say("[ModderLords] the game left to the main menu; it stays open for the next suite");
            }
            else
            {
                sequencer.End();
                if (leave is not null) say($"[ModderLords] the game could not leave to the main menu ({leave.Outcome}: {leave.Message}); closing it, the next suite starts its own");
                await CloseAndRestore(game);
            }
        }
        say("");
        say(smokeReport.ToText());
        if (testReport is null) say("[ModderLords] the game never reached the campaign map, so no step ran; see the smoke report above.");
        else
        {
            await File.WriteAllTextAsync(Path.Combine(channel.Folder, "report.json"), testReport.ToJson(), CancellationToken.None);
            await File.WriteAllTextAsync(Path.Combine(channel.Folder, "report.txt"), testReport.ToText(), CancellationToken.None);
            say(testReport.ToText());
        }
        // A crashed server writes its dump before anything may close it; then the crash prompts are answered.
        await serverDump.WaitAsync(() => !server.Exited.IsCompleted, say);
        await responder;
        if (crash.CrashedSides().Count > 0 && CrashPrompt.AnswerNo() is > 0 and var answered)
            say($"[ModderLords] answered {answered} crash prompt(s) with No; the engine logs are kept in the run folder");
        if (server.Exited.IsCompleted && server.Exited.Result != 0)
            say($"[ModderLords] the server exited: {ExitCodeExplainer.Explain(server.Exited.Result)}");
        foreach (var side in crash.CrashedSides()) say($"[ModderLords] {side} crashed in this run");
        foreach (var file in crash.CopyLogs(channel.Folder)) say("[ModderLords] crash log kept: " + file);
        foreach (var file in serverDump.Files()) say($"[ModderLords] server dump kept: {file} (open with dotnet-dump analyze; clrstack -all)");
        say($"[ModderLords] evidence and logs: {channel.Folder}");
        if ((!options.KeepServer || handOver) && !server.Exited.IsCompleted)
        {
            say("[ModderLords] stopping the server…");
            await server.StopAsync(TimeSpan.FromSeconds(60));
        }
        // The world a coop.saveWorld step asked to keep is the server's shutdown save, so it is copied once the server
        // has stopped (and a server kept running has saved nothing yet).
        if (testReport is not null && server.Exited.IsCompleted)
            foreach (var line in KeepCoopWorlds(testReport, paths.SavesDir, profile.SaveName, TestSuites.WorldsDir, kept)) say("[ModderLords] " + line);
        // Both reports count: steps that pass in a game that then crashed, or where Coop logged a fatal error, still fail.
        var passed = testReport?.Passed == true && smokeReport.Overall != Core.Smoke.SmokeVerdict.Fail;
        return (passed ? 0 : 1, testReport, channel.Folder, testReport is null ? "the game never reached the campaign map" : null);
    }

    /// <summary>
    /// Watches the kept game's <c>coop.join</c> (sent with seq 0, outside the plan) in the run folder, and cancels
    /// <paramref name="failed"/> when it fails, so the wait for the map ends and the suite is started again.
    /// </summary>
    private static async Task WatchJoin(string folder, Action<string> say, CancellationTokenSource failed, CancellationToken stop)
    {
        var tail = new Core.Smoke.LogTail(Path.Combine(folder, FileTestRunChannel.ResultsFile("client:1")), fromStart: true);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var hello = false;
        while (!stop.IsCancellationRequested && !failed.IsCancellationRequested)
        {
            // The kept game reads run.json within a frame of it appearing; one that has not answered by now never will
            // (its agent stopped), and the smoke test's own limits would wait out a still-busy game for 20 minutes.
            if (!hello && clock.Elapsed > JoinHelloTimeout)
            {
                say($"[ModderLords] the kept game never joined this run (no hello within {JoinHelloTimeout.TotalSeconds:F0} s)");
                failed.Cancel();
                return;
            }
            foreach (var line in tail.ReadNew())
            {
                System.Text.Json.Nodes.JsonNode? node;
                try { node = System.Text.Json.Nodes.JsonNode.Parse(line); }
                catch (System.Text.Json.JsonException) { continue; }
                if (node?["op"]?.ToString() == "hello") hello = true;
                if (node?["op"]?.ToString() != "result" || node["seq"]?.ToString() != "0") continue;
                if (node["status"]?.ToString() == "pass") { say("[ModderLords] the kept game joined the restarted server"); return; }
                say($"[ModderLords] the kept game could not join: {node["message"]}");
                failed.Cancel();
                return;
            }
            try { await Task.Delay(500, stop); } catch (OperationCanceledException) { return; }
        }
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
        if (CheckApi(client.Plan, testPlan.Loads.Select(l => l.Dll), options, say) is { } broken) return Stop(broken);

        var channel = FileTestRunChannel.Create(Path.Combine(ProfileStore.RootDir, "tests"), testPlan, DateTimeOffset.Now);
        say($"[ModderLords] single-player test run {channel.RunId} -> {channel.Folder}");
        foreach (var line in PickedCopies.Write(channel.Folder, TestPlan.GameSide, client.Plan.SelectedModules, client.Plan.ExcludedFolders))
            say("[ModderLords] picked " + line);
        var plan = client.Plan with { ExtraArguments = [FileTestRunChannel.ClientArgument, channel.SwitchValue(TestPlan.GameSide)] };
        say("[ModderLords] " + plan.Describe());

        var crash = new CrashWatch(ServerPaths.CrashesDir);
        System.Diagnostics.Process game;
        try { game = ClientLaunchSession.Start(plan, note => say("[ModderLords] " + note)); }
        catch (Exception ex) { return Stop("could not start the game: " + ex.Message); }
        using var windowWatch = new CancellationTokenSource();
        _ = TestGameWindow.Watch(game, options.Minimized, note => say("[ModderLords] " + note), windowWatch.Token);
        var responder = CrashResponder.RunAsync(crash, () => false, say, windowWatch.Token);

        // The run starts at launch here, not on the map as in Coop, so the hello waits out the game's start-up; a game
        // that exits or crashes on the way ends the wait sooner.
        var sequencer = new TestSequencer(channel, new TestSequencerOptions { HelloTimeout = TimeSpan.FromMinutes(10) })
        {
            Abort = () => game.HasExited ? $"the game exited (code {game.ExitCode})" : crash.Check(),
        };
        sequencer.Progress += line => say("[test] " + line);
        var testReport = await sequencer.RunAsync(testPlan, channel.RunId, cancel);
        windowWatch.Cancel();
        await responder;
        if (crash.CrashedSides().Count > 0 && CrashPrompt.AnswerNo() is > 0 and var answered)
            say($"[ModderLords] answered {answered} crash prompt(s) with No; the engine logs are kept in the run folder");

        await File.WriteAllTextAsync(Path.Combine(channel.Folder, "report.json"), testReport.ToJson(), CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(channel.Folder, "report.txt"), testReport.ToText(), CancellationToken.None);
        say("");
        say(testReport.ToText());
        say($"[ModderLords] evidence and logs: {channel.Folder}");
        foreach (var line in KeepWorlds(testReport, Path.Combine(GamePaths.UserDataDir(), "Game Saves"), TestSuites.WorldsDir))
            say("[ModderLords] " + line);

        foreach (var file in crash.CopyLogs(channel.Folder)) say("[ModderLords] crash log kept: " + file);

        // A crashed game is closed even when asked to keep it: its crash prompt was answered No, so no dump is written;
        // the engine logs copied above say what crashed.
        var keep = !crash.Crashed && (options.KeepClient || (options.KeepOpenOnFail && !testReport.Passed));
        if (keep && !game.HasExited) say("[ModderLords] the game was left running, as asked");
        else if (!game.HasExited)
        {
            say(crash.Crashed ? "[ModderLords] closing the crashed game and its crash dialog…" : "[ModderLords] closing the game…");
            try { game.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
            try { await game.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token); } catch (OperationCanceledException) { }
        }
        return (testReport.Passed ? 0 : 1, testReport, channel.Folder, null);
    }
}
