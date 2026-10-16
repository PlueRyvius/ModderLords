using System;
using System.Collections.Generic;
using System.Linq;
using Common.Messaging;
using GameInterface;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.Players;
using GameInterface.Services.Players.Data;
using GameInterface.Services.Heroes.Messages;
using ModderLords.CompatSync;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.CharacterDevelopment;
using TaleWorlds.CampaignSystem.ComponentInterfaces;
using TaleWorlds.CampaignSystem.Extensions;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.Localization;

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
    /// <summary>
    /// <c>modderlords.hero &lt;req&gt; &lt;target&gt;</c>: everything the editor shows for that hero. The target is a player's
    /// Steam id, or <c>hero:&lt;id&gt;</c> for a companion or family member of a player's clan (<see cref="HeroTargetWire"/>).
    /// </summary>
    [CommandLineFunctionality.CommandLineArgumentFunction("hero", AdminWire.CommandGroup)]
    public static string HeroCommand(List<string> args) => PlayerAdmin.Run("hero", args, req =>
    {
        if (args.Count < 2) return AdminWire.Reply("hero", req, "Usage: modderlords.hero <request id> <steam id | hero:id>");
        if (FindTarget(args[1], out var steamId, out var hero, out var party) is { } missing) return AdminWire.Reply("hero", req, missing);
        return AdminWire.Reply("hero", req, null, Describe(steamId, hero!, party));
    });

    /// <summary>
    /// <c>modderlords.edit &lt;req&gt; &lt;target&gt; &lt;token&gt;...</c> (<see cref="HeroEditWire"/>). Every token is checked
    /// before any is applied, so a bad one changes nothing. Refused while the player's party is in a battle or a siege,
    /// where a mission holds its own copy of the hero.
    /// </summary>
    [CommandLineFunctionality.CommandLineArgumentFunction("edit", AdminWire.CommandGroup)]
    public static string EditCommand(List<string> args) => PlayerAdmin.Run("edit", args, req =>
    {
        if (args.Count < 3) return AdminWire.Reply("edit", req, "Usage: modderlords.edit <request id> <steam id | hero:id> <key=value>...");
        if (FindTarget(args[1], out var steamId, out var hero, out var party) is { } missing) return AdminWire.Reply("edit", req, missing);
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
        Log.Info($"host edit of {hero!.Name} ({args[1]}): "
            + (changes.Count == 0 ? "nothing to change" : string.Join("; ", changes.Cast<string>()))
            + (notApplied.Count > 0 ? "; NOT applied: " + string.Join("; ", notApplied.Cast<string>()) : ""));

        var data = Describe(steamId, hero, party);
        data["changes"] = changes;
        data["notApplied"] = notApplied;
        return AdminWire.Reply("edit", req, null, data);
    });

    /// <summary>
    /// <c>modderlords.rename &lt;req&gt; &lt;target&gt; &lt;name&gt;</c> (<see cref="HeroNameWire"/>). Hero.SetName writes the
    /// name fields inside Hero, where Coop intercepts both stores and sends them to every client.
    /// </summary>
    [CommandLineFunctionality.CommandLineArgumentFunction("rename", AdminWire.CommandGroup)]
    public static string RenameCommand(List<string> args) => PlayerAdmin.Run("rename", args, req =>
    {
        if (args.Count < 3) return AdminWire.Reply("rename", req, "Usage: modderlords.rename <request id> <steam id | hero:id> <encoded name>");
        if (!HeroNameWire.TryDecode(args[2], out var name, out var error)) return AdminWire.Reply("rename", req, error);
        if (FindTarget(args[1], out var steamId, out var hero, out var party) is { } missing) return AdminWire.Reply("rename", req, missing);
        var busy = PlayerAdmin.BusyReason(party);
        if (busy.Length > 0) return AdminWire.Reply("rename", req, $"{hero!.Name} is {busy}. Rename them once it is over.");

        var before = hero!.Name?.ToString() ?? "";
        var changes = new List<object?>();
        if (before != name)
        {
            hero.SetName(new TextObject(name), new TextObject(name));
            changes.Add($"Name {before} -> {hero.Name}");
            Log.Info($"host renamed {before} ({args[1]}) to {hero.Name}");
        }
        var data = Describe(steamId, hero, party);
        data["changes"] = changes;
        data["notApplied"] = new List<object?>();
        return AdminWire.Reply("rename", req, null, data);
    });

    /// <summary>
    /// <c>modderlords.resetperks &lt;req&gt; &lt;target&gt;</c>: takes away every perk the hero has chosen, so they can be
    /// chosen again in the game's character screen. Skills, focus and attributes are left as they are.
    /// </summary>
    [CommandLineFunctionality.CommandLineArgumentFunction("resetperks", AdminWire.CommandGroup)]
    public static string ResetPerksCommand(List<string> args) => PlayerAdmin.Run("resetperks", args, req =>
    {
        if (args.Count < 2) return AdminWire.Reply("resetperks", req, "Usage: modderlords.resetperks <request id> <steam id | hero:id>");
        if (FindTarget(args[1], out var steamId, out var hero, out var party) is { } missing) return AdminWire.Reply("resetperks", req, missing);
        var busy = PlayerAdmin.BusyReason(party);
        if (busy.Length > 0) return AdminWire.Reply("resetperks", req, $"{hero!.Name} is {busy}. Reset their perks once it is over.");
        if (CharacterTransfer.SetPerkValueInternal is not { } setPerk)
            return AdminWire.Reply("resetperks", req, "This game version has no Hero.SetPerkValueInternal, so a perk cannot be taken away.");

        var taken = 0;
        foreach (var perk in PerkObject.All.Where(p => hero!.GetPerkValue(p)).ToList())
        {
            setPerk.Invoke(hero, new object[] { perk, false });
            if (!hero!.GetPerkValue(perk)) taken++;
        }
        // Perks can raise maximum hit points; without them the hero must not keep more than the new maximum.
        if (hero!.HitPoints > hero.MaxHitPoints) hero.HitPoints = hero.MaxHitPoints;
        var left = PerkObject.All.Count(p => hero.GetPerkValue(p));
        Log.Info($"host reset the perks of {hero.Name} ({args[1]}): {taken} taken away, {left} left");

        var data = Describe(steamId, hero, party);
        data["changes"] = new List<object?> { $"Perks {taken + left} -> {left}" };
        data["notApplied"] = left > 0 ? new List<object?> { $"{left} perk(s) could not be taken away" } : new List<object?>();
        return AdminWire.Reply("resetperks", req, null, data);
    });

    /// <summary>
    /// <c>modderlords.companions &lt;req&gt;</c>: every living hero of a player's clan other than the players themselves,
    /// companions and family alike. Each can be edited as <c>hero:&lt;heroId&gt;</c>.
    /// </summary>
    [CommandLineFunctionality.CommandLineArgumentFunction("companions", AdminWire.CommandGroup)]
    public static string CompanionsCommand(List<string> args) => PlayerAdmin.Run("companions", args, req =>
    {
        if (!ContainerProvider.TryResolve<IPlayerManager>(out var players)) return AdminWire.Reply("companions", req, "Coop's player registry is not available yet.");
        var leads = PlayerLeads(players);
        var playerHeroes = new HashSet<Hero>(leads);
        var seen = new HashSet<Hero>();
        var list = new List<object?>();
        foreach (var lead in leads)
        {
            if (lead.Clan is not { } clan) continue;
            foreach (var hero in clan.Heroes.Concat(clan.Companions))
            {
                if (hero is null || !hero.IsAlive || playerHeroes.Contains(hero) || !seen.Add(hero)) continue;
                list.Add(new Dictionary<string, object?>
                {
                    ["heroId"] = hero.StringId,
                    ["name"] = hero.Name?.ToString() ?? "",
                    ["kind"] = hero.CompanionOf is not null ? "companion" : "family",
                    ["owner"] = lead.Name?.ToString() ?? "",
                    ["clan"] = clan.Name?.ToString() ?? "",
                    ["level"] = hero.Level,
                    ["age"] = (int)hero.Age,
                    ["where"] = hero.PartyBelongedTo?.Name?.ToString() ?? hero.CurrentSettlement?.Name?.ToString() ?? (hero.IsPrisoner ? "prisoner" : ""),
                    ["busy"] = PlayerAdmin.BusyReason(hero.PartyBelongedTo),
                });
            }
        }
        return AdminWire.Reply("companions", req, null, new Dictionary<string, object?> { ["list"] = list });
    });

    /// <summary>The heroes the registered players play.</summary>
    internal static List<Hero> PlayerLeads(IPlayerManager players) =>
        players.Players.Where(p => p.ControllerId != "Server").Select(p => PlayerHeroes.HeroFor(p.HeroId)).Where(h => h is not null).Select(h => h!).ToList();

    /// <summary>
    /// The hero a command names: a player's own (by Steam id), or with <c>hero:&lt;id&gt;</c> another living hero of a
    /// player's clan. <paramref name="steamId"/> is empty for the second kind.
    /// </summary>
    internal static string? FindTarget(string target, out string steamId, out Hero? hero, out MobileParty? party)
    {
        steamId = "";
        if (!HeroTargetWire.IsHero(target, out var heroStringId))
        {
            var missing = Find(target, out var player, out hero, out party);
            if (missing is null) steamId = player!.ControllerId;
            return missing;
        }

        hero = null;
        party = null;
        if (!ContainerProvider.TryResolve<IPlayerManager>(out var players)) return "Coop's player registry is not available yet.";
        var clans = new HashSet<Clan>(PlayerLeads(players).Where(h => h.Clan is not null).Select(h => h.Clan));
        var found = clans.SelectMany(c => c.Heroes.Concat(c.Companions)).FirstOrDefault(h => h is not null && h.StringId == heroStringId);
        if (found is null) return $"No hero \"{heroStringId}\" is in a player's clan.";
        if (!found.IsAlive) return $"{found.Name} is dead.";
        hero = found;
        party = found.PartyBelongedTo;
        return null;
    }

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

    private const int MaxRenown = 1_000_000;

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
            case HeroEditKind.Age:
                const int maxAge = 150;
                if (v < 0 || v > maxAge) return $"Age must be 0 to {maxAge}.";
                read = () => (int)hero.Age;
                apply = () => hero.SetBirthDay(CampaignTime.YearsFromNow(-v));
                return null;
            case HeroEditKind.Level:
                var maxLevel = MaximumLevel(model, dev);
                if (v < 1 || v > maxLevel) return $"Level must be 1 to {maxLevel}.";
                read = () => hero.Level;
                apply = () =>
                {
                    // Keep the hero's XP threshold consistent with the explicit level, including when lowering it.
                    dev.SetInitialLevel(v);
                    hero.Level = v;
                    // Coop relays level changes through this GameInterface event; a direct field write alone would stay server-side.
                    MessageBroker.Instance.Publish(hero, new HeroLevelChanged(v, hero));
                };
                return null;
            case HeroEditKind.Gold:
                if (v < 0) return "Gold cannot be negative.";
                read = () => hero.Gold;
                apply = () => hero.ChangeHeroGold(v - hero.Gold);
                return null;
            case HeroEditKind.ClanRenown:
                if (hero.Clan is not { } clan) return $"{hero.Name} has no clan.";
                if (v < 0 || v > MaxRenown) return $"Clan renown must be 0 to {MaxRenown}.";
                read = () => (int)clan.Renown;
                // Clan.AddRenown also moves the clan's tier, and Coop patches it to send the new renown to every client.
                apply = () => clan.AddRenown(v - clan.Renown, false);
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

    private static int MaximumLevel(CharacterDevelopmentModel model, HeroDeveloper dev)
    {
        var maxSkillPoint = model.GetMaxSkillPoint();
        for (var level = 1; level < 1024; level++)
            if (dev.GetXpRequiredForLevel(level + 1) == maxSkillPoint) return level;
        return 1023;
    }

    /// <summary>The hero as the editor shows it. Lists every attribute, skill and trait this world has, modded ones included.</summary>
    internal static Dictionary<string, object?> Describe(Player player, Hero hero, MobileParty? party) => Describe(player.ControllerId, hero, party);

    /// <summary>As above; <paramref name="steamId"/> is empty for a hero no player plays.</summary>
    internal static Dictionary<string, object?> Describe(string steamId, Hero hero, MobileParty? party)
    {
        var model = Campaign.Current.Models.CharacterDevelopmentModel;
        var dev = hero.HeroDeveloper;
        var personality = new HashSet<TraitObject>(DefaultTraits.Personality);
        return new Dictionary<string, object?>
        {
            ["steamId"] = steamId,
            ["heroId"] = hero.StringId,
            ["name"] = hero.Name?.ToString() ?? "",
            ["culture"] = hero.Culture?.Name?.ToString() ?? "",
            ["age"] = (int)hero.Age,
            ["maxAge"] = 150,
            ["female"] = hero.IsFemale,
            ["level"] = hero.Level,
            ["maxLevel"] = MaximumLevel(model, dev),
            ["gold"] = hero.Gold,
            ["clan"] = hero.Clan?.Name?.ToString() ?? "",
            ["renown"] = hero.Clan is { } c ? (int)c.Renown : -1,
            ["maxRenown"] = MaxRenown,
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
