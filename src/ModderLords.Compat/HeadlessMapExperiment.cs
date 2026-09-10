using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Xml;
using HarmonyLib;
using TaleWorlds.Library;

namespace ModderLords.Compat;

/// <summary>Opt-in sandbox map bounds for the diagnostic headless scene projection.</summary>
internal static class HeadlessMapExperiment
{
    private static Vec2 _min, _max, _size;
    private static float _height;
    private static bool _installed;
    private static Type? _headlessType;

    internal static void Install()
    {
        if (_installed) return;
        var path = Environment.GetEnvironmentVariable("MODDERLORDS_HEADLESS_MAP");
        if (string.IsNullOrEmpty(path)) return;
        if (!ServerDetect.IsDedicatedServer) throw new InvalidOperationException("Headless map requires a dedicated server");
        var expected = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(),
            "../../Modules/DedicatedServer.Windows/SceneObj/Main_map/modderlords-map.xml"));
        if (!string.Equals(Path.GetFullPath(path), expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Map metadata must describe the host's Main_map");
        var document = new XmlDocument();
        document.Load(path);
        var root = document.DocumentElement ?? throw new InvalidDataException("Missing map metadata");
        using (var sha = SHA256.Create())
        using (var stream = File.OpenRead(Path.Combine(Path.GetDirectoryName(path), "navmesh.bin")))
        {
            var hash = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
            if (hash != root.GetAttribute("navmesh-sha256")) throw new InvalidDataException("Headless map navmesh hash mismatch");
        }
        var min = Numbers(root.GetAttribute("border_min"), 3);
        var max = Numbers(root.GetAttribute("border_max"), 3);
        var size = Numbers(root.GetAttribute("terrain-size"), 2);
        if (max[0] <= min[0] || max[1] <= min[1] || size.Any(x => x <= 0))
            throw new InvalidDataException("Invalid map dimensions");
        _min = new Vec2(min[0], min[1]); _max = new Vec2(max[0], max[1]);
        _height = max[2]; _size = new Vec2(size[0], size[1]);
        var type = AccessTools.TypeByName("SandBox.MapScene") ?? throw new MissingMemberException("SandBox.MapScene");
        // The host implementation lives in DedicatedServer.Core, not in SandBox.
        _headlessType = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => a.GetName().Name == "DedicatedServer.Core")
            .Select(a => a.GetType("A.G")).FirstOrDefault(t => t != null);
        if (_headlessType == null || !type.IsAssignableFrom(_headlessType))
            throw new MissingMemberException("Unsupported DedicatedServer.Core headless MapScene implementation");
        var borders = AccessTools.Method(type, "GetMapBorders", new[] { typeof(Vec2).MakeByRefType(), typeof(Vec2).MakeByRefType(), typeof(float).MakeByRefType() });
        var terrain = AccessTools.Method(type, "GetTerrainSize", Type.EmptyTypes);
        if (borders?.ReturnType != typeof(void) || terrain?.ReturnType != typeof(Vec2))
            throw new MissingMethodException("Unsupported headless map bounds signatures");
        var harmony = new Harmony("ModderLords.HeadlessMapExperiment");
        harmony.Patch(borders, prefix: new HarmonyMethod(typeof(HeadlessMapExperiment), nameof(Borders)) { priority = Priority.First });
        harmony.Patch(terrain, prefix: new HarmonyMethod(typeof(HeadlessMapExperiment), nameof(Size)) { priority = Priority.First });
        _installed = true;
        Log.Info($"headless-map: validated navmesh; borders={_min}..{_max}; height={_height}; terrain={_size}");
    }

    private static float[] Numbers(string text, int count)
    {
        var values = text.Split(',').Select(x => float.Parse(x, CultureInfo.InvariantCulture)).ToArray();
        if (values.Length != count || values.Any(x => float.IsNaN(x) || float.IsInfinity(x)))
            throw new InvalidDataException("Invalid map coordinate");
        return values;
    }
    private static bool Borders(object __instance, ref Vec2 minimumPosition, ref Vec2 maximumPosition, ref float maximumHeight)
    {
        if (__instance.GetType() != _headlessType) return true;
        minimumPosition = _min; maximumPosition = _max; maximumHeight = _height; return false;
    }
    private static bool Size(object __instance, ref Vec2 __result)
    { if (__instance.GetType() != _headlessType) return true; __result = _size; return false; }
}
