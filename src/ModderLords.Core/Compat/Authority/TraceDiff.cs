using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ModderLords.Core.Compat.Authority;

/// <summary>One line of ModderLords.Compat-trace-{side}.jsonl, written by the game module's RootTracer (cumulative counts).</summary>
public sealed record TraceRecordDto(string Method, long Ran, long GatedSkips, double FirstSeen, double LastSeen, double T);

public enum TraceOutcome
{
    /// <summary>The verdict predicts where the root runs, and the trace agrees.</summary>
    Expected,
    /// <summary>The trace contradicts the verdict: it ran where the verdict says it should not, or a root with no server claim ran on clients only.</summary>
    FalseNegativeCandidate,
    /// <summary>An action-needed verdict whose root never fired on either side, so this session cannot judge it.</summary>
    Untestable,
    /// <summary>A root with no server/client claim that never fired.</summary>
    NeverExercised,
}

public sealed record TraceJoin(string Method, AuthorityVerdict Verdict, RootTrigger Trigger, long ServerRan, long ClientRan, long ClientGatedSkips,
    TraceOutcome Outcome, string Reason)
{
    /// <summary>Set by <see cref="TraceDiff.AnnotateErrorBursts"/>: Coop client errors logged in the 30 s window the root last fired in.</summary>
    public int ErrorsNearby { get; init; }
}

public sealed class TraceTotals
{
    public int Expected { get; set; }
    public int FalseNegativeCandidates { get; set; }
    public int Untestable { get; set; }
    public int NeverExercised { get; set; }
    /// <summary>expected / (expected + false-negative candidates); null when neither happened.</summary>
    public double? Soundness => Expected + FalseNegativeCandidates == 0 ? null : (double)Expected / (Expected + FalseNegativeCandidates);
    [JsonIgnore]
    public string SoundnessText => Soundness is { } s ? Math.Round(s * 100).ToString(CultureInfo.InvariantCulture) + "%" : "n/a";
}

public sealed class TraceReport
{
    public string ModuleId { get; init; } = "";
    public List<TraceJoin> Joins { get; init; } = new();
    public Dictionary<string, TraceTotals> Totals { get; init; } = new();
    /// <summary>Traced methods that fired but are not roots of the report (a stale recipe, or overload ids that differ).</summary>
    public List<string> Unmatched { get; init; } = new();

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() }, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public string Summary
    {
        get
        {
            var all = new TraceTotals();
            foreach (var t in Totals.Values) { all.Expected += t.Expected; all.FalseNegativeCandidates += t.FalseNegativeCandidates; all.Untestable += t.Untestable; all.NeverExercised += t.NeverExercised; }
            return $"{Joins.Count} root(s): {all.Expected} as predicted, {all.FalseNegativeCandidates} contradicted, {all.Untestable} untestable this session, {all.NeverExercised} never exercised; soundness {all.SoundnessText}";
        }
    }
}

/// <summary>
/// Compares the classifier's verdicts with what actually ran, from the trace files of one host + client session. The
/// expectation per verdict and trigger is written out in <see cref="Expect"/>; a root the session never exercised is
/// reported as such, not counted against the classifier.
/// </summary>
public static class TraceDiff
{
    /// <summary>Parses a jsonl trace; the last line per method wins (counts are cumulative).</summary>
    public static Dictionary<string, TraceRecordDto> ParseTrace(IEnumerable<string> lines)
    {
        var result = new Dictionary<string, TraceRecordDto>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonDocument doc;
            try { doc = JsonDocument.Parse(line); } catch (JsonException) { continue; }
            using (doc)
            {
                var root = doc.RootElement;
                if (!root.TryGetProperty("method", out var m)) continue;
                var method = m.GetString() ?? "";
                result[method] = new TraceRecordDto(method, Num(root, "ran"), Num(root, "gatedSkips"), Dbl(root, "firstSeen"), Dbl(root, "lastSeen"), Dbl(root, "t"));
            }
        }
        return result;

