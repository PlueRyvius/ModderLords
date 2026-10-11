using ModderLords.Core.Compat;
using ModderLords.Core.Modules;
using System.Collections.Immutable;
using ModderLords.Core.Profiles;

namespace ModderLords.Core.Launch;

/// <summary>
/// Turns a profile into a running copy of the player's own game: find the install, discover mods wherever they
/// live, order them, and hand back the command line. The player-facing sibling of <see cref="LaunchSession"/>.
///
/// It shares the catalogue and sorter with the server path. Ambiguous IDs and custom roots use a private client
/// view so the requested copy wins over the game's normal discovery order, and so does a mod with a "server only"
/// folder to leave out. No manifests are rewritten and no server config, save preparation, or resolver hook is involved.
/// </summary>
public static class ClientLaunchSession
{
    public sealed record Prepared(
        string GameRoot,
        ModuleCatalog Catalog,
        IReadOnlyList<DiscoveredModule> Officials,
        IReadOnlyList<DiscoveredModule> Mods,
        LoadOrder.Result Order,
        ClientLaunchPlan Plan,
        IReadOnlyList<string> Messages)
    {
        /// <summary>The engine-agnostic half of this launch. Roles are meaningless on the client, so every mod is
        /// reported as it ships; the ids, versions and order are what the shared export code reads.</summary>
        public ModuleSelectionResult Modules =>
            new(Catalog, Mods.Select(m => new Overlay.ModSelection(m, Overlay.ServerRole.AsShipped)).ToList(), Order);
    }

    /// <summary>Discovers modules for a client launch: the game's own Modules folder, every Steam Workshop item,
    /// and any extra folders the profile names. No dedicated server is involved, so no server root is scanned.</summary>
    public static ModuleCatalog Scan(Profile profile, out string? gameRoot)
    {
        var libraries = GamePaths.SteamLibraries().ToList();
        gameRoot = profile.GameRoot ?? ModuleCatalog.FindGameRoot(libraries);
        return ModuleCatalog.Scan(serverModulesRoot: "", gameRoot, libraries, profile.CustomModRoots);
    }

    /// <param name="scanned">
    /// A catalogue the caller has already scanned for this profile, with the game root that scan found. The app's
    /// order preview passes the one from its last Rescan, so ticking a box does not walk the whole Workshop again.
    /// A launch passes nothing and always scans fresh.
    /// </param>
    public static Prepared Prepare(Profile profile, (ModuleCatalog Catalog, string? GameRoot)? scanned = null)
    {
        var messages = new List<string>();
        string? gameRoot;
        ModuleCatalog catalog;
        if (scanned is { } s) (catalog, gameRoot) = s;
        else catalog = Scan(profile, out gameRoot);
        if (gameRoot is null)
            throw new InvalidOperationException("Bannerlord install not found. Set the game folder in the profile.");
        messages.AddRange(catalog.Problems.Select(p => "catalog: " + p));

        // Officials come from the game folder only: a Workshop item is never an official module, and matching by
        // FOLDER name is what the sorter's head/tail lookups use (SandBox the folder carries the id "Sandbox").
        var installedOfficials = catalog.Modules
            .Where(m => m.Source == ModuleSourceKind.GameModules && OfficialModules.IsGameModule(m.Id))
            .ToList();
        var officials = profile.ClientOfficialModules
            .Select(want => installedOfficials.FirstOrDefault(m => m.FolderName.Equals(want, StringComparison.OrdinalIgnoreCase)
                                                                  || m.Id.Equals(want, StringComparison.OrdinalIgnoreCase)))
            .Where(m => m is not null).Select(m => m!)
            .DistinctBy(m => m.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var want in profile.ClientOfficialModules)
            if (!officials.Any(m => m.FolderName.Equals(want, StringComparison.OrdinalIgnoreCase) || m.Id.Equals(want, StringComparison.OrdinalIgnoreCase)))
                messages.Add($"official module {want} is not installed in {GamePaths.ModulesDir(gameRoot)}; skipped");

        // Roles are a dedicated-server concept (they decide what the overlay strips out of a manifest). On the
        // player's own machine every enabled mod simply loads, so the selection is used for its module list only.
        var mods = ModuleSelector.Select(profile, catalog, messages, ModuleSelector.ModuleSide.Client).Select(s => s.Module).ToList();

        var order = LoadOrder.Compute(officials, mods, profile.Mods.Select(m => m.Id).ToList(), LoadOrder.Profile.Client,
            profile.ManualLoadOrder ? LoadOrder.OrderPolicy.Manual : LoadOrder.OrderPolicy.Suggest,
            CompatDb.Current.ClientFollowsCoop());
        messages.AddRange(order.Issues.Select(i => "order: " + i));

        // "Server only" folders. Community mods only: the profile has no entry for an official module, and hiding
        // part of the game's own modules from the game is not something a tick box should be able to do.
        var excludedFolders = ClientFolderExclusions.Resolve(mods, profile, messages, CompatDb.Current);

        var plan = new ClientLaunchPlan
        {
            GameRoot = gameRoot, ModuleIds = order.ModuleIds,
            ExcludedFolders = excludedFolders,
            SelectedModules = officials.Concat(mods).ToList(),
            // A Client optional mod this player does not have is not missing: the game starts without it.
            MissingModules = profile.EnabledMods.Where(pm => pm.Role != Overlay.ServerRole.ClientOptional && !mods.Any(m => m.Id.Equals(pm.Id, StringComparison.OrdinalIgnoreCase)))
                .Select(pm => pm.Id).Concat(profile.ClientOfficialModules.Where(id => !officials.Any(m => m.Id.Equals(id, StringComparison.OrdinalIgnoreCase))))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            RequiresIsolatedView = excludedFolders.Count > 0 || mods.Any(m => m.Source == ModuleSourceKind.Custom ||
                catalog.Candidates(m.Id).Select(c => Path.GetFullPath(c.FolderPath)).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1),
        };
        var problems = plan.Validate().ToList();
        if (problems.Count > 0) throw new InvalidOperationException(string.Join(Environment.NewLine, problems));
        if (profile.AutomaticCompatibility && ModderLords.Analysis.CompatibilityPlanner.BundledContracts().Any(c =>
            c.Provider == "ModderLords" && ((c.OfflineValidated && c.RuntimeValidated) || ModderLords.Operations.OperationValidationMode.Allows(c.Id)) &&
            mods.Any(m => m.Id == c.Module)))
        {
            var inputs = Compat.OperationAnalysisService.CreateRequest(profile,
                new(catalog, mods.Select(m => new Overlay.ModSelection(m, Overlay.ServerRole.AsShipped)).ToList(), order), gameRoot);
            plan = plan with { OperationInputs = inputs with { Modules = inputs.Modules.Select(m => m with { RunsOnServer = false }).ToImmutableArray() } };
        }

        return new Prepared(gameRoot, catalog, officials, mods, order, plan, messages);
    }

