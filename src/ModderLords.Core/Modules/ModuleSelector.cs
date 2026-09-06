using ModderLords.Core.Overlay;
using ModderLords.Core.Profiles;
using ModderLords.Core.Saves;

namespace ModderLords.Core.Modules;

/// <summary>
/// Turns the mods a profile names into concrete folders on disk. The same mod can exist in the game's Modules
/// folder and in the workshop at different versions, so something has to choose, and both launch paths choose the
/// same way.
/// </summary>
public static class ModuleSelector
{
    /// <summary>Picks a concrete folder for each enabled profile mod, reporting anything the user should know.</summary>
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
}
