namespace ModderLords.Coop.Launch;

/// <summary>
/// Keeps the normal Host button from silently turning a TAOM profile into a vanilla world.
///
/// A TAOM profile has to point at a campaign that was created with TAOM loaded — the pre-baked
/// default_new_game.sav lists Native;SandBoxCore;Sandbox;Coop and nothing else, and serving it with TAOM's modules
/// attached loads the vanilla map (navmesh CRC 1465536726 rather than TAOM's 3101457840).
///
/// The launcher can now make that campaign itself when the full recipe is enabled: it stages the headless map and
/// simulation assets and runs a creation pass before hosting. So these messages are the fallback for a profile that
/// is <i>not</i> in a state to be created automatically, not the normal path.
/// </summary>
public static class TaomLaunchPolicy
{
    private static readonly HashSet<string> TaomIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "TAOM", "TAOM_Map",
    };

    private static readonly HashSet<string> PreparationIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "LOTRLOME_Armory", "TAOM", "TAOM_Map",
    };

    public static bool IsTaom(IEnumerable<string> moduleIds) => moduleIds.Any(TaomIds.Contains);
    public static bool NeedsPreparation(string moduleId) => PreparationIds.Contains(moduleId);
    public static bool HasCompleteRecipe(IEnumerable<string> moduleIds)
    {
        var ids = moduleIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return ids.Contains("TAOM") && ids.Contains("TAOM_Map") && ids.Contains("LOTRLOME_Armory");
    }

    /// <summary>
    /// Folders that are never empty in a working install. Measured 2026-09-22: a TAOM install whose content folders
    /// had been emptied (only its DLLs and manifest left) still loaded, logged each missing file, and showed players
    /// a blank culture list at character creation, which reads as a co-op bug. Checking for content catches that at
    /// launch instead.
    /// </summary>
    private static readonly (string ModuleId, string RelativePath)[] RequiredContent =
    {
        ("TAOM", "ModuleData"), ("TAOM", "GUI"),
        ("TAOM.Dependencies", "ModuleData"),
        ("TAOM_Map", "ModuleData"),
        ("LOTRLOME_Armory", "ModuleData"),
    };

    /// <summary>
    /// One line per selected TAOM module whose install is missing content, naming the folder. Empty when every
    /// selected module looks complete. Modules that are not selected are not checked.
    /// </summary>
    public static IReadOnlyList<string> InstallProblems(IEnumerable<(string Id, string FolderPath)> modules)
    {
        var problems = new List<string>();
        foreach (var (id, folder) in modules)
        {
            var missing = RequiredContent
                .Where(r => r.ModuleId.Equals(id, StringComparison.OrdinalIgnoreCase))
                .Select(r => r.RelativePath)
                .Where(rel => !HasAnyFile(Path.Combine(folder, rel)))
                .ToList();
            if (missing.Count > 0)
                problems.Add($"{id} is installed without its {string.Join(" and ", missing)} content ({folder}). " +
                             "The game would load it and show players a broken character creation. Reinstall " + id + ".");
        }
        return problems;
    }

    private static bool HasAnyFile(string dir)
    {
        try { return Directory.Exists(dir) && Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Any(); }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    /// <summary>
    /// TAOM's co-op support ships in the shared ModderLords.Compat module, which the launcher loads only with Settings
    /// sync on. Without it TAOM runs on every peer as if it were alone.
    /// </summary>
    public static string? SyncProblem(IEnumerable<string> moduleIds, bool settingsSync) =>
        !settingsSync && IsTaom(moduleIds)
            ? "TAOM needs Settings sync on: ModderLords' TAOM co-op support is in the ModderLords.Compat module, which " +
              "the server and every player load only with Settings sync. Turn it on in the Server tab."
            : null;

    /// <summary>Returns a plain-language blocker, or null when the selected save is usable.</summary>
    public static string? MessageFor(IEnumerable<string> moduleIds, string? saveName, Func<string, bool> saveExists)
    {
        var ids = moduleIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!IsTaom(ids)) return null;
        var missing = new[] { "TAOM", "TAOM_Map", "LOTRLOME_Armory" }.Where(id => !ids.Contains(id)).ToList();
        if (missing.Count > 0)
            return "TAOM hosting needs TAOM, TAOM_Map, and LOTRLOME_Armory enabled. Missing: " + string.Join(", ", missing) + ".";
        // Reached only when the launcher is not going to create the campaign itself, so the advice has to be the
        // manual route. LaunchSession prints the "will be created automatically" line instead when it can.
        if (string.IsNullOrWhiteSpace(saveName))
            return "TAOM needs a campaign save created with TAOM loaded. Leave the save name empty to have the launcher " +
                   "create one, or create the campaign in Bannerlord and use Import client save.";
        if (!saveExists(saveName))
            return $"TAOM save '{saveName}' was not found. Clear the save name to have the launcher create a campaign, " +
                   "or create it in Bannerlord with TAOM loaded and use Import client save.";
        return null;
    }
}