    /// <summary>
    /// Starts the game detached, exactly as <see cref="ClientLauncher.Start"/> does: no job object, because closing
    /// ModderLords must never close the player's game.
    /// </summary>
    /// <param name="note">Told what the launch did on the way that a player may need to know. Optional.</param>
    public static System.Diagnostics.Process Start(ClientLaunchPlan plan, Action<string>? note = null)
    {
        if (plan.MissingModules.Count > 0)
            throw new InvalidOperationException("Missing selected mods: " + string.Join(", ", plan.MissingModules) +
                ". Download them and Rescan, or untick them to launch without them. The imported list has been kept.");
        var errors = plan.Validate().ToList();
        if (errors.Count > 0) throw new InvalidOperationException(string.Join(Environment.NewLine, errors));
        if (Unblock(plan) is { } unblocked) note?.Invoke(unblocked);
        if (plan.UsesIsolatedView && OperatingSystem.IsWindows())
            plan = plan with { GameRoot = ClientModuleView.Create(plan, Path.Combine(Profiles.ProfileStore.RootDir, "client-launches")) };
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = plan.Exe,
            WorkingDirectory = plan.WorkingDirectory,
            UseShellExecute = plan.OperationInputs == null,
        };
        if (plan.OperationInputs != null)
        {
            var report = Compat.OperationAnalysisService.Analyze(plan.OperationInputs);
            var directory = Path.Combine(Profiles.ProfileStore.RootDir, "operation-client-sessions", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "local-inputs.json");
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                System.Text.Json.JsonSerializer.Serialize(stream, new { report.Plan.ModuleOrder, report.Gaps,
                    Files = report.LocalFiles, Values = report.Fingerprints.Where(f => f.Module == "$environment") }, ModderLords.Analysis.AnalysisJson.Options);
            psi.Environment["MODDERLORDS_OPERATION_INPUTS"] = path;
            // A server plan must arrive through the authenticated operation channel, not an inherited environment.
            psi.Environment.Remove("MODDERLORDS_OPERATION_PLAN");
        }
        foreach (var argument in plan.Arguments) psi.ArgumentList.Add(argument);
        return System.Diagnostics.Process.Start(psi) ?? throw new InvalidOperationException("Windows did not start " + plan.Exe);
    }

    /// <summary>
    /// Takes Windows' "downloaded from the internet" mark off the assemblies of every mod about to load, and says
    /// what it did, or null when nothing was marked.
    ///
    /// A marked DLL is refused by the game ("An attempt was made to load an assembly from a network location") and
    /// the mod then does nothing. The Mods tab offers a button for it, but a player who launches from Host mode never
    /// sees that list, and the game names the file by its path inside the launch view, which is a link: the marked
    /// file is the mod's own, wherever it was unzipped. Reported 2026-10-11 by a player who had checked the copy in
    /// the game folder, found no Unblock box on it, and had no way to tell that the launch used another copy.
    /// </summary>
    public static string? Unblock(ClientLaunchPlan plan)
    {
        var blocked = plan.SelectedModules
            .Select(m => (m.Id, Files: BlockedFiles.Find(m.FolderPath)))
            .Where(m => m.Files.Count > 0).ToList();
        if (blocked.Count == 0) return null;
        foreach (var mod in blocked) BlockedFiles.UnblockAll(mod.Files);
        var refused = blocked.Select(m => (m.Id, Count: m.Files.Count(BlockedFiles.IsBlocked))).Where(m => m.Count > 0).ToList();
        var mods = string.Join(", ", blocked.Select(m => m.Id));
        return refused.Count == 0
            ? $"unblocked {blocked.Sum(m => m.Files.Count)} file(s) Windows had marked as downloaded, in {mods}"
            : $"Windows has marked files in {mods} as downloaded and {refused.Sum(m => m.Count)} could not be unblocked " +
              $"({string.Join(", ", refused.Select(m => m.Id))}). The game will not load them. Close Bannerlord and its launcher and try again, " +
              "or run ModderLords as administrator once.";
    }
}
