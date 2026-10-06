using ModderLords.Core.Modules;
using ModderLords.Core.Overlay;
using ModderLords.Core.Profiles;

namespace ModderLords.Core.Launch;

/// <summary>
/// The "server only" folders of a profile's mods, worked out for one client launch: which of the names a profile
/// lists are really folders of the copy being launched, and what to say about the ones that are not.
///
/// The names follow the same rules as the server's list (<see cref="ServerFolderExclusions"/>): a folder directly
/// inside the mod, never <c>bin</c> or a path, because the private view is built one top-level junction at a time
/// exactly as the server's shadow is. Nothing here can stop a launch. A name that cannot be honoured is reported
/// and the game is given the folder, which is the behaviour of every version before this one.
/// </summary>
public static class ClientFolderExclusions
{
    /// <summary>Starts the message that says which folders this game is not shown.</summary>
    public const string LeftOutNote = "left out of this game (server only): ";

    /// <summary>Starts the message for names that matched no folder of the launched copy.</summary>
    public const string NotLeftOutNote = "not left out of this game (this copy has no such folder): ";

    /// <summary>
    /// Module id to the folders that will be left out, named as they are on disk. A mod with nothing to leave out
    /// has no entry, which is what lets it stay a single junction (or need no private view at all).
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> Resolve(
        IEnumerable<DiscoveredModule> mods, Profile profile, List<string> messages)
    {
        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in mods)
        {
            var requested = profile.Mods.FirstOrDefault(m => m.Id.Equals(mod.Id, StringComparison.OrdinalIgnoreCase))?.ClientExcludedFolders;
            if (requested is null || requested.Count == 0) continue;

            var (valid, rejected) = ServerFolderExclusions.Split(requested);
            if (rejected.Count > 0)
                messages.Add($"folders: {mod.Id}: WARNING: ignored "
                    + string.Join(", ", rejected.Select(r => $"\"{r}\" ({Problem(r)})"))
                    + " in the server-only folders. Only the name of a folder directly inside the mod can be left out.");
            if (valid.Count == 0) continue;

            List<string> onDisk;
            try { onDisk = Directory.EnumerateDirectories(mod.FolderPath).Select(d => Path.GetFileName(d)!).ToList(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { onDisk = []; }

            var excluded = new List<string>();
            var absent = new List<string>();
            foreach (var name in valid)
            {
                var actual = onDisk.FirstOrDefault(d => d.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (actual is null) absent.Add(name); else excluded.Add(actual);
            }
            if (excluded.Count > 0)
            {
                result[mod.Id] = excluded;
                messages.Add($"folders: {mod.Id}: {LeftOutNote}{string.Join(", ", excluded)} (from {mod.FolderPath})");
            }
            if (absent.Count > 0) messages.Add($"folders: {mod.Id}: {NotLeftOutNote}{string.Join(", ", absent)}");
        }
        return result;
    }

    /// <summary>
    /// <see cref="ServerFolderExclusions.Problem"/> in words that are true of a player's game: its reason for
    /// refusing <c>bin</c> talks about the server, and here it is the game that loads the mod's code from it.
    /// </summary>
    private static string? Problem(string name) =>
        name.Equals("bin", StringComparison.OrdinalIgnoreCase) ? "the game loads the mod's code from bin" : ServerFolderExclusions.Problem(name);
}
