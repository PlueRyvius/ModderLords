using System;

namespace ModderLords.Compat;

/// <summary>
/// The decision-making half of world creation: when to start, when to give up, what to report, and what to
/// exit with. Deliberately free of every TaleWorlds type so it can be source-linked into the net10 test
/// project (the pattern <c>ModderLords.Core.Tests.csproj</c> already uses for the sync module's parsers) —
/// which makes this the first part of the compat module that is testable at all.
///
/// <see cref="WorldCreator"/> supplies the engine facts (is a game already running, is the campaign up) and
/// performs the effects; everything about *whether* and *when* lives here.
/// </summary>
internal sealed class CreateWorldPolicy
{
    /// <summary>
    /// The phases, in order. They are the spike's checkpoints, and each is announced on stdout, so a failed
    /// run says exactly how far it got without anyone reading a 175 MB log.
    /// </summary>
    internal enum Phase
    {
        /// <summary>Not asked for. The overwhelmingly common case; costs one null check per tick.</summary>
        Off,
        Armed,
        Starting,
        CampaignCreated,
        MapReady,
        Saving,
        Saved,
        Failed,
    }

    /// <summary>The stdout contract. The spike harness greps for these, so they are asserted in tests.</summary>
    internal const string Prefix = "worldcreate:";

    internal const string EnvSaveName = "MODDERLORDS_CREATE_WORLD";
    internal const string EnvTimeout = "MODDERLORDS_CREATE_WORLD_TIMEOUT";

    /// <summary>Exit codes. Distinct from the engine's own so a deliberate stop is never read as a crash.</summary>
    internal const int ExitCreated = 11;
    internal const int ExitFailed = 12;

    /// <summary>
    /// There is deliberately no settle delay. Measured 2026-09-08 (spike stage 0b): even with no
    /// <c>/coopsave</c> on the command line the host still loads a save of its own, so there is no idle state
    /// to wait for — whoever starts a game first wins. Arming happens at the "every module is loaded" hook,
    /// which is before the host's state machine runs, and we start immediately from there.
    /// </summary>
    internal static readonly TimeSpan SettleDelay = TimeSpan.Zero;

    /// <summary>
    /// Whole-run budget. Generous because a heavy mod legitimately loads for a long time — TAOM's authors
    /// quote up to two hours for the client — but not unbounded, or a stuck run is a hang again.
    /// </summary>
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(15);

    private readonly TimeSpan _timeout;
    private bool _armed;
    private DateTime _armedAt;
    private DateTime _phaseSince;

    internal CreateWorldPolicy(string? saveName, TimeSpan? timeout = null)
    {
        SaveName = string.IsNullOrWhiteSpace(saveName) ? null : saveName!.Trim();
        _timeout = timeout ?? DefaultTimeout;
        Current = SaveName is null ? Phase.Off : Phase.Armed;
    }

    /// <summary>Reads the mode from the environment, so the launcher arms it the way it arms the hook.</summary>
    internal static CreateWorldPolicy FromEnvironment(Func<string, string?> read)
    {
        var name = read(EnvSaveName);
        var timeout = ParseTimeout(read(EnvTimeout));
        return new CreateWorldPolicy(name, timeout);
    }

    /// <summary>Seconds, or null for the default. Anything unparseable is ignored rather than silently zeroing the budget.</summary>
    internal static TimeSpan? ParseTimeout(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return int.TryParse(raw!.Trim(), out var seconds) && seconds > 0 ? TimeSpan.FromSeconds(seconds) : null;
    }

    internal string? SaveName { get; }
    internal Phase Current { get; private set; }
    /// <summary>False until <see cref="Arm"/> has run. Ticks arriving before that must be inert.</summary>
    internal bool IsArmed => _armed;
    internal bool IsEnabled => SaveName is not null;
    internal string? FailureReason { get; private set; }

    /// <summary>
    /// Starts the clock, from the "every module is loaded" hook. Returns the line to print — the arming
    /// announcement is what tells a failed run apart from one where the env var never reached the module at
    /// all, so it has to be emitted rather than merely recorded.
    /// </summary>
    internal string? Arm(DateTime now)
    {
        if (!IsEnabled || _armed) return null;
        _armed = true;
        _armedAt = now;
        _phaseSince = now;
        return $"{Prefix} phase={Name(Phase.Armed)} detail={SaveName}";
    }

    /// <summary>
    /// Whether this tick should kick off the campaign. Only once, only after the settle delay, and only when
    /// the host has not already started a game of its own — we add a world, we never race one.
    /// </summary>
    internal bool ShouldStart(DateTime now, bool gameAlreadyRunning)
    {
        if (!_armed || Current != Phase.Armed) return false;
        if (SettleDelay > TimeSpan.Zero && now - _armedAt < SettleDelay) return false;
        if (gameAlreadyRunning)
        {
            Fail(now, "the host already started a game; nothing to create into");
            return false;
        }
        return true;
    }

    /// <summary>Records a transition and returns the line to print, or null when nothing changed.</summary>
    internal string? Advance(Phase to, DateTime now, string? detail = null)
    {
        if (to == Current) return null;
        Current = to;
        _phaseSince = now;
        var name = Name(to);
        return detail is null ? $"{Prefix} phase={name}" : $"{Prefix} phase={name} detail={detail}";
    }

    internal string Fail(DateTime now, string reason)
    {
        FailureReason = reason;
        var from = Name(Current);
        Current = Phase.Failed;
        _phaseSince = now;
        return $"{Prefix} fail phase={from} reason={reason}";
    }

    internal string Succeeded(string path, long bytes) => $"{Prefix} ok name={SaveName} path={path} bytes={bytes}";

    /// <summary>
    /// True once the whole-run budget is spent. Checked every tick so a stuck phase cannot hang forever.
    ///
    /// The <c>_armed</c> guard is not decoration: OnApplicationTick fires before the hook that arms this, so
    /// without it the first tick compares against DateTime.MinValue, "times out" instantly, and the run dies
    /// in six seconds claiming it waited for -2147483648 seconds.
    /// </summary>
    internal bool HasTimedOut(DateTime now) =>
        _armed && Current != Phase.Saved && Current != Phase.Failed && now - _armedAt >= _timeout;

    internal string TimeoutMessage(DateTime now) =>
        $"{Prefix} fail phase={Name(Current)} reason=timed out after {(int)(now - _armedAt).TotalSeconds}s in this phase";

    /// <summary>How long the current phase has been running, for progress reporting.</summary>
    internal TimeSpan InPhase(DateTime now) => now - _phaseSince;

    /// <summary>The process exit code for wherever this ended up.</summary>
    internal int ExitCode => Current == Phase.Saved ? ExitCreated : ExitFailed;

    /// <summary>Lower-case hyphenated, matching the harness's patterns exactly.</summary>
    internal static string Name(Phase p) => p switch
    {
        Phase.Off => "off",
        Phase.Armed => "armed",
        Phase.Starting => "starting",
        Phase.CampaignCreated => "campaign-created",
        Phase.MapReady => "map-ready",
        Phase.Saving => "saving",
        Phase.Saved => "saved",
        Phase.Failed => "failed",
        _ => "unknown",
    };
}
