using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using HarmonyLib;
using ModderLords.CompatSync;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.CharacterDevelopment;
using TaleWorlds.CampaignSystem.ComponentInterfaces;
using TaleWorlds.CampaignSystem.Extensions;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.Localization;
using TaleWorlds.ModuleManager;
using TaleWorlds.ObjectSystem;

namespace ModderLords.CompatSync.Coop.Admin;

/// <summary>
/// Server only: exports a player's character to a <see cref="CharacterFile"/> and imports one onto a player's
/// existing hero. Every import is sanitized against this world first (<see cref="CharacterSanitizer"/>); a check run
/// reports what would come across without changing anything, and the host picks which parts to apply.
/// <para>
/// As with the editor, every change goes through the game's own methods, which Coop replicates. Perks are the one
/// place a game method is internal: Hero.SetPerkValueInternal, the method HeroDeveloper.AddPerk itself calls and Coop's
/// SetPerkValuePatch intercepts, is called by reflection to take a perk away.
/// </para>
/// </summary>
internal static class CharacterTransfer
{
    /// <summary>What an import can bring across. The host picks any of them.</summary>
    public static readonly string[] Parts = { "stats", "gold", "look", "gear", "name" };

    private static readonly Regex FileToken = new Regex("^[A-Za-z0-9_-]{1,64}$");
    private static readonly MethodInfo? SetPerkValueInternal = AccessTools.Method(typeof(Hero), "SetPerkValueInternal");

    /// <summary>Where the launcher drops a file to import: under the server's user folder, next to the ban list.</summary>
    internal static string? ImportsDir =>
        Environment.GetEnvironmentVariable("BANNERLORD_USER_DIR") is { Length: > 0 } dir ? Path.Combine(dir, "ModderLords", "imports") : null;

    /// <summary><c>modderlords.export &lt;req&gt; &lt;steam id&gt;</c>: the player's character, in the reply.</summary>
    [CommandLineFunctionality.CommandLineArgumentFunction("export", AdminWire.CommandGroup)]
    public static string Export(List<string> args) => PlayerAdmin.Run("export", args, req =>
    {
        if (args.Count < 2) return AdminWire.Reply("export", req, "Usage: modderlords.export <request id> <steam id>");
        if (HeroAdmin.Find(args[1], out _, out var hero, out _) is { } missing) return AdminWire.Reply("export", req, missing);
        var file = FromHero(hero!);
        Log.Info($"exported {hero!.Name} ({args[1]}): {file.Skills.Count} skills, {file.Perks.Count} perks, {file.Battle.Count(s => s.Item.Length > 0)} battle items");
        return AdminWire.Reply("export", req, null, new Dictionary<string, object?> { ["character"] = file.ToJsonObject() });
    });

    /// <summary>
    /// <c>modderlords.import &lt;req&gt; &lt;steam id&gt; &lt;file token&gt; check|&lt;part,part...&gt;</c>: reads
    /// imports\&lt;token&gt;.json, sanitizes it against this world, and either reports (check) or applies the named parts.
    /// The file is deleted once applied.
    /// </summary>
    [CommandLineFunctionality.CommandLineArgumentFunction("import", AdminWire.CommandGroup)]
    public static string Import(List<string> args) => PlayerAdmin.Run("import", args, req =>
    {
        if (args.Count < 4) return AdminWire.Reply("import", req, "Usage: modderlords.import <request id> <steam id> <file token> check|stats,gold,look,gear,name");
        if (ImportsDir is not { } dir) return AdminWire.Reply("import", req, "This server was started without BANNERLORD_USER_DIR, so it has nowhere to read a character from.");
        if (!FileToken.IsMatch(args[2])) return AdminWire.Reply("import", req, "That is not a file the launcher prepared.");
        var path = Path.Combine(dir, args[2] + ".json");
        if (!File.Exists(path)) return AdminWire.Reply("import", req, "The character file to import is gone; import it again.");
        if (HeroAdmin.Find(args[1], out var player, out var hero, out var party) is { } missing) return AdminWire.Reply("import", req, missing);

        CharacterFile source;
        try { source = CharacterFile.Parse(File.ReadAllText(path)); }
        catch (FormatException ex) { return AdminWire.Reply("import", req, ex.Message); }
        var notes = new List<string>();
        var clean = CharacterSanitizer.Sanitize(source, new GameWorld(), notes);

        var check = args[3] == "check";
        var parts = check ? new List<string>() : args[3].Split(',').Where(p => p.Length > 0).Distinct().ToList();
        if (parts.FirstOrDefault(p => !Parts.Contains(p)) is { } unknown) return AdminWire.Reply("import", req, $"Unknown part \"{unknown}\".");
        if (!check)
        {
            var busy = PlayerAdmin.BusyReason(party);
            if (busy.Length > 0) return AdminWire.Reply("import", req, $"{hero!.Name} is {busy}. Import once it is over.");
            var applied = Apply(hero!, clean, parts, notes);
            Log.Info($"imported onto {hero!.Name} ({player!.ControllerId}) from \"{clean.Name}\": {string.Join(", ", parts)}; {applied}");
            try { File.Delete(path); } catch { /* the launcher cleans the folder too */ }
        }

        var data = HeroAdmin.Describe(player!, hero!, party);
        data["notes"] = notes.Cast<object?>().ToList();
        data["applied"] = parts.Cast<object?>().ToList();
        data["source"] = new Dictionary<string, object?>
        {
            ["name"] = clean.Name,
            ["level"] = clean.Level,
            ["exportedAt"] = clean.ExportedAt,
            ["skills"] = clean.Skills.Count,
            ["perks"] = clean.Perks.Count,
            ["missingModules"] = MissingModules(clean).Cast<object?>().ToList(),
        };
        return AdminWire.Reply("import", req, null, data);
    });

