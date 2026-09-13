using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
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
/// Client only: keeps encounter-menu options disabled until the server's MapEvent has reached this client.
/// <para>
/// Coop replaces some vanilla encounter actions on a client with a request to the server built from
/// PlayerEncounter.Current._mapEvent. The encounter menu opens before that MapEvent arrives, and a click in the gap
/// sends nothing. Measured on Surrender (2026-09-12): Coop's handler could not resolve an id for the null MapEvent and
/// dropped the request, having already set _playerSurrender, so every later click returned early and Surrender did
/// nothing for the rest of the encounter. Upstream Coop still does this on `development` (issue #3305). TAOM's slower
/// encounter setup makes the gap wide enough to hit by hand.
/// </para>
/// <para>
/// Which options are held is <see cref="EncounterOptionGatePolicy.GatedConditions"/>: one Harmony postfix serves them
/// all, so adding an option that the logs prove fails the same way is one table entry, not another patch.
/// </para>
/// </summary>
public static class EncounterOptionGate
{
    private static readonly Harmony Harmony = new Harmony("ModderLords.Compat.EncounterOptionGate");
    private static readonly AccessTools.FieldRef<PlayerEncounter, MapEvent> MapEventField =
        AccessTools.FieldRefAccess<PlayerEncounter, MapEvent>("_mapEvent");
    private static readonly TextObject WaitingTooltip =
        new TextObject("{=!}Waiting for the server to set up the battle...");

    private static bool _installTried;
    private static PlayerEncounter? _gatedEncounter;
    private static readonly HashSet<string> GatedLabels = new HashSet<string>(StringComparer.Ordinal);
    private static Stopwatch? _waiting;
    private static bool _stuckReported;

    /// <summary>Patches every listed vanilla condition once, on a client. Safe to call every tick.</summary>
    public static void EnsureInstalled()
    {
        if (_installTried) return;
        _installTried = true;
        if (!IsClient()) return;
        var installed = new List<string>();
        foreach (var kv in EncounterOptionGatePolicy.GatedConditions)
        {
            try
            {
                var condition = AccessTools.Method(typeof(EncounterGameMenuBehavior), kv.Key);
                if (condition == null) { Log.Warn($"encounter option gate: {kv.Value} not gated, {kv.Key} not found"); continue; }
                // Last, so Coop's own postfixes on these conditions (which can turn an option on, e.g. Surrender for an
                // incapacitated defender) have already settled whether it is shown.
                Harmony.Patch(condition, postfix: new HarmonyMethod(typeof(EncounterOptionGate), nameof(ConditionPostfix)) { priority = Priority.Last });
                installed.Add(kv.Value);
            }
            catch (Exception ex) { Log.Warn($"encounter option gate: {kv.Value} not gated: {ex.GetBaseException().Message}"); }
        }
        if (installed.Count > 0)
            Log.Info($"encounter option gate installed for {EncounterOptionGatePolicy.Describe(installed)}: held until the server's battle reaches this client");
    }

    public static void ConditionPostfix(MenuCallbackArgs __0, bool __result, MethodBase __originalMethod)
    {
        try
        {
            var encounter = PlayerEncounter.Current;
            var resolved = encounter != null && MapEventField(encounter) != null;
            if (!EncounterOptionGatePolicy.ShouldGate(IsClient(), __result, encounter != null, resolved)) return;

            __0.IsEnabled = false;
            __0.Tooltip = WaitingTooltip;
            var label = EncounterOptionGatePolicy.LabelFor(__originalMethod.Name);
            if (_gatedEncounter != encounter)
            {
                _gatedEncounter = encounter;
                GatedLabels.Clear();
                _waiting = Stopwatch.StartNew();
                _stuckReported = false;
            }
            if (GatedLabels.Add(label)) Log.Info($"encounter option gate: disabled {label} while the server's battle is on its way");
        }
        catch (Exception ex) { Log.Warn("encounter option gate check failed, leaving the option as it was: " + ex.GetBaseException().Message); }
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
            var labels = EncounterOptionGatePolicy.Describe(GatedLabels);
            if (EncounterOptionGatePolicy.ShouldRefresh(gated: true, sameEncounter: true, mapEventResolved: MapEventField(current) != null))
            {
                Log.Info($"encounter option gate: the server's battle arrived after {waited:0.0}s; {labels} re-enabled");
                Clear();
                Campaign.Current?.CurrentMenuContext?.Refresh();
                return;
            }
            if (!_stuckReported && EncounterOptionGatePolicy.IsStuck(waited))
            {
                _stuckReported = true;
                Log.Warn($"encounter option gate: still no battle from the server after {waited:0}s; {labels} stay disabled " +
                         "because clicking now would be silently dropped by Coop");
            }
        }
        catch (Exception ex) { Log.Warn("encounter option gate tick failed: " + ex.GetBaseException().Message); Clear(); }
    }

    private static void Clear()
    {
        _gatedEncounter = null;
        GatedLabels.Clear();
        _waiting = null;
        _stuckReported = false;
    }

    private static bool IsClient()
    {
        try { return Common.ModInformation.IsClient; } catch { return false; }
    }
}
