namespace ModderLords.Core.Modules;

/// <summary>
/// The modules TaleWorlds ship with the game, identified by id.
///
/// A module's own <c>&lt;ModuleType value="Official"/&gt;</c> is NOT evidence of anything: it is a line in a file the
/// mod author wrote, and community mods do set it — sometimes by copying an official module's manifest as a
/// template. Trusting it once meant a mod dropped into the game's Modules folder with that line disappeared from
/// the Mods tab entirely, with nothing said and no way to enable it. So membership is decided by this list.
/// </summary>
public static class OfficialModules
{
    /// <summary>The game will not start without these, so they are never presented as a choice.</summary>
    public static readonly IReadOnlySet<string> Required = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "Native", "SandBoxCore", "Sandbox", "SandBox" };

    /// <summary>Official modules a player can reasonably turn off. Some of them have to be off for coop.</summary>
    public static readonly IReadOnlySet<string> Optional = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "StoryMode", "CustomBattle", "BirthAndDeath", "Multiplayer", "FastMode" };

    /// <summary>Paid expansions. Coop's ModuleValidator rejects a client that has one enabled.</summary>
    public static readonly IReadOnlySet<string> Dlc = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "NavalDLC" };

    /// <summary>Every module the game ships, DLC included.</summary>
    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(Required.Concat(Optional).Concat(Dlc), StringComparer.OrdinalIgnoreCase);

    /// <summary>True for a module TaleWorlds ship. Community mods cannot join this set by claiming to.</summary>
    public static bool IsGameModule(string id) => All.Contains(id);

    public static bool IsRequired(string id) => Required.Contains(id);
    public static bool IsDlc(string id) => Dlc.Contains(id);
}
