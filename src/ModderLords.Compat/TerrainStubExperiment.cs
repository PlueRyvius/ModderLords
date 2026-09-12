using System;
using System.Collections.Generic;
using System.Globalization;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Core;

namespace ModderLords.Compat;

/// <summary>
/// SPIKE, not a fix. Answers the stripped scene's empty terrain-type list with a fabricated one, to settle whether an
/// empty list is what makes a coop client die in create_texture_array.
///
/// <para>It costs nothing to fabricate because of what the measurement found: the server already returns the
/// <em>correct</em> terrain type at every position (see docs/FIELD-BATTLE-TERRAIN.md — terrain type, face index and the
/// out-parameter of GetEnvironmentTerrainTypesCount all match a single-player client on all 2304 samples). Only the
/// list comes back empty. So a neighbourhood of "the type that is already right" needs no terrain data at all.</para>
///
/// <para>Two changes, both deliberately crude:</para>
/// <list type="number">
/// <item>An empty environment terrain-type list becomes N copies of the current position's type.</item>
/// <item>GetHeightAtPoint stops returning true with a height of 0. A query that admits it cannot answer can be handled
/// by a caller; one that lies cannot.</item>
/// </list>
///
/// <para>Off unless MODDERLORDS_TERRAIN_STUB is set. This must never be on in a release: it makes the server assert
/// things about the world that are not true.</para>
/// </summary>
internal static class TerrainStubExperiment
{
    /// <summary>Set to "1" for the default neighbourhood size, or to the size itself.</summary>
    public const string Variable = "MODDERLORDS_TERRAIN_STUB";

    /// <summary>What a full client scene returned at every one of the 2304 measured samples.</summary>
    private const int DefaultNeighbourhood = 49;

    private static Type? _headless;
    private static int _size;
    private static bool _installed;
    private static int _listsFilled;
    private static int _heightLies;

    internal static void Install(Harmony harmony)
    {
        if (_installed) return;
        var raw = Environment.GetEnvironmentVariable(Variable);
        if (string.IsNullOrEmpty(raw)) return;
        if (!ServerDetect.IsDedicatedServer) throw new InvalidOperationException("The terrain stub is server-only");
        _size = raw == "1" ? DefaultNeighbourhood : Parse(raw!);
        _headless = MapSceneTarget.Headless();

        foreach (var target in MapSceneTarget.Both("GetEnvironmentTerrainTypesCount",
                     new[] { typeof(CampaignVec2).MakeByRefType(), typeof(TerrainType).MakeByRefType() }))
            harmony.Patch(target, postfix: new HarmonyMethod(typeof(TerrainStubExperiment), nameof(FillTypes)));
        foreach (var target in MapSceneTarget.Both("GetHeightAtPoint",
                     new[] { typeof(CampaignVec2).MakeByRefType(), typeof(float).MakeByRefType() }))
            harmony.Patch(target, postfix: new HarmonyMethod(typeof(TerrainStubExperiment), nameof(OwnUpToNoHeight)));

        _installed = true;
        Log.Warn($"SPIKE {Variable} is on: empty terrain-type lists become {_size} copies of the current type, and " +
                 "GetHeightAtPoint will report failure instead of a height of 0. This server is now telling " +
                 "deliberate lies about the world; do not draw gameplay conclusions from this run.");
    }

    /// <summary>Null when the spike is off, so a normal run logs nothing at all about it.</summary>
    internal static string? Summary() => _installed
        ? $"terrain stub: filled {_listsFilled} empty type list(s), corrected {_heightLies} false height success(es)"
        : null;

    private static int Parse(string raw)
    {
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var size) || size < 1 || size > 512)
            throw new ArgumentException(Variable + " must be 1 (for the default) or a neighbourhood size up to 512");
        return size;
    }

    /// <summary>
    /// The out terrain type is taken by name — both declarations agree on it, and the measurement showed this
    /// particular value is already correct on the server, which is the whole reason a fabricated list is defensible.
    /// </summary>
    private static void FillTypes(object __instance, ref List<TerrainType> __result, ref TerrainType currentPositionTerrainType)
    {
        if (__instance.GetType() != _headless) return;
        if (__result != null && __result.Count > 0) return;      // idempotent: both declarations may fire
        var current = currentPositionTerrainType;
        var filled = new List<TerrainType>(_size);
        for (var i = 0; i < _size; i++) filled.Add(current);
        __result = filled;
        if (_listsFilled++ == 0) Log.Info($"terrain stub: first empty type list filled ({_size} x {current})");
    }

    private static void OwnUpToNoHeight(object __instance, ref bool __result, ref float height)
    {
        if (__instance.GetType() != _headless) return;
        if (!__result || height != 0f) return;
        __result = false;
        if (_heightLies++ == 0) Log.Info("terrain stub: first height-of-zero success reported as a failure instead");
    }
}
