using System;
using System.Collections.Generic;
using System.Linq;

namespace ModderLords.CompatSync.Coop;

/// <summary>
/// The decisions behind <see cref="EncounterOptionGate"/>, kept free of TaleWorlds and Coop types so the test project
/// can compile this file in and assert them directly.
/// </summary>
internal static class EncounterOptionGatePolicy
{
    /// <summary>
    /// Vanilla encounter-menu conditions whose Coop client path builds a server request from the client's own MapEvent,
    /// which is still null when the menu first opens. An option joins this list only with log evidence that it fails in
    /// that window — Attack, for instance, is built server-side and works, so gating it would only slow it down.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, string> GatedConditions = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        // 2026-09-12: "Battle encounter option clicked: surrender; mapEvent=<null>" then "Failed to get id because object
        // was null (MapEvent)". Coop dropped the request after latching _playerSurrender (upstream issue #3305).
        ["game_menu_encounter_surrender_on_condition"] = "Surrender",
    };

    /// <summary>
    /// Past this, the server's MapEvent is not merely late: the gate says so once, and keeps the options disabled,
    /// because enabling them would only reproduce the silent failure it exists to prevent.
    /// </summary>
    internal const double StuckAfterSeconds = 20.0;

    internal static bool IsGated(string conditionName) => GatedConditions.ContainsKey(conditionName);

    internal static string LabelFor(string conditionName) => GatedConditions.TryGetValue(conditionName, out var label) ? label : conditionName;

    /// <summary>Disable an option only when it would otherwise be shown, on a client, inside an encounter whose MapEvent has not arrived.</summary>
    internal static bool ShouldGate(bool isClient, bool optionShown, bool hasEncounter, bool mapEventResolved)
        => isClient && optionShown && hasEncounter && !mapEventResolved;

    /// <summary>Refresh the menu once, when a gated encounter's MapEvent has just arrived.</summary>
    internal static bool ShouldRefresh(bool gated, bool sameEncounter, bool mapEventResolved)
        => gated && sameEncounter && mapEventResolved;

    internal static bool IsStuck(double waitedSeconds) => waitedSeconds > StuckAfterSeconds;

    internal static string Describe(IEnumerable<string> labels) => string.Join(", ", labels.Distinct(StringComparer.Ordinal).OrderBy(l => l, StringComparer.Ordinal));
}
