using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;

namespace ModderLords.Diagnostics;

/// <summary>
/// Diagnostic: records what the campaign map scene answers about terrain, so a server's answers can be diffed
/// against a single-player client's on the same map.
///
/// It exists to settle one question and no more. A dedicated server runs a <em>stripped</em> map scene — the
/// projection drops terrain, layers, nodes and outer_mesh because the landscape renderer deadlocks headless — and
/// the standing hypothesis for the field-battle client crash (create_texture_array) is that a scene without
/// terrain layers still answers terrain queries, just wrongly. Nothing has ever instrumented that. This does.
///
/// Off unless MODDERLORDS_TERRAIN_PROBE is set, and it writes once per process. Everything is by reflection: the
/// server's IMapScene is an obfuscated type inside DedicatedServer.Core, and this must bind to the interface only.
///
/// Compiled into BOTH module assemblies, because the two sides of the comparison load different modules: the server
/// always loads DedicatedServer.ModderLordsCompat, while a client can only load the community ModderLords.Compat.
/// Each host passes its own logger.
/// </summary>
public static class TerrainProbe
{
    /// <summary>Output CSV path, or "1" for the default next to the mod log.</summary>
    public const string PathVariable = "MODDERLORDS_TERRAIN_PROBE";
    /// <summary>Samples per axis across the map borders. Default 48 (2304 rows).</summary>
    public const string GridVariable = "MODDERLORDS_TERRAIN_PROBE_GRID";
    /// <summary>Extra positions to sample exactly, "x,y;x,y" — the battle position, when one is known.</summary>
    public const string PointsVariable = "MODDERLORDS_TERRAIN_PROBE_AT";

    private static bool _finished;
    private static bool _off;
    private static bool _announced;

    /// <summary>
    /// Cheap enough to call every tick: an env read and a null check until a campaign exists, and nothing at all
    /// once it has answered. Say when it is armed — "no output" otherwise cannot be told apart from "not loaded".
    /// </summary>
    public static void Tick(Action<string> info, Action<string> warn)
    {
        if (_finished || _off) return;
        var target = Environment.GetEnvironmentVariable(PathVariable);
        if (string.IsNullOrEmpty(target)) { _off = true; return; }
        if (!_announced)
        {
            _announced = true;
            info("terrain probe armed (" + PathVariable + "=" + target + "); waiting for the campaign map scene");
        }
        object? mapScene;
        try
        {
            var campaign = Type.GetType("TaleWorlds.CampaignSystem.Campaign, TaleWorlds.CampaignSystem", false);
            var current = campaign?.GetProperty("Current", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            if (current == null) return;
            mapScene = campaign!.GetProperty("MapSceneWrapper", BindingFlags.Public | BindingFlags.Instance)?.GetValue(current);
        }
        catch { return; }
        if (mapScene == null) return;
        _finished = true;
        try
        {
            var path = Resolve(target!);
            var rows = Write(mapScene, path);
            info("terrain probe: wrote " + rows + " samples to " + path);
        }
        catch (Exception ex)
        {
            // A diagnostic that takes the session down with it is worse than no diagnostic.
            warn("terrain probe failed: " + ex.GetBaseException().Message);
        }
    }

    private static string Resolve(string target)
    {
        if (target != "1") return target;
        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var dir = Path.Combine(docs, "Mount and Blade II Bannerlord", "Configs", "ModLogs");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "terrain-probe-" + Side() + ".csv");
    }

    private static string Side()
    {
        try
        {
            return Directory.GetCurrentDirectory().EndsWith("Win64_Shipping_Server", StringComparison.OrdinalIgnoreCase)
                ? "server" : "client";
        }
        catch { return "unknown"; }
    }

