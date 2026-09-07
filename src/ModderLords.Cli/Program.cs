using ModderLords.Core.Export;
using ModderLords.Core.Launch;
using ModderLords.Coop.Launch;
using ModderLords.Core.Logs;
using ModderLords.Core.Modules;
using ModderLords.Core.Overlay;
using ModderLords.Core.Profiles;
using ModderLords.Core.Saves;
using ModderLords.Coop.Saves;

// Command-line driver (Phase 0/1). Commands:
//   catalog  [--root <DedicatedServer>] [--source <dir>]...
//   sync     --mods Id[:Run|DependencyOnly|AsShipped],...  [--remove-all]
//   launch   [--mods ...] [--save NAME] [--port 7210] [--region EU] [--dry-run] [--quiet-engine] [--stop-after SECONDS]

DataDirMigration.RunIfNeeded();

var opts = ParseArgs(args);
if (!opts.TryGetValue("cmd", out var cmd))
{
    Console.WriteLine("commands: catalog | sync --mods Id[:Role],... [--remove-all] | launch [--mods ...] [--save NAME] [--port N] [--region EU] [--dry-run] [--quiet-engine] [--stop-after S]");
    Console.WriteLine("          play --profile NAME [--dry-run]   (start the player's own game with a profile's mods)");
    return 1;
}

// The player-side launch needs no dedicated server at all, so it runs before the server package is resolved.
if (cmd == "play")
{
    var profileName = opts.GetValueOrDefault("profile") ?? "default";
    var playProfile = ProfileStore.Load(profileName);
    if (playProfile is null) { Console.Error.WriteLine($"No profile named '{profileName}'. Try: profiles"); return 2; }

    ClientLaunchSession.Prepared prepared;
    try { prepared = ClientLaunchSession.Prepare(playProfile); }
    catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 2; }

    foreach (var m in prepared.Messages) Console.WriteLine("[ModderLords] " + m);
    Console.WriteLine($"game   : {prepared.GameRoot}");
    Console.WriteLine($"mods   : {prepared.Mods.Count} enabled, {prepared.Officials.Count} official");
    foreach (var id in prepared.Plan.ModuleIds)
    {
        var origin = prepared.Officials.FirstOrDefault(m => m.Id == id)?.Source
                  ?? prepared.Mods.FirstOrDefault(m => m.Id == id)?.Source;
        Console.WriteLine($"  {id,-32} {origin}");
    }
    Console.WriteLine("command: " + prepared.Plan.Describe());
    if (opts.ContainsKey("dry-run")) return 0;

    if (ClientLauncher.IsClientRunning()) { Console.Error.WriteLine("Bannerlord (or its launcher) is already running."); return 2; }
    var started = ClientLaunchSession.Start(prepared.Plan);
    Console.WriteLine($"started pid {started.Id}");
    return 0;
}

var root = opts.GetValueOrDefault("root") ?? ServerPaths.FindWorkshopDedicatedServerRoots().FirstOrDefault();
if (root is null) { Console.Error.WriteLine("No DedicatedServer folder found; pass --root."); return 2; }
var paths = ServerPaths.Create(root);
var problems = paths.Validate().ToList();
foreach (var p in problems) Console.Error.WriteLine("[ModderLords] " + p);
if (problems.Count > 0) return 2;

var libraries = ServerPaths.SteamLibraries().ToList();
var gameRoot = opts.GetValueOrDefault("game") ?? ModuleCatalog.FindGameRoot(libraries);
var customRoots = opts.TryGetValue("source", out var src) ? src.Split(';', StringSplitOptions.RemoveEmptyEntries) : [];
var catalog = ModuleCatalog.Scan(paths.ModulesRoot, gameRoot, libraries, customRoots);
foreach (var p in catalog.Problems) Console.Error.WriteLine("[ModderLords] catalog: " + p);

var overlayRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ModderLords", "overlay", "default");
var applier = new OverlayApplier { KeepForDependencyOnly = LaunchSession.KeepForDependencyOnly };

