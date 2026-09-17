
using ModderLords.Core.Compat;
using ModderLords.Core.Config;
using ModderLords.Core.Export;
using ModderLords.Core.Launch;
using ModderLords.Core.Logs;
using ModderLords.Core.Modules;
using ModderLords.Core.Overlay;
using ModderLords.Core.Perf;
using ModderLords.Core.Profiles;
using ModderLords.Core.Saves;
using ModderLords.Coop.Compat;
using ModderLords.Coop.Config;
using ModderLords.Coop.Launch;
using ModderLords.Coop.Live;
using ModderLords.Coop.Saves;

namespace ModderLords.Coop.Launch;

/// <summary>
/// The one place that turns a Profile into a running engine: resolve paths, discover modules, pick sources, compute
/// the order, apply the overlay, render server-config.json, prepare the save, build the plan, start the process.
/// Used by both the CLI and the app so they cannot drift.
/// </summary>
public sealed class LaunchSession
{
    public const int DefaultCreateWorldTimeoutSeconds = 900;

    public sealed record Prepared(
        ServerPaths Paths,
        ModuleCatalog Catalog,
        IReadOnlyList<ModSelection> Selections,
        LoadOrder.Result Order,
        OverlayPlan OverlayPlan,
        LaunchPlan Plan,
        IReadOnlyList<string> Messages)
    {
        /// <summary>The engine-agnostic half of this launch, for the code shared with the client path.</summary>
        public ModuleSelectionResult Modules => new(Catalog, Selections, Order);
    }

    public sealed record HeadlessContent(IReadOnlyDictionary<string, string> AssetPaths, IReadOnlyDictionary<string, string> MapPaths);

    /// <summary>Submodule class types that survive DependencyOnly, from the compat database (MCM's settings core when no DB ships).</summary>
    public static IReadOnlyCollection<string> KeepForDependencyOnly => CompatDb.Current.KeepForDependencyOnly();

    public static ServerPaths ResolvePaths(Profile profile)
    {
        var root = profile.DedicatedServerRoot ?? ServerPaths.FindWorkshopDedicatedServerRoots().FirstOrDefault()
            ?? throw new InvalidOperationException("No DedicatedServer folder found. Subscribe to Bannerlord Coop on the workshop or set the path in the profile.");
        return ServerPaths.Create(root);
    }

    public static ModuleCatalog Scan(Profile profile, ServerPaths paths, out string? gameRoot)
    {
        var libraries = ServerPaths.SteamLibraries().ToList();
        gameRoot = profile.GameRoot ?? ModuleCatalog.FindGameRoot(libraries);
        return ModuleCatalog.Scan(paths.ModulesRoot, gameRoot, libraries, profile.CustomModRoots);
    }

    /// <summary>Picks a concrete folder for each enabled profile mod. Shared with the client launch path.</summary>
    public static IReadOnlyList<ModSelection> Select(Profile profile, ModuleCatalog catalog, List<string> messages)
        => ModuleSelector.Select(profile, catalog, messages)
            .Where(s => !ClientManifest.CoopClientModuleIds.Contains(s.Module.Id) &&
                !catalog.Modules.Any(m => m.IsStock && m.Id.Equals(s.Module.Id, StringComparison.OrdinalIgnoreCase))).ToList();

    public const string CompatModuleId = "DedicatedServer.ModderLordsCompat";
    public const string SyncModuleId = "ModderLords.Compat";

    /// <summary>The launcher's own guard module, shipped under compat\ next to the executable.</summary>
    public static DiscoveredModule? LocateCompatModule() => LocateBundled(CompatModuleId);

    /// <summary>The shared client+server sync module, shipped under compat\ next to the executable (players install a copy).</summary>
    public static DiscoveredModule? LocateSyncModule() => LocateBundled(SyncModuleId);