    private static int Write(object mapScene, string path)
    {
        var api = new MapSceneApi(mapScene);
        var text = new StringBuilder();

        // The header is the first thing to read in a diff: if the CRCs or the borders differ, the two runs are not
        // on the same map and every row below is noise. That confusion has already cost this investigation a day.
        text.Append("# side=").Append(Side())
            .Append(" impl=").Append(mapScene.GetType().FullName)
            .Append(" utc=").Append(DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)).AppendLine();
        api.GetMapBorders(out var min, out var max, out var maxHeight);
        var size = api.GetTerrainSize();
        text.Append("# borders=").Append(F(min.Item1)).Append(',').Append(F(min.Item2))
            .Append("..").Append(F(max.Item1)).Append(',').Append(F(max.Item2))
            .Append(" maxHeight=").Append(F(maxHeight))
            .Append(" terrainSize=").Append(F(size.Item1)).Append(',').Append(F(size.Item2)).AppendLine();
        text.Append("# sceneXmlCrc=").Append(api.GetSceneXmlCrc())
            .Append(" navMeshCrc=").Append(api.GetSceneNavigationMeshCrc())
            .Append(" navMeshFaces=").Append(api.GetNumberOfNavigationMeshFaces()).AppendLine();
        text.AppendLine("x,y,terrain,face,faceGroup,faceIsland,heightOk,height,patchScene,patchU,patchV,envCurrent,envTypes,snow,rain");