switch (cmd)
{
    case "profiles":
        Console.WriteLine("profiles dir: " + ProfileStore.ProfilesDir);
        foreach (var n in ProfileStore.List()) Console.WriteLine("  " + n + (ProfileStore.Load(n) is null ? "  (FAILS TO LOAD)" : ""));
        return 0;

    case "catalog":
        Console.WriteLine($"game root : {gameRoot ?? "(not found)"}");
        Console.WriteLine($"server    : {paths.DedicatedServerRoot}");
        foreach (var m in catalog.Modules.OrderBy(m => m.IsStock ? 0 : 1).ThenBy(m => m.Id))
        {
            var bins = (m.HasServerBin ? "S" : "-") + (m.HasClientBin ? "C" : "-");
            var flags = (m.IsOfficial ? " official" : "") + (m.HasHeadlessExclusions ? " client-only-tags" : "") + (m.HasCode ? "" : " data-only");
            Console.WriteLine($"{m.Source,-11} {m.Id,-28} {m.Version,-14} bins={bins}{flags}  {m.FolderPath}");
        }
        return 0;

    case "sync":
    {
        if (opts.ContainsKey("remove-all"))
        {
            foreach (var r in applier.RemoveAll(overlayRoot)) Console.WriteLine("[ModderLords] removed junction " + r);
            return 0;
        }
        var selections = ParseSelections(opts.GetValueOrDefault("mods"), catalog, out var selErrors);
        foreach (var e in selErrors) Console.Error.WriteLine("[ModderLords] " + e);
        if (selErrors.Count > 0) return 2;
        var result = ApplyOverlay(selections);
        return result is null ? 2 : 0;
    }

    // What Launch client would do to this PC's Bannerlord launcher mod list, without touching anything.
    case "client-plan" when opts.ContainsKey("profile"):
    {
        var name = opts["profile"];
        var profile = ProfileStore.Load(name);
        if (profile is null) { Console.Error.WriteLine("[ModderLords] no such profile: " + name); return 2; }
        var prepared = LaunchSession.Prepare(profile, applySideEffects: false);
        var installed = prepared.Catalog.Modules
            .Where(m => m.Source is ModuleSourceKind.GameModules or ModuleSourceKind.Workshop)
            .Select(m => m.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var launcherData = ClientManifest.DefaultLauncherDataPath();

        Console.WriteLine("launcher data : " + launcherData);
        Console.WriteLine("server order  : " + string.Join(", ", prepared.Order.ModuleIds));
        foreach (var issue in prepared.Order.Issues) Console.WriteLine("order issue   : " + issue);
        Console.WriteLine("server mods   : " + string.Join(", ", ClientManifest.From(prepared.Modules).Select(e => $"{e.Id} {e.Version}")));
        Console.WriteLine("client-visible: " + string.Join(", ", installed.OrderBy(x => x)));
        var plan = LauncherDataSync.ComputePlan(ClientManifest.From(prepared.Modules), prepared.Order, launcherData, installed);
        Console.WriteLine("target order  : " + string.Join(", ", plan.TargetOrder));
        Console.WriteLine(plan.Changes.Count == 0 ? "no changes" : "changes:");
        foreach (var ch in plan.Changes) Console.WriteLine("  " + ch);
        return 0;
    }

    case "launch" when opts.ContainsKey("profile"):    case "launch" when opts.ContainsKey("profile"):
    {
        // Same path the app uses: profile -> overlay -> config -> recipes -> engine.
        var profile = ProfileStore.Load(opts["profile"]);
        if (profile is null) { Console.Error.WriteLine("[ModderLords] profile not found: " + opts["profile"]); return 2; }
        if (opts.TryGetValue("save", out var ps)) profile.SaveName = ps;
        var prepared = LaunchSession.Prepare(profile, applySideEffects: !opts.ContainsKey("dry-run"));
        foreach (var m in prepared.Messages) Console.WriteLine("[ModderLords] " + m);
        Console.WriteLine("[ModderLords] launch plan");
        Console.Write(prepared.Plan.Describe());
        if (opts.ContainsKey("dry-run")) return 0;
        ProfileStore.Save(profile);
        return await RunEngine(prepared.Plan, opts);
    }

    case "launch":
    {
        var selections = ParseSelections(opts.GetValueOrDefault("mods"), catalog, out var selErrors);
        foreach (var e in selErrors) Console.Error.WriteLine("[ModderLords] " + e);
        if (selErrors.Count > 0) return 2;
        if (opts.ContainsKey("settings-sync"))
        {
            var sync = LaunchSession.LocateSyncModule();
            if (sync is null) Console.Error.WriteLine("[ModderLords] --settings-sync: module not found under compat\\ next to the CLI");
            else selections.Add(new ModSelection(sync, ServerRole.AsShipped));
        }
        if (opts.ContainsKey("compat"))
        {
            var compat = LaunchSession.LocateCompatModule();
            if (compat is null) Console.Error.WriteLine("[ModderLords] --compat: module not found under compat\\ next to the CLI");
            else selections.Add(new ModSelection(compat, ServerRole.AsShipped));
        }
        foreach (var s in selections)
        {
            var scan = ModderLords.Core.Compat.AssemblyScan.Scan(s.Module);
            Console.WriteLine($"[ModderLords] scan {s.Module.Id,-28} {scan.Summary}" + (scan.Notes.Count > 0 ? "  (" + string.Join("; ", scan.Notes) + ")" : ""));
        }

        var stock = catalog.Modules.Where(m => m.IsStock).ToList();
        var order = LoadOrder.Compute(stock, selections.Select(s => s.Module).ToList());
        foreach (var i in order.Issues) Console.WriteLine("[ModderLords] order: " + i);

        // Overlay first (it decides the junction paths the hook's search dirs point at), then the plan.
        OverlayPlan? overlayPlan = null;
        if (selections.Count > 0 && !opts.ContainsKey("dry-run"))
        {
            overlayPlan = OverlayPlanner.Plan(overlayRoot, paths.ModulesRoot, selections);
            if (ApplyOverlay(selections) is null) return 2;
        }

        var extraEnv = new Dictionary<string, string>();
        if (selections.Count > 0)
        {
            var hook = HookSetup.LocateHook();
            if (hook is null) { Console.Error.WriteLine("[ModderLords] ModderLords.Hook.dll not found next to the launcher; mods with helper DLLs will fail to load"); }
            else
            {
                var dirs = HookSetup.SearchDirs(paths, (overlayPlan ?? OverlayPlanner.Plan(overlayRoot, paths.ModulesRoot, selections)).Entries, gameRoot);
                foreach (var kv in HookSetup.Environment(hook, dirs, verbose: opts.ContainsKey("hook-verbose"), sidecarPath: HookSetup.SidecarPathFor(DateTime.Now))) extraEnv[kv.Key] = kv.Value;
            }
        }

        var plan = new LaunchPlan
        {
            Paths = paths,
            ModuleIds = order.ModuleIds,
            EnginePort = int.TryParse(opts.GetValueOrDefault("port"), out var port) ? port : 7210,
            Region = opts.GetValueOrDefault("region") ?? "EU",
            SaveName = opts.GetValueOrDefault("save"),
            ExtraEnvironment = extraEnv,
        };
        Console.WriteLine("[ModderLords] launch plan");
        Console.Write(plan.Describe());
        if (opts.ContainsKey("dry-run")) return 0;

        if (plan.SaveName is { } saveName)
        {
            var prep = SavePreparer.EnsureExists(paths, saveName);
            Console.WriteLine(prep.CreatedFromTemplate
                ? $"[ModderLords] save '{saveName}' did not exist; created a fresh world from {prep.TemplateUsed}"
                : $"[ModderLords] hosting existing save {prep.SavePath}");
        }
        return await RunEngine(plan, opts);
    }

    default:
        Console.Error.WriteLine("unknown command " + cmd);
        return 1;
}

OverlayApplier.ApplyResult? ApplyOverlay(List<ModSelection> selections)
{
    var plan = OverlayPlanner.Plan(overlayRoot, paths.ModulesRoot, selections);
    foreach (var e in plan.Entries)
    {
        Console.WriteLine($"[ModderLords] overlay {e.Selection.Module.Id} [{e.Selection.Role}] {e.Kind} <- {e.Selection.Module.FolderPath}");
        foreach (var n in e.Notes) Console.WriteLine($"             - {n}");
    }
    try
    {
        var result = applier.Apply(plan);
        foreach (var a in result.Applied)
            foreach (var c in a.ManifestChanges) Console.WriteLine($"             {a.ModuleId}: {c}");
        foreach (var r in result.Removed) Console.WriteLine("[ModderLords] removed stale junction " + r);
        foreach (var w in result.Warnings) Console.WriteLine("[ModderLords] WARNING " + w);
        return result;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine("[ModderLords] overlay failed: " + ex.Message);
        return null;
    }
}

static List<ModSelection> ParseSelections(string? spec, ModuleCatalog catalog, out List<string> errors)
{
    errors = new List<string>();
    var list = new List<ModSelection>();
    if (string.IsNullOrWhiteSpace(spec)) return list;
    foreach (var item in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        var parts = item.Split(':', 2);
        var id = parts[0];
        var role = ServerRole.Run;
        if (parts.Length == 2 && !Enum.TryParse(parts[1], ignoreCase: true, out role)) { errors.Add($"{item}: unknown role '{parts[1]}' (Run|DependencyOnly|AsShipped)"); continue; }
        var candidates = catalog.Candidates(id).ToList();
        if (candidates.Count == 0) { errors.Add($"{id}: not found in game Modules, workshop, or custom sources"); continue; }
        // Prefer a folder named after the id (a real install) over workshop-id-named copies, then the highest version.
        var pick = candidates.OrderByDescending(c => c.FolderName.Equals(id, StringComparison.OrdinalIgnoreCase)).ThenByDescending(c => c.Version).First();
        if (candidates.Count > 1) Console.WriteLine($"[ModderLords] {id}: {candidates.Count} copies found, using {pick.FolderPath} ({pick.Version})");
        list.Add(new ModSelection(pick, role));
    }
    return list;
}

static async Task<int> RunEngine(LaunchPlan plan, Dictionary<string, string> opts)
{
    var quietEngine = opts.ContainsKey("quiet-engine");
    var stopAfter = int.TryParse(opts.GetValueOrDefault("stop-after"), out var s) ? s : 0;
    var logPath = Path.Combine(Path.GetTempPath(), $"modderlords-launch-{DateTime.Now:yyyyMMdd-HHmmss}.log");
    using var log = new StreamWriter(logPath) { AutoFlush = true };
    Console.WriteLine($"[ModderLords] full output -> {logPath}");

    using var engine = EngineProcess.Start(plan);
    Console.WriteLine($"[ModderLords] engine pid {engine.ProcessId}. Type a command and Enter to send it; 'quit' stops the server.");
    var serving = new TaskCompletionSource();
    engine.LineReceived += line =>
    {
        var c = LogClassifier.Classify(line.Text);
        log.WriteLine($"{line.At:HH:mm:ss.fff} {line.Stream,-6} {c.Category,-10} {line.Text}");
        if (line.Text.Contains("SERVING", StringComparison.Ordinal)) serving.TrySetResult();
        if (quietEngine && c.Category is LogCategory.Engine) return;
        var prev = Console.ForegroundColor;
        Console.ForegroundColor = c.Category switch
        {
            LogCategory.Error => ConsoleColor.Red,
            LogCategory.Warning => ConsoleColor.Yellow,
            LogCategory.Milestone => ConsoleColor.Green,
            LogCategory.ModuleLoad => ConsoleColor.Cyan,
            LogCategory.Server => ConsoleColor.White,
            LogCategory.Coop => ConsoleColor.Magenta,
            LogCategory.Tool => ConsoleColor.Green,
            _ => ConsoleColor.DarkGray,
        };
        Console.WriteLine($"{c.Category,-10} {line.Text}");
        Console.ForegroundColor = prev;
    };

    Console.CancelKeyPress += (_, e) => { e.Cancel = true; _ = engine.StopAsync(TimeSpan.FromSeconds(15)); };

    if (stopAfter > 0)
    {
        _ = Task.Run(async () =>
        {
            await Task.WhenAny(serving.Task, Task.Delay(TimeSpan.FromSeconds(stopAfter)));
            await Task.Delay(TimeSpan.FromSeconds(5));
            Console.WriteLine("[ModderLords] auto-stop: sending 'stop' over stdin");
            await engine.StopAsync(TimeSpan.FromSeconds(30));
        });
    }
    else
    {
        _ = Task.Run(async () =>
        {
            while (engine.IsRunning)
            {
                var input = Console.ReadLine();
                if (input is null) break;
                if (input.Equals("quit", StringComparison.OrdinalIgnoreCase)) { await engine.StopAsync(TimeSpan.FromSeconds(15)); break; }
                try { await engine.SendCommandAsync(input); } catch (Exception ex) { Console.WriteLine("[ModderLords] send failed: " + ex.Message); }
            }
        });
    }

    var code = await engine.Exited;
    var uptime = DateTimeOffset.Now - engine.StartedAt;
    Console.WriteLine($"[ModderLords] engine exit code {code} after {uptime:hh\\:mm\\:ss}. {ExitCodeExplainer.Explain(code)}");
    log.WriteLine($"[ModderLords] exit {code}: {ExitCodeExplainer.Explain(code)}");
    return code;
}

static Dictionary<string, string> ParseArgs(string[] a)
{
    var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (int i = 0; i < a.Length; i++)
    {
        if (a[i].StartsWith("--"))
        {
            var key = a[i][2..];
            if (i + 1 < a.Length && !a[i + 1].StartsWith("--")) d[key] = a[++i]; else d[key] = "true";
        }
        else if (!d.ContainsKey("cmd")) d["cmd"] = a[i];
    }
    return d;
}
