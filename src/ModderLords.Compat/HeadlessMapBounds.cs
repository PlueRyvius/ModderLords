using System;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Map;
using TaleWorlds.Engine;
using TaleWorlds.Library;

namespace ModderLords.Compat;

/// <summary>
/// Reads a map's bounds from the scene itself, the way a client does, and reports whether they agree with the
/// values the launcher injected. Measurement only — it changes no answers.
///
/// <para>Why this exists. The headless map scene hardcodes the vanilla campaign map:
/// <c>border_min (62, 30)</c>, <c>border_max (790, 640)</c>, max height <c>620</c>, terrain <c>848 x 848</c>. Every
/// one of those is wrong for a mod that replaces the map, which is what <see cref="HeadlessMapExperiment"/> exists
/// to correct — and it corrects them from a sidecar (<c>modderlords-map.xml</c>) that the launcher generates, hashes
/// the navmesh into, validates, and exits the process over when it disagrees.</para>
///
/// <para>A client needs none of that. <c>SandBox.MapScene.Load</c> reads the numbers out of the scene:</para>
/// <code>
/// _minimumPositionCache = _scene.GetFirstEntityWithName("border_min").GetGlobalFrame().origin.AsVec2;
/// _maximumPositionCache = _scene.GetFirstEntityWithName("border_max").GetGlobalFrame().origin.AsVec2;
/// _maximumHeightCache   = _scene.GetFirstEntityWithName("border_max").GetGlobalFrame().origin.z;
/// _scene.GetTerrainData(out var nodeDimension, out var nodeSize, out _, out _);
/// </code>
///
/// <para>Those two entities are empty transform markers, a hundred and fifty bytes each. The projection used to
/// delete them along with everything else renderable, which is the only reason the sidecar had to exist. It now
/// keeps them, so the server can answer the same question the same way — for any map-replacing mod, with nothing
/// prepared in advance.</para>
///
/// <para>This logs the comparison rather than acting on it. If the derived bounds match the injected ones on a real
/// map, the sidecar and everything guarding it can go.</para>
///
/// <para><b>OFF by default, and it killed a server to earn that.</b> Enabled on its first outing, it took the
/// engine down with an access violation in native code moments after the map scene loaded — every call it makes
/// crosses into native, and a native fault cannot be caught by the try/catch around it. It now announces each call
/// before making it, so a repeat names the exact one, and it refuses to ask for terrain data on a scene whose
/// terrain descriptor was stripped. A diagnostic that can take down what it is measuring does not belong on a
/// default launch.</para>
/// </summary>
internal static class HeadlessMapBounds
{
    /// <summary>Set to 1 to run the comparison. Off by default: every call it makes crosses into native code.</summary>
    public const string Variable = "MODDERLORDS_HEADLESS_MAP_BOUNDS_CHECK";

    /// <summary>The launcher's switch, by name: this module cannot reference ModderLords.Core, where it is declared.</summary>
    private const string KeepTerrainVariable = "MODDERLORDS_HEADLESS_MAP_KEEP_TERRAIN";

    private static bool _done;

    internal static void Tick()
    {
        if (_done || !ServerDetect.IsDedicatedServer) return;
        if (Environment.GetEnvironmentVariable(Variable) != "1") { _done = true; return; }
        try
        {
            if (Campaign.Current?.MapSceneWrapper is not IMapScene wrapper) return;
            var scene = AccessTools.Field(MapSceneTarget.Base, "_scene")?.GetValue(wrapper) as Scene;
            if (scene == null) return;
            _done = true;

            var min = scene.GetFirstEntityWithName("border_min");
            var max = scene.GetFirstEntityWithName("border_max");
            if (min == null || max == null)
            {
                Log.Info("map bounds: the scene carries no border_min/border_max markers, so its bounds cannot be " +
                         "derived; the launcher's injected values stand. Re-prepare the map to keep the markers.");
                return;
            }

            // Each native call is announced before it is made. A native access violation cannot be caught, so the
            // last line printed is the only way to know which one faulted.
            Log.Info("map bounds: reading the border markers' global frames");
            var derivedMin = min.GetGlobalFrame().origin.AsVec2;
            var maxOrigin = max.GetGlobalFrame().origin;
            var derivedMax = maxOrigin.AsVec2;
            var derivedHeight = maxOrigin.z;

            wrapper.GetMapBorders(out var liveMin, out var liveMax, out var liveHeight);
            Log.Info($"map bounds derived from the scene : {derivedMin}..{derivedMax} height={derivedHeight:0.###}");
            Log.Info($"map bounds currently in effect    : {liveMin}..{liveMax} height={liveHeight:0.###}");
            var agrees = Near(derivedMin, liveMin) && Near(derivedMax, liveMax)
                         && Math.Abs(derivedHeight - liveHeight) < 0.01f;
            Log.Info(agrees
                ? "map bounds: the scene agrees with the injected borders"
                : "map bounds: WARNING the scene and the injected borders disagree");

            // Terrain size needs the scene's <terrain> descriptor, and asking a scene that has none is a plausible
            // reading of the access violation this used to cause. Only ask when the descriptor was kept.
            if (Environment.GetEnvironmentVariable(KeepTerrainVariable) != "1")
            {
                Log.Info("map bounds: terrain size not derived; the descriptor is stripped " +
                         "(set MODDERLORDS_HEADLESS_MAP_KEEP_TERRAIN=1 to include it)");
                return;
            }
            Log.Info("map bounds: asking the scene for its terrain data");
            scene.GetTerrainData(out var nodeDimension, out var nodeSize, out _, out _);
            var derivedTerrain = new Vec2(nodeDimension.X * nodeSize, nodeDimension.Y * nodeSize);
            var liveTerrain = wrapper.GetTerrainSize();
            Log.Info($"map bounds: terrain derived={derivedTerrain} in effect={liveTerrain} " +
                     (Near(derivedTerrain, liveTerrain) ? "(agree)" : "(DISAGREE)"));
        }
        catch (Exception ex)
        {
            _done = true;
            Log.Warn("map bounds: could not derive bounds from the scene: " + ex.GetBaseException().Message);
        }
    }

    private static bool Near(Vec2 a, Vec2 b) => Math.Abs(a.x - b.x) < 0.01f && Math.Abs(a.y - b.y) < 0.01f;
}
