using ModderLords.Core.Testing;
using Xunit;

namespace ModderLords.Core.Tests;

/// <summary>The tests folder is pruned at the start of each run: old run folders, and crash dumps once read.</summary>
public sealed class TestRunRetentionTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("modderlords-retention").FullName;
    private static readonly DateTimeOffset Now = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Run(int day, int n)
    {
        var folder = Path.Combine(_root, $"202611{day:00}-1200{n:00}-core-ab{n:00}");
        Directory.CreateDirectory(Path.Combine(folder, "launch"));
        File.WriteAllText(Path.Combine(folder, "report.txt"), "ok");
        return folder;
    }

    private static string Dump(string run, DateTimeOffset written)
    {
        var path = Path.Combine(Directory.CreateDirectory(Path.Combine(run, "crash")).FullName, "server-1.dmp");
        File.WriteAllBytes(path, new byte[64]);
        File.SetLastWriteTimeUtc(path, written.UtcDateTime);
        return path;
    }

    [Fact]
    public void The_newest_runs_are_kept_and_older_ones_deleted()
    {
        var runs = Enumerable.Range(1, 5).Select(n => Run(10, n)).ToList();

        var said = TestRunRetention.Prune(_root, Now, keepRuns: 3);

        Assert.All(runs.Take(2), r => Assert.False(Directory.Exists(r)));
        Assert.All(runs.Skip(2), r => Assert.True(Directory.Exists(r)));
        Assert.Contains(said, s => s.Contains("2 oldest"));
    }

    [Fact]
    public void Only_folders_named_as_runs_are_touched()
    {
        var other = Directory.CreateDirectory(Path.Combine(_root, "notes")).FullName;
        Run(10, 1);
        Run(10, 2);

        TestRunRetention.Prune(_root, Now, keepRuns: 1);

        Assert.True(Directory.Exists(other));
    }

    [Fact]
    public void An_old_dump_is_deleted_and_the_runs_logs_stay()
    {
        var run = Run(1, 1);
        var dump = Dump(run, Now.AddDays(-8));

        var said = TestRunRetention.Prune(_root, Now);

        Assert.False(File.Exists(dump));
        Assert.True(File.Exists(Path.Combine(run, "report.txt")));
        Assert.Contains(said, s => s.Contains("crash dump"));
    }

    [Fact]
    public void A_recent_dump_is_kept()
    {
        var dump = Dump(Run(15, 1), Now.AddDays(-1));

        Assert.Empty(TestRunRetention.Prune(_root, Now));
        Assert.True(File.Exists(dump));
    }

    [Fact]
    public void No_tests_folder_yet_is_nothing_to_do() =>
        Assert.Empty(TestRunRetention.Prune(Path.Combine(_root, "missing"), Now));
}
