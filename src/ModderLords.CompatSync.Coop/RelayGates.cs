using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace ModderLords.CompatSync.Coop;

/// <summary>
/// Recipe schema v3, client side: a prefix on each relayed method sends the call to the server and lets the player's own
/// game run it too, so its screens and messages react. Nothing is sent on the server, or while the server's relay runs.
/// Harmony only (the sender is supplied), so the test project runs it against plain methods.
/// </summary>
public static class RelayGates
{
    [ThreadStatic] private static bool t_relaying;
    // Relayed methods can call each other (IG's patrol order reaches SellPrisoners). Only the outermost call is sent: the
    // server runs the whole thing, and sending the inner one too would apply it twice.
    [ThreadStatic] private static int t_depth;
    private static Func<bool> s_isClient = () => false;
    private static Action<string, object?[]>? s_send;
    private static readonly HashSet<string> s_patched = new HashSet<string>(StringComparer.Ordinal);
    private static readonly Dictionary<string, long> s_sent = new Dictionary<string, long>(StringComparer.Ordinal);

    public static void Configure(Func<bool> isClient, Action<string, object?[]> send)
    {
        s_isClient = isClient;
        s_send = send;
    }

    /// <summary>"Ns.Type::Method", the id recipes use (nested types with '+').</summary>
    public static string Id(MethodBase m) => (m.DeclaringType?.FullName ?? "?") + "::" + m.Name;

    /// <summary>Installs the relay prefix on each method (idempotent). Returns how many were armed and how many were not found.</summary>
    public static (int applied, int missing) Apply(Harmony harmony, IEnumerable<string> methodIds, Action<string> warn)
    {
        int applied = 0, missing = 0;
        var prefix = new HarmonyMethod(typeof(RelayGates).GetMethod(nameof(RelayPrefix), BindingFlags.Static | BindingFlags.Public));
        var finalizer = new HarmonyMethod(typeof(RelayGates).GetMethod(nameof(RelayFinalizer), BindingFlags.Static | BindingFlags.Public));
        foreach (var id in methodIds.Distinct(StringComparer.Ordinal))
        {
            if (s_patched.Contains(id)) continue;
            var methods = RecipeGates.Resolve(id);
            if (methods.Count != 1) { missing++; warn("relay: expected one method named " + id + ", found " + methods.Count); continue; }
            try
            {
                harmony.Patch(methods[0], prefix: prefix, finalizer: finalizer);
                s_patched.Add(id);
                applied++;
            }
            catch (Exception ex) { missing++; warn("relay: could not arm " + id + ": " + ex.GetBaseException().Message); }
        }
        return (applied, missing);
    }

    /// <summary>Marks this thread as running a relayed call, so the prefix does not send it again.</summary>
    public static IDisposable Relaying()
    {
        t_relaying = true;
        return new Scope();
    }

    private sealed class Scope : IDisposable
    {
        public void Dispose() => t_relaying = false;
    }

    public static bool RelayPrefix(MethodBase __originalMethod, object[] __args, out bool __state)
    {
        __state = false;
        if (t_relaying || s_send is null) return true;
        bool client;
        try { client = s_isClient(); } catch { client = false; }
        if (!client) return true;
        __state = true;
        if (t_depth++ > 0) return true;
        var id = Id(__originalMethod);
        try
        {
            s_send(id, __args);
            lock (s_sent) s_sent[id] = (s_sent.TryGetValue(id, out var n) ? n : 0) + 1;
        }
        catch { /* sending must never break the player's own action */ }
        return true;
    }

    /// <summary>Unwinds the nesting count, also when the relayed method throws (a finalizer returning void rethrows).</summary>
    public static void RelayFinalizer(bool __state)
    {
        if (__state && t_depth > 0) t_depth--;
    }

    public static long SentCount
    {
        get { lock (s_sent) return s_sent.Values.Sum(); }
    }

    public static string CountsSummary()
    {
        lock (s_sent)
            return "relays sent: " + string.Join(", ", s_sent.OrderByDescending(kv => kv.Value).Take(5).Select(kv => kv.Key.Substring(kv.Key.LastIndexOf('.') + 1) + "=" + kv.Value));
    }
}
