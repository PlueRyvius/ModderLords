using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace ModderLords.Operations;

/// <summary>Compare the remote plan with locally prepared inputs, and rehash files before any adapter installs.</summary>
public static class InputAttestation
{
    public static bool Validate(JObject plan, JObject local, string side, Func<string, string> hashFile, out string reason)
    {
        reason = "Local launch inputs are incomplete";
        if (side != "Client" && side != "Server") return false;
        if (local["Gaps"] is not JArray gaps || gaps.Count != 0 || local["Files"] is not JArray files || local["Values"] is not JArray values) return false;
        if (plan["ModuleOrder"] is not JArray order || !JToken.DeepEquals(order, local["ModuleOrder"]))
        { reason = "Selected module order differs from the session plan"; return false; }
        if (plan["Fingerprints"] is not JArray expected) return false;
        if (expected.Any(f => f is not JObject)) return false;
        foreach (var fingerprint in expected.OfType<JObject>())
        {
            var fingerprintSide = (string?)fingerprint["Side"];
            if (fingerprintSide != null && fingerprintSide != side) continue;
            var module = (string?)fingerprint["Module"]; var name = (string?)fingerprint["Name"];
            var expectedHash = (string?)fingerprint["Sha256"];
            if (module == null || name == null || !PlanIntegrity.IsHash(expectedHash)) return false;
            bool Matches(JToken candidate) => (string?)candidate["Module"] == module && (string?)candidate["Name"] == name && (string?)candidate["Side"] == fingerprintSide;
            if (module == "$environment")
            {
                var observed = values.Where(Matches).ToArray();
                if (observed.Length != 1 || (string?)observed[0]["Sha256"] != expectedHash)
                { reason = "Environment fingerprint differs: " + name; return false; }
                continue;
            }
            var candidates = files.OfType<JObject>().Where(f => f["Fingerprint"] is JObject fp && Matches(fp)).ToArray();
            if (candidates.Length == 0) { reason = "Missing local launch input: " + module + "/" + name; return false; }
            foreach (var candidate in candidates)
            {
                var path = (string?)candidate["Path"];
                try
                {
                    // Paths come only from the local preparation file, never from the remote message.
                    if (string.IsNullOrEmpty(path) || !Path.IsPathRooted(path) || (string?)candidate["Fingerprint"]?["Sha256"] != expectedHash || hashFile(path!) != expectedHash)
                    { reason = "Input changed or differs: " + module + "/" + name; return false; }
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                { reason = "Local input is unreadable: " + module + "/" + name; return false; }
            }
        }
        reason = ""; return true;
    }
}