    private static DiscoveredModule? LocateBundled(string id)
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "compat", id);
        if (!File.Exists(Path.Combine(dir, "SubModule.xml"))) return null;
        return ModuleCatalog.TryParse(dir, ModuleSourceKind.Custom, out _);
    }

    /// <summary>Adds the bundled compat modules to the selections when the profile asks for them and they are installed.</summary>
    public static IReadOnlyList<ModSelection> WithCompat(Profile profile, IReadOnlyList<ModSelection> selections, List<string> messages)
        => WithCompat(profile, selections, messages, LocateBundled);

    /// <summary>
    /// As above, with the bundled-module lookup supplied (tests). The launcher's own modules always come from compat\ next
    /// to the exe: recipes.json is written there, so a copy a profile ticked — usually the one Launch client installed
    /// into the game's Modules on a machine that also hosts — would load on the server without the recipe.
    /// </summary>
    public static IReadOnlyList<ModSelection> WithCompat(Profile profile, IReadOnlyList<ModSelection> selections, List<string> messages,
        Func<string, DiscoveredModule?> locateBundled)
    {
        var list = new List<ModSelection>();
        foreach (var s in selections)
        {
            var own = BundledIds.Contains(s.Module.Id) ? locateBundled(s.Module.Id) : null;
            if (own is null) { list.Add(s); continue; }
            if (!SameFolder(own.FolderPath, s.Module.FolderPath))
                messages.Add($"{s.Module.Id}: the server uses the launcher's own copy, not the one ticked in the profile ({s.Module.FolderPath})");
            list.Add(new ModSelection(own, ServerRole.AsShipped));
        }
        if (profile.CompatGuards && !list.Any(s => s.Module.Id.Equals(CompatModuleId, StringComparison.OrdinalIgnoreCase)))
        {
            var compat = locateBundled(CompatModuleId);
            if (compat is null) messages.Add("server guards requested but the compat module is missing next to the launcher; continuing without it");
            else list.Add(new ModSelection(compat, ServerRole.AsShipped));
        }
        if (profile.SettingsSync && !list.Any(s => s.Module.Id.Equals(SyncModuleId, StringComparison.OrdinalIgnoreCase)))
        {
            var sync = locateBundled(SyncModuleId);
            if (sync is null) messages.Add("settings sync requested but the ModderLords.Compat module is missing next to the launcher; continuing without it");
            else list.Add(new ModSelection(sync, ServerRole.AsShipped));
        }
        return list;
    }

    private static bool SameFolder(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    /// <summary>Everything except starting the process. Applies the overlay and writes server-config.json.</summary>
    /// <param name="scanned">
    /// A catalogue already scanned for this profile, reused only for a preview (<paramref name="applySideEffects"/>
    /// false). A real launch ignores it and scans fresh, so it can never act on a folder that has since changed.
    /// </param>
    public static Prepared Prepare(Profile profile, bool applySideEffects = true, bool allowTaomWorldCreation = false,
        (ModuleCatalog Catalog, string? GameRoot)? scanned = null, bool experimentalCompat = false)
    {
        var messages = new List<string>();
        var paths = ResolvePaths(profile);
        var problems = paths.Validate().ToList();
        if (problems.Count > 0) throw new InvalidOperationException(string.Join(Environment.NewLine, problems));

        string? gameRoot;
        ModuleCatalog catalog;
        if (!applySideEffects && scanned is { } previous) (catalog, gameRoot) = previous;
        else catalog = Scan(profile, paths, out gameRoot);
        messages.AddRange(catalog.Problems.Select(p => "catalog: " + p));
        var selections = WithCompat(profile, Select(profile, catalog, messages), messages);
        if (applySideEffects)
        {
            var missing = profile.EnabledMods.Where(pm => !ClientManifest.CoopClientModuleIds.Contains(pm.Id) &&
                !selections.Any(s => s.Module.Id.Equals(pm.Id, StringComparison.OrdinalIgnoreCase)) &&
                !catalog.Modules.Any(m => m.IsStock && m.Id.Equals(pm.Id, StringComparison.OrdinalIgnoreCase)))
                .Select(pm => pm.Id).ToList();
            if (missing.Count > 0) throw new InvalidOperationException("Missing selected mods: " + string.Join(", ", missing) +
                ". Download them and Rescan, or untick them. The imported list has been kept.");
        }

        // A curated "Broken" verdict has to reach the console before the engine starts. It was only ever a badge on
        // the Mods tab, so a mod known to hang the server looked no different from any other at launch -- which cost
        // two fifteen-minute stalls before anyone connected the two. A warning, not a refusal: the verdict is a
        // record of what was observed, and overruling it is the user's call.
        foreach (var s in selections)
            if (CompatDb.Current.Find(s.Module.Id) is { Verdict: CompatVerdict.Broken } broken)
                messages.Add($"WARNING {s.Module.Id} is recorded as Broken on the Coop server" +
                             (string.IsNullOrWhiteSpace(broken.Notes) ? "." : ": " + broken.Notes));

        // Refused before anything is written, unlike the verdict above: this is not a judgement call. With the override
        // off, a map mod's own cache means the server loads SandBox's vanilla cache against that map and crashes.
        if (DistanceCacheOverride.PreflightProblem(selections.Select(s => s.Module), profile.UseModDistanceCache) is { } cacheProblem)
        {
            if (applySideEffects) throw new InvalidOperationException(cacheProblem);
            messages.Add("WARNING " + cacheProblem);
        }

        var taomSaveMessage = TaomLaunchPolicy.MessageFor(selections.Select(s => s.Module.Id), profile.SaveName,
            name => SavePreparer.Exists(paths, name));
        if (taomSaveMessage is not null)
        {
            var canCreate = allowTaomWorldCreation && TaomLaunchPolicy.HasCompleteRecipe(selections.Select(s => s.Module.Id)) &&
                !string.IsNullOrWhiteSpace(profile.SaveName) && !SavePreparer.Exists(paths, profile.SaveName);
            if (applySideEffects && !canCreate) throw new InvalidOperationException(taomSaveMessage);
            // One or the other, never both. Printing the "create it in Bannerlord and import it" advice directly
            // under "this will be created automatically" told the user to do work the launcher was about to do.
            if (canCreate) messages.Add("TAOM: this campaign will be created automatically before the server starts");
            else messages.Add("TAOM: " + taomSaveMessage);
        }

        // Refused, not warned: the old warning was one line in a long console, and on 2026-09-14 a TAOM host and join ran
        // with a generated recipe that no peer ever loaded. Ticking Server-only logic asks for gating; without Settings sync
        // it silently does nothing.
        if (ServerOnlyLogicProblem(profile, selections.Select(s => s.Module.Id), experimentalCompat) is { } serverOnlyProblem)
        {
            if (applySideEffects) throw new InvalidOperationException(serverOnlyProblem);
            messages.Add("WARNING " + serverOnlyProblem);
        }
        if (IgnoredServerOnlyTicks(profile, selections.Select(s => s.Module.Id), experimentalCompat) is { Count: > 0 } ignored)
            messages.Add($"experimental compatibility is off: Server-only logic for {string.Join(", ", ignored)} is ignored (Server tab, Advanced)");

        var stock = catalog.Modules.Where(m => m.IsStock).ToList();
        var order = LoadOrder.Compute(stock, selections.Select(s => s.Module).ToList(), profile.Mods.Select(m => m.Id).ToList(),
            policy: profile.ManualLoadOrder ? LoadOrder.OrderPolicy.Manual : LoadOrder.OrderPolicy.Suggest);
        messages.AddRange(order.Issues.Select(i => "order: " + i));

        var overlayRoot = ProfileStore.OverlayDirFor(profile.Name);
        var content = PrepareHeadlessContent(overlayRoot, selections, applySideEffects, messages);
        var headlessAssets = content.AssetPaths;
        var headlessMaps = content.MapPaths;
        var overlayPlan = OverlayPlanner.Plan(overlayRoot, paths.ModulesRoot, selections,
            headlessAssetPaths: headlessAssets, headlessMapPaths: headlessMaps);
        // The planner's warnings (a Run mod that declares itself client-only and reaches for the render stack) have to
        // reach the launch messages: the CLI prints plan notes itself, the app only ever sees this list.
        foreach (var e in overlayPlan.Entries)
            foreach (var n in e.Notes.Where(n => n.StartsWith("WARNING", StringComparison.Ordinal)))
                messages.Add($"{e.Selection.Module.Id}: {n}");

        var extraEnv = new Dictionary<string, string>();
        if (headlessMaps.TryGetValue("TAOM_Map", out var mapPath))
        {
            extraEnv["MODDERLORDS_HEADLESS_MAP"] = Path.Combine(mapPath, "modderlords-map.xml");
            extraEnv["MODDERLORDS_HEADLESS_MAP_MODULE"] = "TAOM_Map";
        }
        if (selections.Count > 0)
        {
            var hook = HookSetup.LocateHook();
            if (hook is null) messages.Add("ModderLords.Hook.dll is missing next to the launcher; mods with helper DLLs will fail to load");
            else foreach (var kv in HookSetup.Environment(hook, HookSetup.SearchDirs(paths, overlayPlan.Entries, gameRoot), sidecarPath: HookSetup.SidecarPathFor(DateTime.Now))) extraEnv[kv.Key] = kv.Value;
        }
        // Host-side live MCM edits: the sync module polls this directory (see Live/LiveSettingsClient). Only meaningful
        // when the module is loaded, so it is tied to SettingsSync; a fresh dir per launch so nothing stale is shown.
        if (profile.SettingsSync)
        {
            var liveDir = Live.LiveProtocol.LiveDirFor(profile.Name);
            extraEnv[Live.LiveProtocol.EnvVar] = liveDir;
            if (applySideEffects)
            {
                Live.LiveProtocol.Reset(liveDir);
                var (overrides, compatDefaults) = Live.CompatSettingsDefaults.Merge(Live.SettingsOverridesStore.Load(profile.Name), ModDefaultSettings(selections));
                foreach (var d in compatDefaults)
                    messages.Add($"{d.ModId}: compat database sets {d.SettingsId}.{d.PropId} = {d.Value} for co-op (override it in Mod settings to change)");
                if (!overrides.IsEmpty)
                {
                    Live.SettingsOverridesStore.WriteToLiveDir(liveDir, overrides);
                    messages.Add($"mod settings: {overrides.Count} override(s) in {overrides.Objects.Count} settings object(s) staged; applied once the server has loaded");
                }
            }
        }
        else
        {
            // The defaults travel through settings sync; without it the server would get them and clients would not.
            foreach (var (modId, defaults) in ModDefaultSettings(selections))
                messages.Add($"WARNING {modId}: the compat database needs settings sync for {string.Join(", ", defaults.SelectMany(o => o.Value.Keys.Select(p => o.Key + "." + p)))}; turn Settings sync on");
        }

        // Mods that detect co-op by module id carry their own list and miss the id this Coop loads under (TAOM.Dependencies
        // lists "Coop"; the Workshop build is "CoopNightly"), so every peer runs their single-player paths. The compat
        // database names the lines each needs. The mod's own folder is edited: the client reads that copy, and the
        // overlay mirrors top-level files from it for the server when it is applied below.
        if (applySideEffects)
        {
            var tokens = new Dictionary<string, string>
            {
                [EnsureLinesApplier.CoopModuleIdToken] = catalog.Modules.FirstOrDefault(m => m.IsStock && m.FolderName == "Coop")?.Id ?? "CoopNightly",
            };
            foreach (var s in selections)
                if (CompatDb.Current.Find(s.Module.Id) is { EnsureLines.Count: > 0 } rec)
                    foreach (var m in EnsureLinesApplier.Apply(s.Module.FolderPath, rec.EnsureLines, tokens))
                        messages.Add($"{s.Module.Id}: {m}");
        }

        if (applySideEffects)
        {
            var applier = new OverlayApplier { KeepForDependencyOnly = KeepForDependencyOnly };
            var result = applier.Apply(overlayPlan);
            foreach (var a in result.Applied) foreach (var c in a.ManifestChanges) messages.Add($"{a.ModuleId}: {c}");
            foreach (var r in result.Removed) messages.Add("removed stale junction " + r);
            foreach (var w in result.Warnings) messages.Add("WARNING " + w);
            foreach (var s in selections)
            {
                var pm = profile.Mods.FirstOrDefault(m => m.Id.Equals(s.Module.Id, StringComparison.OrdinalIgnoreCase));
                if (pm is not null) pm.LastVersion = s.Module.Version;
            }

            // Always called, both ways: switching the override off has to put SandBox's original cache back.
            var cache = DistanceCacheOverride.Sync(paths, selections.Select(s => s.Module).ToList(), profile.UseModDistanceCache);
            messages.AddRange(cache.Messages);

            ServerConfig.Write(paths, profile.SaveName, profile.Server);
            WriteRecipes(profile, selections, messages, experimentalCompat);
            if (!string.IsNullOrWhiteSpace(profile.SaveName) && !(allowTaomWorldCreation &&
                TaomLaunchPolicy.HasCompleteRecipe(selections.Select(s => s.Module.Id)) && !SavePreparer.Exists(paths, profile.SaveName)))
            {
                var prep = SavePreparer.EnsureExists(paths, profile.SaveName);
                if (prep.CreatedFromTemplate) messages.Add($"save '{profile.SaveName}' did not exist; created a fresh world from {prep.TemplateUsed}");
            }
        }

        // Pre-flight: does the world we are about to load actually match the modules we are about to run? Warn only —
        // outside applySideEffects too, so --dry-run reports it without the save having to be created first.
        messages.AddRange(SaveModuleCheck.MessagesForLaunch(paths.SavesDir, profile.SaveName, PlannedCommunityVersions(selections)));

        var plan = new LaunchPlan
        {
            Paths = paths,
            ModuleIds = order.ModuleIds,
            EnginePort = profile.Server.EnginePort,
            Region = profile.Server.Region,
            SaveName = string.IsNullOrWhiteSpace(profile.SaveName) ? null : profile.SaveName,
            Password = string.IsNullOrEmpty(profile.Server.Password) ? null : profile.Server.Password,
            Visibility = profile.Server.Visibility,
            ExtraEnvironment = extraEnv,
        };
        return new Prepared(paths, catalog, selections, order, overlayPlan, plan, messages);
    }

    /// <summary>Stages safe server assets and a render-free map under the profile overlay. It never writes into an
    /// installed mod or the official server package.</summary>
    public static HeadlessContent PrepareHeadlessContent(string overlayRoot, IReadOnlyList<ModSelection> selections,
        bool applySideEffects, List<string> messages)
    {
        var assets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var maps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!applySideEffects || !TaomLaunchPolicy.IsTaom(selections.Select(s => s.Module.Id)))
            return new HeadlessContent(assets, maps);
        var generatedRoot = Path.Combine(overlayRoot, ".generated");
        try
        {
            foreach (var selection in selections.Where(s => TaomLaunchPolicy.NeedsPreparation(s.Module.Id)))
            {
                var projection = HeadlessAssetProjection.Prepare(selection.Module.Id, selection.Module.FolderPath, generatedRoot);
                if (projection is not null)
                {
                    assets[selection.Module.Id] = projection.OutputPath;
                    messages.Add(projection.Reused
                        ? $"TAOM: reusing prepared server assets for {selection.Module.Id}"
                        : $"TAOM: prepared {projection.AssetCount} simulation assets for {selection.Module.Id} ({projection.Bytes / 1024 / 1024} MB)");
                }
            }

            var map = selections.FirstOrDefault(s => s.Module.Id.Equals("TAOM_Map", StringComparison.OrdinalIgnoreCase));
            if (map is not null)
            {
                var source = Path.Combine(map.Module.FolderPath, "SceneObj", "Main_map");
                var output = Path.Combine(generatedRoot, "TAOM_Map", "Main_map");
                var keepTerrain = !string.IsNullOrEmpty(
                    Environment.GetEnvironmentVariable(HeadlessMapProjection.KeepTerrainVariable));
                var projection = HeadlessMapProjection.Prepare(source, output, keepTerrain);
                maps[map.Module.Id] = projection.OutputPath;
                messages.Add((projection.Reused ? "TAOM: reusing prepared server map" : "TAOM: prepared a private headless map")
                    + (keepTerrain ? " WITH its terrain descriptor kept (" + HeadlessMapProjection.KeepTerrainVariable + ")" : ""));
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("TAOM server preparation failed: " + ex.Message, ex);
        }
        return new HeadlessContent(assets, maps);
    }

    /// <summary>Creates the first phase of an automatic TAOM launch. It generates the campaign and exits; the caller
    /// must start the ordinary plan again after the generated save appears.</summary>
    public static LaunchPlan CreateWorldPlan(Prepared prepared, string saveName, int timeoutSeconds = DefaultCreateWorldTimeoutSeconds)
    {
        SavePreparer.ValidateSaveName(saveName);
        if (SavePreparer.Exists(prepared.Paths, saveName))
            throw new InvalidOperationException($"Save '{saveName}' already exists; automatic world creation will not overwrite it.");
        var env = prepared.Plan.ExtraEnvironment.ToDictionary(k => k.Key, v => v.Value, StringComparer.OrdinalIgnoreCase);
        env["MODDERLORDS_CREATE_WORLD"] = saveName;
        env["MODDERLORDS_CREATE_WORLD_TIMEOUT"] = timeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        env["MODDERLORDS_CREATE_WORLD_LOG"] = Path.Combine(prepared.Paths.LogsDir,
            $"worldcreate-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.log");
        return new LaunchPlan
        {
            Paths = prepared.Plan.Paths,
            ModuleIds = prepared.Plan.ModuleIds,
            EnginePort = prepared.Plan.EnginePort,
            Region = prepared.Plan.Region,
            SaveName = null,
            Password = null,
            Visibility = null,
            ExtraEnvironment = env,
        };
    }

    public sealed record Drift(string ModuleId, string LastVersion, string CurrentVersion);

    /// <summary>Mods whose installed version differs from what the profile last launched with. Players must update too; a running server needs a restart.</summary>
    public static IReadOnlyList<Drift> DetectDrift(Profile profile, ModuleCatalog catalog)
    {
        var list = new List<Drift>();
        var scratch = new List<string>();
        foreach (var sel in Select(profile, catalog, scratch))
        {
            var pm = profile.Mods.FirstOrDefault(m => m.Id.Equals(sel.Module.Id, StringComparison.OrdinalIgnoreCase));
            if (pm?.LastVersion is { } last && !SaveHeaderReader.VersionsEqual(last, sel.Module.Version))
                list.Add(new Drift(sel.Module.Id, last, sel.Module.Version));
        }
        return list;
    }

    /// <summary>The compat database's setting defaults for every selected mod that has any.</summary>
    private static IEnumerable<(string ModId, IReadOnlyDictionary<string, Dictionary<string, string>> Defaults)> ModDefaultSettings(IEnumerable<ModSelection> selections)
    {
        foreach (var s in selections)
            if (CompatDb.Current.Find(s.Module.Id) is { DefaultSettings.Count: > 0 } rec)
                yield return (s.Module.Id, rec.DefaultSettings);
    }

    /// <summary>Re-creates the junctions/shadow folders for the profile without touching configs or saves (after a workshop update or Steam re-download).</summary>
    public static OverlayApplier.ApplyResult Resync(Profile profile)
    {
        var paths = ResolvePaths(profile);
        var catalog = Scan(profile, paths, out _);
        var selections = WithCompat(profile, Select(profile, catalog, new List<string>()), new List<string>());
        var plan = OverlayPlanner.Plan(ProfileStore.OverlayDirFor(profile.Name), paths.ModulesRoot, selections);
        return new OverlayApplier { KeepForDependencyOnly = KeepForDependencyOnly }.Apply(plan);
    }

    /// <summary>
    /// Server-only logic is ticked for a mod that will run, but Settings sync is off. The shared ModderLords.Compat module
    /// is what carries the recipe to players and applies it, so without it nothing is gated and players keep running
    /// that code themselves. Null when there is nothing to stop.
    /// </summary>
    public static string? ServerOnlyLogicProblem(Profile profile, IEnumerable<string> selectedModIds, bool experimentalCompat = true)
    {
        if (profile.SettingsSync || !experimentalCompat) return null;
        var selected = new HashSet<string>(selectedModIds, StringComparer.OrdinalIgnoreCase);
        var flagged = profile.Mods.Where(m => m.Enabled && m.ServerAuthoritative && selected.Contains(m.Id)).Select(m => m.Id).ToList();
        if (flagged.Count == 0) return null;
        return $"Server-only logic is ticked for {string.Join(", ", flagged)}, but Settings sync is off. Settings sync loads the shared "
             + "ModderLords.Compat module, which sends the recipe to players and applies it; without it nothing is gated and players "
             + "keep running that code themselves. Turn on Settings sync (each player also needs this build's compat\\ModderLords.Compat "
             + "in their Modules), or untick Server-only logic for those mods.";
    }

    /// <summary>
    /// Ticked Server-only logic that this launch will not act on because experimental compatibility is off. The ticks
    /// stay in the profile, so turning it back on restores them. Empty when it is on.
    /// </summary>
    public static IReadOnlyList<string> IgnoredServerOnlyTicks(Profile profile, IEnumerable<string> selectedModIds, bool experimentalCompat)
    {
        if (experimentalCompat) return [];
        var selected = new HashSet<string>(selectedModIds, StringComparer.OrdinalIgnoreCase);
        return profile.Mods.Where(m => m.Enabled && m.ServerAuthoritative && selected.Contains(m.Id)).Select(m => m.Id).ToList();
    }

    /// <summary>
    /// The mods whose code the recipe may gate, rewrite or relay. Experimental compatibility (per-mod gates, relays,
    /// player-check rewrites, state sync, tracing) is off unless the user turned it on: then this is empty, and
    /// recipes.json carries only the general parts (settings hints, battle scene exclusions).
    /// </summary>
    public static HashSet<string> ServerOnlyMods(Profile profile, bool experimentalCompat) => experimentalCompat
        ? profile.Mods.Where(m => m.Enabled && m.ServerAuthoritative).Select(m => m.Id).ToHashSet(StringComparer.OrdinalIgnoreCase)
        : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Layer 1: recipes.json inside the bundled sync module, built from the scan of every mod flagged server-authoritative.</summary>
    public static void WriteRecipes(Profile profile, IReadOnlyList<ModSelection> selections, List<string> messages, bool experimentalCompat = false)
    {
        var sync = LocateSyncModule();
        var flagged = ServerOnlyMods(profile, experimentalCompat);
        // Ground-truth tracing (docs/DEVELOPMENT.md, "Authority classifier, step 8"): both sides count every traced entry point.
        var traced = (experimentalCompat ? Environment.GetEnvironmentVariable("MODDERLORDS_TRACE_MODS") ?? "" : "")
            .Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(id => selections.Any(s => s.Module.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
            .Select(id => selections.First(s => s.Module.Id.Equals(id, StringComparison.OrdinalIgnoreCase)).Module.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (sync is null)
        {
            if (flagged.Count > 0) messages.Add("server-only logic requested but the ModderLords.Compat module is missing; recipes not written");
            return;
        }
        var flaggedSelections = selections.Where(s => flagged.Contains(s.Module.Id)).ToList();
        var analysed = selections.Where(s => flagged.Contains(s.Module.Id) || traced.Contains(s.Module.Id)).ToList();
        var entries = flaggedSelections.Select(s =>
        {
            var pm = profile.Mods.First(m => m.Id.Equals(s.Module.Id, StringComparison.OrdinalIgnoreCase));
            return (s.Module.Id, AssemblyScan.Scan(s.Module), (IReadOnlyCollection<string>)pm.ClientSideBehaviors);
        }).ToList();
        var authority = BuildAuthorityReports(analysed, messages);
        var db = CompatDb.Current;
        var hints = selections.Select(s => db.Find(s.Module.Id)).Where(r => r is not null)
            .Select(r => (r!.Id, (IReadOnlyList<string>)r.SettingsTypes, (IReadOnlyList<string>)r.IgnoreSettingsTypes)).ToList();
        // Field battles: the server's scene choice is what every client loads, so scenes without a terrain shader cache
        // must not be chosen. The server install ships no scene content; the launcher sees the game's and the mods'.
        IReadOnlyList<string>? excludedScenes = null;
        if (Environment.GetEnvironmentVariable("MODDERLORDS_BATTLE_SCENE_PICK") != "0")
        {
            excludedScenes = global::ModderLords.Core.Compat.BattleSceneCache.Scan(selections.Select(s => s.Module.FolderPath));
            if (excludedScenes.Count > 0)
                messages.Add($"battle scenes: {excludedScenes.Count} scene(s) ship without a terrain shader cache and will not be chosen for field battles"
                    + (excludedScenes.Any(s => s.StartsWith("battle_terrain", StringComparison.Ordinal)) ? $" ({string.Join(", ", excludedScenes.Where(s => s.StartsWith("battle_terrain", StringComparison.Ordinal)).Take(4))})" : ""));
        }
        var set = Compat.RecipeSet.Build(entries, "ModderLords", hints, authority, excludedScenes, traced);
        set.WriteInto(sync.FolderPath);
        if (traced.Count > 0 && (authority is null || traced.Any(t => !set.Mods.Any(m => m.Id.Equals(t, StringComparison.OrdinalIgnoreCase) && m.TraceRoots is { Count: > 0 }))))
            messages.Add("trace: MODDERLORDS_TRACE_MODS names a mod that could not be analysed; it is not traced");
        foreach (var r in set.Mods)
        {
            if (r.TraceRoots is { Count: > 0 }) messages.Add($"trace {r.Id}: {r.TraceRoots.Count} entry point(s) counted on both sides (MODDERLORDS_TRACE_MODS); compare with: trace-diff --mod {r.Id}");
            if (r.CampaignBehaviors.Count + r.MissionBehaviors.Count > 0) messages.Add($"recipe {r.Id}: {r.CampaignBehaviors.Count} campaign behaviour(s), {r.MissionBehaviors.Count} mission behaviour(s) server-only");
            if (r.Handlers.Count + r.Unpatch.Count > 0) messages.Add($"recipe {r.Id}: {r.Handlers.Count} handler(s) server-only, {r.Unpatch.Count} leaking postfix(es) removed on clients");
            if (r.Notes is { } notes) messages.Add($"recipe {r.Id}: {notes}" + (notes.Contains("relay", StringComparison.Ordinal) || notes.Contains("review", StringComparison.Ordinal) ? $" (details: authority --mod {r.Id})" : ""));
            if (r.Settings is { } h) messages.Add($"recipe {r.Id}: settings hints {h.Include.Count} include, {h.Exclude.Count} exclude");
        }
    }

    /// <summary>
    /// Static authority analysis for the mods flagged server-authoritative, against the installed Coop. Null when Coop's
    /// GameInterface.dll cannot be found or read, in which case recipes fall back to whole-behaviour gating.
    /// </summary>
    private static IReadOnlyDictionary<string, global::ModderLords.Core.Compat.Authority.AuthorityReport>? BuildAuthorityReports(
        IReadOnlyList<ModSelection> flagged, List<string> messages)
    {
        if (flagged.Count == 0) return null;
        try
        {
            var gameInterface = global::ModderLords.Core.Compat.Authority.CoopSinks.FindGameInterface(ServerPaths.SteamLibraries());
            if (gameInterface is null)
            {
                messages.Add("server-only logic: Coop's GameInterface.dll not found, so whole behaviours are gated instead of the code analysis");
                return null;
            }
            var coop = global::ModderLords.Core.Compat.Authority.CoopSinks.Load(gameInterface);
            var reports = new Dictionary<string, global::ModderLords.Core.Compat.Authority.AuthorityReport>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in flagged)
            {
                var report = global::ModderLords.Core.Compat.Authority.AuthorityScan.Classify(
                    global::ModderLords.Core.Compat.Authority.ModAnalysis.Analyse(s.Module), coop);
                reports[s.Module.Id] = report;
                messages.Add($"authority {s.Module.Id}: {report.Summary}");
            }
            return reports;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException or InvalidOperationException)
        {
            messages.Add("server-only logic: code analysis failed (" + ex.Message + "), so whole behaviours are gated");
            return null;
        }
    }

    /// <summary>Planned community modules with the versions the server will advertise (for save diffs and client export).</summary>
    public static IReadOnlyDictionary<string, string> PlannedCommunityVersions(Prepared p) => PlannedCommunityVersions(p.Selections);

    /// <summary>The launcher's own bundled modules are added to every launch and are not part of anyone's world.</summary>
    private static readonly IReadOnlySet<string> BundledIds =
        new HashSet<string>(new[] { CompatModuleId, SyncModuleId }, StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyDictionary<string, string> PlannedCommunityVersions(IReadOnlyList<ModSelection> selections) =>
        selections.Where(s => !BundledIds.Contains(s.Module.Id))
            .ToDictionary(s => s.Module.Id, s => s.Module.Version, StringComparer.OrdinalIgnoreCase);
}
