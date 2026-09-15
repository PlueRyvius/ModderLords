using System;
using System.Collections.Generic;
using System.Linq;

namespace ModderLords.CompatSync.Coop;

/// <summary>
/// Client: holds relayed calls until their control has been still for a moment, keeping only the latest per action and
/// target. Dragging a slider fires its setter for every step (15 calls in a second on 2026-09-14), and the server's rate
/// limit dropped the last one, so the server kept a stale value. The target is the call's game-object arguments (the
/// town); its primitive arguments are the value that gets replaced. No game types, so the test project runs it.
/// </summary>
public sealed class RelayCoalescer
{
    private readonly TimeSpan _quiet;
    private readonly List<string> _order = new List<string>();
    private readonly Dictionary<string, (string method, List<string> kinds, List<string> values, DateTime at)> _pending =
        new Dictionary<string, (string method, List<string> kinds, List<string> values, DateTime at)>(StringComparer.Ordinal);

    public RelayCoalescer(TimeSpan quiet) => _quiet = quiet;

    public int PendingCount => _pending.Count;

    /// <summary>Method plus the game-object arguments; primitive kinds ("b", "i", "f", ...) are one letter, game types longer.</summary>
    public static string Key(string method, IReadOnlyList<string> kinds, IReadOnlyList<string> values) =>
        method + "|" + string.Join("|", kinds.Select((k, i) => k.Length > 1 && i < values.Count ? values[i] : "_"));

    public void Add(string method, List<string> kinds, List<string> values, DateTime now)
    {
        var key = Key(method, kinds, values);
        if (!_pending.ContainsKey(key)) _order.Add(key);
        _pending[key] = (method, kinds, values, now);
    }

    /// <summary>Calls whose target has had no newer value for the quiet period, in the order their targets were first touched.</summary>
    public List<(string method, List<string> kinds, List<string> values)> Due(DateTime now)
    {
        var due = new List<(string method, List<string> kinds, List<string> values)>();
        foreach (var key in _order.ToList())
        {
            var p = _pending[key];
            if (now - p.at < _quiet) continue;
            due.Add((p.method, p.kinds, p.values));
            _pending.Remove(key);
            _order.Remove(key);
        }
        return due;
    }
}
