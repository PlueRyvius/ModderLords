
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
            if (sync is null) messages.Add("settings sync requested but the ModderLords.Compat module is missing next to the launcher; continuing without it");
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
        if (applySideEffects)
        {
            var missing = profile.EnabledMods.Where(pm => !ClientManifest.CoopClientModuleIds.Contains(pm.Id) &&
                !selections.Any(s => s.Module.Id.Equals(pm.Id, StringComparison.OrdinalIgnoreCase)) &&
                !catalog.Modules.Any(m => m.IsStock && m.Id.Equals(pm.Id, StringComparison.OrdinalIgnoreCase)))
                .Select(pm => pm.Id).ToList();
            if (missing.Count > 0) throw new InvalidOperationException("Missing selected mods: " + string.Join(", ", missing) +
                ". Download them and Rescan, or untick them. The imported list has been kept.");
        }

        var stock = catalog.Modules.Where(m => m.IsStock).ToList();
        var order = LoadOrder.Compute(stock, selections.Select(s => s.Module).ToList(), profile.Mods.Select(m => m.Id).ToList());
        messages.AddRange(order.Issues.Select(i => "order: " + i));

        var overlayRoot = ProfileStore.OverlayDirFor(profile.Name);
        var overlayPlan = OverlayPlanner.Plan(overlayRoot, paths.ModulesRoot, selections);

        var extraEnv = new Dictionary<string, string>();
        if (selections.Count > 0)
        {
            var hook = HookSetup.LocateHook();
            if (hook is null) messages.Add("ModderLords.Hook.dll is missing next to the launcher; mods with helper DLLs will fail to load");
            else foreach (var kv in HookSetup.Environment(hook, HookSetup.SearchDirs(paths, overlayPlan.Entries, gameRoot))) extraEnv[kv.Key] = kv.Value;
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
                var overrides = Live.SettingsOverridesStore.Load(profile.Name);
                if (!overrides.IsEmpty)
                {
                    Live.SettingsOverridesStore.WriteToLiveDir(liveDir, overrides);
                    messages.Add($"mod settings: {overrides.Count} override(s) in {overrides.Objects.Count} settings object(s) staged; applied once the server has loaded");
                }
            }
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
            if (flagged.Count > 0) messages.Add("server-only logic requested but the ModderLords.Compat module is missing; recipes not written");
            return;
        }
        var entries = selections.Where(s => flagged.Contains(s.Module.Id)).Select(s =>
        {
            var pm = profile.Mods.First(m => m.Id.Equals(s.Module.Id, StringComparison.OrdinalIgnoreCase));
            return (s.Module.Id, AssemblyScan.Scan(s.Module), (IReadOnlyCollection<string>)pm.ClientSideBehaviors);
        }).ToList();
        var db = CompatDb.Current;
        var hints = selections.Select(s => db.Find(s.Module.Id)).Where(r => r is not null)
            .Select(r => (r!.Id, (IReadOnlyList<string>)r.SettingsTypes, (IReadOnlyList<string>)r.IgnoreSettingsTypes)).ToList();
        var set = Compat.RecipeSet.Build(entries, "ModderLords", hints);
        set.WriteInto(sync.FolderPath);
        if (flagged.Count > 0 && !profile.SettingsSync) messages.Add("server-only logic is flagged for " + string.Join(", ", flagged) + " but Settings sync (the shared module) is off, so clients will not receive the recipe");
        foreach (var r in set.Mods)
        {
            if (r.CampaignBehaviors.Count + r.MissionBehaviors.Count > 0) messages.Add($"recipe {r.Id}: {r.CampaignBehaviors.Count} campaign behaviour(s), {r.MissionBehaviors.Count} mission behaviour(s) server-only");
            if (r.Settings is { } h) messages.Add($"recipe {r.Id}: settings hints {h.Include.Count} include, {h.Exclude.Count} exclude");
        }
    }

    /// <summary>Planned community modules with the versions the server will advertise (for save diffs and client export).</summary>
    public static IReadOnlyDictionary<string, string> PlannedCommunityVersions(Prepared p) => p.Modules.Versions;
}
