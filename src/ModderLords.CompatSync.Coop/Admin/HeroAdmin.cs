using System;
using System.Collections.Generic;
using System.Linq;
using GameInterface;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.Players;
using GameInterface.Services.Players.Data;
using ModderLords.CompatSync;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.CharacterDevelopment;
using TaleWorlds.CampaignSystem.Extensions;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Core;
using TaleWorlds.Library;

namespace ModderLords.CompatSync.Coop.Admin;

/// <summary>
/// Server only: reads and edits a player's hero for the Characters tab's editor.
/// <para>
/// Every change goes through the game's own methods (HeroDeveloper.AddAttribute / RemoveAttribute / AddFocus /
/// SetInitialSkillLevel, Hero.SetTraitLevel / ChangeHeroGold), which Coop already intercepts and replicates to every
/// client; Coop's own coop.debug.hero.boost_fighter edits a player's hero the same way. Nothing here writes a field
/// directly, so nothing bypasses that sync.
/// </para>
/// <para>
/// Skills are set with SetInitialSkillLevel: the value becomes exactly what the host typed and the hero's level is left
/// alone. Raising a skill through experience instead would level the hero up as a side effect.
/// </para>
/// </summary>
internal static class HeroAdmin
{
    /// <summary><c>modderlords.hero &lt;req&gt; &lt;steam id&gt;</c>: everything the editor shows for that player's hero.</summary>
    [CommandLineFunctionality.CommandLineArgumentFunction("hero", AdminWire.CommandGroup)]
    public static string HeroCommand(List<string> args) => PlayerAdmin.Run("hero", args, req =>
    {
        if (args.Count < 2) return AdminWire.Reply("hero", req, "Usage: modderlords.hero <request id> <steam id>");
        if (Find(args[1], out var player, out var hero, out var party) is { } missing) return AdminWire.Reply("hero", req, missing);
        return AdminWire.Reply("hero", req, null, Describe(player!, hero!, party));
    });

    /// <summary>
    /// <c>modderlords.edit &lt;req&gt; &lt;steam id&gt; &lt;token&gt;...</c> (<see cref="HeroEditWire"/>). Every token is checked
    /// before any is applied, so a bad one changes nothing. Refused while the player's party is in a battle or a siege,
    /// where a mission holds its own copy of the hero.
    /// </summary>
    [CommandLineFunctionality.CommandLineArgumentFunction("edit", AdminWire.CommandGroup)]
    public static string EditCommand(List<string> args) => PlayerAdmin.Run("edit", args, req =>
    {
        if (args.Count < 3) return AdminWire.Reply("edit", req, "Usage: modderlords.edit <request id> <steam id> <key=value>...");
        if (Find(args[1], out var player, out var hero, out var party) is { } missing) return AdminWire.Reply("edit", req, missing);
        var busy = PlayerAdmin.BusyReason(party);
        if (busy.Length > 0) return AdminWire.Reply("edit", req, $"{hero!.Name} is {busy}. Edit them once it is over.");

        var edits = new List<(HeroEdit Edit, Action Apply, Func<int> Read)>();
        foreach (var token in args.Skip(2))
        {
            if (!HeroEditWire.TryParse(token, out var edit, out var error)) return AdminWire.Reply("edit", req, error);
            if (Prepare(hero!, edit!, out var apply, out var read) is { } refused) return AdminWire.Reply("edit", req, refused);
            edits.Add((edit!, apply!, read!));
        }

        var changes = new List<object?>();
        var notApplied = new List<object?>();
        foreach (var (edit, apply, read) in edits)
        {
            var before = read();
            if (before == edit.Value) continue;
            apply();
            var after = read();
            var line = $"{edit.Kind}{(edit.Id.Length > 0 ? " " + edit.Id : "")} {before} -> {after}";
            if (after == edit.Value) changes.Add(line);
            else notApplied.Add($"{line} (asked for {edit.Value})");
        }
        Log.Info($"host edit of {hero!.Name} ({player!.ControllerId}): "
            + (changes.Count == 0 ? "nothing to change" : string.Join("; ", changes.Cast<string>()))
            + (notApplied.Count > 0 ? "; NOT applied: " + string.Join("; ", notApplied.Cast<string>()) : ""));

        var data = Describe(player, hero, party);
        data["changes"] = changes;
        data["notApplied"] = notApplied;
        return AdminWire.Reply("edit", req, null, data);
    });

    /// <summary>The player, hero and party behind a Steam id, or why they cannot be had.</summary>
    internal static string? Find(string steamId, out Player? player, out Hero? hero, out MobileParty? party)
    {
        hero = null;
        party = null;
        player = null;
        if (!ContainerProvider.TryResolve<IPlayerManager>(out var players)) return "Coop's player registry is not available yet.";
        if (!players.TryGetPlayer(steamId, out var p) || p is null) return $"No player with Steam id {steamId} is registered on this server.";
        player = p;
        hero = PlayerHeroes.HeroFor(p.HeroId);
        if (hero is null) return $"The hero of {steamId} ({p.HeroId}) is not in this world.";
        if (ContainerProvider.TryResolve<IObjectManager>(out var objects)) objects.TryGetObject(p.MobilePartyId, out party);
        return null;
    }

