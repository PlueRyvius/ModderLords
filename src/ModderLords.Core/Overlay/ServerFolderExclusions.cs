namespace ModderLords.Core.Overlay;

/// <summary>
/// The names a host (or a compat record) may ask to have left out of the server's view of a mod, and which of them
/// can be honoured. Shared by the planner, which applies them, the client launch, which follows the same rules for
/// the folders a game is not shown, and the app, which shows a host why a stored name will be ignored.
///
/// A name is only ever a folder directly inside the mod. That is not a limitation of taste: the shadow is built one
/// top-level junction at a time, so a top-level folder is the only thing it can leave out without copying anything.
/// </summary>
public static class ServerFolderExclusions
{
    /// <summary>
    /// Why this name cannot be honoured, or null when it can. A path (or "..") would reach outside the one level the
    /// shadow controls; <c>bin</c> and <c>SubModule.xml</c> are the two things the shadow always supplies itself, so
    /// "leaving them out" would produce a module the engine cannot load rather than a lighter one.
    /// </summary>
    public static string? Problem(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "empty";
        if (name.IndexOfAny(['\\', '/', ':']) >= 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return "a path, not the name of a folder directly inside the mod";
        if (name.Contains("..", StringComparison.Ordinal) || name.Trim() == ".")
            return "a path, not the name of a folder directly inside the mod";
        if (name.Equals("bin", StringComparison.OrdinalIgnoreCase)) return "the server loads the mod's code from bin";
        if (name.Equals("SubModule.xml", StringComparison.OrdinalIgnoreCase)) return "the manifest is not a folder";
        return null;
    }

    /// <summary>
    /// Why the dedicated server is never shown this top-level folder of a mod, whatever a list says, or null when the
    /// lists decide. <paramref name="foldersInMod"/> is every top-level folder name of the same copy of the mod.
    /// <list type="bullet">
    /// <item><c>AssetPackages</c>: the client pack. The server reads a projected <c>DsAssetPackages</c> instead; a full
    /// client pack crashed the headless engine (docs/TAOM-SERVER-INVESTIGATION.md).</item>
    /// <item><c>Assets</c>: unpacked editor assets, which a mod in development carries. The server loads them ("Loading
    /// packages ...Modules/&lt;mod&gt;/Assets"), and a material with a texture makes it look for a texture it does not
    /// have, not even <c>_missing_texture</c>, recursing until the stack overflows (exit 0xC00000FD; harness suite
    /// smithing/ballista-coop, 2026-11-19).</item>
    /// <item><c>AssetSources</c> and <c>RuntimeDataCache</c>, but only beside <c>Assets</c>: they are its sources and
    /// its compiled cache. Without <c>Assets</c> they stay the host's choice, as before.</item>
    /// </list>
    /// </summary>
    public static string? NeverShownToServer(string name, IEnumerable<string> foldersInMod)
    {
        if (name.Equals("AssetPackages", StringComparison.OrdinalIgnoreCase))
            return "the dedicated server is never shown AssetPackages.";
        if (name.Equals("Assets", StringComparison.OrdinalIgnoreCase))
            return "the dedicated server is never shown unpacked Assets: it crashes on their textures.";
        if ((name.Equals("AssetSources", StringComparison.OrdinalIgnoreCase)
             || name.Equals("RuntimeDataCache", StringComparison.OrdinalIgnoreCase))
            && foldersInMod.Contains("Assets", StringComparer.OrdinalIgnoreCase))
            return $"the dedicated server is never shown {name} beside unpacked Assets.";
        return null;
    }

    /// <summary>The names that can be honoured, without repeats, and the ones that cannot.</summary>
    public static (IReadOnlyList<string> Valid, IReadOnlyList<string> Rejected) Split(IEnumerable<string>? names)
    {
        var valid = new List<string>();
        var rejected = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in names ?? [])
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var name = raw.Trim();
            if (!seen.Add(name)) continue;
            (Problem(name) is null ? valid : rejected).Add(name);
        }
        return (valid, rejected);
    }
}
