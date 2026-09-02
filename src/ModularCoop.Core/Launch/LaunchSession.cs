using ModularCoop.Core.Config;
using ModularCoop.Core.Modules;
using ModularCoop.Core.Overlay;
using ModularCoop.Core.Profiles;
using ModularCoop.Core.Saves;

namespace ModularCoop.Core.Launch;

/// <summary>
/// The one place that turns a Profile into a running engine: resolve paths, discover modules, pick sources, compute
/// the order, apply the overlay, render server-config.json, prepare the save, build the plan, start the process.
/// Used by both the CLI and the app so they cannot drift.
/// </summary>
public sealed class LaunchSession
{
    public sealed record Prepared(
        ServerPaths Paths,
        ModuleCatalog Catalog,
        IReadOnlyList<ModSelection> Selections,
        LoadOrder.Result Order,
        OverlayPlan OverlayPlan,
        LaunchPlan Plan,
        IReadOnlyList<string> Messages);

    public static readonly string[] KeepForDependencyOnly = ["MCM.MCMSubModule", "MCM.Internal.MCMImplementationSubModule"];

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

    /// <summary>Picks a concrete folder for each enabled profile mod.</summary>
    public static IReadOnlyList<ModSelection> Select(Profile profile, ModuleCatalog catalog, List<string> messages)
    {
        var list = new List<ModSelection>();
        foreach (var pm in profile.EnabledMods)
        {
            var candidates = catalog.Candidates(pm.Id).ToList();
            DiscoveredModule? pick = null;
            if (pm.SourcePath is not null)
                pick = candidates.FirstOrDefault(c => Junction.PathsEqual(c.FolderPath, pm.SourcePath))
                       ?? (Directory.Exists(pm.SourcePath) ? ModuleCatalog.TryParse(pm.SourcePath, ModuleSourceKind.Custom, out _) : null);
            pick ??= candidates.OrderByDescending(c => c.FolderName.Equals(pm.Id, StringComparison.OrdinalIgnoreCase)).ThenByDescending(c => c.Version).FirstOrDefault();
            if (pick is null) { messages.Add($"{pm.Id}: not installed anywhere the tool looks; skipped"); continue; }
            if (candidates.Count > 1 && pm.SourcePath is null) messages.Add($"{pm.Id}: {candidates.Count} copies found, using {pick.FolderPath}");
            if (pm.LastVersion is not null && !SaveHeaderReader.VersionsEqual(pm.LastVersion, pick.Version))
                messages.Add($"{pm.Id}: version changed since last launch ({pm.LastVersion} -> {pick.Version}); players must update too");
            list.Add(new ModSelection(pick, pm.Role));
        }
        return list;
    }

    public const string CompatModuleId = "DedicatedServer.ModularCoopCompat";
    public const string SyncModuleId = "ModularCoop.Compat";

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
    {
        var list = selections.ToList();
        if (profile.CompatGuards && !list.Any(s => s.Module.Id.Equals(CompatModuleId, StringComparison.OrdinalIgnoreCase)))
        {
            var compat = LocateCompatModule();
            if (compat is null) messages.Add("server guards requested but the compat module is missing next to the launcher; continuing without it");
            else list.Add(new ModSelection(compat, ServerRole.AsShipped));
        }
        if (profile.SettingsSync && !list.Any(s => s.Module.Id.Equals(SyncModuleId, StringComparison.OrdinalIgnoreCase)))
        {
            var sync = LocateSyncModule();
            if (sync is null) messages.Add("settings sync requested but the ModularCoop.Compat module is missing next to the launcher; continuing without it");
            else list.Add(new ModSelection(sync, ServerRole.AsShipped));
        }
        return list;
    }

