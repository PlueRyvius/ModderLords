using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace ModderLords.CompatSync.Coop;

/// <summary>
/// Ground truth for the authority classifier: counts how often each of a mod's entry points actually runs on this
/// peer. A prefix on every listed method increments a counter and always returns true, so nothing changes in what the
/// mod does. Free of game and Coop types, like <see cref="RecipeGates"/>, so the test project runs it with real Harmony.
/// The prefix runs at <see cref="Priority.First"/>: a gate prefix that returns false skips later prefixes, and the
/// trace must see the call either way. A call on a client to a method that a handler gate skips is counted as a
/// gated skip rather than a run, because the gate's prefix is what decides the body never runs.
/// </summary>
public sealed class RootTracer
{
    private static readonly ConcurrentDictionary<MethodBase, long[]> s_counts = new ConcurrentDictionary<MethodBase, long[]>();
    private static readonly ConcurrentDictionary<MethodBase, double[]> s_seen = new ConcurrentDictionary<MethodBase, double[]>();
    private static readonly Stopwatch s_clock = Stopwatch.StartNew();
    private static Func<bool> s_isClient = () => false;
    private static HashSet<MethodBase> s_gated = new HashSet<MethodBase>();

    private readonly Harmony _harmony;
    private readonly Action<string> _warn;
    private readonly HashSet<string> _installed = new HashSet<string>(StringComparer.Ordinal);

    public RootTracer(string harmonyId, Func<bool> isClient, Action<string> warn)
    {
        _harmony = new Harmony(harmonyId);
        _warn = warn;
        s_isClient = isClient;
    }

    /// <summary>One method's counters since the process started (cumulative, never reset).</summary>
    public readonly struct TraceRecord
    {
        public TraceRecord(string methodId, long ran, long gatedSkips, double firstSeen, double lastSeen)
        {
            MethodId = methodId; Ran = ran; GatedSkips = gatedSkips; FirstSeen = firstSeen; LastSeen = lastSeen;
        }
        public string MethodId { get; }
        public long Ran { get; }
        public long GatedSkips { get; }
        /// <summary>Seconds since the tracer's clock started (process lifetime, not game time).</summary>
        public double FirstSeen { get; }
        public double LastSeen { get; }
    }

    /// <summary>Seconds since the tracer's clock started; the same clock the records use.</summary>
    public static double Now => s_clock.Elapsed.TotalSeconds;

    /// <summary>
    /// Installs the counting prefix on each listed method (idempotent per id). <paramref name="gatedHandlerIds"/> names
    /// the handlers a client gate skips, so their client calls are counted as skips. Returns how many ids were traced,
    /// how many were not found, and how many could not be patched (abstract, generic definitions, no body, or Harmony refused).
    /// </summary>
    public (int applied, int missing, int failed) Install(IEnumerable<string> rootIds, IEnumerable<string>? gatedHandlerIds = null)
    {
        var gated = new HashSet<MethodBase>(s_gated);
        foreach (var id in gatedHandlerIds ?? Enumerable.Empty<string>())
            foreach (var m in RecipeGates.Resolve(id)) gated.Add(m);
        s_gated = gated;

        int applied = 0, missing = 0, failed = 0;
        var prefix = new HarmonyMethod(typeof(RootTracer).GetMethod(nameof(TracePrefix), BindingFlags.Static | BindingFlags.Public)) { priority = Priority.First };
        foreach (var id in rootIds)
        {
            if (_installed.Contains(id)) continue;
            var methods = RecipeGates.Resolve(id).Where(Patchable).ToList();
            if (methods.Count == 0) { missing++; continue; }
            var ok = 0;
            foreach (var m in methods)
            {
                try { _harmony.Patch(m, prefix: prefix); ok++; }
                catch (Exception ex) { _warn("trace: could not trace " + id + ": " + ex.GetBaseException().Message); }
            }
            if (ok == 0) { failed++; continue; }
            _installed.Add(id);
            applied++;
        }
        return (applied, missing, failed);
    }

