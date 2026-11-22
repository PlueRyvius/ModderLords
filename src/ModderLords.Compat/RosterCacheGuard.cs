using System;
using System.Reflection;
using HarmonyLib;
using TaleWorlds.CampaignSystem.Roster;
using TaleWorlds.Library;

namespace ModderLords.Compat;

/// <summary>
/// Keeps a troop roster's cached element list in step with the roster.
///
/// <para><c>TroopRoster.GetTroopRoster()</c> hands out a cached list and rebuilds it only when the roster's
/// <c>VersionNo</c> has moved since. Every game call that adds or removes troops moves it. Measured 2026-11-21 on the
/// v1.5.7 server with Coop 0.1.8 and TAOM 2.0.41's code loaded: a joined player's party roster held its hero
/// (<c>_count</c> 1, <c>_totalHeroes</c> 1) at <c>VersionNo</c> 0, with an empty list cached at version 0, so the
/// roster had been filled without a version bump after something had read it. The game's battle code reads that
/// list (<c>MapEventParty.Update</c>), built the player's side empty, and Coop ended every battle the player started
/// ("Local player origin missing from battle reserves").</para>
///
/// <para>So before the list is handed out, a cache that claims to be current but holds a different number of elements
/// from the roster is marked stale, and the game rebuilds it as it would after any change.</para>
/// </summary>
internal static class RosterCacheGuard
{
    // Compiled field accessors: GetTroopRoster runs for every party, many times a tick.
    private static AccessTools.FieldRef<TroopRoster, MBList<TroopRosterElement>>? _list;
    private static AccessTools.FieldRef<TroopRoster, int>? _listVersion;
    private static AccessTools.FieldRef<TroopRoster, int>? _count;
    private static MethodInfo? _setVersion;
    private static int _repaired;

    internal static string Install(Harmony harmony)
    {
        var type = typeof(TroopRoster);
        var target = AccessTools.Method(type, "GetTroopRoster", Type.EmptyTypes);
        _setVersion = AccessTools.PropertySetter(type, "VersionNo");
        if (target == null || _setVersion == null || AccessTools.Field(type, "_troopRosterElements")?.FieldType != typeof(MBList<TroopRosterElement>)
            || AccessTools.Field(type, "_troopRosterElementsVersion") == null || AccessTools.Field(type, "_count") == null)
            return "not installed: TroopRoster's cache fields are not in this game";
        _list = AccessTools.FieldRefAccess<TroopRoster, MBList<TroopRosterElement>>("_troopRosterElements");
        _listVersion = AccessTools.FieldRefAccess<TroopRoster, int>("_troopRosterElementsVersion");
        _count = AccessTools.FieldRefAccess<TroopRoster, int>("_count");
        harmony.Patch(target, prefix: new HarmonyMethod(typeof(RosterCacheGuard), nameof(Prefix)));
        return "installed";
    }

    private static void Prefix(TroopRoster __instance)
    {
        try
        {
            var cached = _list!(__instance);
            if (cached == null || _listVersion!(__instance) != __instance.VersionNo) return;   // the game rebuilds it anyway
            var count = _count!(__instance);
            if (cached.Count == count) return;
            _setVersion!.Invoke(__instance, new object[] { __instance.VersionNo + 1 });
            if (_repaired++ < 20)
                Log.Info($"roster cache: {AccessTools.Property(typeof(TroopRoster), "OwnerParty")?.GetValue(__instance)} listed {cached.Count} element(s) for a roster of {count}; rebuilt");
        }
        catch (Exception ex)
        {
            if (_repaired++ < 20) Log.Info("roster cache: check failed: " + ex.GetBaseException().Message);
        }
    }
}
