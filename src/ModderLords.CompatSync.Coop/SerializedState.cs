using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using HarmonyLib;

namespace ModderLords.CompatSync.Coop;

/// <summary>
/// Makes a mod's lazily rebuilt lookup state safe to reach from the campaign's worker threads, by running every method
/// that touches that state under one lock.
/// <para>
/// Bannerlord ticks moving parties in parallel, and a mod's model postfix runs on those threads. A mod that keeps a
/// "dirty" flag and rebuilds dictionaries on first use is not thread-safe: two threads both see the flag set, both
/// clear and refill, and the second Add throws "An item with the same key has already been added" (or a reader sees a
/// dictionary mid-rebuild). Free of game types on purpose, so the test project runs it with real Harmony.
/// </para>
/// <para>
/// One lock for every guarded method: guarded types call into each other (Bellum's incident index invalidates the
/// council cache), and separate locks taken in opposite orders could deadlock. Monitor is re-entrant, so a guarded
/// method calling another is fine. Nothing guarded waits on worker threads, so the lock cannot deadlock against the
/// parallel tick itself.
/// </para>
/// </summary>
public static class SerializedState
{
    private static readonly object Gate = new object();
    private static readonly HashSet<MethodBase> Guarded = new HashSet<MethodBase>();

    /// <summary>
    /// Guards every method declared on <paramref name="type"/> or its nested (compiler-generated) types whose IL reads
    /// or writes one of the named fields of <paramref name="type"/>. Returns how many methods are guarded; a named field
    /// that does not exist is reported through <paramref name="warn"/>, since it means the mod changed.
    /// </summary>
    public static int Guard(Harmony harmony, Type type, IReadOnlyCollection<string> fieldNames, Action<string> warn)
    {
        var fields = new HashSet<FieldInfo>();
        foreach (var name in fieldNames)
        {
            var field = AccessTools.DeclaredField(type, name);
            if (field == null) warn($"{type.Name}.{name} not found");
            else fields.Add(field);
        }
        if (fields.Count == 0) return 0;

        var prefix = new HarmonyMethod(typeof(SerializedState), nameof(Enter)) { priority = Priority.First };
        var finalizer = new HarmonyMethod(typeof(SerializedState), nameof(Exit));
        var count = 0;
        foreach (var method in Candidates(type))
        {
            if (!Touches(method, fields)) continue;
            lock (Guarded) { if (!Guarded.Add(method)) { count++; continue; } }
            try { harmony.Patch(method, prefix: prefix, finalizer: finalizer); count++; }
            catch (Exception ex)
            {
                lock (Guarded) Guarded.Remove(method);
                warn($"{type.Name}.{method.Name} not guarded: {ex.GetBaseException().Message}");
            }
        }
        return count;
    }

    private static IEnumerable<MethodBase> Candidates(Type type)
    {
        const BindingFlags all = BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        foreach (var t in new[] { type }.Concat(type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)))
        {
            if (t.ContainsGenericParameters) continue;
            foreach (var m in t.GetMethods(all))
                if (!m.IsAbstract && !m.ContainsGenericParameters && m.GetMethodBody() != null) yield return m;
        }
    }

    private static bool Touches(MethodBase method, HashSet<FieldInfo> fields)
    {
        try
        {
            return PatchProcessor.ReadMethodBody(method).Any(op => op.Value is FieldInfo f && fields.Contains(f));
        }
        catch { return false; }
    }

    private static void Enter(out bool __state)
    {
        __state = false;
        Monitor.Enter(Gate, ref __state);
    }

    private static void Exit(bool __state)
    {
        if (__state) Monitor.Exit(Gate);
    }
}
