using System;
using System.Collections.Generic;
using System.Linq;

namespace ModderLords.Operations;

public static class ModuleOrderAttestation
{
    // These are explicit platform/test exclusions, not the mod-controlled IsOfficial flag.
    private static readonly HashSet<string> PlatformModules = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    { "Native", "SandBoxCore", "Sandbox", "CustomBattle", "StoryMode", "BirthAndDeath", "DedicatedServer.Windows", "ModderLords.Compat", "ModularSmithing2" };
    public static bool Validate(IEnumerable<string> selected, IEnumerable<string> engineOrder, out string reason)
    {
        var expected = selected.ToArray(); var actual = engineOrder.ToArray();
        if (actual.Any(string.IsNullOrEmpty) || actual.Distinct(StringComparer.OrdinalIgnoreCase).Count() != actual.Length)
        { reason = "Engine module order contains missing or duplicate identities"; return false; }
        // A platform module explicitly present in the plan still participates in order validation.
        var compared = actual.Where(id => !PlatformModules.Contains(id) || expected.Contains(id, StringComparer.OrdinalIgnoreCase)).ToArray();
        if (!expected.SequenceEqual(compared, StringComparer.OrdinalIgnoreCase))
        { reason = "Engine module order differs from the resolved compatibility plan"; return false; }
        reason = ""; return true;
    }
}
