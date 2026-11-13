using ModderLords.Core.Testing;
using Xunit;

namespace ModderLords.Core.Tests;

/// <summary>A test run stops as soon as the engine's watchdog catches a crash, rather than waiting out the step.</summary>
public sealed class CrashWatchTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ml-crashes-" + Guid.NewGuid().ToString("N"));
    private string Crashes => Path.Combine(_root, "crashes");
    private string Logs => Path.Combine(_root, "logs");

    public CrashWatchTests() => Directory.CreateDirectory(Logs);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    // As the watchdog wrote them for a game that hit a MissingMethodException making a world (2026-11-12, v1.5.7).
    private const string Caught = "Waiting for an exception event...\nCrash occurred. Asking for dump.\n";
    private const string Dumped = Caught + """
        Caught Exception! Taking dump...
        ExceptionRecord:
        ExceptionCode: 0xE0434352
        ExceptionFlags: 0x81
        Parameter-0: 0xffffffff80131513 == 18446744071563318547
        Dump file created at C:\dump.dmp.
        """;

    private void Log(string name, string text) => File.WriteAllText(Path.Combine(Logs, name), text);

    [Fact]
    public void The_watchdog_catching_a_crash_stops_the_run_before_anyone_answers_its_dialog()
    {
        Log("watchdog_log_100.txt", Caught);                  // an earlier crash: not this run's
        Log("watchdog_log_45132.txt", "Waiting for an exception event...\n");
        Log("rgl_log_45132.txt", "[23:57:53.424] opening item_holsters.xml \n\n[23:57:53.432] Initializing new game begin... \n\n");
        var watch = new CrashWatch(Crashes, Logs) { Every = TimeSpan.Zero };
        Assert.Null(watch.Check());

        File.AppendAllText(Path.Combine(Logs, "watchdog_log_45132.txt"), "Crash occurred. Asking for dump.\n");
        Assert.Equal($"the game crashed during: Initializing new game begin...; logs: {Logs}", watch.Check());
        Assert.True(watch.Crashed);

        var run = Path.Combine(_root, "run");
        var kept = watch.CopyLogs(run).Select(Path.GetFileName);
        Assert.Equal(["watchdog_log_45132.txt", "rgl_log_45132.txt"], kept);
    }

    [Fact]
    public void A_crash_report_written_after_the_dialog_names_the_exception()
    {
        var watch = new CrashWatch(Crashes, Logs) { Every = TimeSpan.Zero };
        var folder = Directory.CreateDirectory(Path.Combine(Crashes, "2026-11-13_05.45.18")).FullName;
        File.WriteAllText(Path.Combine(folder, "watchdog_log_6080.txt"), Dumped);
        File.WriteAllText(Path.Combine(folder, "rgl_log_6080.txt"), "[23:43:19.104] Initializing new game begin... \n");

        Assert.Equal($"the game crashed (MissingMethodException) during: Initializing new game begin...; logs: {folder}", watch.Check());
        Assert.Equal(folder, watch.Folder);
    }

    [Theory]
    [InlineData(Dumped, "MissingMethodException")]
    [InlineData("ExceptionCode: 0xE0434352\nParameter-0: 0xffffffff80004003 == 1", "NullReferenceException")]
    [InlineData("ExceptionCode: 0xE0434352\nParameter-0: 0xffffffff8013FFFF == 1", "a .NET exception, HRESULT 0x8013FFFF")]
    [InlineData("ExceptionCode: 0xc0000005\nParameter-0: 0x1 == 1", "native exception 0xC0000005")]
    [InlineData(Caught, null)]
    public void The_watchdog_log_names_what_crashed(string log, string? expected) =>
        Assert.Equal(expected, CrashWatch.ExceptionName(log));
}
