using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace ModderLords.Compat;

/// <summary>
/// Counts which terrain-related <c>IMapScene</c> members the server actually calls, so a spike stops being a guess
/// about which answer the battle consumes.
///
/// <para>Why this exists: the first stub spike patched <c>GetEnvironmentTerrainTypesCount</c> — the member the probe
/// had measured as returning an empty list — and it was called <b>zero</b> times through an entire field battle,
/// while <c>GetHeightAtPoint</c> fired five milliseconds before
/// <c>[BattleSync] Using "FieldBattleMissionInitializer"</c>. Measuring one member at a time and inferring from
/// silence is how this investigation has burned runs. Count them all at once instead.</para>
///
/// <para>Deliberately does not cover the navmesh and pathfinding members (<c>GetPathBetweenAIFaces</c>,
/// <c>GetFaceIndex</c> and friends). Those run constantly for thousands of parties, they were measured to be correct
/// anyway, and counting them would tax the server for nothing.</para>
///
/// <para>Off unless MODDERLORDS_MAPSCENE_CENSUS is set. Unlike the other spikes it changes no answers at all, so it is
/// safe to leave on alongside one.</para>
/// </summary>
internal static class MapSceneCallCensus
{
    public const string Variable = "MODDERLORDS_MAPSCENE_CENSUS";

    /// <summary>
    /// Everything a battle could plausibly ask about terrain, minus anything hot. GetFaceTerrainType was in this
    /// list once and ran to 47 million calls in a single session, dragging the server from 64 fps to the low 40s:
    /// counting it measured the census more than the game.
    /// </summary>
    private static readonly HashSet<string> Watched = new HashSet<string>(StringComparer.Ordinal)
    {
        "GetEnvironmentTerrainTypes",
        "GetEnvironmentTerrainTypesCount",
        "GetMapPatchAtPosition",
        "GetHeightAtPoint",
        "GetTerrainHeightAndNormal",
        "GetGroundNormal",
        "GetTerrainTypeAtPosition",
        "GetSnowAmountAtPosition",
        "GetRainAmountAtPosition",
        "GetWinterTimeFactor",
        "SetAtmosphereColorgrade",
        "GetAtmosphereStates",
        "GetTerrainSize",
        "GetMapBorders",
        "GetTerrainTypeName",
        "GetSceneLevel",
        "SetSceneLevels",
        "GetSiegeCampFrames",
    };

    private static readonly ConcurrentDictionary<MethodBase, string> Names = new ConcurrentDictionary<MethodBase, string>();
    private static readonly ConcurrentDictionary<string, int> Counts = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
    /// <summary>Members the census could not hook. Named in every report, so their silence is never read as data.</summary>
    private static readonly List<string> Unpatchable = new List<string>();

    private static bool _installed;
    private static string _last = "";

    internal static void Install(Harmony harmony)
    {
        if (_installed || string.IsNullOrEmpty(Environment.GetEnvironmentVariable(Variable))) return;
        if (!ServerDetect.IsDedicatedServer) throw new InvalidOperationException("The map scene census is server-only");
        var scene = MapSceneTarget.Headless();
        var postfix = new HarmonyMethod(typeof(MapSceneCallCensus), nameof(Count));
        var patched = 0;
        foreach (var pair in MapSceneTarget.AllImplementations(scene))
        {
            if (!Watched.Contains(pair.Key)) continue;
            // Two interface members can share one implementation under obfuscation; record the first name and patch once.
            if (!Names.TryAdd(pair.Value, pair.Key)) continue;
            // Where A.G does not re-implement a member, the interface map hands back a slot Harmony refuses with
            // "you can only patch implemented methods"; the declared virtual on SandBox.MapScene is the patchable
            // one. Falling back keeps a census complete — an unpatched member would otherwise report "never called",
            // which is a wrong answer wearing the clothes of a measurement.
            var target = pair.Value;
            try { harmony.Patch(target, postfix: postfix); }
            catch (Exception)
            {
                target = pair.Value.GetBaseDefinition();
                if (target == pair.Value) { Names.TryRemove(pair.Value, out _); Unpatchable.Add(pair.Key); continue; }
                Names.TryAdd(target, pair.Key);
                try { harmony.Patch(target, postfix: postfix); }
                catch (Exception ex) { Unpatchable.Add(pair.Key + " (" + ex.GetBaseException().Message + ")"); continue; }
            }
            patched++;
        }
        if (patched == 0) throw new MissingMethodException("the census matched no IMapScene members");
        _installed = true;
        Log.Info($"map scene census: counting calls to {patched} terrain member(s) on {scene.FullName}");
    }

    /// <summary>Null when off, and null again when nothing has changed: a census is only worth a line when it moves.</summary>
    internal static string? Summary()
    {
        if (!_installed) return null;
        var called = Counts.Where(c => c.Value > 0).OrderByDescending(c => c.Value)
            .Select(c => c.Key + "=" + c.Value).ToList();
        var silent = Names.Values.Distinct().Where(n => !Counts.ContainsKey(n)).OrderBy(n => n, StringComparer.Ordinal).ToList();
        var line = "map scene census: " + (called.Count == 0 ? "nothing called yet" : string.Join(" ", called))
                   + (silent.Count == 0 ? "" : "; never called: " + string.Join(", ", silent))
                   + (Unpatchable.Count == 0 ? "" : "; NOT COUNTED: " + string.Join(", ", Unpatchable));
        if (line == _last) return null;
        _last = line;
        return line;
    }

    private static void Count(MethodBase __originalMethod)
    {
        if (!Names.TryGetValue(__originalMethod, out var name)) return;
        Counts.AddOrUpdate(name, 1, (_, n) => n + 1);
    }
}
