using System.Diagnostics;

namespace ModularCoop.Core.Launch;

public enum OutputStream { Stdout, Stderr }

public sealed record EngineLine(DateTimeOffset At, OutputStream Stream, string Text);

/// <summary>Runs one engine process, streams its output line by line, and lets the caller write console commands.</summary>
public sealed class EngineProcess : IDisposable
{
    private readonly Process _process;
    private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _started;

    public event Action<EngineLine>? LineReceived;

    public DateTimeOffset StartedAt { get; private set; }
    public int? ProcessId => _started ? _process.Id : null;
    public Task<int> Exited => _exit.Task;
    public bool IsRunning => _started && !_process.HasExited;

    private EngineProcess(Process process) => _process = process;

    public static EngineProcess Start(LaunchPlan plan)
    {
        var p = new Process { StartInfo = plan.ToStartInfo(), EnableRaisingEvents = true };
        var ep = new EngineProcess(p);
        p.OutputDataReceived += (_, e) => { if (e.Data is not null) ep.LineReceived?.Invoke(new EngineLine(DateTimeOffset.Now, OutputStream.Stdout, e.Data)); };
        p.ErrorDataReceived += (_, e) => { if (e.Data is not null) ep.LineReceived?.Invoke(new EngineLine(DateTimeOffset.Now, OutputStream.Stderr, e.Data)); };
        p.Exited += (_, _) =>
        {
            int code;
            try { code = p.ExitCode; } catch { code = int.MinValue; }
            ep._exit.TrySetResult(code);
        };
        ep.StartedAt = DateTimeOffset.Now;
        p.Start();
        ep._started = true;
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        return ep;
    }

    /// <summary>Writes one console command to the engine's stdin. Whether the engine listens is verified in Phase 0/1.</summary>
    public async Task SendCommandAsync(string command)
    {
        if (!IsRunning) throw new InvalidOperationException("Engine is not running");
        await _process.StandardInput.WriteLineAsync(command);
        await _process.StandardInput.FlushAsync();
    }

    /// <summary>Asks politely (stop command + close stdin), then kills the whole tree after the timeout.</summary>
    public async Task<int> StopAsync(TimeSpan graceful)
    {
        if (!IsRunning) return await Exited;
        try { await SendCommandAsync("stop"); _process.StandardInput.Close(); } catch { /* engine may not read stdin */ }
        var done = await Task.WhenAny(Exited, Task.Delay(graceful));
        if (done != Exited)
        {
            try { _process.Kill(entireProcessTree: true); } catch { }
        }
        return await Exited;
    }

    public void Dispose() => _process.Dispose();
}
