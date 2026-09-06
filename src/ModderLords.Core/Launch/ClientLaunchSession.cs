using ModderLords.Core.Modules;
using ModderLords.Core.Profiles;

namespace ModderLords.Core.Launch;

/// <summary>
/// Turns a profile into a running copy of the player's own game: find the install, discover mods wherever they
/// live, order them, and hand back the command line. The player-facing sibling of <see cref="LaunchSession"/>.
///
/// It shares the catalogue and the sorter with the server path and deliberately shares nothing else. There is no
/// overlay (the client resolves Workshop ids itself), no resolver hook (the game finds its own mods' assemblies),
/// no server config, no save preparation, and none of the compat modules — those exist to make mods survive a
/// headless engine, which is not a problem the player's game has.
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
        IReadOnlyList<string> Messages);

    /// <summary>Discovers modules for a client launch: the game's own Modules folder, every Steam Workshop item,
    /// and any extra folders the profile names. No dedicated server is involved, so no server root is scanned.</summary>
    public static ModuleCatalog Scan(Profile profile, out string? gameRoot)
    {
        var libraries = GamePaths.SteamLibraries().ToList();
        gameRoot = profile.GameRoot ?? ModuleCatalog.FindGameRoot(libraries);
        return ModuleCatalog.Scan(serverModulesRoot: "", gameRoot, libraries, profile.CustomModRoots);
    }

    public static Prepared Prepare(Profile profile)
    {
        var messages = new List<string>();
        var catalog = Scan(profile, out var gameRoot);
        if (gameRoot is null)
            throw new InvalidOperationException("Bannerlord install not found. Set the game folder in the profile.");
        messages.AddRange(catalog.Problems.Select(p => "catalog: " + p));

        // Officials come from the game folder only: a Workshop item is never an official module, and matching by
        // FOLDER name is what the sorter's head/tail lookups use (SandBox the folder carries the id "Sandbox").
        var installedOfficials = catalog.Modules
            .Where(m => m.Source == ModuleSourceKind.GameModules && m.IsOfficial)
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
        var mods = LaunchSession.Select(profile, catalog, messages).Select(s => s.Module).ToList();

        var order = LoadOrder.Compute(officials, mods, profile.Mods.Select(m => m.Id).ToList(), LoadOrder.Profile.Client);
        messages.AddRange(order.Issues.Select(i => "order: " + i));

        var plan = new ClientLaunchPlan { GameRoot = gameRoot, ModuleIds = order.ModuleIds };
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
