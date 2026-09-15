using ModderLords.Core.Compat.Authority;
using ModderLords.Core.Export;
using ModderLords.Core.Launch;
using ModderLords.Coop.Launch;
using ModderLords.Core.Logs;
using ModderLords.Core.Modules;
using ModderLords.Core.Overlay;
using ModderLords.Core.Profiles;
using ModderLords.Core.Saves;
using ModderLords.Coop.Saves;
using ModderLords.Coop.Config;

// Command-line driver (Phase 0/1). Commands:
//   catalog  [--root <DedicatedServer>] [--source <dir>]...
//   sync     --mods Id[:Run|DependencyOnly|AsShipped],...  [--remove-all]
//   launch   [--mods ...] [--save NAME] [--port 7210] [--join-port 4200] [--region EU] [--dry-run] [--quiet-engine] [--stop-after SECONDS]
//            [--manual-order] [--stall-seconds N|off] [--mod-distance-cache]
//            [--create-world NAME] [--create-world-timeout SECONDS]
//            [--data-dir PATH] [--coop-data-dir PATH] [--world-log PATH]
//   saves
//   import-save --from NAME|PATH [--as NAME] [--overwrite]
//   authority --mod ID [--all] [--json] [--out PATH]

var opts = ParseArgs(args);
if (opts.ContainsKey("data-dir") && opts.ContainsKey("profile"))
{
    Console.Error.WriteLine("[ModderLords] --data-dir diagnostics cannot use saved profiles; pass --mods explicitly.");
    return 2;
}
// Diagnostic invocations must not migrate or touch the user's launcher data.
if (!opts.ContainsKey("data-dir")) DataDirMigration.RunIfNeeded();
if (!opts.TryGetValue("cmd", out var cmd))
{
    Console.WriteLine("commands: catalog | sync --mods Id[:Role],... [--remove-all] | launch [--mods ...] [--save NAME] [--port N] [--join-port N] [--region EU] [--dry-run] [--quiet-engine] [--stop-after S]");
    Console.WriteLine("          play --profile NAME [--dry-run]   (start the player's own game with a profile's mods)");
    Console.WriteLine("          launch --create-world NAME [--create-world-timeout S] [--data-dir PATH] [--world-log PATH]   (generate, save, exit)");
    Console.WriteLine("          saves | import-save --from NAME|PATH [--as NAME] [--overwrite]   (seed the server with a world the real game built)");
    Console.WriteLine("          authority --mod ID [--all] [--json] [--out PATH]   (which parts of a mod must be server-only, read from its code)");
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
var diagnosticData = opts.GetValueOrDefault("data-dir");
var paths = ServerPaths.Create(root, diagnosticData,
    opts.GetValueOrDefault("coop-data-dir") ?? (diagnosticData is null ? null : Path.Combine(diagnosticData, "CoopData")));
// Validate creation before overlays, config, caches, or logs can be changed.
if (cmd == "launch" && opts.GetValueOrDefault("create-world") is { } requestedWorld)
{
    try { WorldCreationRequest.Validate(paths, requestedWorld, opts.ContainsKey("save")); }
    catch (Exception ex) { Console.Error.WriteLine("[ModderLords] --create-world: " + ex.Message); return 2; }
}
var problems = paths.Validate().ToList();
foreach (var p in problems) Console.Error.WriteLine("[ModderLords] " + p);
if (problems.Count > 0) return 2;

var libraries = ServerPaths.SteamLibraries().ToList();
var gameRoot = opts.GetValueOrDefault("game") ?? ModuleCatalog.FindGameRoot(libraries);
var customRoots = opts.TryGetValue("source", out var src) ? src.Split(';', StringSplitOptions.RemoveEmptyEntries) : [];
var catalog = ModuleCatalog.Scan(paths.ModulesRoot, gameRoot, libraries, customRoots);
foreach (var p in catalog.Problems) Console.Error.WriteLine("[ModderLords] catalog: " + p);

var overlayRoot = diagnosticData is null
    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ModderLords", "overlay", "default")
    : Path.Combine(paths.DataDir, "overlay");
var applier = new OverlayApplier { KeepForDependencyOnly = LaunchSession.KeepForDependencyOnly };

switch (cmd)
{
    case "profiles":
        Console.WriteLine("profiles dir: " + ProfileStore.ProfilesDir);
        foreach (var n in ProfileStore.List()) Console.WriteLine("  " + n + (ProfileStore.Load(n) is null ? "  (FAILS TO LOAD)" : ""));
        return 0;

    // The server cannot build a world with its own modules; the real game can. This carries one across.
    case "saves":
    {
        Console.WriteLine("client saves: " + ServerPaths.ClientSavesDir());
        foreach (var h in SavePreparer.ClientSaves())
            Console.WriteLine($"  {h.Name,-28} {h.ApplicationVersion,-16} day {h.DayLong,-10:F0} {h.LastWriteUtc.ToLocalTime():yyyy-MM-dd HH:mm}  [{string.Join(", ", h.CommunityModuleIds)}]");
        Console.WriteLine("server saves: " + paths.SavesDir);
        foreach (var h in SaveHeaderReader.ReadAll(paths.SavesDir))
            Console.WriteLine($"  {h.Name,-28} {h.ApplicationVersion,-16} day {h.DayLong,-10:F0} {h.LastWriteUtc.ToLocalTime():yyyy-MM-dd HH:mm}  [{string.Join(", ", h.CommunityModuleIds)}]");
        return 0;
    }

    case "import-save":
    {
        var from = opts.GetValueOrDefault("from");
        if (from is null) { Console.Error.WriteLine("import-save --from NAME|PATH [--as NAME] [--overwrite]   (NAME is a client save; see: saves)"); return 2; }
        // A bare name means one of the player's own saves; anything path-like is taken as given.
        var source = File.Exists(from) ? from : Path.Combine(ServerPaths.ClientSavesDir(), from + ".sav");
        var target = opts.GetValueOrDefault("as") ?? Path.GetFileNameWithoutExtension(source);
        try
        {
            var r = SavePreparer.ImportFrom(source, paths, target, opts.ContainsKey("overwrite"));
            foreach (var w in r.Warnings) Console.WriteLine("[ModderLords] WARNING " + w);
            Console.WriteLine($"[ModderLords] imported {r.SourcePath}");
            Console.WriteLine($"[ModderLords]       -> {r.SavePath}");
            Console.WriteLine($"[ModderLords] host it with: launch --mods ... --save {target}");
        }
        catch (Exception ex) { Console.Error.WriteLine("[ModderLords] import failed: " + ex.Message); return 2; }
        return 0;
    }

    // Static authority analysis: nothing is loaded or launched.
    case "authority":
    {
        var modId = opts.GetValueOrDefault("mod");
        if (modId is null || modId == "true") { Console.Error.WriteLine("authority --mod ID [--all] [--json] [--out PATH]   (ID as listed by: catalog)"); return 2; }
        var mod = catalog.Modules.FirstOrDefault(m => m.Id.Equals(modId, StringComparison.OrdinalIgnoreCase));
        if (mod is null) { Console.Error.WriteLine($"No module '{modId}' in the catalog. Try: catalog"); return 2; }
        var gameInterface = CoopSinks.FindGameInterface(libraries);
        if (gameInterface is null) { Console.Error.WriteLine("Coop's GameInterface.dll was not found (game Modules\\Coop or a Workshop item)."); return 2; }
        var coop = CoopSinks.Load(gameInterface);
        var report = AuthorityScan.Classify(ModAnalysis.Analyse(mod), coop);
        if (opts.GetValueOrDefault("out") is { } outPath && outPath != "true") File.WriteAllText(outPath, report.ToJson());
        if (opts.ContainsKey("json")) { Console.WriteLine(report.ToJson()); return 0; }

        Console.WriteLine($"coop   : {coop.Summary}");
        Console.WriteLine($"{mod.Id} {mod.Version}: {report.Summary}");
        if (report.PlayerComparisonMethods.Count > 0)
            Console.WriteLine($"  player checks: {report.PlayerComparisonMethods.Count} server-run method(s) ask \"is this the player's?\" (rewritten to any player on the server)");
        if (report.SyncStateMembers.Count > 0)
            Console.WriteLine($"  state sync: {report.SyncStateMembers.Count} static field(s) sent from the server to clients");
        foreach (var n in report.Notes.Take(5)) Console.WriteLine("  note: " + n);
        var shown = opts.ContainsKey("all") ? report.Roots : report.ActionNeeded;
        foreach (var r in shown.OrderBy(r => r.Verdict).ThenBy(r => r.Root.Method, StringComparer.Ordinal))
        {
            Console.WriteLine($"  {r.Verdict,-15} {r.Root.Trigger,-12} {r.Root.Method}{(r.Opaque ? "  (uses reflection)" : "")}");
            Console.WriteLine($"      {r.Reason}");
            if (r.Evidence.Count > 0) Console.WriteLine("      via " + string.Join(" -> ", r.Evidence));
            foreach (var f in r.Flags ?? []) Console.WriteLine("      flag: " + f);
            if (r.GateInstead is { Count: > 0 } g) Console.WriteLine("      gated instead on clients: " + string.Join(", ", g));
            if (r.RelayVia is { } via) Console.WriteLine("      relayed to the server via " + via);
            if (r.SharedState is { Count: > 0 } st) Console.WriteLine("      shared state: " + string.Join(", ", st.Select(f => report.SyncStateMembers.Contains(f) ? f + " (synced)" : f + " (not syncable: instance or unsupported type)")));
        }
        return 0;
    }

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
        if (opts.ContainsKey("compat") || opts.ContainsKey("create-world"))
        {
            var compat = LaunchSession.LocateCompatModule();
            if (compat is null) { Console.Error.WriteLine("[ModderLords] required compatibility module not found under compat\\ next to the CLI"); return 2; }
            else selections.Add(new ModSelection(compat, ServerRole.AsShipped));
        }
        foreach (var s in selections)
        {
            var scan = ModderLords.Core.Compat.AssemblyScan.Scan(s.Module);
            Console.WriteLine($"[ModderLords] scan {s.Module.Id,-28} {scan.Summary}" + (scan.Notes.Count > 0 ? "  (" + string.Join("; ", scan.Notes) + ")" : ""));
        }

        var stock = catalog.Modules.Where(m => m.IsStock).ToList();
        // The order the mods were typed in is the user's stated intent; it used to be discarded, so --mods could not
        // express an order at all. --manual-order makes it authoritative over what the manifests declare.
        var order = LoadOrder.Compute(stock, selections.Select(s => s.Module).ToList(), selections.Select(s => s.Module.Id).ToList(),
            policy: opts.ContainsKey("manual-order") ? LoadOrder.OrderPolicy.Manual : LoadOrder.OrderPolicy.Suggest);
        foreach (var i in order.Issues) Console.WriteLine("[ModderLords] order: " + i);

        // Same refusal as LaunchSession.Prepare, before the overlay or the server package is touched: a map mod's own
        // distance cache without --mod-distance-cache means the server loads SandBox's vanilla cache and crashes.
        if (DistanceCacheOverride.PreflightProblem(selections.Select(x => x.Module), opts.ContainsKey("mod-distance-cache")) is { } cacheProblem)
        {
            if (!opts.ContainsKey("dry-run")) { Console.Error.WriteLine("[ModderLords] " + cacheProblem); return 2; }
            Console.WriteLine("[ModderLords] WARNING " + cacheProblem);
        }

        // Overlay first (it decides the junction paths the hook's search dirs point at), then the plan.
        OverlayPlan? overlayPlan = null;
        var contentMessages = new List<string>();
        var headlessContent = LaunchSession.PrepareHeadlessContent(overlayRoot, selections, !opts.ContainsKey("dry-run"), contentMessages);
        if (selections.Count > 0 && !opts.ContainsKey("dry-run"))
        {
            overlayPlan = OverlayPlanner.Plan(overlayRoot, paths.ModulesRoot, selections,
                headlessAssetPaths: headlessContent.AssetPaths, headlessMapPaths: headlessContent.MapPaths);
            if (ApplyOverlay(selections, overlayPlan) is null) return 2;
        }

        // World creation: the compat module generates a campaign in-process and exits. Mutually exclusive
        // with --save, because there is deliberately no save to load yet.
        var createWorld = opts.GetValueOrDefault("create-world");
        if (createWorld is not null && opts.ContainsKey("save"))
        {
            Console.Error.WriteLine("[ModderLords] --create-world and --save are mutually exclusive: one makes a world, the other loads one");
            return 2;
        }

        var extraEnv = new Dictionary<string, string>();
        if (headlessContent.MapPaths.TryGetValue("TAOM_Map", out var headlessMapPath))
        {
            extraEnv["MODDERLORDS_HEADLESS_MAP"] = Path.Combine(headlessMapPath, "modderlords-map.xml");
            extraEnv["MODDERLORDS_HEADLESS_MAP_MODULE"] = "TAOM_Map";
        }
        if (createWorld is not null)
        {
            try { SavePreparer.ValidateSaveName(createWorld); }
            catch (Exception ex) { Console.Error.WriteLine("[ModderLords] --create-world: " + ex.Message); return 2; }
            extraEnv["MODDERLORDS_CREATE_WORLD"] = createWorld;
            extraEnv["MODDERLORDS_CREATE_WORLD_LOG"] = opts.GetValueOrDefault("world-log")
                ?? Path.Combine(paths.LogsDir, $"worldcreate-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.log");
            if (opts.GetValueOrDefault("create-world-timeout") is { } t) extraEnv["MODDERLORDS_CREATE_WORLD_TIMEOUT"] = t;
            Console.WriteLine($"[ModderLords] create-world mode: the server will generate '{createWorld}' and exit; it will not serve");
        }
        if (selections.Count > 0)
        {
            var hook = HookSetup.LocateHook();
            if (hook is null) { Console.Error.WriteLine("[ModderLords] ModderLords.Hook.dll not found next to the launcher; mods with helper DLLs will fail to load"); }
            else
            {
                var dirs = HookSetup.SearchDirs(paths, (overlayPlan ?? OverlayPlanner.Plan(overlayRoot, paths.ModulesRoot, selections)).Entries, gameRoot);
                foreach (var kv in HookSetup.Environment(hook, dirs, verbose: opts.ContainsKey("hook-verbose"), sidecarPath: diagnosticData is null ? HookSetup.SidecarPathFor(DateTime.Now) : Path.Combine(paths.LogsDir, $"hook-{Guid.NewGuid():N}.log"))) extraEnv[kv.Key] = kv.Value;
            }
        }

        var plan = new LaunchPlan
        {
            Paths = paths,
            ModuleIds = order.ModuleIds,
            EnginePort = int.TryParse(opts.GetValueOrDefault("port"), out var port) ? port : 7210,
            Region = opts.GetValueOrDefault("region") ?? "EU",
            SaveName = opts.GetValueOrDefault("save"),
            Visibility = diagnosticData is null ? null : ModderLords.Core.Profiles.ServerVisibility.None,
            ExtraEnvironment = extraEnv,
        };
        // Off unless asked for: this is the one thing the launcher writes inside the DedicatedServer package.
        // Called both ways so --no-mod-distance-cache (or simply omitting the flag) restores SandBox's original.
        if (!opts.ContainsKey("dry-run"))
            foreach (var m in DistanceCacheOverride.Sync(paths, selections.Select(x => x.Module).ToList(),
                         opts.ContainsKey("mod-distance-cache")).Messages)
                Console.WriteLine("[ModderLords] " + m);

        // Pre-flight: the world about to be loaded vs the modules about to load it. This ad-hoc --mods path is the one
        // used for diagnostic runs, so it needs the check as much as LaunchSession.Prepare does. Warn, never block.
        if (createWorld is null)
        {
            foreach (var m in SaveModuleCheck.MessagesForLaunch(paths.SavesDir, plan.SaveName,
                         selections.ToDictionary(x => x.Module.Id, x => x.Module.Version, StringComparer.OrdinalIgnoreCase)))
            {
                var c0 = Console.ForegroundColor;
                Console.ForegroundColor = m.StartsWith("WARNING", StringComparison.Ordinal) ? ConsoleColor.Yellow : ConsoleColor.Gray;
                Console.WriteLine("[ModderLords] " + m);
                Console.ForegroundColor = c0;
            }
        }

        Console.WriteLine("[ModderLords] launch plan");
        foreach (var m in contentMessages) Console.WriteLine("[ModderLords] " + m);
        Console.Write(plan.Describe());
        if (opts.ContainsKey("dry-run")) return 0;

        if (createWorld is not null)
        {
            // Nothing to bootstrap: generating the world is the entire point of this run.
            Console.WriteLine($"[ModderLords] not preparing a save; '{createWorld}' is what this run will create");
        }
        else if (plan.SaveName is { } saveName)
        {
            var prep = SavePreparer.EnsureExists(paths, saveName);
            Console.WriteLine(prep.CreatedFromTemplate
                ? $"[ModderLords] save '{saveName}' did not exist; created a fresh world from {prep.TemplateUsed}"
                : $"[ModderLords] hosting existing save {prep.SavePath}");
        }
        // The official server chooses its save from server-config.json. Render the ad-hoc request explicitly so a
        // previous run cannot make --create-world load an unrelated old campaign (or make --save load the wrong one).
        var configured = ServerConfig.Read(paths.ServerConfigPath);
        var adHocSettings = configured?.Settings ?? new ServerSettings();
        // --port is the ENGINE port. JoinPort is the Coop port clients dial, a different number entirely (7210
        // against 4200 by default), and assigning one to the other both misdirects clients and rewrites the user's
        // server-config.json behind their back. Keep what is configured unless --join-port says otherwise.
        if (int.TryParse(opts.GetValueOrDefault("join-port"), out var joinPort)) adHocSettings.JoinPort = joinPort;
        if (diagnosticData is not null) adHocSettings.Steam = false;
        // --compat is intentionally driven by the existing server-config.json. Preserve its save name when no
        // explicit --save was supplied. World creation is the one exception: it must clear a stale configured save
        // so the creation process cannot accidentally load an unrelated campaign.
        var configuredSave = createWorld is not null ? "" : plan.SaveName ?? configured?.SaveName ?? "";
        ServerConfig.Write(paths, configuredSave, adHocSettings);
        return await RunEngine(plan, opts);
    }

    default:
        Console.Error.WriteLine("unknown command " + cmd);
        return 1;
}

