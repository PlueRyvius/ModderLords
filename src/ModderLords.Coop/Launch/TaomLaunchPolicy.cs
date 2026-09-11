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

    public static bool IsTaom(IEnumerable<string> moduleIds) => moduleIds.Any(TaomIds.Contains);

    /// <summary>Returns a plain-language blocker, or null when the selected save is usable.</summary>
    public static string? MessageFor(IEnumerable<string> moduleIds, string? saveName, Func<string, bool> saveExists)
    {
        if (!IsTaom(moduleIds)) return null;
        if (string.IsNullOrWhiteSpace(saveName))
            return "TAOM needs a campaign save created with TAOM loaded. Create the campaign in Bannerlord, then use Import client save.";
        if (!saveExists(saveName))
            return $"TAOM save '{saveName}' was not found. Create it in Bannerlord with TAOM loaded, then use Import client save.";
        return null;
    }
}