        var grid = ReadGrid();
        var rows = 0;
        // Cell centres, not border lines: the border itself is off the navmesh on every map and produces a band of
        // identical "no answer" rows that agree trivially and prove nothing.
        for (var iy = 0; iy < grid; iy++)
        for (var ix = 0; ix < grid; ix++)
        {
            var x = min.Item1 + (max.Item1 - min.Item1) * (ix + 0.5f) / grid;
            var y = min.Item2 + (max.Item2 - min.Item2) * (iy + 0.5f) / grid;
            Row(text, api, x, y);
            rows++;
        }
        foreach (var point in ReadPoints())
        {
            Row(text, api, point.Item1, point.Item2);
            rows++;
        }
        File.WriteAllText(path, text.ToString());
        return rows;
    }

    private static int ReadGrid()
    {
        var raw = Environment.GetEnvironmentVariable(GridVariable);
        if (string.IsNullOrEmpty(raw)) return 48;
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var grid) || grid < 2 || grid > 512)
            throw new ArgumentException(GridVariable + " must be between 2 and 512");
        return grid;
    }

    private static List<Tuple<float, float>> ReadPoints()
    {
        var points = new List<Tuple<float, float>>();
        var raw = Environment.GetEnvironmentVariable(PointsVariable);
        if (string.IsNullOrEmpty(raw)) return points;
        foreach (var pair in raw!.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split(',');
            if (parts.Length != 2
                || !float.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
                || !float.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
                throw new ArgumentException(PointsVariable + " expects \"x,y;x,y\"; got \"" + pair + "\"");
            points.Add(Tuple.Create(x, y));
        }
        return points;
    }

    private static void Row(StringBuilder text, MapSceneApi api, float x, float y)
    {
        var face = api.GetFaceIndex(x, y);
        var patch = api.GetMapPatchAtPosition(x, y);
        var gotHeight = api.GetHeightAtPoint(x, y, out var height);
        var env = api.GetEnvironmentTerrainTypesCount(x, y, out var envCurrent);
        text.Append(F(x)).Append(',').Append(F(y)).Append(',')
            .Append(api.GetTerrainTypeAtPosition(x, y)).Append(',')
            .Append(face.Item1).Append(',').Append(face.Item2).Append(',').Append(face.Item3).Append(',')
            .Append(gotHeight ? 1 : 0).Append(',').Append(F(height)).Append(',')
            .Append(patch.Item1).Append(',').Append(F(patch.Item2)).Append(',').Append(F(patch.Item3)).Append(',')
            .Append(envCurrent).Append(',').Append(env).Append(',')
            .Append(F(api.GetSnowAmountAtPosition(x, y))).Append(',')
            .Append(F(api.GetRainAmountAtPosition(x, y)))
            .AppendLine();
    }

    /// <summary>Fixed precision, invariant: a float formatted two ways diffs as a difference that is not one.</summary>
    private static string F(float value) =>
        float.IsNaN(value) || float.IsInfinity(value) ? "nan" : value.ToString("0.####", CultureInfo.InvariantCulture);

    /// <summary>
    /// The IMapScene surface this probe needs, bound once by reflection. Bind failures throw here, at setup, rather
    /// than producing a CSV of empty columns that reads like an answer.
    /// </summary>
    private sealed class MapSceneApi
    {
        private readonly object _scene;
        private readonly MethodInfo _borders, _terrainSize, _xmlCrc, _navCrc, _faceCount;
        private readonly MethodInfo _faceIndex, _terrainAt, _envCount, _patchAt, _heightAt, _snow, _rain;
        private readonly MethodInfo _vec2Add, _zero;
        private readonly ConstructorInfo _vec2Ctor;
        private readonly FieldInfo _vecX, _vecY, _faceIdx, _faceGroup, _faceIsland, _patchScene, _patchCoords;

        internal MapSceneApi(object scene)
        {
            _scene = scene;
            var map = Type.GetType("TaleWorlds.CampaignSystem.Map.IMapScene, TaleWorlds.CampaignSystem", false)
                      ?? throw new MissingMemberException("TaleWorlds.CampaignSystem.Map.IMapScene");
            if (!map.IsInstanceOfType(scene))
                throw new InvalidOperationException("map scene does not implement IMapScene: " + scene.GetType().FullName);
            _borders = Bind(map, "GetMapBorders");
            _terrainSize = Bind(map, "GetTerrainSize");
            _xmlCrc = Bind(map, "GetSceneXmlCrc");
            _navCrc = Bind(map, "GetSceneNavigationMeshCrc");
            _faceCount = Bind(map, "GetNumberOfNavigationMeshFaces");
            _faceIndex = Bind(map, "GetFaceIndex");
            _terrainAt = Bind(map, "GetTerrainTypeAtPosition");
            _envCount = Bind(map, "GetEnvironmentTerrainTypesCount");
            _patchAt = Bind(map, "GetMapPatchAtPosition");
            _heightAt = Bind(map, "GetHeightAtPoint");
            _snow = Bind(map, "GetSnowAmountAtPosition");
            _rain = Bind(map, "GetRainAmountAtPosition");

            var vec2 = Type.GetType("TaleWorlds.Library.Vec2, TaleWorlds.Library", false)
                       ?? throw new MissingMemberException("TaleWorlds.Library.Vec2");
            _vec2Ctor = vec2.GetConstructor(new[] { typeof(float), typeof(float) })
                        ?? throw new MissingMethodException("Vec2(float, float)");
            _vecX = Field(vec2, "x");
            _vecY = Field(vec2, "y");

            // CampaignVec2 has no public constructor; Zero plus a Vec2 is the supported way to name a position.
            var campaignVec2 = Type.GetType("TaleWorlds.CampaignSystem.CampaignVec2, TaleWorlds.CampaignSystem", false)
                               ?? throw new MissingMemberException("TaleWorlds.CampaignSystem.CampaignVec2");
            _zero = campaignVec2.GetProperty("Zero", BindingFlags.Public | BindingFlags.Static)?.GetGetMethod()
                    ?? throw new MissingMethodException("CampaignVec2.Zero");
            _vec2Add = campaignVec2.GetMethod("op_Addition", BindingFlags.Public | BindingFlags.Static, null,
                           new[] { campaignVec2, vec2 }, null)
                       ?? throw new MissingMethodException("CampaignVec2 + Vec2");

            var faceRecord = Type.GetType("TaleWorlds.Library.PathFaceRecord, TaleWorlds.Library", false)
                             ?? throw new MissingMemberException("TaleWorlds.Library.PathFaceRecord");
            _faceIdx = Field(faceRecord, "FaceIndex");
            _faceGroup = Field(faceRecord, "FaceGroupIndex");
            _faceIsland = Field(faceRecord, "FaceIslandIndex");

            var patch = Type.GetType("TaleWorlds.CampaignSystem.Map.MapPatchData, TaleWorlds.CampaignSystem", false)
                        ?? throw new MissingMemberException("TaleWorlds.CampaignSystem.Map.MapPatchData");
            _patchScene = Field(patch, "sceneIndex");
            _patchCoords = Field(patch, "normalizedCoordinates");
        }

        private static MethodInfo Bind(Type map, string name) =>
            map.GetMethod(name, BindingFlags.Public | BindingFlags.Instance)
            ?? throw new MissingMethodException("IMapScene." + name);

        private static FieldInfo Field(Type owner, string name) =>
            owner.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new MissingFieldException(owner.Name + "." + name);

        private object Vec2(float x, float y) => _vec2Ctor.Invoke(new object[] { x, y });
        private object Position(float x, float y) => _vec2Add.Invoke(null, new[] { _zero.Invoke(null, null), Vec2(x, y) })!;
        private Tuple<float, float> AsPair(object vec) => Tuple.Create((float)_vecX.GetValue(vec)!, (float)_vecY.GetValue(vec)!);

        internal void GetMapBorders(out Tuple<float, float> min, out Tuple<float, float> max, out float maxHeight)
        {
            var args = new object?[3];
            _borders.Invoke(_scene, args);
            min = AsPair(args[0]!);
            max = AsPair(args[1]!);
            maxHeight = (float)args[2]!;
        }

        internal Tuple<float, float> GetTerrainSize() => AsPair(_terrainSize.Invoke(_scene, null)!);
        internal uint GetSceneXmlCrc() => (uint)_xmlCrc.Invoke(_scene, null)!;
        internal uint GetSceneNavigationMeshCrc() => (uint)_navCrc.Invoke(_scene, null)!;
        internal int GetNumberOfNavigationMeshFaces() => (int)_faceCount.Invoke(_scene, null)!;

        /// <summary>The enum's numeric value, not its name: the point is to compare answers, not to read them.</summary>
        internal int GetTerrainTypeAtPosition(float x, float y) =>
            (int)_terrainAt.Invoke(_scene, new[] { Position(x, y) })!;

        internal Tuple<int, int, int> GetFaceIndex(float x, float y)
        {
            var record = _faceIndex.Invoke(_scene, new[] { Position(x, y) })!;
            return Tuple.Create((int)_faceIdx.GetValue(record)!, (int)_faceGroup.GetValue(record)!, (int)_faceIsland.GetValue(record)!);
        }

        internal Tuple<int, float, float> GetMapPatchAtPosition(float x, float y)
        {
            var patch = _patchAt.Invoke(_scene, new[] { Position(x, y) })!;
            var coords = AsPair(_patchCoords.GetValue(patch)!);
            return Tuple.Create((int)_patchScene.GetValue(patch)!, coords.Item1, coords.Item2);
        }

        internal bool GetHeightAtPoint(float x, float y, out float height)
        {
            var args = new object?[] { Position(x, y), 0f };
            var ok = (bool)_heightAt.Invoke(_scene, args)!;
            height = (float)args[1]!;
            return ok;
        }

        /// <summary>The per-type counts around the position, joined with "|" so one CSV cell holds the whole answer.</summary>
        internal string GetEnvironmentTerrainTypesCount(float x, float y, out int current)
        {
            var args = new object?[] { Position(x, y), null };
            var list = _envCount.Invoke(_scene, args);
            current = (int)args[1]!;
            if (list is not IEnumerable values) return "";
            var text = new StringBuilder();
            foreach (var value in values)
            {
                if (text.Length > 0) text.Append('|');
                text.Append((int)value);
            }
            return text.ToString();
        }

        internal float GetSnowAmountAtPosition(float x, float y) => (float)_snow.Invoke(_scene, new[] { Vec2(x, y) })!;
        internal float GetRainAmountAtPosition(float x, float y) => (float)_rain.Invoke(_scene, new[] { Vec2(x, y) })!;
    }
}