OverlayApplier.ApplyResult? ApplyOverlay(List<ModSelection> selections, OverlayPlan? planned = null)
{
    var plan = planned ?? OverlayPlanner.Plan(overlayRoot, paths.ModulesRoot, selections);
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
    // A campaign load that repeats one state forever exits with nothing and logs nothing fatal. Call it out rather
    // than running for minutes looking healthy.
    var stallThreshold = LoadStallDetector.ParseThreshold(opts.GetValueOrDefault("stall-seconds"));
    if (opts.ContainsKey("stall-seconds") && stallThreshold is null)
        Console.Error.WriteLine("[ModderLords] --stall-seconds: expected a number of seconds, or 'off'; using the default");
    var stall = new LoadStallDetector(engine.StartedAt, stallThreshold);
    Console.WriteLine(stall.IsEnabled
        ? $"[ModderLords] will report if loading goes quiet for {stall.Threshold.TotalMinutes:0.#} min (--stall-seconds N, or off)"
        : "[ModderLords] loading-progress warnings are off");
    void Warn(string message)
    {
        log.WriteLine(message);
        var c0 = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("[ModderLords] " + message);
        Console.ForegroundColor = c0;
    }
    engine.LineReceived += line =>
    {
        var c = LogClassifier.Classify(line.Text);
        log.WriteLine($"{line.At:HH:mm:ss.fff} {line.Stream,-6} {c.Category,-10} {line.Text}");
        if (line.Text.Contains("SERVING", StringComparison.Ordinal)) serving.TrySetResult();
        if (stall.Observe(line.Text, line.At) is { } warning) Warn(warning);
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

    // Observe() only fires on a line; a run that has gone entirely silent needs the clock polled as well.
    using var stallPoll = new Timer(_ => { if (stall.Check(DateTimeOffset.Now) is { } w) Warn(w); }, null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));

    if (stopAfter > 0)
    {
        _ = Task.Run(async () =>
        {
            var completed = await Task.WhenAny(serving.Task, Task.Delay(TimeSpan.FromSeconds(stopAfter)));
            var reason = completed == serving.Task ? "serving reached" : "timeout reached";
            await Task.Delay(TimeSpan.FromSeconds(5));
            Console.WriteLine($"[ModderLords] auto-stop: {reason}; sending 'stop' over stdin");
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