    private static bool Patchable(MethodBase m)
    {
        if (m.IsAbstract || m.ContainsGenericParameters) return false;
        if ((m.MethodImplementationFlags & (MethodImplAttributes.InternalCall | MethodImplAttributes.Native)) != 0) return false;
        if ((m.Attributes & MethodAttributes.PinvokeImpl) != 0) return false;
        try { return m.GetMethodBody() != null; } catch { return false; }
    }

    /// <summary>Counts the call; never changes the outcome.</summary>
    public static bool TracePrefix(MethodBase __originalMethod)
    {
        if (__originalMethod is null) return true;
        var counts = s_counts.GetOrAdd(__originalMethod, _ => new long[2]);
        var skipped = s_gated.Contains(__originalMethod) && s_isClient();
        System.Threading.Interlocked.Increment(ref counts[skipped ? 1 : 0]);
        var now = Now;
        var seen = s_seen.GetOrAdd(__originalMethod, _ => new[] { now, now });
        seen[1] = now;
        return true;
    }

    /// <summary>Every traced method that has fired, ids resolved here rather than in the hot path.</summary>
    public static List<TraceRecord> Snapshot()
    {
        var list = new List<TraceRecord>();
        foreach (var kv in s_counts)
        {
            var id = (kv.Key.DeclaringType?.FullName ?? "?") + "::" + kv.Key.Name;
            var seen = s_seen.TryGetValue(kv.Key, out var s) ? s : new[] { 0.0, 0.0 };
            list.Add(new TraceRecord(id, System.Threading.Interlocked.Read(ref kv.Value[0]), System.Threading.Interlocked.Read(ref kv.Value[1]), seen[0], seen[1]));
        }
        // Overloads share an id: merge them the way the classifier's ids do.
        return list.GroupBy(r => r.MethodId, StringComparer.Ordinal)
            .Select(g => new TraceRecord(g.Key, g.Sum(r => r.Ran), g.Sum(r => r.GatedSkips), g.Min(r => r.FirstSeen), g.Max(r => r.LastSeen)))
            .OrderBy(r => r.MethodId, StringComparer.Ordinal).ToList();
    }

    /// <summary>How many traced methods have fired at all.</summary>
    public static int FiredCount => s_counts.Count;

    /// <summary>"trace: N method(s) fired, R run(s), S gated skip(s) (busiest: …)" or a note that nothing fired.</summary>
    public static string CountsSummary(int top = 5)
    {
        var snap = Snapshot();
        if (snap.Count == 0) return "trace: no traced method has fired yet";
        var busiest = snap.OrderByDescending(r => r.Ran + r.GatedSkips).Take(top).Select(r => $"{Short(r.MethodId)} {r.Ran}/{r.GatedSkips}");
        return $"trace: {snap.Count} method(s) fired, {snap.Sum(r => r.Ran)} run(s), {snap.Sum(r => r.GatedSkips)} gated skip(s) (ran/skipped: {string.Join("; ", busiest)})";
    }

    private static string Short(string id)
    {
        var i = id.IndexOf("::", StringComparison.Ordinal);
        var type = i > 0 ? id.Substring(0, i) : id;
        var dot = type.LastIndexOf('.');
        return (dot >= 0 ? type.Substring(dot + 1) : type) + (i > 0 ? "." + id.Substring(i + 2) : "");
    }

    /// <summary>One JSON object per line, cumulative counts; the last line for a method id is the one that counts.</summary>
    public static string ToJsonLines(IEnumerable<TraceRecord> records, double at)
    {
        var sb = new System.Text.StringBuilder();
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        foreach (var r in records)
        {
            sb.Append("{\"t\":").Append(at.ToString("F1", inv))
              .Append(",\"method\":\"").Append(Escape(r.MethodId)).Append('"')
              .Append(",\"ran\":").Append(r.Ran.ToString(inv))
              .Append(",\"gatedSkips\":").Append(r.GatedSkips.ToString(inv))
              .Append(",\"firstSeen\":").Append(r.FirstSeen.ToString("F1", inv))
              .Append(",\"lastSeen\":").Append(r.LastSeen.ToString("F1", inv))
              .Append("}\n");
        }
        return sb.ToString();
    }

    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
