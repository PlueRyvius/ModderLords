using ModderLords.Core.Overlay;
using ModderLords.Core.Profiles;

namespace ModderLords.Coop.Launch;

/// <summary>
/// Refuses to start the server with the BUTR loader stack's code loaded.
///
/// Harmony, ButterLib, UIExtenderEx and MCM are client-side infrastructure. Their DLLs live in the game's client
/// bins and pull in a runtime stack the DedicatedServer package simply does not contain - Mono.Cecil, the MonoMod
/// assemblies, ButterLib's WinForms crash renderer. Set any of them to Run or AsShipped and the engine dies during
/// assembly load with exit code -532462766 (0xE0434352, an unhandled managed exception), roughly five seconds in,
/// before any module is initialised and before anything is written to disk.
///
/// <see cref="ServerRole.DependencyOnly"/> is the setting that works, and is what the launcher's own automatic
/// compatibility picks: the id and version stay in the load order for the Coop handshake, and no code is loaded.
///
/// Measured 2026-09-19, one profile, four launches, changing one module each time:
/// <list type="bullet">
/// <item>ButterLib + Harmony + MCM as code -> crash</item>
/// <item>Harmony + MCM as code -> crash</item>
/// <item>MCM as code -> crash</item>
/// <item>all four DependencyOnly -> world generated, server ran</item>
/// </list>
/// Each of those cost a full launch to discover, which is what this exists to stop.
/// </summary>
public static class ClientStackPolicy
{
    /// <summary>Measured to crash this server package. Refused outright.</summary>
    private static readonly string[] Fatal =
        { "Bannerlord.Harmony", "Bannerlord.ButterLib", "Bannerlord.MBOptionScreen" };

    /// <summary>
    /// Same family and same client-only bins, but never actually observed running on a server - every measured
    /// launch already had it DependencyOnly. Warned rather than refused: blocking a combination nobody has seen
    /// fail would be guessing, and if someone's setup does work this must not be what breaks it.
    /// </summary>
    private const string Suspect = "Bannerlord.UIExtenderEx";

    private static bool LoadsCode(ServerRole role) => role is ServerRole.Run or ServerRole.AsShipped;

    /// <summary>The blocking problem, or null when nothing in the profile asks for this stack's code.</summary>
    public static string? Problem(Profile profile)
    {
        var named = profile.EnabledMods
            .Where(m => LoadsCode(m.Role) && Fatal.Contains(m.Id, StringComparer.OrdinalIgnoreCase))
            .Select(m => m.Id).ToList();
        if (named.Count == 0) return null;

        return $"{string.Join(", ", named)} {(named.Count == 1 ? "is" : "are")} set to load code on the server. " +
               "That stack is client-side: it needs Mono.Cecil and the MonoMod assemblies, which the DedicatedServer " +
               "package does not ship, and the engine dies during assembly load before any module starts. " +
               $"Set {(named.Count == 1 ? "it" : "them")} to Dependency-only. If you need a mod whose setup runs " +
               "through Harmony at campaign creation - RBM Campaign's economy pass, for instance - create that " +
               "campaign in the real game with the same load order and bring it across with Import client save.";
    }

    /// <summary>The non-blocking note, or null. Separate from <see cref="Problem"/> so a warning can never refuse a launch.</summary>
    public static string? Warning(Profile profile)
    {
        var ui = profile.EnabledMods.FirstOrDefault(m =>
            LoadsCode(m.Role) && m.Id.Equals(Suspect, StringComparison.OrdinalIgnoreCase));
        return ui is null ? null
            : $"WARNING {Suspect} is set to load code on the server. It is part of the same client-side stack as " +
              "Harmony and ButterLib, which crash the engine in that state, and it extends the game's UI - which a " +
              "headless server does not have. Dependency-only is almost certainly what you want.";
    }
}
