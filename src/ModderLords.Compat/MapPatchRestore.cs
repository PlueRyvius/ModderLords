using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Map;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace ModderLords.Compat;

/// <summary>
/// Restores <c>GetMapPatchAtPosition</c> on the headless server, which is the measured cause of the field-battle
/// client crash (docs/FIELD-BATTLE-TERRAIN.md).
///
/// <para>Decompiling `SandBox.MapScene` settles what the probe could only describe. The whole query is:</para>
/// <code>
/// if (_battleTerrainIndexMap != null) { ...index into it... }
/// return default(MapPatchData);          // sceneIndex 0, coordinates 0,0
/// </code>
/// <para>The server was not computing a wrong answer. It has a null <c>byte[]</c> and returns the default struct,
/// which is precisely the `sceneIndex` 0 and `0,0` coordinates measured at all 2304 sample positions.</para>
///
/// <para>That array is filled in `MapScene.AfterLoad` by <see cref="MBMapScene.GetBattleSceneIndexMap"/> — a public
/// managed API over the native scene, present in the engine the dedicated server already runs. So this asks the
/// engine for the same bytes and answers the query with the same arithmetic the client uses. No format is parsed
/// and nothing is prepared per map: any mod that replaces the campaign map is covered, because the data comes from
/// whatever scene the server actually loaded.</para>
///
/// <para>It does <b>not</b> need <c>MODDERLORDS_HEADLESS_MAP_KEEP_TERRAIN</c>: measured, the engine returns the
/// index map from a fully stripped scene. The two were first run together on an assumption, which was wrong.</para>
///
/// <para>Measured 2026-09-12 on TAOM's map: a 1024x1024 index map, 182 distinct scene indices, and field battles
/// that load. It is not a complete cure — two of TAOM's 81 selectable battle scenes ship without a terrain shader
/// cache and still crash the client — but without this every field battle anywhere loads scene 0, which is one of
/// those two.</para>
/// </summary>
internal static class MapPatchRestore
{
    /// <summary>
    /// On by default. Set to 0 (or false/off) to leave the map patch unanswered, which is only useful for
    /// reproducing the original failure: without this, the server reports scene 0 for the whole map and every
    /// field battle loads one arbitrary battle terrain.
    /// </summary>
    public const string Variable = "MODDERLORDS_MAP_PATCH_RESTORE";

    private static Type? _headless;
    private static byte[]? _indexMap;
    private static int _width, _height;
    private static Vec2 _terrainSize;
    private static bool _armed, _installed;
    private static int _served;

    /// <summary>How many served answers to print in full. A count proves the patch fired; only the values prove it
    /// served the right thing, and a battle asks for exactly one or two.</summary>
    private const int LoggedAnswers = 6;

    internal static void Install(Harmony harmony)
    {
        if (_installed || !Enabled()) return;
        if (!ServerDetect.IsDedicatedServer) throw new InvalidOperationException("The map patch restore is server-only");
        _headless = MapSceneTarget.Headless();
        foreach (var target in MapSceneTarget.Implementations(_headless, "GetMapPatchAtPosition",
                     new[] { typeof(CampaignVec2).MakeByRefType() }))
            harmony.Patch(target, postfix: new HarmonyMethod(typeof(MapPatchRestore), nameof(Serve)));
        _installed = true;
        Log.Info("map patch restore: armed; will read the battle scene index map once the map scene has loaded");
    }

