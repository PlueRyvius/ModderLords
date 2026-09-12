using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;

namespace ModderLords.Core.Overlay;

/// <summary>Creates a private, render-free copy of a map mod's Main_map scene for the dedicated engine.</summary>
public static class HeadlessMapProjection
{
    private const string Marker = ".modderlords-headless-map";

    /// <summary>
    /// Set to keep the scene's &lt;terrain&gt; descriptor instead of removing it.
    ///
    /// <para>The projection does two separable things: it empties &lt;entities&gt; of every game_entity, and it removes
    /// the &lt;terrain&gt; element. Only the first is obviously about rendering. The second is what leaves
    /// GetMapPatchAtPosition answering sceneIndex 0 for the whole map, which is the measured cause of the
    /// field-battle client crash (docs/FIELD-BATTLE-TERRAIN.md) — and terrain.bin itself is copied into the
    /// projection intact, so the data is already there, merely undeclared.</para>
    ///
    /// <para>Whether the headless engine can carry the terrain descriptor without reaching the landscape renderer it
    /// deadlocks in has never been tested separately from entity removal. This switch is how to test it. If the
    /// server still serves with it on, the fix costs a line rather than a parser for a 56 MB format.</para>
    /// </summary>
    public const string KeepTerrainVariable = "MODDERLORDS_HEADLESS_MAP_KEEP_TERRAIN";
    private static readonly string[] BinaryFiles = ["navmesh.bin", "terrain.bin", "flora.bin", "atmosphere.xml"];

    public sealed record Result(string SourcePath, string OutputPath, string MetadataPath, string NavmeshSha256, bool Reused);

    public static Result Prepare(string sourceScene, string outputScene, bool? keepTerrain = null)
    {
        keepTerrain ??= !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(KeepTerrainVariable));
        sourceScene = Path.GetFullPath(sourceScene);
        outputScene = Path.GetFullPath(outputScene);
        if (!Directory.Exists(sourceScene)) throw new DirectoryNotFoundException($"Map scene was not found: {sourceScene}");
        if (IsInside(outputScene, sourceScene)) throw new InvalidOperationException("Headless map output must be outside the source scene");

        var sourceFiles = BinaryFiles.Select(name => Path.Combine(sourceScene, name)).ToList();
        var sourceXml = Path.Combine(sourceScene, "scene.xscene");
        if (!File.Exists(sourceXml) || sourceFiles.Any(f => !File.Exists(f)))
            throw new InvalidDataException($"Map scene is incomplete: {sourceScene}");
        var navmeshHash = Sha256(sourceFiles[0]);
        var metadata = Path.Combine(outputScene, "modderlords-map.xml");
        // The cache has to know which rule produced it, or flipping the switch silently reuses a scene built under
        // the other one — the same trap that made a mixed-version run look like a projection bug.
        if (TryReuse(outputScene, sourceScene, navmeshHash, metadata, keepTerrain.Value, out var reused)) return reused!;

        RefuseUnexpected(outputScene);
        Directory.CreateDirectory(outputScene);
        var document = XDocument.Load(sourceXml, LoadOptions.PreserveWhitespace);
        var root = document.Root ?? throw new InvalidDataException("Map scene XML has no root element");
        var entities = root.Element("entities") ?? throw new InvalidDataException("Map scene has no entities element");
        var min = Position(root, "border_min");
        var max = Position(root, "border_max");
        if (max[0] <= min[0] || max[1] <= min[1]) throw new InvalidDataException("Map border dimensions are invalid");
        var removed = entities.Descendants("game_entity").Count();
        entities.RemoveNodes();
        var terrain = root.Element("terrain") ?? throw new InvalidDataException("Map scene has no terrain dimensions");
        var nodeSize = ParseDouble(terrain.Attribute("node_size"), "terrain node_size");
        var nodesX = ParseInt(terrain.Attribute("node_dimension_x"), "terrain node_dimension_x");
        var nodesY = ParseInt(terrain.Attribute("node_dimension_y"), "terrain node_dimension_y");
        var terrainSize = $"{nodeSize * nodesX:0.###############},{nodeSize * nodesY:0.###############}";
        if (!keepTerrain.Value) terrain.Remove();

