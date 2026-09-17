using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace ModderLords.Operations;

public static class LoadedAssemblyAttestation
{
    /// <summary>Checks every loaded copy of a fingerprinted assembly. Dependencies not yet loaded remain subject to contract checks.</summary>
    public static bool Validate(JArray fingerprints, string side, IEnumerable<KeyValuePair<string, string>> loaded,
        Func<string, string> hashFile, out string reason)
    {
        reason = "";
        var expected = fingerprints.OfType<JObject>().Where(f =>
            (f["Side"]?.Type == JTokenType.Null || f["Side"] == null || (string?)f["Side"] == side)
            && ((string?)f["Name"])?.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) == true)
            .GroupBy(f => Path.GetFileNameWithoutExtension((string)f["Name"]!), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(f => (string?)f["Sha256"]).Distinct().ToArray(), StringComparer.OrdinalIgnoreCase);
        foreach (var assembly in loaded)
        {
            if (!expected.TryGetValue(assembly.Key, out var hashes)) continue;
            try
            {
                if (hashes.Length != 1 || !PlanIntegrity.IsHash(hashes[0]) || string.IsNullOrEmpty(assembly.Value) || hashFile(assembly.Value) != hashes[0])
                { reason = "Loaded assembly differs from selected input: " + assembly.Key; return false; }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            { reason = "Loaded assembly cannot be verified: " + assembly.Key; return false; }
        }
        return true;
    }
}