        static long Num(JsonElement e, string name) => e.TryGetProperty(name, out var p) && p.TryGetInt64(out var v) ? v : 0;
        static double Dbl(JsonElement e, string name) => e.TryGetProperty(name, out var p) && p.TryGetDouble(out var v) ? v : 0;
    }

    public static Dictionary<string, TraceRecordDto> ParseTraceFile(string path) => ParseTrace(File.ReadLines(path));

    /// <summary>What the verdict claims for each side: true = should run there, false = should not, null = no claim.</summary>
    public static (bool? server, bool? client) Expect(AuthorityVerdict verdict, RootTrigger trigger) => verdict switch
    {
        AuthorityVerdict.ServerOnly or AuthorityVerdict.NeedsStateSync when trigger is RootTrigger.Simulation or RootTrigger.Session => (true, false),
        AuthorityVerdict.ServerOnly or AuthorityVerdict.NeedsStateSync => (true, null),
        AuthorityVerdict.NeedsRelay => (null, true),
        AuthorityVerdict.PlayerStateUnsynced => (false, true),
        AuthorityVerdict.LeakingPostfix => (true, false),
        AuthorityVerdict.Both => (true, true),
        _ => (null, null),
    };

    private static bool ActionNeeded(AuthorityVerdict v) => v is AuthorityVerdict.ServerOnly or AuthorityVerdict.NeedsStateSync or AuthorityVerdict.NeedsRelay
        or AuthorityVerdict.PlayerStateUnsynced or AuthorityVerdict.LeakingPostfix or AuthorityVerdict.Both;

    public static TraceReport Compare(AuthorityReport report, IReadOnlyDictionary<string, TraceRecordDto> server, IReadOnlyDictionary<string, TraceRecordDto> client)
    {
        var result = new TraceReport { ModuleId = report.ModuleId };
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in report.Roots)
        {
            // A UI-registering handler is not gated itself; its server work is gated in its place.
            var ids = r.GateInstead is { Count: > 0 } g ? g : [r.Root.Method];
            long sRan = 0, cRan = 0, cSkips = 0;
            foreach (var id in ids)
            {
                seen.Add(id);
                if (server.TryGetValue(id, out var s)) sRan += s.Ran;
                if (client.TryGetValue(id, out var c)) { cRan += c.Ran; cSkips += c.GatedSkips; }
            }
            seen.Add(r.Root.Method);
            var (outcome, reason) = Judge(r.Verdict, r.Root.Trigger, sRan, cRan, cSkips);
            result.Joins.Add(new TraceJoin(r.Root.Method, r.Verdict, r.Root.Trigger, sRan, cRan, cSkips, outcome, reason));
            var key = r.Verdict.ToString();
            if (!result.Totals.TryGetValue(key, out var t)) result.Totals[key] = t = new TraceTotals();
            switch (outcome)
            {
                case TraceOutcome.Expected: t.Expected++; break;
                case TraceOutcome.FalseNegativeCandidate: t.FalseNegativeCandidates++; break;
                case TraceOutcome.Untestable: t.Untestable++; break;
                default: t.NeverExercised++; break;
            }
        }
        result.Unmatched.AddRange(server.Keys.Concat(client.Keys).Distinct(StringComparer.Ordinal).Where(k => !seen.Contains(k)).OrderBy(k => k, StringComparer.Ordinal));
        result.Joins.Sort((a, b) => a.Outcome != b.Outcome ? a.Outcome.CompareTo(b.Outcome) : string.CompareOrdinal(a.Method, b.Method));
        return result;
    }

    private static (TraceOutcome, string) Judge(AuthorityVerdict verdict, RootTrigger trigger, long sRan, long cRan, long cSkips)
    {
        var (expectServer, expectClient) = Expect(verdict, trigger);
        var fired = sRan + cRan + cSkips > 0;
        if (!fired)
            return ActionNeeded(verdict) ? (TraceOutcome.Untestable, "never fired on either side this session") : (TraceOutcome.NeverExercised, "never fired");

        var problems = new List<string>();
        if (expectServer == true && sRan == 0) problems.Add("expected on the server, never ran there");
        if (expectServer == false && sRan > 0) problems.Add($"ran {sRan}x on the server");
        if (expectClient == false && cRan > 0) problems.Add($"ran {cRan}x on a client (gate skipped {cSkips})");
        if (expectClient == true && cRan == 0) problems.Add("expected on a client, never ran there");
        // No claim at all, yet it is simulation code that runs on clients while the server never touches it.
        if (expectServer is null && expectClient is null && trigger is RootTrigger.Simulation or RootTrigger.Session && cRan > 0 && sRan == 0)
            problems.Add($"no verdict claim, but {trigger.ToString().ToLowerInvariant()} code ran {cRan}x on a client and never on the server");

        if (problems.Count > 0) return (TraceOutcome.FalseNegativeCandidate, string.Join("; ", problems));
        if (expectServer is null && expectClient is null) return (TraceOutcome.Expected, $"no claim; server {sRan}, client {cRan}");
        return (TraceOutcome.Expected, $"server {sRan}, client {cRan}" + (cSkips > 0 ? $" (gate skipped {cSkips})" : ""));
    }

    private static readonly Regex CoopTimestamp = new(@"^\s*\[?(\d{1,2}):(\d{2}):(\d{2})(?:[.,](\d{1,3}))?", RegexOptions.Compiled);

    /// <summary>
    /// Buckets Coop client-log error lines (any line containing "ERR" or "Error") into 30 s windows by wall-clock time,
    /// and annotates each join with the count in the window its root last fired in. <paramref name="clientLogStartedAt"/>
    /// is the wall-clock time the client's trace clock started (the compat client log's first line carries it).
    /// </summary>
    public static TraceReport AnnotateErrorBursts(TraceReport report, IEnumerable<string> coopClientLogLines, TimeSpan clientLogStartedAt,
        IReadOnlyDictionary<string, TraceRecordDto> client, double windowSeconds = 30)
    {
        var buckets = new Dictionary<long, int>();
        foreach (var line in coopClientLogLines)
        {
            if (!line.Contains("ERR", StringComparison.Ordinal) && !line.Contains("Error", StringComparison.Ordinal)) continue;
            var m = CoopTimestamp.Match(line);
            if (!m.Success) continue;
            var t = new TimeSpan(0, int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value),
                m.Groups[4].Success ? int.Parse(m.Groups[4].Value.PadRight(3, '0')) : 0);
            var since = (t - clientLogStartedAt).TotalSeconds;
            if (since < 0) since += 86400;
            var bucket = (long)Math.Floor(since / windowSeconds);
            buckets[bucket] = buckets.TryGetValue(bucket, out var n) ? n + 1 : 1;
        }
        var joins = report.Joins.Select(j =>
        {
            if (!client.TryGetValue(j.Method, out var rec) || rec.LastSeen <= 0) return j;
            var bucket = (long)Math.Floor(rec.LastSeen / windowSeconds);
            return buckets.TryGetValue(bucket, out var n) ? j with { ErrorsNearby = n } : j;
        }).ToList();
        return new TraceReport { ModuleId = report.ModuleId, Joins = joins, Totals = report.Totals, Unmatched = report.Unmatched };
    }
}
