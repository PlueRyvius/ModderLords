using System;
using System.Collections.Generic;
using System.Linq;

namespace ModderLords.Compat;

/// <summary>
/// Decides which failed asserts reach the host's debug manager. Kept free of TaleWorlds types so the test project can
/// compile it in.
///
/// Measured failure this exists for, 2026-09-12: a TAOM server raised
/// <c>Path finding target is not valid (MobileParty.cs:3907 ComputePath)</c> about 7,300 times a second. Every one was
/// written to <c>rgl_log_errors_&lt;pid&gt;.txt</c> — 1.2 MB/s, 1.1 GB and 2.7 GB in two sessions — and the server's
/// tick rate fell from 63/s to 15.5/s, which clients felt as pauses while they waited for it. The assert said the same
/// thing every time; only the first few carried information.
///
/// Rules: every assert site (file and line) is forwarded the first <see cref="ForwardFirst"/> times, then once per
/// <see cref="ForwardEvery"/> so it stays visible in the log. A site never seen before always gets through, so this
/// can hide a repeat but never a new failure.
/// </summary>
internal sealed class AssertThrottlePolicy
{
    public const int ForwardFirst = 20;
    public const long ForwardEvery = 10_000;

    private sealed class Site
    {
        public long Total;
        public long SuppressedSinceReport;
    }

    private readonly Dictionary<string, Site> _sites = new Dictionary<string, Site>(StringComparer.Ordinal);
    private readonly object _lock = new object();

    public static string KeyFor(string? callerFile, string? callerMethod, int callerLine)
        => $"{callerFile ?? "?"}:{callerLine} {callerMethod ?? "?"}";

    /// <summary>Counts one failed assert at <paramref name="key"/>; true when it should be forwarded.</summary>
    public bool ShouldForward(string key, out long total)
    {
        lock (_lock)
        {
            if (!_sites.TryGetValue(key, out var site)) _sites[key] = site = new Site();
            total = ++site.Total;
            if (total <= ForwardFirst || total % ForwardEvery == 0) return true;
            site.SuppressedSinceReport++;
            return false;
        }
    }

    /// <summary>
    /// The busiest sites suppressed since the last call, or null when nothing was suppressed. Resets the per-report
    /// counts, so each summary describes one reporting period.
    /// </summary>
    public string? TakeSummary(int top = 3)
    {
        List<KeyValuePair<string, Site>> busiest;
        lock (_lock)
        {
            busiest = _sites.Where(kv => kv.Value.SuppressedSinceReport > 0)
                .OrderByDescending(kv => kv.Value.SuppressedSinceReport).Take(top)
                .Select(kv => new KeyValuePair<string, Site>(kv.Key, new Site { Total = kv.Value.Total, SuppressedSinceReport = kv.Value.SuppressedSinceReport }))
                .ToList();
            foreach (var site in _sites.Values) site.SuppressedSinceReport = 0;
        }
        if (busiest.Count == 0) return null;
        return "repeated asserts held back from the engine log: " +
               string.Join("; ", busiest.Select(kv => $"{kv.Key} x{kv.Value.SuppressedSinceReport:N0} (total {kv.Value.Total:N0})"));
    }
}
