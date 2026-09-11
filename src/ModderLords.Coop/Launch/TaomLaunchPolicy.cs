namespace ModderLords.Coop.Launch;

/// <summary>
/// Keeps the normal Host button from silently turning a TAOM profile into a vanilla world. The release launcher does
/// not yet stage the diagnostic TAOM map and simulation assets, so a TAOM profile must point at a campaign that was
/// already created with TAOM loaded.
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
        if (string.IsNullOrWhiteSpace(saveName))
            return "TAOM needs a campaign save created with TAOM loaded. Create the campaign in Bannerlord, then use Import client save.";
        if (!saveExists(saveName))
            return $"TAOM save '{saveName}' was not found. Create it in Bannerlord with TAOM loaded, then use Import client save.";
        return null;
    }
}