    /// <summary>Checks one edit against this hero and the game's own limits; on success, how to apply it and read it back.</summary>
    private static string? Prepare(Hero hero, HeroEdit edit, out Action? apply, out Func<int>? read)
    {
        apply = null;
        read = null;
        var model = Campaign.Current.Models.CharacterDevelopmentModel;
        var dev = hero.HeroDeveloper;
        var v = edit.Value;
        switch (edit.Kind)
        {
            case HeroEditKind.Gold:
                if (v < 0) return "Gold cannot be negative.";
                read = () => hero.Gold;
                apply = () => hero.ChangeHeroGold(v - hero.Gold);
                return null;
            case HeroEditKind.HitPoints:
                if (v < 1 || v > hero.MaxHitPoints) return $"Hit points must be 1 to {hero.MaxHitPoints}.";
                read = () => hero.HitPoints;
                apply = () => hero.HitPoints = v;
                return null;
            case HeroEditKind.UnspentAttributePoints:
                if (v < 0 || v > 1000) return "Unspent attribute points must be 0 to 1000.";
                read = () => dev.UnspentAttributePoints;
                apply = () => dev.UnspentAttributePoints = v;
                return null;
            case HeroEditKind.UnspentFocusPoints:
                if (v < 0 || v > 1000) return "Unspent focus points must be 0 to 1000.";
                read = () => dev.UnspentFocusPoints;
                apply = () => dev.UnspentFocusPoints = v;
                return null;
            case HeroEditKind.Attribute:
            {
                var attribute = Attributes.All.FirstOrDefault(a => a.StringId == edit.Id);
                if (attribute is null) return $"This world has no attribute \"{edit.Id}\".";
                if (v < 0 || v > model.MaxAttribute) return $"{attribute.Name} must be 0 to {model.MaxAttribute}.";
                read = () => hero.GetAttributeValue(attribute);
                apply = () =>
                {
                    var change = v - hero.GetAttributeValue(attribute);
                    if (change > 0) dev.AddAttribute(attribute, change, checkUnspentPoints: false);
                    else dev.RemoveAttribute(attribute, -change);
                };
                return null;
            }
            case HeroEditKind.Focus:
            {
                var skill = Skills.All.FirstOrDefault(s => s.StringId == edit.Id);
                if (skill is null) return $"This world has no skill \"{edit.Id}\".";
                if (v < 0 || v > model.MaxFocusPerSkill) return $"Focus in {skill.Name} must be 0 to {model.MaxFocusPerSkill}.";
                read = () => dev.GetFocus(skill);
                apply = () => dev.AddFocus(skill, v - dev.GetFocus(skill), checkUnspentFocusPoints: false);
                return null;
            }
            case HeroEditKind.Skill:
            {
                var skill = Skills.All.FirstOrDefault(s => s.StringId == edit.Id);
                if (skill is null) return $"This world has no skill \"{edit.Id}\".";
                var max = model.GetMaxSkillPoint();
                if (v < 0 || v > max) return $"{skill.Name} must be 0 to {max}.";
                read = () => hero.GetSkillValue(skill);
                apply = () => dev.SetInitialSkillLevel(skill, v);
                return null;
            }
            case HeroEditKind.Trait:
            {
                var trait = TraitObject.All.FirstOrDefault(t => t.StringId == edit.Id);
                if (trait is null) return $"This world has no trait \"{edit.Id}\".";
                if (v < trait.MinValue || v > trait.MaxValue) return $"{trait.Name} must be {trait.MinValue} to {trait.MaxValue}.";
                read = () => hero.GetTraitLevel(trait);
                apply = () => hero.SetTraitLevel(trait, v);
                return null;
            }
            default:
                return $"Unknown edit {edit}.";
        }
    }

    /// <summary>The hero as the editor shows it. Lists every attribute, skill and trait this world has, modded ones included.</summary>
    internal static Dictionary<string, object?> Describe(Player player, Hero hero, MobileParty? party)
    {
        var model = Campaign.Current.Models.CharacterDevelopmentModel;
        var dev = hero.HeroDeveloper;
        var personality = new HashSet<TraitObject>(DefaultTraits.Personality);
        return new Dictionary<string, object?>
        {
            ["steamId"] = player.ControllerId,
            ["heroId"] = player.HeroId,
            ["name"] = hero.Name?.ToString() ?? "",
            ["culture"] = hero.Culture?.Name?.ToString() ?? "",
            ["age"] = (int)hero.Age,
            ["female"] = hero.IsFemale,
            ["level"] = hero.Level,
            ["gold"] = hero.Gold,
            ["hp"] = hero.HitPoints,
            ["maxHp"] = hero.MaxHitPoints,
            ["unspentAttr"] = dev.UnspentAttributePoints,
            ["unspentFocus"] = dev.UnspentFocusPoints,
            ["maxAttribute"] = model.MaxAttribute,
            ["maxFocus"] = model.MaxFocusPerSkill,
            ["maxSkill"] = model.GetMaxSkillPoint(),
            ["busy"] = PlayerAdmin.BusyReason(party),
            ["perks"] = PerkObject.All.Count(p => hero.GetPerkValue(p)),
            ["attributes"] = Attributes.All.Select(a => (object?)new Dictionary<string, object?>
            {
                ["id"] = a.StringId,
                ["name"] = a.Name?.ToString() ?? a.StringId,
                ["value"] = hero.GetAttributeValue(a),
            }).ToList(),
            ["skills"] = Skills.All.Select(s => (object?)new Dictionary<string, object?>
            {
                ["id"] = s.StringId,
                ["name"] = s.Name?.ToString() ?? s.StringId,
                ["attribute"] = s.Attributes?.FirstOrDefault()?.StringId ?? "",
                ["value"] = hero.GetSkillValue(s),
                ["focus"] = dev.GetFocus(s),
            }).ToList(),
            ["traits"] = TraitObject.All.Select(t => (object?)new Dictionary<string, object?>
            {
                ["id"] = t.StringId,
                ["name"] = t.Name?.ToString() ?? t.StringId,
                ["value"] = hero.GetTraitLevel(t),
                ["min"] = t.MinValue,
                ["max"] = t.MaxValue,
                ["personality"] = personality.Contains(t),
            }).ToList(),
        };
    }
}
