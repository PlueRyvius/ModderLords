using ModderLords.Core.Testing;

namespace ModderLords.Coop.Testing;

/// <summary>
/// The dedicated server's own crash dump in a test run. The server runs on .NET, so the runtime writes the dump itself
/// (<c>DOTNET_DbgEnableMiniDump</c>, set on the server's plan) into the run folder: managed stacks that point at the
/// failing mod call, which the engine's watchdog log never gives. The runtime says on the server's console when it
/// starts and when it is done; until then the process must be left alone, or the dump is cut short (three empty dumps
/// on 2026-11-15, each when the crash prompt was answered or the server stopped while it was still writing).
/// </summary>
public sealed class ServerDump
{
    /// <summary>How long a dump with heap may take: 1.6 GB for a TAOM server took nearly 5 minutes (2026-11-15).</summary>
    public static readonly TimeSpan Budget = TimeSpan.FromMinutes(10);

    private readonly object _lock = new();
    private DateTimeOffset? _writingSince;

    public ServerDump(string runFolder) => Folder = Path.Combine(runFolder, "crash");

    public string Folder { get; }

    /// <summary>The runtime's crash dump settings for the server: a dump with heap, into the run's crash folder.</summary>
    public IReadOnlyDictionary<string, string> Environment => new Dictionary<string, string>
    {
        ["DOTNET_DbgEnableMiniDump"] = "1",
        ["DOTNET_DbgMiniDumpType"] = "2",
        ["DOTNET_DbgMiniDumpName"] = Path.Combine(Folder, "server-%p.dmp"),
    };

    /// <summary>Whether the runtime is writing a dump now (started, not reported done, within the budget).</summary>
    public bool Writing
    {
        get { lock (_lock) return _writingSince is { } since && !Written && DateTimeOffset.Now - since < Budget; }
    }

    public bool Written { get; private set; }

    /// <summary>Reads one line of the server's console: the runtime's own createdump lines.</summary>
    public void Observe(string line)
    {
        if (!line.Contains("[createdump]", StringComparison.Ordinal)) return;
        lock (_lock)
        {
            if (line.Contains("Writing", StringComparison.OrdinalIgnoreCase)) { Directory.CreateDirectory(Folder); _writingSince ??= DateTimeOffset.Now; }
            if (line.Contains("successfully written", StringComparison.OrdinalIgnoreCase)) Written = true;
        }
    }

    /// <summary>
    /// Waits while a dump is being written, up to its budget, and no longer than the server lives: the dump is read
    /// from the server's memory, so once it has exited nothing more can come.
    /// </summary>
    public async Task WaitAsync(Func<bool> serverRunning, Action<string> say)
    {
        if (!Writing) return;
        say("[ModderLords] the server crashed and is writing its dump; waiting for it before closing anything…");
        while (Writing && serverRunning()) await Task.Delay(500);
        // The runtime's "written" line can trail the exit by a moment.
        if (!Written) await Task.Delay(1000);
        say(Written ? "[ModderLords] the server's dump is written"
            : serverRunning() ? "[ModderLords] the server's dump did not finish in time"
            : "[ModderLords] the server exited before its dump was written");
    }

    /// <summary>The dumps in the run folder.</summary>
    public IEnumerable<string> Files() => Directory.Exists(Folder) ? Directory.GetFiles(Folder, "server-*.dmp") : [];
}

/// <summary>
/// During a test run, answers the engine's crash prompt with No as soon as a crash shows in a watchdog log (the
/// maintainer's choice, 2026-11-15: the logs are enough; see <see cref="CrashPrompt"/>), except while the server is
/// still writing its dump: the answer lets the crashed process exit.
/// </summary>
public static class CrashResponder
{
    public static async Task RunAsync(CrashWatch crash, Func<bool> dumpWriting, Action<string> say, CancellationToken cancel)
    {
        try
        {
            while (!cancel.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancel);
                if (dumpWriting() || crash.CrashedSides().Count == 0) continue;
                var answered = CrashPrompt.AnswerNo();
                if (answered > 0) say($"[ModderLords] answered {answered} crash prompt(s) with No; the engine logs are kept in the run folder");
            }
        }
        catch (OperationCanceledException) { }
    }
}