    /// <summary>The character as a file: everything about the hero, by game id, and this world's mod list.</summary>
    internal static CharacterFile FromHero(Hero hero)
    {
        var dev = hero.HeroDeveloper;
        var file = new CharacterFile
        {
            ExportedAt = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            Modules = TaleWorlds.Engine.Utilities.GetModulesNames()
                .Select(id => new KeyValuePair<string, string>(id, ModuleHelper.GetModuleInfo(id)?.Version.ToString() ?? "")).ToList(),
            Name = hero.Name?.ToString() ?? "",
            FirstName = hero.FirstName?.ToString() ?? "",
            Female = hero.IsFemale,
            Age = hero.Age,
            Culture = hero.Culture?.StringId ?? "",
            Body = hero.BodyProperties.ToString(),
            Level = hero.Level,
            Gold = hero.Gold,
            UnspentAttributePoints = dev.UnspentAttributePoints,
            UnspentFocusPoints = dev.UnspentFocusPoints,
            Perks = PerkObject.All.Where(p => hero.GetPerkValue(p)).Select(p => p.StringId).ToList(),
            Battle = Slots(hero.BattleEquipment),
            Civilian = Slots(hero.CivilianEquipment),
        };
        foreach (var a in Attributes.All) file.Attributes[a.StringId] = hero.GetAttributeValue(a);
        foreach (var s in Skills.All)
        {
            file.Skills[s.StringId] = hero.GetSkillValue(s);
            file.Focus[s.StringId] = dev.GetFocus(s);
        }
        foreach (var t in TraitObject.All) file.Traits[t.StringId] = hero.GetTraitLevel(t);
        return file;
    }

    private static List<CharacterSlot> Slots(Equipment equipment)
    {
        var slots = new List<CharacterSlot>();
        for (var i = EquipmentIndex.WeaponItemBeginSlot; i < EquipmentIndex.NumEquipmentSetSlots; i++)
        {
            var e = equipment[i];
            slots.Add(new CharacterSlot { Slot = i.ToString(), Item = e.Item?.StringId ?? "", Modifier = e.ItemModifier?.StringId ?? "" });
        }
        return slots;
    }

