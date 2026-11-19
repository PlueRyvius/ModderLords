namespace ModderLords.Core.Modules;

/// <summary>
/// The server's module list may hold only two kinds of module: what the dedicated server package ships
/// (<see cref="ModuleCatalog.IsServerPackageModule"/>) and what the profile selected. Anything else is a module the
/// players' games were never told about, and Coop refuses every join over it, with a message that names the mod but
/// not why the server loaded it.
///
/// The in-game test harness checks this before every Coop launch, so a regression stops the run with the cause instead
/// of a join timeout (2026-11-18: another tool's mods in the server's Modules folder, "module 'Improved Garrisons' is
/// required").
/// </summary>
public static class ServerModuleListCheck
{
    /// <summary>Null when the list is clean; otherwise one message naming every module that should not be there.</summary>
    public static string? Problem(IEnumerable<string> serverModuleIds, IEnumerable<string> selectedIds,
        IReadOnlyList<DiscoveredModule> foreignServerModules)
    {
        var selected = new HashSet<string>(selectedIds, StringComparer.OrdinalIgnoreCase);
        var extra = serverModuleIds
            .Where(id => !ModuleCatalog.IsServerPackageModule(id) && !selected.Contains(id))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (extra.Count == 0) return null;

        var described = extra.Select(id => foreignServerModules.FirstOrDefault(f => f.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) is { } f
            ? $"{id} (a real folder in the server's Modules folder that another tool put there: {f.FolderPath})"
            : id);
        return "the server would load modules the profile did not select, and Coop would refuse every player over them: "
               + string.Join("; ", described);
    }
}
