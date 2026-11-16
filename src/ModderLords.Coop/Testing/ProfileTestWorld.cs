using ModderLords.Coop.Saves;

namespace ModderLords.Coop.Testing;

/// <summary>
/// The test world of a profile whose world ModderLords generates (TAOM, or the Host tab's tick box), for Coop runs that
/// name no world. It is generated once and kept under test-worlds as <c>profile-&lt;slug&gt;.sav</c>, with Coop's session
/// file and a note of the mods it was made with. Every run copies it fresh, like any named test world, so each run
/// starts from the same world and the profile's own save is never served to a test (tests change the world they run on).
/// A new one is generated when the profile's mods change, or on <c>--new-world</c>.
/// </summary>
public static class ProfileTestWorld
{
    /// <summary>The kept world's name under test-worlds for a profile.</summary>
    public static string Name(string profileName)
    {
        var slug = new string(profileName.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
        return "profile-" + (slug.Length == 0 ? "unnamed" : slug);
    }

    /// <summary>The note beside the kept world: the enabled mods, in load order, one id per line.</summary>
    public static string NotePath(string worldsDir, string name) => Path.Combine(worldsDir, name + ".mods.txt");

    /// <summary>
    /// Why the kept world cannot be used, so a new one has to be generated; null when it can. A world made with other
    /// mods, or another load order, is not the world these mods would make.
    /// </summary>
    public static string? Stale(string worldsDir, string name, IReadOnlyList<string> modIds, bool newWorld)
    {
        if (!File.Exists(Path.Combine(worldsDir, name + ".sav"))) return "no test world is kept for this profile yet";
        if (newWorld) return "a new test world was asked for (--new-world)";
        var note = NotePath(worldsDir, name);
        if (!File.Exists(note)) return "the kept test world has no note of the mods it was made with";
        var made = File.ReadAllLines(note).Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        return made.SequenceEqual(modIds, StringComparer.OrdinalIgnoreCase) ? null : "this profile's mods changed since its test world was made";
    }

    /// <summary>
    /// Keeps a just-generated save as the profile's test world: the save and Coop's session file, copied from the
    /// server's saves into <paramref name="worldsDir"/>, and the note of <paramref name="modIds"/>. Returns the kept save.
    /// </summary>
    public static string Keep(string generatedSave, string worldsDir, string name, IEnumerable<string> modIds)
    {
        Directory.CreateDirectory(worldsDir);
        var to = Path.Combine(worldsDir, name + ".sav");
        CopyOver(generatedSave, to);
        var session = SavePreparer.SessionPath(generatedSave);
        var toSession = SavePreparer.SessionPath(to);
        if (File.Exists(session)) CopyOver(session, toSession);
        else if (File.Exists(toSession)) File.Delete(toSession);
        File.WriteAllLines(NotePath(worldsDir, name), modIds);
        return to;
    }

    // Write then move: a kept world half-copied over is worse than none.
    private static void CopyOver(string from, string to)
    {
        var tmp = to + ".modderlords-tmp";
        File.Copy(from, tmp, overwrite: true);
        File.Move(tmp, to, overwrite: true);
    }
}
