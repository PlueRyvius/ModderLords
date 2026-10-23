using System.Collections.Generic;
using HarmonyLib;
using ModderLords.CompatSync;

namespace ModderLords.CompatSync.Coop;

/// <summary>
/// Both sides, whenever Bellum Civile is loaded (not only in Bellum validation runs): serialises Bellum's privy-council
/// and council-incident lookup caches, which its party-size and army-food model postfixes read from the parallel
/// moving-party tick.
/// <para>
/// Measured 2026-09-30 on a client: "An item with the same key has already been added" from
/// PrivyCouncilBehavior.EnsureRecordIndexes under GetOfficeRecords, inside Coop's ParallelTickMovingParties, which
/// catches it and skips that party's move for the frame. CouncilIncidentBehavior.EnsureAspectIndex has the same shape
/// and also rebuilds whenever an aspect expires. A lock changes no result, only when each thread may run.
/// </para>
/// </summary>
public static class BellumThreadSafety
{
    private static readonly Harmony Harmony = new Harmony("ModderLords.Compat.BellumThreadSafety");
    private static bool _installTried;

    /// <summary>Type name → the fields that make up its lazily rebuilt state.</summary>
    internal static readonly IReadOnlyDictionary<string, string[]> SharedCaches = new Dictionary<string, string[]>
    {
        ["BellumCivile.Behaviors.PrivyCouncilBehavior"] = new[]
        {
            "_officeRecords", "_recordIndexesDirty", "_recordsByKingdomId", "_recordByOfficeByKingdomId", "_clanById",
            "_runtimeSnapshotsByKingdomId", "_runtimeUnavailableKingdomIds",
        },
        ["BellumCivile.Behaviors.CouncilIncidentBehavior"] = new[]
        {
            "_aspectIndexDirty", "_nextAspectIndexExpiryDay", "_aspectMultiplierByLookup", "_aspectMultiplierByKey", "_aspectExpiryDayByKey",
        },
    };

    /// <summary>Guards the listed caches once Bellum is loaded. Safe to call every tick.</summary>
    public static void EnsureInstalled()
    {
        if (_installTried) return;
        _installTried = true;
        var guarded = new List<string>();
        foreach (var kv in SharedCaches)
        {
            var type = AccessTools.TypeByName(kv.Key);
            if (type == null) continue;
            var n = SerializedState.Guard(Harmony, type, kv.Value, m => Log.Warn("Bellum thread safety: " + m));
            guarded.Add($"{type.Name} ({n} methods)");
        }
        if (guarded.Count > 0) Log.Info("Bellum thread safety: serialised " + string.Join(", ", guarded));
    }
}
