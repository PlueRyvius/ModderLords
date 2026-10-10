using System;
using HarmonyLib;
using TaleWorlds.CampaignSystem.Map.DistanceCache;
using TaleWorlds.CampaignSystem.Settlements;

namespace ModderLords.Compat;

/// <summary>
/// Says which settlement pair the distance cache had no answer for, before the game works the answer out on the
/// navmesh. Measurement only — it changes no answers.
///
/// <para>The cache a map ships holds every pair, so a miss should not happen. When one does, the game calls
/// <c>Scene.GetPathBetweenAIFaces</c>, and on the headless server that call has ended the process with an access
/// violation (measured 2026-10-10, Empires of Europe 1100 world creation, from
/// <c>CharacterRelationCampaignBehavior.UpdateFriendshipAndEnemies</c>). A native fault cannot be caught, so the
/// pair is printed first: the last line before the crash names it.</para>
/// </summary>
internal static class DistanceCacheMissProbe
{
    private const int Logged = 12;
    private static int _misses;

    internal static void Install(Harmony harmony)
    {
        if (!ServerDetect.IsDedicatedServer) return;
        var target = AccessTools.Method(typeof(SandBoxNavigationCache), "GetRealDistanceAndLandRatioBetweenSettlements");
        if (target == null) { Log.Info("distance cache: miss probe not installed (method not found)"); return; }
        harmony.Patch(target, prefix: new HarmonyMethod(typeof(DistanceCacheMissProbe), nameof(Miss)));
    }

    private static void Miss(NavigationCacheElement<Settlement> settlement1, NavigationCacheElement<Settlement> settlement2)
    {
        var n = ++_misses;
        if (n > Logged) return;
        try
        {
            Log.Warn($"distance cache: no stored distance for {Describe(settlement1)} -> {Describe(settlement2)} (miss {n}); the game will path it on the navmesh"
                     + (n == Logged ? "; further misses are not listed" : ""));
        }
        catch (Exception ex) { Log.Warn("distance cache: miss probe failed: " + ex.GetBaseException().Message); }
    }

    private static string Describe(NavigationCacheElement<Settlement> e)
    {
        var s = e.Settlement;
        if (s == null) return "(no settlement)";
        var position = e.IsPortUsed ? s.PortPosition : s.GatePosition;
        var face = position.Face;
        var registered = Settlement.Find(s.StringId);
        return $"{s.StringId}{(e.IsPortUsed ? " port" : "")} at ({position.X:0.#}, {position.Y:0.#}) face {face.FaceIndex}"
               + (face.IsValid() ? "" : " INVALID")
               + (ReferenceEquals(registered, s) ? "" : registered == null ? " [not a registered settlement]" : " [a different object from the registered settlement of this id]");
    }
}
