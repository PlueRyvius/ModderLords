namespace ModderLords.CompatSync.Coop;

/// <summary>
/// The decisions behind <see cref="SurrenderGate"/>, kept free of TaleWorlds and Coop types so the test project can
/// compile this file in and assert them directly.
/// </summary>
internal static class SurrenderGatePolicy
{
    /// <summary>
    /// Past this, the server's MapEvent is not merely late: the gate says so once, and keeps the option disabled,
    /// because enabling it would only reproduce the silent failure it exists to prevent.
    /// </summary>
    internal const double StuckAfterSeconds = 20.0;

    /// <summary>
    /// Disable the option only when it would otherwise be shown, on a client, inside an encounter whose MapEvent has
    /// not arrived. Coop's surrender prefix reads that MapEvent; with none it drops the request and latches
    /// PlayerEncounter._playerSurrender, after which Surrender does nothing for the rest of the encounter.
    /// </summary>
    internal static bool ShouldGate(bool isClient, bool optionShown, bool hasEncounter, bool mapEventResolved)
        => isClient && optionShown && hasEncounter && !mapEventResolved;

    /// <summary>Refresh the menu once, when a gated encounter's MapEvent has just arrived.</summary>
    internal static bool ShouldRefresh(bool gated, bool sameEncounter, bool mapEventResolved)
        => gated && sameEncounter && mapEventResolved;

    internal static bool IsStuck(double waitedSeconds) => waitedSeconds > StuckAfterSeconds;
}
