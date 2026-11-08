using ModderLords.Core.Modules;
using ModderLords.Testing;

namespace ModderLords.Core.Testing;

/// <summary>
/// The mod copies a launch picked, hashed before the games start and written into the run folder for the in-game
/// <c>mods.fingerprint</c> step to check the copies the games actually loaded against (decisions 23 and 24).
/// </summary>
public static class PickedCopies
{
    /// <summary>
    /// Writes <c>expected-fingerprints-&lt;side&gt;.txt</c> for <paramref name="side"/> and returns one line per mod
    /// for the console. Official modules are left out: they are the game's own. The server's code is not compared,
    /// since a server launch can leave a mod's DLLs out by its role; its data is.
    /// </summary>
    public static IReadOnlyList<string> Write(string runFolder, string side, IEnumerable<DiscoveredModule> modules,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? excluded = null)
    {
        var server = side == "server";
        var bin = server ? ModuleFingerprint.ServerBin : ModuleFingerprint.ClientBin;
        var picked = new List<(string Id, string Folder, ModuleFingerprint Print, bool CompareCode)>();
        foreach (var m in modules.Where(m => !OfficialModules.IsGameModule(m.Id)).DistinctBy(m => m.Id, StringComparer.OrdinalIgnoreCase))
        {
            IReadOnlyList<string>? skip = null;
            excluded?.TryGetValue(m.Id, out skip);
            picked.Add((m.Id, Path.GetFullPath(m.FolderPath), ModuleFingerprint.Of(m.FolderPath, bin, skip), !server));
        }
        File.WriteAllText(Path.Combine(runFolder, ModuleFingerprint.ExpectedFile(side)), ModuleFingerprint.WriteExpected(picked));
        return picked.Select(p => $"{side}: {p.Id} {p.Print} from {p.Folder}").ToList();
    }
}
