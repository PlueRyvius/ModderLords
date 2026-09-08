namespace ModderLords.Core.Logs;

/// <summary>
/// Notices when a launch has stopped making progress instead of failing.
///
/// The failure mode this exists for: the server enters the campaign-load state machine, throws inside a tick, has the
/// exception swallowed, and re-enters the same state forever. Nothing exits, nothing is logged as fatal, and the
/// launcher happily runs for minutes while the tick counter never advances. The engine does say where it is —
/// "loading step @tick N: manager=... gameType=..." — so a stall is a step line whose tick has not moved.
///
/// Two things learned since, both of which changed the design:
///
/// A heavy mod can be legitimately slow. TAOM's own authors put its load at up to two hours, and a client observed
/// mid-load was burning 123% CPU with near-zero disk I/O, flat memory and 0% GPU — busy, not wedged. A fixed
/// 90-second threshold calls that a stall, and a warning that fires on every healthy heavy launch is worse than no
/// warning, because it teaches you to ignore the one that matters. Hence <see cref="Threshold"/> is configurable.
///
/// Silence and repetition are not equally suspicious. A step being re-entered while its tick never advances is the
/// real signature and is stated plainly. Nothing being logged at all is much weaker evidence — it is exactly what a
/// long CPU-bound load looks like from outside — so it is worded as a heads-up, not a verdict.
///
/// Pure and clock-injected so it can be tested without waiting.
/// </summary>
public sealed class LoadStallDetector
{
    /// <summary>
    /// How long without progress before saying anything. Five minutes: the known-good stack reaches SERVING in about
    /// seventy seconds, so this still catches a hang long before the five and a half minutes of silence that made the
    /// original investigation so expensive, while leaving room for a mod that is merely slow.
    /// </summary>
    public static readonly TimeSpan DefaultThreshold = TimeSpan.FromMinutes(5);

    /// <summary>Report nothing at all. For a mod known to load for hours, where the warning could only be noise.</summary>
    public static readonly TimeSpan Off = TimeSpan.Zero;

    private readonly TimeSpan _threshold;
    private DateTimeOffset _lastProgress;
    private string? _step;
    private int _repeats;
    private bool _serving;
    private TimeSpan _nextReportAt;
    private int _linesSinceProgress;

    public LoadStallDetector(DateTimeOffset startedAt, TimeSpan? threshold = null)
    {
        _threshold = threshold ?? DefaultThreshold;
        _lastProgress = startedAt;
        _nextReportAt = _threshold;
    }

    /// <summary>The quiet period this instance uses. <see cref="Off"/> (zero) disables it entirely.</summary>
    public TimeSpan Threshold => _threshold;

    public bool IsEnabled => _threshold > TimeSpan.Zero;

    /// <summary>The step the engine last reported, for the stall message.</summary>
    public string? CurrentStep => _step;

    /// <summary>How many times the current step has been seen without the tick advancing.</summary>
    public int Repeats => _repeats;

    /// <summary>
    /// Feeds one engine line. Returns a message when there is something to say, otherwise null. Any line that counts
    /// as progress — a new loading step, or SERVING — resets the clock; SERVING disarms the detector for good.
    /// </summary>
    public string? Observe(string text, DateTimeOffset at)
    {
        if (_serving || !IsEnabled) return null;

        if (text.Contains("SERVING", StringComparison.Ordinal))
        {
            _serving = true;
            return null;
        }

        var step = ExtractStep(text);
        if (step is not null)
        {
            if (step == _step) _repeats++;
            else { _step = step; _repeats = 0; Reset(at); }
            return null;
        }

        _linesSinceProgress++;
        return Check(at);
    }

    private void Reset(DateTimeOffset at)
    {
        _lastProgress = at;
        _nextReportAt = _threshold;
        _linesSinceProgress = 0;
    }

    /// <summary>
    /// Polls the clock with no new line, so a run that has gone completely silent is still caught.
    ///
    /// Reports repeatedly rather than once, at doubling intervals. A single warning followed by permanent silence
    /// reads like a verdict; on a load that is merely slow what you want is an occasional heartbeat saying how long
    /// it has been, and the doubling keeps a genuinely long wait from becoming a wall of text.
    /// </summary>
    public string? Check(DateTimeOffset at)
    {
        if (_serving || !IsEnabled) return null;
        var quiet = at - _lastProgress;
        if (quiet < _nextReportAt) return null;

        _nextReportAt = TimeSpan.FromTicks(_nextReportAt.Ticks * 2);
        var mins = $"{quiet.TotalMinutes:0.#} min";

        // The strong signal: the engine keeps saying where it is, and it is the same place every time.
        if (_step is not null && _repeats > 0)
            return $"WARNING no loading progress for {mins}: stuck at '{_step}', re-entered {_repeats} time(s). " +
                "A load that repeats one step is usually a swallowed exception in the campaign state machine, often a save built with a different module set.";

        var where = _step is null ? "the engine has not reported a loading step yet" : $"the last step was '{_step}'";

        // Busy but not advancing. Measured: the real TAOM failure logs its map-scene loop continuously while the
        // loading step never moves, so "nothing has been logged" would have been simply untrue -- and the fact that
        // the engine is talking without getting anywhere is the more suspicious of the two.
        if (_linesSinceProgress > 0)
            return $"still loading after {mins} — {where}. The engine is still logging ({_linesSinceProgress} lines) but the loading step has not advanced. " +
                "That is normal for a slow mod and also what a stuck load looks like; if the process is using CPU it is working.";

        // Genuinely silent. Heavy mods do this while working perfectly well.
        return $"still loading after {mins} — {where}, and nothing has been logged since. " +
            "Some mods take a very long time here; if the process is still using CPU it is working. This is a heads-up, not an error.";
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

    /// <summary>
    /// Parses a user-supplied quiet period: a number of seconds, or "off"/"none" to disable. Null when the text is
    /// not something we understand, so a typo can be reported rather than silently turning the warning off.
    /// </summary>
    public static TimeSpan? ParseThreshold(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text.Trim();
        if (t.Equals("off", StringComparison.OrdinalIgnoreCase) || t.Equals("none", StringComparison.OrdinalIgnoreCase)) return Off;
        return int.TryParse(t, out var seconds) && seconds >= 0 ? TimeSpan.FromSeconds(seconds) : null;
    }
}
