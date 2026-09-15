using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace ModderLords.CompatSync.Coop;

/// <summary>
/// The Harmony half of generated recipes (schema v2). Free of game and Coop types on purpose, so the test project can
/// run it against plain methods with real Harmony instead of a game launch.
/// <list type="bullet">
/// <item>Handlers: an event handler's body is skipped on clients (a prefix that returns false there).</item>
/// <item>Unpatch: a mod's postfix/finalizer is removed from a target Coop skips on clients, because Harmony runs
/// postfixes even when a prefix skipped the original. A postfix the mod has not attached yet is kept pending and
/// retried: TAOM attaches its diplomacy patches in OnGameInitializationFinished, after the recipe first arrives.</item>
/// </list>
/// </summary>
public sealed class RecipeGates
{
    private static Func<bool> s_isClient = () => false;
    private static readonly Dictionary<string, long[]> s_counts = new Dictionary<string, long[]>(StringComparer.Ordinal);

    private readonly Harmony _harmony;
    private readonly Func<bool> _isClient;
    private readonly Action<string> _warn;
    private readonly Action<string> _info;
    private readonly HashSet<string> _gated = new HashSet<string>(StringComparer.Ordinal);
    private readonly List<(string target, string patch)> _pending = new List<(string target, string patch)>();

    public RecipeGates(string harmonyId, Func<bool> isClient, Action<string> warn, Action<string>? info = null)
    {
        _harmony = new Harmony(harmonyId);
        _isClient = isClient;
        _warn = warn;
        _info = info ?? (_ => { });
        s_isClient = isClient;
    }

    /// <summary>"Ns.Type::Method" → (type, method); false when the id has no separator.</summary>
    public static bool Split(string id, out string type, out string method)
    {
        var i = id.IndexOf("::", StringComparison.Ordinal);
        type = i > 0 ? id.Substring(0, i) : "";
        method = i > 0 ? id.Substring(i + 2) : "";
        return i > 0 && method.Length > 0;
    }

    /// <summary>Every non-abstract method of that name declared on the type (overloads share an id).</summary>
    private static List<MethodBase> Resolve(string id)
    {
        if (!Split(id, out var typeName, out var name)) return new List<MethodBase>();
        var type = AccessTools.TypeByName(typeName);
        if (type is null) return new List<MethodBase>();
        return AccessTools.GetDeclaredMethods(type).Where(m => m.Name == name && !m.IsAbstract).Cast<MethodBase>().ToList();
    }

    /// <summary>Installs the client-skip prefix on each handler (idempotent). Returns how many were gated and how many were not found.</summary>
    public (int applied, int missing) SkipHandlers(IEnumerable<string> handlerIds)
    {
        int applied = 0, missing = 0;
        var prefix = new HarmonyMethod(typeof(RecipeGates).GetMethod(nameof(ClientSkipPrefix), BindingFlags.Static | BindingFlags.Public));
        foreach (var id in handlerIds)
        {
            if (_gated.Contains(id)) continue;
            var methods = Resolve(id);
            if (methods.Count == 0) { missing++; _warn("recipe: handler not found: " + id); continue; }
            try
            {
                foreach (var m in methods) _harmony.Patch(m, prefix: prefix);
                _gated.Add(id);
                applied++;
            }
            catch (Exception ex) { missing++; _warn("recipe: could not gate handler " + id + ": " + ex.GetBaseException().Message); }
        }
        return (applied, missing);
    }

    /// <summary>The handler's body runs on the server and nowhere else. Every call is counted, so the log can show the gates firing.</summary>
    public static bool ClientSkipPrefix(MethodBase __originalMethod)
    {
        var client = s_isClient();
        var key = (__originalMethod?.DeclaringType?.Name ?? "?") + "." + (__originalMethod?.Name ?? "?");
        lock (s_counts)
        {
            if (!s_counts.TryGetValue(key, out var c)) s_counts[key] = c = new long[2];
            c[client ? 1 : 0]++;
        }
        return !client;
    }

    /// <summary>"handler gates: Type.Method ran X, skipped Y; …" for the busiest handlers, or a note that none has fired yet.</summary>
    public static string CountsSummary(int top = 5)
    {
        lock (s_counts)
        {
            if (s_counts.Count == 0) return "handler gates: none has fired yet";
            var ran = s_counts.Values.Sum(c => c[0]);
            var skipped = s_counts.Values.Sum(c => c[1]);
            var busiest = s_counts.OrderByDescending(kv => kv.Value[0] + kv.Value[1]).Take(top)
                .Select(kv => $"{kv.Key} ran {kv.Value[0]}, skipped {kv.Value[1]}");
            return $"handler gates: {s_counts.Count} handler(s) fired, {ran} run(s), {skipped} skip(s) ({string.Join("; ", busiest)})";
        }
    }

    /// <summary>Postfixes still waiting for their mod to attach them.</summary>
    public int PendingCount => _pending.Count;

    /// <summary>
    /// On a client, detaches each listed postfix/finalizer from its target. On the server nothing changes. Returns how
    /// many were removed and how many were not attached; those are kept and tried again by <see cref="RetryPending"/>.
    /// </summary>
    public (int removed, int notAttached) RemovePostfixes(IEnumerable<(string target, string patch)> entries)
    {
        if (!_isClient()) return (0, 0);
        int removed = 0, notAttached = 0;
        foreach (var entry in entries)
        {
            var n = TryRemove(entry.target, entry.patch);
            if (n > 0) { removed += n; _pending.Remove(entry); continue; }
            notAttached++;
            if (!_pending.Contains(entry))
            {
                _pending.Add(entry);
                _warn("recipe: postfix not attached yet, will retry: " + entry.patch + " on " + entry.target);
            }
        }
        return (removed, notAttached);
    }

    /// <summary>Called from the tick: removes pending postfixes once their mod has attached them. Returns how many were removed.</summary>
    public int RetryPending()
    {
        if (_pending.Count == 0 || !_isClient()) return 0;
        var removed = 0;
        foreach (var entry in _pending.ToList())
        {
            var n = TryRemove(entry.target, entry.patch);
            if (n == 0) continue;
            removed += n;
            _pending.Remove(entry);
            _info("recipe: leaking postfix removed once attached: " + entry.patch + " on " + entry.target);
        }
        return removed;
    }

    private int TryRemove(string target, string patch)
    {
        if (!Split(patch, out var patchType, out var patchName)) return 0;
        var removed = 0;
        foreach (var original in Resolve(target))
        {
            var info = Harmony.GetPatchInfo(original);
            if (info is null) continue;
            foreach (var p in info.Postfixes.Concat(info.Finalizers).ToList())
            {
                if (p.PatchMethod.Name != patchName || p.PatchMethod.DeclaringType?.FullName != patchType) continue;
                _harmony.Unpatch(original, p.PatchMethod);
                removed++;
            }
        }
        return removed;
    }
}
