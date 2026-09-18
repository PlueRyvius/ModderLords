using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using ModderLords.Operations;
using Newtonsoft.Json.Linq;

namespace ModderLords.CompatSync.Coop.Operations;

/// <summary>
/// Re-checks, inside the game, the method surfaces the maintainer captured offline. This is the load-bearing half of
/// pinning a contract to what it patches rather than to the file that carries it: a provider may ship any number of
/// unrelated changes, but the methods an adapter rewrites must be exactly the ones that were reviewed.
/// </summary>
internal static class TargetSurfaceCheck
{
    /// <summary>
    /// Null when every surface matches; otherwise the first disagreement, worded for a launch refusal. Callers are
    /// expected to refuse rather than to degrade: an adapter whose target has moved is no longer reviewed code.
    /// </summary>
    public static string? Problem(JObject contract)
    {
        var surfaces = contract["TargetSurfaces"] as JArray;
        var targets = ((JArray?)contract["Targets"] ?? new JArray()).Values<string>().ToArray();
        var id = (string?)contract["Id"] ?? "contract";
        if (surfaces == null || surfaces.Count == 0)
            return id + " installs an adapter but pins no method surfaces";

        foreach (var target in targets)
        {
            var surface = surfaces.OfType<JObject>().FirstOrDefault(s => (string?)s["Method"] == target);
            if (surface == null) return id + " has no captured surface for " + target;

            var method = Resolve(target);
            if (method == null) return "Patched method is missing: " + target;

            string actual;
            try { actual = MethodSurface.Of(method); }
            catch (Exception ex) { return "Patched method cannot be read (" + ex.GetType().Name + "): " + target; }

            var expected = (string?)surface["BodyHash"];
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                return "Patched method has changed since it was reviewed: " + target;
        }
        return null;
    }

    private static MethodBase? Resolve(string identity)
    {
        var split = identity.Split(new[] { "::" }, 2, StringSplitOptions.None);
        if (split.Length != 2) return null;
        var type = AccessTools.TypeByName(split[0]);
        if (type == null) return null;
        // Declared-only: an inherited method of the same name is a different method, and patching it would not be
        // what was reviewed.
        return type.GetMethods(AccessTools.allDeclared).Cast<MethodBase>()
            .Concat(type.GetConstructors(AccessTools.allDeclared))
            .FirstOrDefault(m => m.Name == split[1]);
    }
}
