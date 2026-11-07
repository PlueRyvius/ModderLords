using System;
using System.Collections.Generic;

namespace ModderLords.Testing;

/// <summary>How one step ended.</summary>
internal sealed class StepEnd
{
    public StepEnd(bool passed, string message) { Passed = passed; Message = message; }
    public bool Passed { get; }
    public string Message { get; }
}

/// <summary>
/// Runs one step's coroutine a tick at a time against a clock it is given, so the waits (until, settled, timeouts) are
/// tested without a game. TaleWorlds-free.
/// </summary>
internal sealed class StepRunner
{
    private readonly IEnumerator<StepWait?> _body;
    private readonly DateTime _deadline;
    private StepWait? _wait;
    private DateTime _waitStarted;
    private DateTime? _waitDeadline;
    // Settled: the last value read, and since when it has read that.
    private string? _lastValue;
    private DateTime _sameSince;

    public StepRunner(StepBody body, StepContext context, DateTime now, TimeSpan timeout)
    {
        Context = context;
        _deadline = now + timeout;
        _body = body(context).GetEnumerator();
    }

    public StepContext Context { get; }
    public StepEnd? Outcome { get; private set; }

    /// <summary>Advances the step if what it waits for has come; returns the outcome once it has ended.</summary>
    public StepEnd? Tick(DateTime now)
    {
        if (Outcome != null) return Outcome;
        try
        {
            if (now > _deadline) return End(false, "the step timed out" + (_wait != null ? " waiting for " + _wait.What : ""));
            if (_wait != null)
            {
                if (_waitDeadline is { } waitDeadline && now > waitDeadline) return End(false, "timed out waiting for " + _wait.What);
                if (!Ready(_wait, now)) return null;
                _wait = null;
            }
            // One resume per tick: a step that yields null gets the next frame, as promised.
            if (!_body.MoveNext()) return End(true, "");
            _wait = _body.Current;
            if (_wait != null)
            {
                _waitStarted = now;
                _waitDeadline = _wait.Timeout is { } t ? now + t : (DateTime?)null;
                _lastValue = null;
                _sameSince = now;
                if (!Ready(_wait, now)) return null;
                _wait = null;
            }
            return null;
        }
        catch (StepFailedException ex) { return End(false, ex.Message); }
        catch (Exception ex) { return End(false, ex.GetType().Name + ": " + ex.Message); }
    }

    private bool Ready(StepWait wait, DateTime now)
    {
        if (wait.Read != null)
        {
            var value = StepContext.Format(wait.Read());
            if (value != _lastValue) { _lastValue = value; _sameSince = now; }
            if (wait.Condition != null && !wait.Condition()) return false;
            return now - _sameSince >= wait.Quiet;
        }
        if (wait.Condition != null) return wait.Condition();
        return now - _waitStarted >= wait.Quiet;
    }

    private StepEnd End(bool passed, string message)
    {
        try { _body.Dispose(); } catch { }
        Outcome = new StepEnd(passed, message);
        return Outcome;
    }
}
