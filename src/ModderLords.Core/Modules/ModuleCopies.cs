namespace ModderLords.Core.Modules;

/// <summary>
/// Tells copies of one mod apart when the manifest cannot: same id, same version, different folders.
///
/// That is routine and usually means nothing - a mod subscribed in the workshop and also dropped into the game's
/// Modules folder - so such copies are shown as one. It stops meaning nothing when a host keeps a cut-down copy for
/// the dedicated server next to the full one for their own game (the report behind this: the same mod with and
/// without <c>RuntimeDataCache</c>, 1.2.5). The signal is each copy's set of top-level folder names: one directory
/// listing per copy, no file is opened or hashed, and it is the level at which a person strips a mod by hand.
/// </summary>
public static class ModuleCopies
{
    /// <summary>The names of the folders directly inside a module. Empty when it cannot be listed.</summary>
    public static IReadOnlySet<string> TopLevelFolders(string folderPath)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(folderPath)) names.Add(Path.GetFileName(dir));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return names;
    }

    /// <summary>
    /// One copy for each choice that is really on offer: every version, and within a version every distinct set of
    /// top-level folders. Order of first appearance is kept. <paramref name="keep"/> always stands for its own group,
    /// so the copy a profile pins is never the one folded away; otherwise the folder actually named after the mod
    /// does, which is the tie-break <see cref="ModuleSelector"/> uses too.
    /// </summary>
    public static List<DiscoveredModule> Distinct(IEnumerable<DiscoveredModule> copies, string id, DiscoveredModule? keep = null)
    {
        var result = new List<DiscoveredModule>();
        foreach (var version in copies.GroupBy(m => m.Version ?? "", StringComparer.OrdinalIgnoreCase))
        {
            var sameVersion = version.ToList();
            // The common case, and the reason nothing is listed for a mod installed once.
            if (sameVersion.Count == 1) { result.Add(sameVersion[0]); continue; }
            var shapes = new List<(IReadOnlySet<string> Folders, List<DiscoveredModule> Copies)>();
            foreach (var copy in sameVersion)
            {
                var folders = TopLevelFolders(copy.FolderPath);
                var shape = shapes.FirstOrDefault(s => s.Folders.SetEquals(folders));
                if (shape.Copies is null) shapes.Add((folders, [copy]));
                else shape.Copies.Add(copy);
            }
            foreach (var (_, group) in shapes)
                result.Add(group.FirstOrDefault(c => keep is not null && ReferenceEquals(c, keep))
                           ?? group.OrderByDescending(c => c.FolderName.Equals(id, StringComparison.OrdinalIgnoreCase)).First());
        }
        return result;
    }

    /// <summary>
    /// A copy of the same mod and version that has every top-level folder <paramref name="module"/> has and more,
    /// or null. That is what a copy cut down for the server looks like from the outside, and it is the only case in
    /// which the server's choice of folder is known to be wrong for a player's game. The fullest wins; a draw goes
    /// to the earlier candidate, which is catalog order (game Modules, workshop, extra folders).
    /// </summary>
    public static DiscoveredModule? FullerCopy(DiscoveredModule module, IEnumerable<DiscoveredModule> candidates)
    {
        var others = candidates.Where(c => c.Id.Equals(module.Id, StringComparison.OrdinalIgnoreCase)
                                           && string.Equals(c.Version, module.Version, StringComparison.OrdinalIgnoreCase)
                                           && !Overlay.Junction.PathsEqual(c.FolderPath, module.FolderPath)).ToList();
        if (others.Count == 0) return null;
        var mine = TopLevelFolders(module.FolderPath);
        return others.Select(c => (Copy: c, Folders: TopLevelFolders(c.FolderPath)))
            .Where(x => x.Folders.IsProperSupersetOf(mine))
            .OrderByDescending(x => x.Folders.Count)
            .Select(x => x.Copy)
            .FirstOrDefault();
    }
}
