using ModderLords.Core.Modules;
using ModderLords.Core.Profiles;

namespace ModderLords.Core.Launch;

/// <summary>
/// Turns a profile into a running copy of the player's own game: find the install, discover mods wherever they
/// live, order them, and hand back the command line. The player-facing sibling of <see cref="LaunchSession"/>.
///
/// It shares the catalogue and sorter with the server path. Ambiguous IDs and custom roots use a private client
/// view so the requested copy wins over the game's normal discovery order. No manifests are rewritten and no
/// server config, save preparation, or resolver hook is involved.
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
        var mods = ModuleSelector.Select(profile, catalog, messages).Select(s => s.Module).ToList();

        var order = LoadOrder.Compute(officials, mods, profile.Mods.Select(m => m.Id).ToList(), LoadOrder.Profile.Client,
            profile.ManualLoadOrder ? LoadOrder.OrderPolicy.Manual : LoadOrder.OrderPolicy.Suggest);
        messages.AddRange(order.Issues.Select(i => "order: " + i));

        var plan = new ClientLaunchPlan
        {
            GameRoot = gameRoot, ModuleIds = order.ModuleIds,
            SelectedModules = officials.Concat(mods).ToList(),
            MissingModules = profile.EnabledMods.Where(pm => !mods.Any(m => m.Id.Equals(pm.Id, StringComparison.OrdinalIgnoreCase)))
                .Select(pm => pm.Id).Concat(profile.ClientOfficialModules.Where(id => !officials.Any(m => m.Id.Equals(id, StringComparison.OrdinalIgnoreCase))))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            RequiresIsolatedView = mods.Any(m => m.Source == ModuleSourceKind.Custom ||
                catalog.Candidates(m.Id).Select(c => Path.GetFullPath(c.FolderPath)).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1),
        };
        var problems = plan.Validate().ToList();
        if (problems.Count > 0) throw new InvalidOperationException(string.Join(Environment.NewLine, problems));

        return new Prepared(gameRoot, catalog, officials, mods, order, plan, messages);
    }

    /// <summary>
    /// Starts the game detached, exactly as <see cref="ClientLauncher.Start"/> does: no job object, because closing
    /// ModderLords must never close the player's game.
    /// </summary>
    public static System.Diagnostics.Process Start(ClientLaunchPlan plan)
    {
        if (plan.MissingModules.Count > 0)
            throw new InvalidOperationException("Missing selected mods: " + string.Join(", ", plan.MissingModules) +
                ". Download them and Rescan, or untick them to launch without them. The imported list has been kept.");
        var errors = plan.Validate().ToList();
        if (errors.Count > 0) throw new InvalidOperationException(string.Join(Environment.NewLine, errors));
        if (plan.RequiresIsolatedView && OperatingSystem.IsWindows())
            plan = plan with { GameRoot = ClientModuleView.Create(plan, Path.Combine(Profiles.ProfileStore.RootDir, "client-launches")) };
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = plan.Exe,
            WorkingDirectory = plan.WorkingDirectory,
            UseShellExecute = true,
        };
        psi.ArgumentList.Add(plan.ModuleToken);
        return System.Diagnostics.Process.Start(psi) ?? throw new InvalidOperationException("Windows did not start " + plan.Exe);
    }
}
