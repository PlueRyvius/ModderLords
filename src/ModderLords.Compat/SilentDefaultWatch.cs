using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Core;
using TaleWorlds.Library;

namespace ModderLords.Compat;

/// <summary>
/// Says so, once, the first time the server answers a map-scene query with a stub.
///
/// <para>The dedicated server's map scene returns defaults, empties and zeroes for a known set of members, with no
/// warning and no log line (the inventory is in docs/HEADLESS-MAP-ANSWERS.md). A mod that depends on one does not
/// fail; it gets a plausible wrong answer. That is what the field-battle client crash was: one null
/// <c>byte[]</c> behind <c>GetMapPatchAtPosition</c>, and finding it took a night of measurement because nothing
/// anywhere said a word.</para>
///
/// <para>This turns that whole class of bug into a log line. It changes no answers — it only reports that a
/// stubbed one was read, what the server actually returned, and what it means. One line per member per run: after
/// the first the postfix is a bool read.</para>
///
/// <para>Members another component has restored are skipped, so a fixed query does not keep warning about a
/// problem that no longer exists.</para>
/// </summary>
internal static class SilentDefaultWatch
{
    /// <summary>Set to 0 to silence it. On by default: an unreported wrong answer is the expensive kind.</summary>
    public const string Variable = "MODDERLORDS_STUB_WARNINGS";

    /// <summary>
    /// The load-bearing stubs, with what a caller is actually told. Deliberately excludes the debug and render
    /// sinks, which carry no data a mod can read back, and <c>GetFaceVertexZ</c>, which may sit on the pathfinding
    /// path — the census learned the hard way what patching a hot member costs.
    /// </summary>
    private static readonly (string Member, Type[] Signature, string Consequence)[] Watched =
    {
        ("GetEnvironmentTerrainTypes", new[] { typeof(CampaignVec2).MakeByRefType() },
            "returns an empty terrain-type list; the position's actual terrain composition is unavailable"),
        ("GetEnvironmentTerrainTypesCount", new[] { typeof(CampaignVec2).MakeByRefType(), typeof(TerrainType).MakeByRefType() },
            "returns an empty list (its out-parameter is correct, which makes the empty list beside it easy to miss)"),
        ("GetHeightAtPoint", new[] { typeof(CampaignVec2).MakeByRefType(), typeof(float).MakeByRefType() },
            "reports SUCCESS with a height of 0, so a caller that checks the return value is told the answer is good"),
        ("GetAtmosphereStates", Type.EmptyTypes,
            "returns no atmosphere states; the client's map scene fills these from MBMapScene.LoadAtmosphereData, which the headless one never calls"),
        ("GetSnowAmountAtPosition", new[] { typeof(Vec2) },
            "always returns 0; measured to differ from a client on 30% of sampled positions"),
        ("GetRainAmountAtPosition", new[] { typeof(Vec2) },
            "always returns 0; measured to differ from a client on 22% of sampled positions"),
        ("GetWinterTimeFactor", Type.EmptyTypes,
            "always returns 0, so seasonal logic runs as though it is never winter"),
        ("AddNewEntityToMapScene", new[] { typeof(string), typeof(CampaignVec2).MakeByRefType() },
            "does nothing; a mod adding an entity to the campaign map silently adds none"),
    };

    private static readonly Dictionary<MethodBase, string> Consequences = new Dictionary<MethodBase, string>();
    private static readonly HashSet<MethodBase> Reported = new HashSet<MethodBase>();
    private static readonly Dictionary<MethodBase, string> Names = new Dictionary<MethodBase, string>();

    private static bool _installed;

    internal static void Install(Harmony harmony)
    {
        if (_installed || Environment.GetEnvironmentVariable(Variable) == "0") return;
        if (!ServerDetect.IsDedicatedServer) return;
        var scene = MapSceneTarget.Headless();
        var postfix = new HarmonyMethod(typeof(SilentDefaultWatch), nameof(Report));
        var watched = 0;
        foreach (var (member, signature, consequence) in Watched)
        {
            // A restored query is not a stub any more; warning about it would be noise with a fix already in place.
            if (MapPatchRestore.Restores(member)) continue;
            try
            {
                foreach (var target in MapSceneTarget.Implementations(scene, member, signature))
                {
                    // Only the host's own stubs. Where a member falls through to SandBox's real implementation
                    // there is nothing being faked and nothing to report.
                    if (target.DeclaringType != scene) continue;
                    if (!Names.ContainsKey(target))
                    {
                        Names[target] = member;
                        Consequences[target] = consequence;
                        harmony.Patch(target, postfix: postfix);
                        watched++;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Info($"stub warnings: cannot watch {member} ({ex.GetBaseException().Message})");
            }
        }
        _installed = true;
        Log.Info($"stub warnings: watching {watched} headless map-scene stub(s); each reports once if a mod reads it");
    }

    private static void Report(MethodBase __originalMethod)
    {
        if (!Names.TryGetValue(__originalMethod, out var name)) return;
        lock (Reported)
        {
            if (!Reported.Add(__originalMethod)) return;
        }
        Log.Warn($"a mod just read IMapScene.{name}, which this server does not implement: {Consequences[__originalMethod]}. " +
                 "See docs/HEADLESS-MAP-ANSWERS.md. Reported once per run.");
    }
}
