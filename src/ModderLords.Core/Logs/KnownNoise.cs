using System.Text.RegularExpressions;

namespace ModderLords.Core.Logs;

/// <summary>
/// Server output lines that are understood, harmless and frequent, kept out of our logs and consoles. Each kind is
/// explained once when it first appears and counted, so nothing disappears silently.
/// </summary>
public static partial class KnownNoise
{
    public sealed record Kind(string Key, string Explanation);

    /// <summary>
    /// Coop syncs MapEvent.MapEventVisual, and on a dedicated server that field holds the server core's do-nothing battle
    /// visual (an obfuscated type such as "A.f+A" implementing IMapEventVisual), which has no network id. Coop logs one
    /// error per battle, raid or siege and skips the field; players' games build their own visuals. A long session logs
    /// thousands. Any type name made only of one- or two-letter segments is an obfuscated server-core type: no game or
    /// mod type is named that way, and a rebuilt server core may rename it.
    /// </summary>
    public static readonly Kind StandInBattleVisual = new("coop-id-standin-visual",
        "Coop cannot sync the dedicated server's stand-in battle visual (it has no network id); players' games draw their own. Harmless; one per battle. Further lines are counted, not shown.");

    public static Kind? Match(string line)
    {
        var m = NoIdRx().Match(line);
        if (m.Success && ObfuscatedRx().IsMatch(m.Groups["type"].Value)) return StandInBattleVisual;
        return null;
    }

    [GeneratedRegex(@"\[""ObjectManager""\] Failed to get id for object of type ""(?<type>[^""]+)""")]
    private static partial Regex NoIdRx();

    [GeneratedRegex("^[A-Za-z]{1,2}(?:[.+][A-Za-z]{1,2})+$")]
    private static partial Regex ObfuscatedRx();
}

/// <summary>Per process: decides which lines to hide, says why the first time, and reports the totals.</summary>
public sealed class NoiseCounter
{
    private readonly Dictionary<string, (KnownNoise.Kind Kind, long Count)> _counts = new(StringComparer.Ordinal);

    /// <summary>True when the line should be hidden; <paramref name="explanation"/> is set on the first one of its kind.</summary>
    public bool Hide(string line, out string? explanation)
    {
        explanation = null;
        var kind = KnownNoise.Match(line);
        if (kind == null) return false;
        lock (_counts)
        {
            _counts.TryGetValue(kind.Key, out var seen);
            if (seen.Count == 0) explanation = "[ModderLords] hiding a known harmless server line: " + kind.Explanation + " First one: " + line.Trim();
            _counts[kind.Key] = (kind, seen.Count + 1);
        }
        return true;
    }

    public IReadOnlyList<string> Totals()
    {
        lock (_counts)
            return _counts.Values.Select(v => $"[ModderLords] hid {v.Count} known harmless server line(s): {v.Kind.Key}").ToList();
    }
}
