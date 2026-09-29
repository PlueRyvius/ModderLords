using System;
using System.Collections.Generic;
using System.Linq;

namespace ModderLords.Operations;

public static class ModuleOrderAttestation
{
    // These are explicit platform/test exclusions, not the mod-controlled IsOfficial flag.
    private static readonly HashSet<string> PlatformModules = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    { "Native", "SandBoxCore", "Sandbox", "CustomBattle", "StoryMode", "BirthAndDeath", "DedicatedServer.Windows", "ModularSmithing2" };

    // Connection infrastructure is deliberately asymmetric: the dedicated host has its server-only compat
    // module, and Coop requires the shared compat module to load after Coop on a client but before Coop in the
    // dedicated-server overlay. Their relative positions therefore cannot attest gameplay compatibility.
    private static readonly HashSet<string> ConnectionInfrastructure = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    { "Coop", "CoopNightly", "ModderLords.Compat", "DedicatedServer.ModderLordsCompat" };

    public static string[] SessionOrder(IEnumerable<string> order) =>
        order.Where(id => !ConnectionInfrastructure.Contains(id)).ToArray();

    public static bool Validate(IEnumerable<string> selected, IEnumerable<string> engineOrder, out string reason)
    {
        var expected = SessionOrder(selected); var actual = engineOrder.ToArray();
        if (actual.Any(string.IsNullOrEmpty) || actual.Distinct(StringComparer.OrdinalIgnoreCase).Count() != actual.Length)
        { reason = "Engine module order contains missing or duplicate identities"; return false; }
        // A platform module explicitly present in the plan still participates in order validation.
        var compared = actual.Where(id => !ConnectionInfrastructure.Contains(id) &&
            (!PlatformModules.Contains(id) || expected.Contains(id, StringComparer.OrdinalIgnoreCase))).ToArray();
        if (!expected.SequenceEqual(compared, StringComparer.OrdinalIgnoreCase))
        { reason = "Engine module order differs from the resolved compatibility plan"; return false; }
        reason = ""; return true;
    }
}
