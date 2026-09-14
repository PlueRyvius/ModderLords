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
/// postfixes even when a prefix skipped the original.</item>
/// </list>
/// </summary>
public sealed class RecipeGates
{
    private static Func<bool> s_isClient = () => false;

    private readonly Harmony _harmony;
    private readonly Func<bool> _isClient;
    private readonly Action<string> _warn;
    private readonly HashSet<string> _gated = new HashSet<string>(StringComparer.Ordinal);

    public RecipeGates(string harmonyId, Func<bool> isClient, Action<string> warn)
    {
        _harmony = new Harmony(harmonyId);
        _isClient = isClient;
        _warn = warn;
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

    /// <summary>The handler's body runs on the server and nowhere else.</summary>
    public static bool ClientSkipPrefix() => !s_isClient();

    /// <summary>
    /// On a client, detaches each listed postfix/finalizer from its target. On the server nothing changes. Returns how
    /// many were removed and how many were not attached (the mod may not have patched yet, or patched something else).
    /// </summary>
    public (int removed, int notAttached) RemovePostfixes(IEnumerable<(string target, string patch)> entries)
    {
        if (!_isClient()) return (0, 0);
        int removed = 0, notAttached = 0;
        foreach (var (target, patch) in entries)
        {
            if (!Split(patch, out var patchType, out var patchName)) { notAttached++; continue; }
            var found = false;
            foreach (var original in Resolve(target))
            {
                var info = Harmony.GetPatchInfo(original);
                if (info is null) continue;
                foreach (var p in info.Postfixes.Concat(info.Finalizers).ToList())
                {
                    if (p.PatchMethod.Name != patchName || p.PatchMethod.DeclaringType?.FullName != patchType) continue;
                    _harmony.Unpatch(original, p.PatchMethod);
                    found = true;
                    removed++;
                }
            }
            if (!found) { notAttached++; _warn("recipe: postfix not attached, nothing removed: " + patch + " on " + target); }
        }
        return (removed, notAttached);
    }
}