    /// <summary>Everything except starting the process. Applies the overlay and writes server-config.json.</summary>
    public static Prepared Prepare(Profile profile, bool applySideEffects = true)
    {
        var messages = new List<string>();
        var paths = ResolvePaths(profile);
        var problems = paths.Validate().ToList();
        if (problems.Count > 0) throw new InvalidOperationException(string.Join(Environment.NewLine, problems));

        var catalog = Scan(profile, paths, out var gameRoot);
        messages.AddRange(catalog.Problems.Select(p => "catalog: " + p));
        var selections = WithCompat(profile, Select(profile, catalog, messages), messages);

        var stock = catalog.Modules.Where(m => m.IsStock).ToList();
        var order = LoadOrder.Compute(stock, selections.Select(s => s.Module).ToList(), profile.Mods.Select(m => m.Id).ToList());
        messages.AddRange(order.Issues.Select(i => "order: " + i));

        var overlayRoot = ProfileStore.OverlayDirFor(profile.Name);
        var overlayPlan = OverlayPlanner.Plan(overlayRoot, paths.ModulesRoot, selections);

        var extraEnv = new Dictionary<string, string>();
        if (selections.Count > 0)
        {
            var hook = HookSetup.LocateHook();
            if (hook is null) messages.Add("ModularCoop.Hook.dll is missing next to the launcher; mods with helper DLLs will fail to load");
            else foreach (var kv in HookSetup.Environment(hook, HookSetup.SearchDirs(paths, overlayPlan.Entries, gameRoot))) extraEnv[kv.Key] = kv.Value;
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

            ServerConfig.Write(paths, profile.SaveName, profile.Server);
            WriteRecipes(profile, selections, messages);
            if (!string.IsNullOrWhiteSpace(profile.SaveName))
            {
                var prep = SavePreparer.EnsureExists(paths, profile.SaveName);
                if (prep.CreatedFromTemplate) messages.Add($"save '{profile.SaveName}' did not exist; created a fresh world from {prep.TemplateUsed}");
            }
        }

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

    /// <summary>Re-creates the junctions/shadow folders for the profile without touching configs or saves (after a workshop update or Steam re-download).</summary>
    public static OverlayApplier.ApplyResult Resync(Profile profile)
    {
        var paths = ResolvePaths(profile);
        var catalog = Scan(profile, paths, out _);
        var selections = WithCompat(profile, Select(profile, catalog, new List<string>()), new List<string>());
        var plan = OverlayPlanner.Plan(ProfileStore.OverlayDirFor(profile.Name), paths.ModulesRoot, selections);
        return new OverlayApplier { KeepForDependencyOnly = KeepForDependencyOnly }.Apply(plan);
    }

    /// <summary>Layer 1: recipes.json inside the bundled sync module, built from the scan of every mod flagged server-authoritative.</summary>
    public static void WriteRecipes(Profile profile, IReadOnlyList<ModSelection> selections, List<string> messages)
    {
        var sync = LocateSyncModule();
        var flagged = profile.Mods.Where(m => m.Enabled && m.ServerAuthoritative).Select(m => m.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (sync is null)
        {
            if (flagged.Count > 0) messages.Add("server-only logic requested but the ModularCoop.Compat module is missing; recipes not written");
            return;
        }
        var entries = selections.Where(s => flagged.Contains(s.Module.Id)).Select(s => (s.Module.Id, Compat.AssemblyScan.Scan(s.Module))).ToList();
        var set = Compat.RecipeSet.Build(entries, "Modular Bannerlords Coop");
        set.WriteInto(sync.FolderPath);
        if (flagged.Count > 0 && !profile.SettingsSync) messages.Add("server-only logic is flagged for " + string.Join(", ", flagged) + " but Settings sync (the shared module) is off, so clients will not receive the recipe");
        foreach (var r in set.Mods) messages.Add($"recipe {r.Id}: {r.CampaignBehaviors.Count} campaign behaviour(s), {r.MissionBehaviors.Count} mission behaviour(s) server-only");
    }

    /// <summary>Planned community modules with the versions the server will advertise (for save diffs and client export).</summary>
    public static IReadOnlyDictionary<string, string> PlannedCommunityVersions(Prepared p) =>
        p.Selections.ToDictionary(s => s.Module.Id, s => s.Module.Version, StringComparer.OrdinalIgnoreCase);
}
