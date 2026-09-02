using System.Text;
using System.Text.Json;

namespace ModularCoop.Core.Saves;

/// <summary>
/// Reads the metadata Bannerlord writes at the start of every .sav: an int32 length followed by that many bytes of
/// JSON {"List":{...}} with "Modules" (';' separated ids), "Module_&lt;Id&gt;" versions, "ApplicationVersion",
/// "CharacterName", "MainHeroLevel", "DayLong" ... Read-only; the tool never rewrites save bytes.
/// </summary>
public sealed record SaveHeader(
    string Path,
    IReadOnlyList<string> ModuleIds,
    IReadOnlyDictionary<string, string> ModuleVersions,
    string ApplicationVersion,
    string CharacterName,
    string MainHeroLevel,
    double DayLong,
    DateTime LastWriteUtc,
    long Length)
{
    public string Name => System.IO.Path.GetFileNameWithoutExtension(Path);

    public IEnumerable<string> CommunityModuleIds =>
        ModuleIds.Where(id => !OfficialIds.Contains(id) && !id.StartsWith("DedicatedServer.", StringComparison.OrdinalIgnoreCase));

    private static readonly HashSet<string> OfficialIds = new(StringComparer.OrdinalIgnoreCase)
        { "Native", "SandBoxCore", "Sandbox", "SandBox", "StoryMode", "CustomBattle", "BirthAndDeath", "Multiplayer", "FastMode", "NavalDLC" };
}

public static class SaveHeaderReader
{
    private const int MaxHeader = 4 * 1024 * 1024;

    public static SaveHeader? TryRead(string path, out string? error)
    {
        error = null;
        try
        {
            using var fs = File.OpenRead(path);
            Span<byte> lenBytes = stackalloc byte[4];
            if (fs.Read(lenBytes) != 4) { error = "file too short"; return null; }
            var len = BitConverter.ToInt32(lenBytes);
            if (len <= 2 || len > MaxHeader || len > fs.Length - 4) { error = $"implausible header length {len}"; return null; }
            var buf = new byte[len];
            var read = 0;
            while (read < len) { var n = fs.Read(buf, read, len - read); if (n <= 0) break; read += n; }
            var json = Encoding.UTF8.GetString(buf, 0, read);
            using var doc = JsonDocument.Parse(json);
            var list = doc.RootElement.TryGetProperty("List", out var l) ? l : doc.RootElement;

            string Get(string key) => list.TryGetProperty(key, out var v) ? v.GetString() ?? "" : "";
            var ids = Get("Modules").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var versions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var prop in list.EnumerateObject())
                if (prop.Name.StartsWith("Module_", StringComparison.Ordinal)) versions[prop.Name["Module_".Length..]] = prop.Value.GetString() ?? "";
            double.TryParse(Get("DayLong"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var day);
            var fi = new FileInfo(path);
            return new SaveHeader(path, ids, versions, Get("ApplicationVersion"), Get("CharacterName"), Get("MainHeroLevel"), day, fi.LastWriteTimeUtc, fi.Length);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    public static IEnumerable<SaveHeader> ReadAll(string savesDir)
    {
        if (!Directory.Exists(savesDir)) yield break;
        foreach (var f in Directory.EnumerateFiles(savesDir, "*.sav").OrderByDescending(File.GetLastWriteTimeUtc))
        {
            var name = Path.GetFileNameWithoutExtension(f);
            if (name.Equals(SavePreparer.TemplateFileName[..^4], StringComparison.OrdinalIgnoreCase)) continue;
            var h = TryRead(f, out _);
            if (h is not null) yield return h;
        }
    }

    public sealed record Diff(string ModuleId, string? SaveVersion, string? CurrentVersion, string Kind);

    /// <summary>Community modules that differ between the save and the planned set. Informational only.</summary>
    public static IReadOnlyList<Diff> Compare(SaveHeader save, IReadOnlyDictionary<string, string> plannedCommunityVersions)
    {
        var diffs = new List<Diff>();
        foreach (var id in save.CommunityModuleIds)
        {
            var sv = save.ModuleVersions.GetValueOrDefault(id);
            if (!plannedCommunityVersions.TryGetValue(id, out var cur)) diffs.Add(new Diff(id, sv, null, "missing now"));
            else if (!VersionsEqual(sv, cur)) diffs.Add(new Diff(id, sv, cur, "version changed"));
        }
        foreach (var kv in plannedCommunityVersions)
            if (!save.ModuleIds.Contains(kv.Key, StringComparer.OrdinalIgnoreCase)) diffs.Add(new Diff(kv.Key, null, kv.Value, "added"));
        return diffs;
    }

    /// <summary>Saves store four components (v0.9.28.0); manifests often three (v0.9.28). Compare numerically.</summary>
    public static bool VersionsEqual(string? a, string? b)
    {
        if (a is null || b is null) return a == b;
        static int[] Parts(string v) => v.TrimStart('v', 'e', 'b', 'a', 'd').Split('.').Select(p => int.TryParse(p, out var n) ? n : 0).Concat(new[] { 0, 0, 0, 0 }).Take(4).ToArray();
        return Parts(a).SequenceEqual(Parts(b));
    }
}
