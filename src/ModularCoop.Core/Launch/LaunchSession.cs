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

    /// <summary>Everything except starting the process. Applies the overlay and writes server-config.json.</summary>
    public static Prepared Prepare(Profile profile, bool applySideEffects = true)
    {
        var messages = new List<string>();
        var paths = ResolvePaths(profile);
        var problems = paths.Validate().ToList();
        if (problems.Count > 0) throw new InvalidOperationException(string.Join(Environment.NewLine, problems));

        var catalog = Scan(profile, paths, out var gameRoot);
        messages.AddRange(catalog.Problems.Select(p => "catalog: " + p));
        var selections = Select(profile, catalog, messages);

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

    /// <summary>Planned community modules with the versions the server will advertise (for save diffs and client export).</summary>
    public static IReadOnlyDictionary<string, string> PlannedCommunityVersions(Prepared p) =>
        p.Selections.ToDictionary(s => s.Module.Id, s => s.Module.Version, StringComparer.OrdinalIgnoreCase);
}
