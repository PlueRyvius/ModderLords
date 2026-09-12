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
