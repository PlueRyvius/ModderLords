using ModularCoop.Core.Launch;
using ModularCoop.Core.Logs;

namespace ModularCoop.Core.Tests;

public class CappedLogWriterTests
{
    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "mccap-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        return d;
    }

    [Fact]
    public void Writes_lines_and_flushes_on_dispose()
    {
        var dir = TempDir();
        var path = Path.Combine(dir, "a.log");
        using (var w = new CappedLogWriter(path))
        {
            w.WriteLine("first");
            w.WriteLine("second");
        }
        var lines = File.ReadAllLines(path);
        Assert.Equal(["first", "second"], lines);
        Directory.Delete(dir, true);
    }

    [Fact]
    public void Stops_writing_once_the_cap_is_reached()
    {
        var dir = TempDir();
        var path = Path.Combine(dir, "b.log");
        var line = new string('x', 100);
        using (var w = new CappedLogWriter(path, maxBytes: 1000))
        {
            for (var i = 0; i < 500; i++) w.WriteLine(line);
            Assert.True(w.IsCapped);
            Assert.True(w.DroppedLines > 400, $"expected most lines dropped, got {w.DroppedLines}");
        }
        // The file stays near the cap rather than growing to the 50 KB that was written at it.
        var size = new FileInfo(path).Length;
        Assert.True(size < 2000, $"file should stay near the cap, was {size} bytes");
        Directory.Delete(dir, true);
    }

    [Fact]
    public void Says_in_the_file_that_it_was_capped()
    {
        var dir = TempDir();
        var path = Path.Combine(dir, "c.log");
        using (var w = new CappedLogWriter(path, maxBytes: 200))
        {
            for (var i = 0; i < 50; i++) w.WriteLine(new string('y', 50));
        }
        // A truncated log must announce itself; otherwise it reads as a complete record of a short session.
        Assert.Contains("log capped", File.ReadAllText(path));
        Directory.Delete(dir, true);
    }

    [Fact]
    public void Rotation_enforces_a_byte_budget_not_just_a_file_count()
    {
        var dir = TempDir();
        // Five 10 KB logs, oldest first.
        for (var i = 0; i < 5; i++)
        {
            var p = Path.Combine(dir, $"launch-{i}.log");
            File.WriteAllBytes(p, new byte[10 * 1024]);
            File.SetLastWriteTimeUtc(p, DateTime.UtcNow.AddMinutes(i));
        }

        // Keeping 20 by count would delete nothing; a 25 KB budget must still prune to fit.
        var removed = Preflight.RotateLogs(dir, "launch-*.log", keep: 20, maxTotalBytes: 25 * 1024);

        var left = new DirectoryInfo(dir).GetFiles("launch-*.log");
        Assert.True(removed > 0, "byte budget should have removed something");
        Assert.True(left.Sum(f => f.Length) <= 25 * 1024, "what remains must fit the budget");
        // Newest survives: it is the session someone would be diagnosing.
        Assert.Contains(left, f => f.Name == "launch-4.log");
        Directory.Delete(dir, true);
    }

    [Fact]
    public void Rotation_keeps_the_newest_file_even_if_it_alone_exceeds_the_budget()
    {
        var dir = TempDir();
        var p = Path.Combine(dir, "launch-huge.log");
        File.WriteAllBytes(p, new byte[50 * 1024]);

        Preflight.RotateLogs(dir, "launch-*.log", keep: 20, maxTotalBytes: 1024);

        Assert.True(File.Exists(p), "the only (and newest) log must not be deleted");
        Directory.Delete(dir, true);
    }
}

public class BoundedQueueTests
{
    [Fact]
    public void Never_exceeds_capacity_and_counts_what_it_drops()
    {
        var q = new BoundedQueue<int>(100);
        for (var i = 0; i < 10_000; i++) q.Enqueue(i);

        Assert.Equal(100, q.Count);
        Assert.Equal(9_900, q.Dropped);
    }

    [Fact]
    public void Keeps_the_newest_entries()
    {
        var q = new BoundedQueue<int>(3);
        foreach (var i in new[] { 1, 2, 3, 4, 5 }) q.Enqueue(i);

        var got = new List<int>();
        while (q.TryDequeue(out var v)) got.Add(v);
        // A live console wants the newest output, so the oldest is what gets discarded.
        Assert.Equal([3, 4, 5], got);
    }

    [Fact]
    public void TakeDropped_reports_each_drop_once()
    {
        var q = new BoundedQueue<int>(1);
        q.Enqueue(1); q.Enqueue(2); q.Enqueue(3);

        Assert.Equal(2, q.TakeDropped());
        Assert.Equal(0, q.TakeDropped());
    }

    [Fact]
    public void Holds_its_bound_under_concurrent_producers()
    {
        // Engine output arrives on callback threads, so the cap has to survive parallel writers.
        var q = new BoundedQueue<int>(500);
        Parallel.For(0, 8, _ =>
        {
            for (var i = 0; i < 5_000; i++) q.Enqueue(i);
        });

        Assert.True(q.Count <= 500, $"queue overran its bound: {q.Count}");
        Assert.Equal(40_000 - q.Count, q.Dropped);
    }
}