        var targetXml = Path.Combine(outputScene, "scene.xscene");
        document.Save(targetXml, SaveOptions.DisableFormatting);
        foreach (var file in sourceFiles) File.Copy(file, Path.Combine(outputScene, Path.GetFileName(file)), overwrite: false);

        var map = new XElement("headless-map",
            new XAttribute("border_min", string.Join(',', min.Select(Format))),
            new XAttribute("border_max", string.Join(',', max.Select(Format))),
            new XAttribute("terrain-size", terrainSize),
            new XAttribute("navmesh-sha256", navmeshHash));
        new XDocument(map).Save(metadata);
        File.WriteAllText(Path.Combine(outputScene, Marker), JsonSerializer.Serialize(new
        {
            source = sourceScene,
            removed_entities = removed,
            navmesh_sha256 = navmeshHash,
            keep_terrain = keepTerrain.Value,
            source_xml_length = new FileInfo(sourceXml).Length,
            source_xml_write_utc_ticks = File.GetLastWriteTimeUtc(sourceXml).Ticks,
        }, new JsonSerializerOptions { WriteIndented = true }));
        return new Result(sourceScene, outputScene, metadata, navmeshHash, false);
    }

    private static bool TryReuse(string output, string source, string navmeshHash, string metadata, bool keepTerrain, out Result? result)
    {
        result = null;
        try
        {
            var marker = Path.Combine(output, Marker);
            if (!File.Exists(marker) || !File.Exists(metadata)) return false;
            using var doc = JsonDocument.Parse(File.ReadAllText(marker));
            var root = doc.RootElement;
            if (!root.TryGetProperty("source", out var sourceValue) ||
                !string.Equals(sourceValue.GetString(), source, StringComparison.OrdinalIgnoreCase) ||
                !root.TryGetProperty("navmesh_sha256", out var hashValue) ||
                !string.Equals(hashValue.GetString(), navmeshHash, StringComparison.OrdinalIgnoreCase)) return false;
            var cachedKeepTerrain = root.TryGetProperty("keep_terrain", out var keepValue) && keepValue.GetBoolean();
            if (cachedKeepTerrain != keepTerrain) return false;
            if (BinaryFiles.Any(name => !File.Exists(Path.Combine(output, name))) || !File.Exists(Path.Combine(output, "scene.xscene"))) return false;
            result = new Result(source, output, metadata, navmeshHash, true);
            return true;
        }
        catch { return false; }
    }

    private static void RefuseUnexpected(string output)
    {
        if (!Directory.Exists(output)) return;
        if (!File.Exists(Path.Combine(output, Marker)))
            throw new IOException($"Refusing to overwrite an unexpected map folder: {output}");
        Directory.Delete(output, recursive: true);
    }

    private static bool IsInside(string path, string parent)
    {
        var relative = Path.GetRelativePath(parent, path);
        return relative == "." || (!relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative));
    }

    private static double[] Position(XElement root, string name)
    {
        var element = root.Descendants("game_entity").SingleOrDefault(e => string.Equals((string?)e.Attribute("name"), name, StringComparison.Ordinal));
        var position = element?.Element("transform")?.Attribute("position")?.Value
            ?? throw new InvalidDataException($"Map scene must contain exactly one {name} transform");
        var values = position.Split(',').Select(x => double.Parse(x, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        if (values.Length != 3 || values.Any(double.IsNaN) || values.Any(double.IsInfinity)) throw new InvalidDataException($"Invalid {name} position");
        return values;
    }

    private static double ParseDouble(XAttribute? value, string label) => value is null || !double.TryParse(value.Value, System.Globalization.NumberStyles.Float,
        System.Globalization.CultureInfo.InvariantCulture, out var parsed) || double.IsNaN(parsed) || double.IsInfinity(parsed)
        ? throw new InvalidDataException($"Invalid {label}") : parsed;

    private static int ParseInt(XAttribute? value, string label) => value is null || !int.TryParse(value.Value, out var parsed) || parsed <= 0
        ? throw new InvalidDataException($"Invalid {label}") : parsed;

    private static string Format(double value) => value.ToString("0.###############", System.Globalization.CultureInfo.InvariantCulture);

    private static string Sha256(string path)
    {
        using var sha = SHA256.Create(); using var stream = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
    }
}
