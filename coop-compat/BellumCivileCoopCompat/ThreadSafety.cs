using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using HarmonyLib;

namespace BellumCivileCoopCompat
{
    // Both sides, whenever Bellum is loaded: Bellum's privy-council and council-incident lookup
    // caches are rebuilt lazily, and its party-size and army-food model postfixes read them from
    // Coop's parallel moving-party tick. Two threads rebuilding at once throw "An item with the
    // same key has already been added", and Coop skips that party's move for the frame.
    // Every method that touches those fields runs under one lock. A lock changes no result, only
    // when each thread may run. One lock for all of them, because the two caches call into each
    // other; Monitor is re-entrant, and nothing guarded waits on the worker threads.
    internal static class ThreadSafety
    {
        public static readonly IReadOnlyDictionary<string, string[]> SharedCaches = new Dictionary<string, string[]>
        {
            ["BellumCivile.Behaviors.PrivyCouncilBehavior"] = new[]
            {
                "_officeRecords", "_recordIndexesDirty", "_recordsByKingdomId", "_recordByOfficeByKingdomId", "_clanById",
                "_runtimeSnapshotsByKingdomId", "_runtimeUnavailableKingdomIds",
            },
            ["BellumCivile.Behaviors.CouncilIncidentBehavior"] = new[]
            {
                "_aspectIndexDirty", "_nextAspectIndexExpiryDay", "_aspectMultiplierByLookup", "_aspectMultiplierByKey", "_aspectExpiryDayByKey",
            },
        };

        private static readonly object Gate = new object();

        public static void Install(Harmony harmony)
        {
            var guarded = new List<string>();
            foreach (var kv in SharedCaches)
            {
                var type = AccessTools.TypeByName(kv.Key);
                if (type == null)
                {
                    SubModule.Log(kv.Key + " was not found; left alone.");
                    continue;
                }
                guarded.Add(type.Name + " (" + Guard(harmony, type, kv.Value) + " methods)");
            }
            if (guarded.Count > 0)
                SubModule.Log("Thread safety: locked " + string.Join(", ", guarded) + ".");
        }

        // Guards every method on the type (and its compiler-made nested types) whose IL reads or
        // writes one of the named fields.
        private static int Guard(Harmony harmony, Type type, IEnumerable<string> fieldNames)
        {
            var fields = new HashSet<FieldInfo>();
            foreach (var name in fieldNames)
            {
                var field = AccessTools.DeclaredField(type, name);
                if (field == null) SubModule.Log(type.Name + "." + name + " was not found; Bellum has changed.");
                else fields.Add(field);
            }
            if (fields.Count == 0)
                return 0;

            const BindingFlags all = BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            var prefix = new HarmonyMethod(typeof(ThreadSafety), nameof(Enter)) { priority = Priority.First };
            var finalizer = new HarmonyMethod(typeof(ThreadSafety), nameof(Exit));
            var count = 0;
            foreach (var t in new[] { type }.Concat(type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)))
            {
                if (t.ContainsGenericParameters)
                    continue;
                foreach (var method in t.GetMethods(all))
                {
                    if (method.IsAbstract || method.ContainsGenericParameters || method.GetMethodBody() == null || !Touches(method, fields))
                        continue;
                    try
                    {
                        harmony.Patch(method, prefix: prefix, finalizer: finalizer);
                        count++;
                    }
                    catch (Exception ex) { SubModule.Log(type.Name + "." + method.Name + " not locked: " + ex.GetBaseException().Message); }
                }
            }
            return count;
        }

        private static bool Touches(MethodBase method, HashSet<FieldInfo> fields)
        {
            try { return PatchProcessor.ReadMethodBody(method).Any(op => op.Value is FieldInfo f && fields.Contains(f)); }
            catch { return false; }
        }

        private static void Enter(out bool __state)
        {
            __state = false;
            Monitor.Enter(Gate, ref __state);
        }

        private static void Exit(bool __state)
        {
            if (__state)
                Monitor.Exit(Gate);
        }
    }
}
