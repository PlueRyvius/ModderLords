using System;
using System.Collections.Generic;
using System.Linq;

namespace ModderLords.CompatSync.Coop;

/// <summary>
/// Replaces a field-battle scene that would crash clients (no terrain shader cache) with one from the same candidate
/// tier, chosen by the battle's shared terrain seed so the choice is stable. Free of game and Harmony types so the
/// test project can run it.
/// </summary>
public static class BattleScenePolicy
{
    /// <summary>
    /// Null when <paramref name="picked"/> is fine. Otherwise the replacement: the first tier (in order) that still has a
    /// scene once excluded ones are removed, ordered by id, indexed by a hash of <paramref name="seed"/>. When every
    /// candidate is excluded the original pick is returned unchanged (a crash beats no battle at all only if we can help it).
    /// </summary>
    public static string? Replace(string picked, IEnumerable<IReadOnlyList<string>> tiers, ISet<string> excluded, int seed)
    {
        if (string.IsNullOrEmpty(picked) || !excluded.Contains(picked)) return null;
        foreach (var tier in tiers)
        {
            var ok = tier.Where(s => !excluded.Contains(s)).Distinct(StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal).ToList();
            if (ok.Count == 0) continue;
            return ok[(int)(Hash(seed) % (uint)ok.Count)];
        }
        return picked;
    }

    /// <summary>FNV-1a over the seed's bytes: the same on every peer and every .NET runtime, unlike Random(seed).</summary>
    public static uint Hash(int seed)
    {
        uint h = 2166136261;
        for (var i = 0; i < 4; i++) { h ^= (byte)(seed >> (8 * i)); h *= 16777619; }
        return h;
    }
}
