namespace ModderLords.Core.Overlay;

/// <summary>
/// The names a host (or a compat record) may ask to have left out of the server's view of a mod, and which of them
/// can be honoured. Shared by the planner, which applies them, and the app, which lets a host type them.
///
/// A name is only ever a folder directly inside the mod. That is not a limitation of taste: the shadow is built one
/// top-level junction at a time, so a top-level folder is the only thing it can leave out without copying anything.
/// </summary>
public static class ServerFolderExclusions
{
    /// <summary>One name per line as a host types them: trimmed, blanks dropped, repeats (in any casing) dropped.</summary>
    public static List<string> ParseLines(string? text)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<string>();
        foreach (var line in (text ?? "").Split('\n', '\r'))
        {
            // A trailing slash is how a folder is usually written; it is not a path.
            var name = line.Trim().TrimEnd('\\', '/');
            if (name.Length > 0 && seen.Add(name)) list.Add(name);
        }
        return list;
    }

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
