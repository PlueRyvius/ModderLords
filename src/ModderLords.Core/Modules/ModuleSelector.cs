using ModderLords.Core.Overlay;
using ModderLords.Core.Profiles;
using ModderLords.Core.Saves;

namespace ModderLords.Core.Modules;

/// <summary>
/// Turns the mods a profile names into concrete folders on disk. The same mod can exist in the game's Modules
/// folder and in the workshop at different versions, so something has to choose, and both launch paths and the
/// Mods list choose the same way: through <see cref="Pick"/>.
/// </summary>
public static class ModuleSelector
{
    /// <summary>Which side of a launch is choosing, for packages that ship a folder per side.</summary>
    public enum ModuleSide { Server, Client }

    /// <summary>
    /// The folder name a package uses for the side that is launching. A mod shipped as
    /// <c>&lt;package&gt;/Client/&lt;Id&gt;</c> + <c>&lt;package&gt;/Server/&lt;Id&gt;</c> yields two candidates with
    /// the same id, the same folder name and the same version, so every earlier tie-break draws - and the winner was
    /// whatever the filesystem happened to enumerate first. That is inert while the two trees are byte-identical
    /// (COOP Family 1.4 is), and silently wrong the moment a package ships trees that differ, which is exactly what
    /// its README says to expect. Prefer the side that is actually launching.
    /// </summary>
    private static string SideFolderName(ModuleSide side) => side == ModuleSide.Server ? "Server" : "Client";

    private static bool IsUnderSideFolder(DiscoveredModule m, ModuleSide side)
    {
        var wanted = SideFolderName(side);
        var parent = Path.GetDirectoryName(m.FolderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return parent is not null && string.Equals(Path.GetFileName(parent), wanted, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Picks a concrete folder for each enabled profile mod, reporting anything the user should know.</summary>
    public static IReadOnlyList<ModSelection> Select(Profile profile, ModuleCatalog catalog, List<string> messages,
        ModuleSide side = ModuleSide.Server)
    {
        var list = new List<ModSelection>();
        foreach (var pm in profile.EnabledMods)
        {
            var candidates = catalog.Candidates(pm.Id).ToList();
            var pick = Pick(pm, candidates, side);
            if (pick is null) { messages.Add($"{pm.Id}: not installed anywhere the tool looks; skipped"); continue; }
            if (candidates.Count > 1 && pm.SourcePath is null) messages.Add($"{pm.Id}: {candidates.Count} copies found, using {pick.FolderPath}");
            if (pm.LastVersion is not null && !SaveHeaderReader.VersionsEqual(pm.LastVersion, pick.Version))
                messages.Add($"{pm.Id}: version changed since last launch ({pm.LastVersion} -> {pick.Version}); players must update too");
            list.Add(new ModSelection(pick, pm.Role));
        }
        return list;
    }

    /// <summary>
    /// The copy of one profile mod that loads, or null when it is not installed. This is the whole rule, and the
    /// Mods list calls it too rather than restating it: a list that ticks one copy while the launch links another
    /// is how a host's pinned server copy "reverted" to the game's (1.2.5).
    ///
    /// A pinned folder wins while it still holds this mod, whether or not any scanned folder contains it - the pin
    /// is the user's own choice of copy, and two copies at the same version are not interchangeable just because
    /// their manifests agree. Then the version last launched, then the tie-breaks.
    /// </summary>
    public static DiscoveredModule? Pick(ProfileMod pm, IReadOnlyList<DiscoveredModule> candidates, ModuleSide side = ModuleSide.Server)
    {
        var pick = Pinned(pm, candidates);
        pick ??= pm.LastVersion is null ? null : candidates.FirstOrDefault(c => SaveHeaderReader.VersionsEqual(c.Version, pm.LastVersion));
        return pick ?? candidates
            .OrderByDescending(c => c.FolderName.Equals(pm.Id, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(c => IsUnderSideFolder(c, side))
            .ThenByDescending(c => c.Version)
            .FirstOrDefault();
    }

    /// <summary>
    /// The copy <see cref="ProfileMod.SourcePath"/> names: the catalog's own entry when the folder was scanned,
    /// otherwise the folder read directly. Null when nothing is pinned, the folder is gone, or it no longer holds
    /// this mod - a pin is to a copy of a mod, not to whatever now sits at that path.
    /// </summary>
    public static DiscoveredModule? Pinned(ProfileMod pm, IReadOnlyList<DiscoveredModule> candidates)
    {
        if (pm.SourcePath is null) return null;
        var scanned = candidates.FirstOrDefault(c => Junction.PathsEqual(c.FolderPath, pm.SourcePath));
        if (scanned is not null) return scanned;
        if (!Directory.Exists(pm.SourcePath)) return null;
        var parsed = ModuleCatalog.TryParse(pm.SourcePath, ModuleSourceKind.Custom, out _);
        return parsed is not null && parsed.Id.Equals(pm.Id, StringComparison.OrdinalIgnoreCase) ? parsed : null;
    }
}
