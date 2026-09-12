namespace ModderLords.Core.Logs;

/// <summary>
/// Collapses a line the server repeats without end, so one unfixable complaint cannot bury everything else.
///
/// Measured 2026-09-12: a healthy TAOM session logged 10,827 `[Coop] ["ObjectManager"] Failed to get id for object`
/// lines in 53,273 — a fifth of the console — for heroes and parties Coop's sync layer does not know about. Nothing
/// on the launcher's side can fix that, and it does not stop the server working, but it makes the console useless
/// for watching anything else.
///
/// The first few of each distinct message are shown, then the rest are counted and reported periodically, so the
/// information survives as "this is still happening, N times now" rather than as N screenfuls. The launch log keeps
/// every line regardless: this is about what a person can read, not about what gets recorded.
///
/// Messages are grouped after replacing runs of digits, because the repeated lines usually differ only by an id.
/// </summary>
public sealed class RepeatCollapser
{
    private readonly int _showFirst;
    private readonly int _summariseEvery;
    private readonly Dictionary<string, int> _seen = new(StringComparer.Ordinal);

    public RepeatCollapser(int showFirst = 3, int summariseEvery = 500)
    {
        _showFirst = showFirst;
        _summariseEvery = summariseEvery;
    }

    /// <summary>
    /// What the console should do with this line: the line itself, a summary standing in for a run of them, or
    /// null to show nothing.
    /// </summary>
    public string? Filter(string text)
    {
        var key = Key(text);
        var count = _seen.TryGetValue(key, out var n) ? n + 1 : 1;
        _seen[key] = count;

        if (count <= _showFirst) return text;
        if (count == _showFirst + 1)
            return $"[ModderLords] the line above is repeating; further copies are counted, not shown: {Trim(text)}";
        if (count % _summariseEvery == 0)
            return $"[ModderLords] still repeating ({count:N0} times): {Trim(text)}";
        return null;
    }

    /// <summary>How many times each collapsed message was seen, most frequent first. For an end-of-run summary.</summary>
    public IEnumerable<KeyValuePair<string, int>> Totals =>
        _seen.Where(kv => kv.Value > _showFirst).OrderByDescending(kv => kv.Value);

    /// <summary>Groups lines that differ only by an id, so "hero 4471" and "hero 4472" count as the same complaint.</summary>
    private static string Key(string text)
    {
        var chars = new char[text.Length];
        var n = 0;
        var inDigits = false;
        foreach (var c in text)
        {
            if (char.IsDigit(c)) { if (!inDigits) { chars[n++] = '#'; inDigits = true; } continue; }
            inDigits = false;
            chars[n++] = c;
        }
        return new string(chars, 0, n);
    }

    /// <summary>Enough of the line to recognise it. A collapsed summary that is itself a wall of text helps nobody.</summary>
    private static string Trim(string text) => text.Length <= 100 ? text : text[..97] + "...";
}