    /// <summary>Applies the chosen parts of an already sanitized character. Returns a one-line account for the log.</summary>
    private static string Apply(Hero hero, CharacterFile c, List<string> parts, List<string> notes)
    {
        var done = new List<string>();
        var dev = hero.HeroDeveloper;
        if (parts.Contains("stats"))
        {
            foreach (var a in Attributes.All)
            {
                if (!c.Attributes.TryGetValue(a.StringId, out var target)) continue;
                var change = target - hero.GetAttributeValue(a);
                if (change > 0) dev.AddAttribute(a, change, checkUnspentPoints: false);
                else if (change < 0) dev.RemoveAttribute(a, -change);
            }
            foreach (var s in Skills.All)
            {
                if (c.Skills.TryGetValue(s.StringId, out var level) && level != hero.GetSkillValue(s)) dev.SetInitialSkillLevel(s, level);
                if (c.Focus.TryGetValue(s.StringId, out var focus) && focus != dev.GetFocus(s)) dev.AddFocus(s, focus - dev.GetFocus(s), checkUnspentFocusPoints: false);
            }
            foreach (var t in TraitObject.All)
                if (c.Traits.TryGetValue(t.StringId, out var level) && level != hero.GetTraitLevel(t)) hero.SetTraitLevel(t, level);
            var perks = new HashSet<string>(c.Perks, StringComparer.Ordinal);
            var perkChanges = 0;
            foreach (var p in PerkObject.All)
            {
                var want = perks.Contains(p.StringId);
                if (hero.GetPerkValue(p) == want) continue;
                if (want) dev.AddPerk(p);
                else if (SetPerkValueInternal is not null) SetPerkValueInternal.Invoke(hero, new object[] { p, false });
                else { notes.Add($"perk {p.StringId}: could not be taken away (this game version has no SetPerkValueInternal)"); continue; }
                perkChanges++;
            }
            dev.UnspentAttributePoints = c.UnspentAttributePoints;
            dev.UnspentFocusPoints = c.UnspentFocusPoints;
            done.Add($"stats ({perkChanges} perk change(s))");
        }
        if (parts.Contains("gold"))
        {
            hero.ChangeHeroGold(c.Gold - hero.Gold);
            done.Add("gold " + hero.Gold);
        }
        if (parts.Contains("look"))
        {
            if (c.Body.Length > 0 && BodyProperties.FromString(c.Body, out var body))
            {
                hero.StaticBodyProperties = body.StaticProperties;
                hero.Weight = body.Weight;
                hero.Build = body.Build;
            }
            else if (c.Body.Length > 0) notes.Add("look: the face in the file could not be read, so it was left as it is");
            hero.IsFemale = c.Female;
            if (c.Age > 0) hero.SetBirthDay(CampaignTime.YearsFromNow(-(float)c.Age));
            done.Add("look");
        }
        if (parts.Contains("gear"))
        {
            Equip(hero.BattleEquipment, c.Battle);
            Equip(hero.CivilianEquipment, c.Civilian);
            done.Add("gear");
        }
        if (parts.Contains("name") && c.Name.Length > 0)
        {
            hero.SetName(new TextObject(c.Name), new TextObject(c.FirstName.Length > 0 ? c.FirstName : c.Name));
            done.Add("name " + c.Name);
        }
        return string.Join("; ", done);
    }

    private static void Equip(Equipment equipment, List<CharacterSlot> slots)
    {
        foreach (var s in slots)
        {
            if (!Enum.TryParse<EquipmentIndex>(s.Slot, out var index)) continue;
            var item = s.Item.Length > 0 ? MBObjectManager.Instance.GetObject<ItemObject>(s.Item) : null;
            var modifier = s.Modifier.Length > 0 ? MBObjectManager.Instance.GetObject<ItemModifier>(s.Modifier) : null;
            equipment[index] = item is null ? EquipmentElement.Invalid : new EquipmentElement(item, modifier);
        }
    }

    /// <summary>Mods the character's world had that this one does not: why things were dropped, said in the host's terms.</summary>
    private static List<string> MissingModules(CharacterFile c)
    {
        var here = new HashSet<string>(TaleWorlds.Engine.Utilities.GetModulesNames(), StringComparer.OrdinalIgnoreCase);
        return c.Modules.Where(m => m.Key.Length > 0 && !here.Contains(m.Key)).Select(m => m.Key).ToList();
    }

    /// <summary>This world, as the sanitizer sees it.</summary>
    private sealed class GameWorld : ICharacterWorld
    {
        private readonly CharacterDevelopmentModel _model = Campaign.Current.Models.CharacterDevelopmentModel;
        public bool HasAttribute(string id) => Attributes.All.Any(a => a.StringId == id);
        public bool HasSkill(string id) => Skills.All.Any(s => s.StringId == id);
        public (int Min, int Max)? Trait(string id) => TraitObject.All.FirstOrDefault(t => t.StringId == id) is { } t ? (t.MinValue, t.MaxValue) : ((int, int)?)null;
        public bool HasPerk(string id) => PerkObject.All.Any(p => p.StringId == id);
        public bool HasItem(string id) => MBObjectManager.Instance.GetObject<ItemObject>(id) is not null;
        public bool HasItemModifier(string id) => MBObjectManager.Instance.GetObject<ItemModifier>(id) is not null;
        public bool HasSlot(string slot) => Enum.TryParse<EquipmentIndex>(slot, out var i) && i >= EquipmentIndex.WeaponItemBeginSlot && i < EquipmentIndex.NumEquipmentSetSlots;
        public bool HasCulture(string id) => MBObjectManager.Instance.GetObject<CultureObject>(id) is not null;
        public int MaxAttribute => _model.MaxAttribute;
        public int MaxFocus => _model.MaxFocusPerSkill;
        public int MaxSkill => _model.GetMaxSkillPoint();
    }
}
