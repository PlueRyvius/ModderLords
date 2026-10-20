using System;
using System.Collections.Generic;
using System.Linq;

namespace ModderLords.Coop.Config;

/// <summary>
/// Bannerlord's campaign difficulty presets, for the Gameplay tab: picking one sets every difficulty value in
/// mod-config.json the way the game's own campaign options screen does (CampaignOptionsDifficultyPresets in game
/// 1.4.8: Freebooter, Warrior, Bannerlord). Freebooter is every option at Very easy, Warrior at Easy, Bannerlord at
/// Realistic, except stealth and disguise, which the game keeps at Easy for Bannerlord.
/// <para>
/// Player received damage is no longer a campaign option in 1.4.8 (it moved to the game's Options), so the game's
/// presets do not set it. Coop still reads it from mod-config.json; it follows the same pattern here, so Bannerlord
/// is Realistic throughout. Birth and death, clan perk auto-allocation and all of Coop's own options are not
/// difficulty and are never touched.
/// </para>
/// </summary>
public static class DifficultyPresets
{
    public const string Custom = "Custom";

    /// <summary>Preset names, easiest first.</summary>
    public static readonly string[] Names = ["Freebooter", "Warrior", "Bannerlord"];

    /// <summary>Each mod-config key a preset sets, with its value for each preset in <see cref="Names"/> order.</summary>
    public static readonly (string Path, string[] Values)[] Settings =
    [
        ("difficulty.playerReceivedDamage", ["VeryEasy", "Easy", "Realistic"]),
        ("difficulty.playerTroopsReceivedDamage", ["VeryEasy", "Easy", "Realistic"]),
        ("difficulty.combatAIDifficulty", ["VeryEasy", "Easy", "Realistic"]),
        ("difficulty.recruitmentDifficulty", ["VeryEasy", "Easy", "Realistic"]),
        ("difficulty.playerMapMovementSpeed", ["VeryEasy", "Easy", "Realistic"]),
        ("difficulty.stealthAndDisguiseDifficulty", ["VeryEasy", "Easy", "Easy"]),
        ("difficulty.persuasionSuccessChance", ["VeryEasy", "Easy", "Realistic"]),
        ("difficulty.clanMemberDeathChance", ["VeryEasy", "Easy", "Realistic"]),
        ("difficulty.battleDeath", ["VeryEasy", "Easy", "Realistic"]),
    ];

    /// <summary>The values <paramref name="preset"/> sets; empty for Custom or an unknown name.</summary>
    public static IReadOnlyList<(string Path, string Value)> ValuesFor(string preset)
    {
        var index = Array.FindIndex(Names, n => n.Equals(preset, StringComparison.OrdinalIgnoreCase));
        return index < 0 ? [] : Settings.Select(s => (s.Path, s.Values[index])).ToList();
    }

    /// <summary>
    /// The preset the current values match, or <see cref="Custom"/>. <paramref name="valueOf"/> gives a key's value,
    /// or null when the file does not have it; a preset matches only when every one of its keys is there and agrees.
    /// </summary>
    public static string Detect(Func<string, string?> valueOf)
    {
        foreach (var name in Names)
            if (ValuesFor(name).All(s => string.Equals(valueOf(s.Path), s.Value, StringComparison.OrdinalIgnoreCase))) return name;
        return Custom;
    }
}
