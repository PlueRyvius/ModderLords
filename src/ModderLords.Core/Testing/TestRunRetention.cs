using System.Globalization;
using System.Text.RegularExpressions;

namespace ModderLords.Core.Testing;

/// <summary>
/// Keeps the test runs folder from growing without end. Each run leaves a folder of logs and reports, a few MB; a run
/// whose server crashed also leaves the server's .NET dump, which is about 2 GB for a TAOM server (2026-11-15). At
/// the start of every run the oldest run folders beyond <see cref="KeepRuns"/> are deleted, and dumps older than
/// <see cref="KeepDumpsFor"/> are deleted from the folders kept (a dump is read once, when the crash is looked into; the
/// logs and reports beside it stay). Only folders named as runs are touched (<c>yyyyMMdd-HHmmss-...</c>).
/// </summary>
public static partial class TestRunRetention
{
    public const int KeepRuns = 50;
    public static readonly TimeSpan KeepDumpsFor = TimeSpan.FromDays(7);

    [GeneratedRegex(@"^\d{8}-\d{6}-")]
    private static partial Regex RunFolderName();

    /// <summary>Prunes <paramref name="root"/>; returns what it deleted, one line each. Anything in use is left for next time.</summary>
    public static IReadOnlyList<string> Prune(string root, DateTimeOffset now, int keepRuns = KeepRuns, TimeSpan? keepDumpsFor = null)
    {
        var said = new List<string>();
        if (!Directory.Exists(root)) return said;
        var runs = Directory.EnumerateDirectories(root)
            .Where(d => RunFolderName().IsMatch(Path.GetFileName(d)))
            .OrderByDescending(d => Path.GetFileName(d), StringComparer.Ordinal)
            .ToList();

        var old = runs.Skip(keepRuns).ToList();
        if (old.Count > 0)
        {
            long bytes = 0;
            var deleted = 0;
            foreach (var run in old)
            {
                var size = Size(run);
                try { Directory.Delete(run, recursive: true); bytes += size; deleted++; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            if (deleted > 0) said.Add($"deleted the {deleted} oldest test run folder(s) beyond the newest {keepRuns} ({Mb(bytes)})");
        }

        var cutoff = now - (keepDumpsFor ?? KeepDumpsFor);
        foreach (var run in runs.Take(keepRuns))
        {
            foreach (var dump in Directory.EnumerateFiles(run, "*.dmp", SearchOption.AllDirectories))
            {
                var file = new FileInfo(dump);
                if (file.LastWriteTimeUtc >= cutoff.UtcDateTime) continue;
                try
                {
                    var size = file.Length;
                    file.Delete();
                    said.Add($"deleted a crash dump more than {(keepDumpsFor ?? KeepDumpsFor).TotalDays:0} days old ({Mb(size)}): {dump}");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        return said;
    }

    private static long Size(string folder)
    {
        try { return Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
    }

    private static string Mb(long bytes) => (bytes / 1024d / 1024d).ToString("N0", CultureInfo.InvariantCulture) + " MB";
}
