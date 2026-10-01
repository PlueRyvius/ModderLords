using System;
using System.Collections.Generic;
using System.Linq;

namespace ModderLords.CompatSync.Coop;

/// <summary>
/// Runs each step of a periodic tick on its own, so one that throws does not skip the steps after it. A failing step is
/// logged once, then again only when its message changes; how often each has failed is in <see cref="Summary"/>.
/// <para>
/// Bridge.Tick used to be one block: when the Bellum snapshot broadcast threw (a section over its bounds), everything after
/// it stopped running on every tick, Bellum's prompt timeouts and the clean-up of disconnected peers included, and the same
/// warning was printed every three seconds. Free of game types so the tests can drive it.
/// </para>
/// </summary>
public sealed class TickSteps
{
    private sealed class Failure { public long Count; public string Message = ""; }

    private readonly Dictionary<string, Failure> _failures = new Dictionary<string, Failure>(StringComparer.Ordinal);
    private readonly Action<string> _warn;

    public TickSteps(Action<string> warn) => _warn = warn;

    public void Run(string name, Action step)
    {
        try { step(); }
        catch (Exception ex)
        {
            var message = ex.GetBaseException().Message;
            if (!_failures.TryGetValue(name, out var failure)) _failures[name] = failure = new Failure();
            failure.Count++;
            if (failure.Count == 1 || failure.Message != message)
                _warn($"tick step '{name}' failed (the rest of the tick still runs; repeats are counted, not logged): {message}");
            failure.Message = message;
        }
    }

    public string Summary() => _failures.Count == 0
        ? "tick steps failing 0"
        : "tick steps failing " + string.Join(", ", _failures.OrderBy(f => f.Key, StringComparer.Ordinal).Select(f => $"{f.Key} x{f.Value.Count}"));
}