    /// <summary>Default on; only an explicit 0, false or off turns it off.</summary>
    private static bool Enabled()
    {
        var raw = Environment.GetEnvironmentVariable(Variable);
        if (string.IsNullOrWhiteSpace(raw)) return true;
        raw = raw!.Trim();
        return !(raw == "0"
                 || raw.Equals("false", StringComparison.OrdinalIgnoreCase)
                 || raw.Equals("off", StringComparison.OrdinalIgnoreCase)
                 || raw.Equals("no", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Called on the tick. The index map cannot be read at install time: the native scene has to have finished
    /// loading its terrain first, which is well after modules are set up.
    /// </summary>
    internal static void Tick()
    {
        if (!_installed || _armed) return;
        try
        {
            var wrapper = Campaign.Current?.MapSceneWrapper;
            if (wrapper == null || wrapper.GetType() != _headless) return;
            var scene = AccessTools.Field(MapSceneTarget.Base, "_scene")?.GetValue(wrapper) as Scene;
            if (scene == null) return;

            // Measured 2026-09-13: on the stock map this native call never returns. The server's main thread spins
            // right after the map loads, it never reaches SERVING, and so it never opens its join port or advertises
            // on Steam. Every mod set on the vanilla map was affected. The stock map needs no restore to begin with,
            // so only a mod's own map (the case this was built and measured for) is read.
            if (!HeadlessMapExperiment.IsInstalled)
            {
                _armed = true;
                Log.Info("map patch restore: the stock map is being served; nothing to restore");
                return;
            }

            _terrainSize = wrapper.GetTerrainSize();
            if (_terrainSize.x <= 0f || _terrainSize.y <= 0f) return;

            var data = _indexMap;
            int width = 0, height = 0;
            _armed = true;
            // Announced first: a native call that hangs or faults cannot be caught, so the last line printed has to
            // name it.
            Log.Info("map patch restore: asking the engine for the battle scene index map");
            MBMapScene.GetBattleSceneIndexMap(scene, ref data, ref width, ref height);
            if (data == null || width <= 0 || height <= 0 || data.Length < width * height * 2)
            {
                Log.Warn($"map patch restore: the engine returned no battle scene index map ({width}x{height}). " +
                         "Map patches stay at 0, so every field battle will load the same arbitrary battle terrain " +
                         "and clients will crash entering one. This is the failure this exists to prevent.");
                return;
            }
            _indexMap = data;
            _width = width;
            _height = height;
            // Say what was read, not just that something was. An index map of the right size that is all zeroes
            // would otherwise look like a working fix.
            var distinct = _indexMap.Where((_, i) => i % 2 == 0).Distinct().Count();
            Log.Info($"map patch restore: read a {width}x{height} battle scene index map " +
                     $"({_indexMap.Length} bytes, {distinct} distinct scene indices) over terrain {_terrainSize}");
        }
        catch (Exception ex)
        {
            _armed = true;
            Log.Warn("map patch restore: could not read the battle scene index map: " + ex.GetBaseException().Message);
        }
    }

    /// <summary>
    /// Whether this component answers <paramref name="member"/> itself, so the stub watch does not warn about a
    /// query that has a fix in place.
    /// </summary>
    internal static bool Restores(string member) =>
        _indexMap != null && member == "GetMapPatchAtPosition";

    internal static string? Summary() =>
        _installed && _indexMap != null ? $"map patch restore: answered {_served} query(ies)" : null;

    /// <summary>
    /// The client's arithmetic, from SandBox.MapScene.GetMapPatchAtPosition, unchanged: cell by normalized
    /// position, two bytes per cell, the second holding both normalized coordinates as 4-bit halves over 15.
    /// </summary>
    private static void Serve(object __instance, ref MapPatchData __result, ref CampaignVec2 __0)
    {
        if (_indexMap == null || __instance.GetType() != _headless) return;
        var x = MBMath.ClampIndex((int)MathF.Floor(__0.X / _terrainSize.x * _width), 0, _width);
        var y = MBMath.ClampIndex((int)MathF.Floor(__0.Y / _terrainSize.y * _height), 0, _height);
        var index = (y * _width + x) * 2;
        if (index < 0 || index + 1 >= _indexMap.Length) return;
        var packed = _indexMap[index + 1];
        __result = new MapPatchData
        {
            sceneIndex = _indexMap[index],
            normalizedCoordinates = new Vec2((packed & 0xF) / 15f, ((packed >> 4) & 0xF) / 15f),
        };
        if (_served < LoggedAnswers && !Diagnostics.TerrainProbe.Sweeping)
            Log.Info($"map patch restore: ({__0.X:0.##}, {__0.Y:0.##}) -> cell {x},{y} of {_width}x{_height} " +
                     $"= sceneIndex {__result.sceneIndex}, coords {__result.normalizedCoordinates}");
        _served++;
    }
}
