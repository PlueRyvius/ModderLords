using System;
using System.Diagnostics;
using HarmonyLib;
using ModderLords.CompatSync;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.CampaignBehaviors;
using TaleWorlds.CampaignSystem.Encounters;
using TaleWorlds.CampaignSystem.GameMenus;
using TaleWorlds.CampaignSystem.MapEvents;
using TaleWorlds.Localization;

namespace ModderLords.CompatSync.Coop;

/// <summary>
/// Client only: keeps the encounter menu's Surrender disabled until the server's MapEvent has reached this client.
/// <para>
/// Coop replaces vanilla surrender on a client with a request to the server, built from
/// PlayerEncounter.Current._mapEvent. The encounter menu opens before that MapEvent arrives, and a click in the gap
/// sends nothing: Coop's handler cannot resolve an id for a null MapEvent and drops the request, having already set
/// _playerSurrender, so every later click returns early too. Surrender then silently does nothing for the rest of the
/// encounter. TAOM's slower encounter setup makes the gap wide enough to hit by hand.
/// </para>
/// <para>
/// Attack is deliberately left alone: its request is built on the server, which creates the MapEvent itself, so it
/// works in the same window. Gating it would slow a working option for nothing.
/// </para>
/// </summary>
public static class SurrenderGate
{
    private const string ConditionName = "game_menu_encounter_surrender_on_condition";

    private static readonly Harmony Harmony = new Harmony("ModderLords.Compat.SurrenderGate");
    private static readonly AccessTools.FieldRef<PlayerEncounter, MapEvent> MapEventField =
        AccessTools.FieldRefAccess<PlayerEncounter, MapEvent>("_mapEvent");
    private static readonly TextObject WaitingTooltip =
        new TextObject("{=!}Waiting for the server to set up the battle...");

    private static bool _installTried;
    private static PlayerEncounter? _gatedEncounter;
    private static Stopwatch? _waiting;
    private static bool _stuckReported;

    /// <summary>Patches the vanilla condition once, on a client. Safe to call every tick.</summary>
    public static void EnsureInstalled()
    {
        if (_installTried) return;
        _installTried = true;
        try
        {
            if (!IsClient()) return;
            var condition = AccessTools.Method(typeof(EncounterGameMenuBehavior), ConditionName);
            if (condition == null) { Log.Warn("surrender gate not installed: " + ConditionName + " not found"); return; }
            // Last, so Coop's own postfix on this condition (which can turn the option on for an incapacitated
            // defender) has already settled whether it is shown.
            Harmony.Patch(condition, postfix: new HarmonyMethod(typeof(SurrenderGate), nameof(ConditionPostfix)) { priority = Priority.Last });
            Log.Info("surrender gate installed: Surrender stays disabled until the server's battle reaches this client");
        }
        catch (Exception ex) { Log.Warn("surrender gate not installed: " + ex.GetBaseException().Message); }
    }

    public static void ConditionPostfix(MenuCallbackArgs __0, bool __result)
    {
        try
        {
            var encounter = PlayerEncounter.Current;
            var resolved = encounter != null && MapEventField(encounter) != null;
            if (!SurrenderGatePolicy.ShouldGate(IsClient(), __result, encounter != null, resolved)) return;

            __0.IsEnabled = false;
            __0.Tooltip = WaitingTooltip;
            if (_gatedEncounter == encounter) return;
            _gatedEncounter = encounter;
            _waiting = Stopwatch.StartNew();
            _stuckReported = false;
            Log.Info("surrender gate: disabled Surrender while the server's battle is on its way");
        }
        catch (Exception ex) { Log.Warn("surrender gate check failed, leaving the option as it was: " + ex.GetBaseException().Message); }
    }

    /// <summary>Called from <see cref="Bridge.Tick"/>: re-evaluates the menu once the gated battle has arrived.</summary>
    public static void Tick()
    {
        var gated = _gatedEncounter;
        if (gated == null) return;
        try
        {
            var current = PlayerEncounter.Current;
            if (current != gated)
            {
                // The encounter ended or changed before its battle arrived; nothing to re-enable.
                Clear();
                return;
            }
            var waited = _waiting?.Elapsed.TotalSeconds ?? 0;
            if (SurrenderGatePolicy.ShouldRefresh(gated: true, sameEncounter: true, mapEventResolved: MapEventField(current) != null))
            {
                Log.Info($"surrender gate: the server's battle arrived after {waited:0.0}s; Surrender re-enabled");
                Clear();
                Campaign.Current?.CurrentMenuContext?.Refresh();
                return;
            }
            if (!_stuckReported && SurrenderGatePolicy.IsStuck(waited))
            {
                _stuckReported = true;
                Log.Warn($"surrender gate: still no battle from the server after {waited:0}s; Surrender stays disabled " +
                         "because clicking it now would be silently dropped by Coop");
            }
        }
        catch (Exception ex) { Log.Warn("surrender gate tick failed: " + ex.GetBaseException().Message); Clear(); }
    }

    private static void Clear()
    {
        _gatedEncounter = null;
        _waiting = null;
        _stuckReported = false;
    }

    private static bool IsClient()
    {
        try { return Common.ModInformation.IsClient; } catch { return false; }
    }
}
