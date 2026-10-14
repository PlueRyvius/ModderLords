using System;
using HarmonyLib;
using TaleWorlds.CampaignSystem.GameState;
using TaleWorlds.CampaignSystem.Settlements;

namespace ModderLords.CompatSync.Coop;

/// <summary>
/// Server and client: keeps a stored town, castle or village menu from crashing the game when there is no settlement
/// to show it for.
///
/// A save records the menu the main party had open (<c>MapStateData.GameMenuId</c>), and <c>MapState.OnLoadingFinished</c>
/// reopens it before anything else runs. Those menus' initialisation reads <c>Settlement.CurrentSettlement</c> without a
/// null check. Under Coop the stored menu is the server's main party's, which is a player's party, so it does not
/// describe a joining player's game:
/// <list type="bullet">
/// <item>A player joining while that player is in a town received a world whose stored menu was <c>town_outside</c>
/// with their own party on the map, and crashed with a NullReferenceException in
/// <c>EncounterGameMenuBehavior.encounter_menu_ui_castle_on_init</c> as the load finished.</item>
/// <item>After Coop parked that party and saved, the server itself crashed the same way on every boot.</item>
/// </list>
/// Reported 2026-10-13 (support bundles 20261014-024216-c492df53 and 20261014-030433-25b1921c) and reproduced from
/// the host's save; the crash dump showed the stack above.
///
/// The prefix drops such a menu (clears the stored id) when there is no current settlement, so the game opens on the
/// campaign map instead. When there is a settlement, or for any other menu, it does nothing.
/// </summary>
public static class LoadMenuGuard
{
    private static readonly Harmony Harmony = new Harmony("ModderLords.Compat.LoadMenuGuard");
    private static bool _installed;
    private static int _dropped;

    /// <summary>Installs the prefix once. Returns a line for the log.</summary>
    public static string Install()
    {
        if (_installed) return "load menu guard: already installed";
        var enter = AccessTools.Method(typeof(MapState), nameof(MapState.EnterMenuMode));
        if (enter is null) return "load menu guard: MapState.EnterMenuMode not found; a stored town menu without a settlement can still crash a load";
        Harmony.Patch(enter, prefix: new HarmonyMethod(typeof(LoadMenuGuard), nameof(EnterMenuModePrefix)) { priority = Priority.First });
        _installed = true;
        return "load menu guard: installed; a stored town, castle or village menu with no settlement opens on the map instead of crashing";
    }

    public static bool EnterMenuModePrefix(MapState __instance)
    {
        try
        {
            var id = __instance.GameMenuId;
            if (!LoadMenuGuardPolicy.NeedsSettlement(id)) return true;
            if (!LoadMenuGuardPolicy.ShouldDrop(id, HasCurrentSettlement())) return true;
            __instance.GameMenuId = null;
            if (_dropped++ < 20)
                Log.Warn($"load menu guard: the stored menu '{id}' needs a settlement and this party is in none; opening on the campaign map instead "
                       + "(under Coop the stored menu belongs to the server's main party, a player who was in a town)");
            return false;
        }
        catch (Exception ex)
        {
            // Never make things worse than the game would: let it run as it would have.
            Log.Warn("load menu guard failed, leaving the menu to the game: " + ex.GetBaseException().Message);
            return true;
        }
    }

    /// <summary>
    /// <c>Settlement.CurrentSettlement</c>, where a getter that throws (no main party yet) counts as no settlement: the
    /// menu's own initialisation would throw on the same read.
    /// </summary>
    private static bool HasCurrentSettlement()
    {
        try { return Settlement.CurrentSettlement != null; }
        catch (NullReferenceException) { return false; }
    }
}
