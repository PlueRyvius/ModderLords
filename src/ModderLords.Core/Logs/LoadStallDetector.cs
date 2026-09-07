namespace ModderLords.Core.Logs;

/// <summary>
/// Notices when a launch has stopped making progress instead of failing.
///
/// The failure mode this exists for: the server enters the campaign-load state machine, throws inside a tick, has the
/// exception swallowed, and re-enters the same state forever. Nothing exits, nothing is logged as fatal, and the
/// launcher happily runs for minutes while the tick counter never advances. The engine does say where it is —
/// "loading step @tick N: manager=... gameType=..." — so a stall is a step line whose tick has not moved.
///
/// Pure and clock-injected so it can be tested without waiting.
/// </summary>
public sealed class LoadStallDetector
{
    /// <summary>How long the same loading step may repeat before it is called a stall. World init genuinely takes tens of seconds.</summary>
    public static readonly TimeSpan DefaultThreshold = TimeSpan.FromSeconds(90);

    private readonly TimeSpan _threshold;
    private DateTimeOffset _lastProgress;
    private string? _step;
    private int _repeats;
    private bool _serving;
    private bool _reported;

    public LoadStallDetector(DateTimeOffset startedAt, TimeSpan? threshold = null)
    {
        _threshold = threshold ?? DefaultThreshold;
        _lastProgress = startedAt;
    }

    /// <summary>The step the engine last reported, for the stall message.</summary>
    public string? CurrentStep => _step;

    /// <summary>How many times the current step has been seen without the tick advancing.</summary>
    public int Repeats => _repeats;

    /// <summary>
    /// Feeds one engine line. Returns a message the first time a stall is established, otherwise null. Any line that
    /// counts as progress — a new loading step, or SERVING — resets the clock; SERVING disarms the detector for good.
    /// </summary>
    public string? Observe(string text, DateTimeOffset at)
    {
        if (_serving) return null;

        if (text.Contains("SERVING", StringComparison.Ordinal))
        {
            _serving = true;
            return null;
        }

        var step = ExtractStep(text);
        if (step is not null)
        {
            if (step == _step) _repeats++;
            else { _step = step; _repeats = 0; _lastProgress = at; }
            return null;
        }

        return Check(at);
    }

    /// <summary>Polls the clock with no new line, so a run that has gone completely silent is still caught.</summary>
    public string? Check(DateTimeOffset at)
    {
        if (_serving || _reported || at - _lastProgress < _threshold) return null;
        _reported = true;
        var stuck = _step is null
            ? "the engine never reported a loading step"
            : $"stuck at '{_step}'" + (_repeats > 0 ? $", re-entered {_repeats} time(s)" : "");
        return $"WARNING no loading progress for {(int)(at - _lastProgress).TotalSeconds}s: {stuck}. " +
            "A load that repeats one step is usually a swallowed exception in the campaign state machine, often a save built with a different module set.";
    }

    /// <summary>
    /// The identity of a loading step, or null when the line is not one. Deliberately keyed on the state names rather
    /// than the tick: the tick advancing while the state does not is exactly the loop we are looking for.
    /// </summary>
    public static string? ExtractStep(string text)
    {
        var i = text.IndexOf("loading step @tick", StringComparison.OrdinalIgnoreCase);
        if (i < 0) return null;
        var colon = text.IndexOf(':', i);
        return colon < 0 ? null : text[(colon + 1)..].Trim();
    }
}
