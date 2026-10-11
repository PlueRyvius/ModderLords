using System.IO;
using ModderLords.App;
using Xunit;

namespace ModderLords.App.Tests;

/// <summary>The support bundle takes the game's own logs, not only its error logs.</summary>
public sealed class SupportGameLogsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ml-gamelogs-" + Guid.NewGuid().ToString("N"));

    public SupportGameLogsTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    private string Log(string name, int minutesAgo)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, name);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-minutesAgo));
        return path;
    }

    [Fact]
    public void The_newest_game_logs_are_taken_newest_first_and_error_logs_are_not_counted_among_them()
    {
        var old = Log("rgl_log_100.txt", 300);
        var server = Log("rgl_log_200.txt", 20);
        var game = Log("rgl_log_300.txt", 5);
        Log("rgl_log_errors_300.txt", 1);
        Log("watchdog_log_300.txt", 1);

        Assert.Equal(new[] { game, server }, SupportReportService.GameLogs(_dir, 2));
        Assert.Equal(new[] { game, server, old }, SupportReportService.GameLogs(_dir, SupportReportService.GameLogCount));
    }

    [Fact]
    public void A_pc_that_has_never_run_the_game_has_none()
    {
        Assert.Empty(SupportReportService.GameLogs(Path.Combine(_dir, "absent"), 4));
    }
}
