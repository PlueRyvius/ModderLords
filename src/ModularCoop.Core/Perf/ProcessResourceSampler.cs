using System.Diagnostics;

namespace ModularCoop.Core.Perf;

public sealed record ResourceSample(DateTimeOffset At, double CpuPercent, long WorkingSetBytes);

/// <summary>The CPU arithmetic on its own, so it can be tested without a real process.</summary>
public static class ResourceMath
{
    /// <summary>
    /// Share of the machine the process used between two samples: its CPU time over the wall-clock elapsed, spread
    /// across all cores, as a percentage. Returns 0 rather than infinity when two samples land on the same instant.
    /// </summary>
    public static double CpuPercent(TimeSpan cpuDelta, TimeSpan wallDelta, int processorCount)
    {
        if (wallDelta <= TimeSpan.Zero || processorCount <= 0) return 0;
        var percent = cpuDelta.TotalMilliseconds / (wallDelta.TotalMilliseconds * processorCount) * 100.0;
        return percent < 0 ? 0 : percent;
    }
}

/// <summary>
/// Watches the engine process from the launcher side: CPU share and working set, every few seconds.
///
/// This costs the server nothing at all — it is the launcher reading counters Windows already keeps — and it is the
/// pair of numbers that would have shown the v0.8.3 runaway (working set climbing past 19 GB) long before anyone
/// felt the stutter. Runs on its own timer, never the dispatcher, and any failure skips a sample rather than
/// propagating: a meter must never be able to take down the thing it is measuring.
/// </summary>
public sealed class ProcessResourceSampler : IDisposable
{
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(10);

    private readonly int _processId;
    private readonly TimeSpan _interval;
    private readonly Timer _timer;
    private Process? _process;
    private TimeSpan _lastCpu;
    private DateTimeOffset _lastAt;
    private bool _primed;

    public event Action<ResourceSample>? SampleReady;

    public ProcessResourceSampler(int processId, TimeSpan? interval = null)
    {
        _processId = processId;
        _interval = interval ?? DefaultInterval;
        _timer = new Timer(_ => Sample(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public void Start() => _timer.Change(TimeSpan.Zero, _interval);

    private void Sample()
    {
        try
        {
            _process ??= Process.GetProcessById(_processId);
            _process.Refresh();
            var now = DateTimeOffset.UtcNow;
            var cpu = _process.TotalProcessorTime;
            var workingSet = _process.WorkingSet64;

            if (!_primed)
            {
                // The first reading has no interval behind it; CPU needs two points.
                (_lastCpu, _lastAt, _primed) = (cpu, now, true);
                return;
            }

            var percent = ResourceMath.CpuPercent(cpu - _lastCpu, now - _lastAt, Environment.ProcessorCount);
            (_lastCpu, _lastAt) = (cpu, now);
            SampleReady?.Invoke(new ResourceSample(now, percent, workingSet));
        }
        catch
        {
            // Process gone, access denied, or a transient counter failure: skip this sample and try again later.
            _process = null;
            _primed = false;
        }
    }

    public void Dispose()
    {
        _timer.Dispose();
        _process?.Dispose();
    }
}
