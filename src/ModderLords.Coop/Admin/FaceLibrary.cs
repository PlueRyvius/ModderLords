using System.Text.Json;
using ModderLords.Core.Profiles;

namespace ModderLords.Coop.Admin;

/// <summary>One saved appearance: a name the host chose and the game's BodyProperties text.</summary>
public sealed record SavedFace(string Name, string Body, bool Female, DateTime SavedAt)
{
    public override string ToString() => Name + (Female ? " (female)" : " (male)");
}

/// <summary>
/// The character editor's face library: named appearances the host saved, to put on any hero later, on any server.
/// One JSON file per user (<c>%LOCALAPPDATA%\ModderLords\faces.json</c>), read fresh each time so every editor
/// window sees what another saved.
/// </summary>
public sealed class FaceLibrary
{
    public const int MaxNameLength = 60;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public FaceLibrary(string? path = null) => Path = path ?? System.IO.Path.Combine(ProfileStore.RootDir, "faces.json");

    public string Path { get; }

    /// <summary>The saved faces by name. A missing file is an empty library; a damaged one throws, so it is never overwritten.</summary>
    public IReadOnlyList<SavedFace> Load()
    {
        if (!File.Exists(Path)) return [];
        var faces = JsonSerializer.Deserialize<List<SavedFace>>(File.ReadAllText(Path), Json) ?? [];
        return faces.Where(f => f.Name.Length > 0 && f.Body.Length > 0).OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Why this face cannot be saved under this name, or null.</summary>
    public static string? Problem(string name, string body)
    {
        var n = name.Trim();
        if (n.Length == 0) return "Give the face a name first.";
        if (n.Length > MaxNameLength) return $"A face name can be at most {MaxNameLength} characters.";
        return HeroBodies.Problem(body);
    }

    /// <summary>Saves the face, replacing one of the same name (ignoring case). Returns the library as it now is.</summary>
    public IReadOnlyList<SavedFace> Save(string name, string body, bool female)
    {
        if (Problem(name, body) is { } problem) throw new ArgumentException(problem);
        var n = name.Trim();
        var faces = Load().Where(f => !string.Equals(f.Name, n, StringComparison.CurrentCultureIgnoreCase)).ToList();
        faces.Add(new SavedFace(n, HeroBodies.Normalize(body), female, DateTime.UtcNow));
        Write(faces);
        return Load();
    }

    public IReadOnlyList<SavedFace> Delete(string name)
    {
        var faces = Load().Where(f => !string.Equals(f.Name, name, StringComparison.CurrentCultureIgnoreCase)).ToList();
        Write(faces);
        return Load();
    }

    public bool Contains(string name) => Load().Any(f => string.Equals(f.Name, name.Trim(), StringComparison.CurrentCultureIgnoreCase));

    private void Write(List<SavedFace> faces)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var temp = Path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(faces, Json));
        File.Move(temp, Path, overwrite: true);
    }
}
