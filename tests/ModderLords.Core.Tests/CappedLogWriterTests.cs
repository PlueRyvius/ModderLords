using System.Text.Json;
using ModderLords.Core.Launch;
using ModderLords.Coop.Launch;
using ModderLords.Core.Logs;
using ModderLords.Core.Profiles;

namespace ModderLords.Core.Tests;

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

    // ---- crash reports -------------------------------------------------------------------------------
    // Same policy, but the units are directories holding a ~540 MB minidump each. Warnings no longer dump, so
    // this folder grows slowly now; it is still capped because a real crash should keep leaving evidence.

    private static void MakeReport(string dir, string name, int bytes, int ageMinutes)
    {
        var d = Path.Combine(dir, name);
        Directory.CreateDirectory(d);
        File.WriteAllBytes(Path.Combine(d, "dump.dmp"), new byte[bytes]);
        Directory.SetLastWriteTimeUtc(d, DateTime.UtcNow.AddMinutes(-ageMinutes));
    }

    [Fact]
    public void Crash_rotation_keeps_only_the_newest_reports()
    {
        var dir = TempDir();
        for (var i = 0; i < 8; i++) MakeReport(dir, $"2026-09-12_0{i}.00.00", 1024, ageMinutes: 8 - i);

        var removed = Preflight.RotateCrashDirs(dir, keep: 3, maxTotalBytes: long.MaxValue);

        var left = new DirectoryInfo(dir).GetDirectories().Select(d => d.Name).ToList();
        Assert.Equal(5, removed);
        Assert.Equal(3, left.Count);
        Assert.Contains("2026-09-12_07.00.00", left);
        Directory.Delete(dir, true);
    }

    [Fact]
    public void Crash_rotation_enforces_a_byte_budget_and_spares_the_newest()
    {
        var dir = TempDir();
        // Four 10 KB reports; a 25 KB budget cannot hold them all even though the count allows it.
        for (var i = 0; i < 4; i++) MakeReport(dir, $"report-{i}", 10 * 1024, ageMinutes: 4 - i);

        var removed = Preflight.RotateCrashDirs(dir, keep: 20, maxTotalBytes: 25 * 1024);

        var left = new DirectoryInfo(dir).GetDirectories();
        Assert.True(removed > 0, "byte budget should have removed something");
        Assert.True(left.Sum(d => d.GetFiles("*", SearchOption.AllDirectories).Sum(f => f.Length)) <= 25 * 1024);
        Assert.Contains(left, d => d.Name == "report-3");
        Directory.Delete(dir, true);
    }

    [Fact]
    public void Crash_rotation_keeps_the_newest_report_even_if_it_alone_exceeds_the_budget()
    {
        var dir = TempDir();
        MakeReport(dir, "only-report", 50 * 1024, ageMinutes: 0);

        Preflight.RotateCrashDirs(dir, keep: 5, maxTotalBytes: 1024);

        Assert.True(Directory.Exists(Path.Combine(dir, "only-report")),
            "the only (and newest) crash report must not be deleted — it is the one being diagnosed");
        Directory.Delete(dir, true);
    }

    [Fact]
    public void Crash_rotation_tolerates_a_missing_folder()
    {
        Assert.Equal(0, Preflight.RotateCrashDirs(Path.Combine(Path.GetTempPath(), "mccap-no-such-" + Guid.NewGuid().ToString("N")[..8])));
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

public class TraceDefaultTests
{
    [Fact]
    public void A_new_profile_has_every_trace_switch_off()
    {
        var s = new ServerSettings();
        Assert.False(s.TraceTick);
        Assert.False(s.TracePublish);
        Assert.False(s.TraceBandits);
    }

    [Fact]
    public void Trace_switches_are_never_persisted_so_they_open_off()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mctrace-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var p = new Profile { Name = "tracer" };
            p.Server.TraceTick = true;
            p.Server.TracePublish = true;
            p.Server.TraceBandits = true;
            p.Server.JoinPort = 4321;   // a normal setting, to prove only the trace flags are dropped

            var json = JsonSerializer.Serialize(p, new JsonSerializerOptions { WriteIndented = true });
            Assert.DoesNotContain("TraceTick", json);

            var back = JsonSerializer.Deserialize<Profile>(json)!;
            // The engine emits hundreds of thousands of lines a second with these on, so a trace switch
            // must never survive a restart in a file nobody reads.
            Assert.False(back.Server.TraceTick);
            Assert.False(back.Server.TracePublish);
            Assert.False(back.Server.TraceBandits);
            Assert.Equal(4321, back.Server.JoinPort);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void An_old_profile_file_with_trace_on_still_opens_with_it_off()
    {
        // Profiles written before this change can contain the flags; they must be ignored on read.
        var json = """
        { "Name": "legacy", "Server": { "JoinPort": 4200, "TraceTick": true, "TraceBandits": true } }
        """;
        var p = JsonSerializer.Deserialize<Profile>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Assert.False(p.Server.TraceTick);
        Assert.False(p.Server.TraceBandits);
        Assert.Equal(4200, p.Server.JoinPort);
    }
}
