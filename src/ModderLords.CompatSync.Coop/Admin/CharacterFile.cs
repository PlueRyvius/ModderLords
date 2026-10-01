using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ModderLords.CompatSync;

namespace ModderLords.CompatSync.Coop.Admin;

/// <summary>One equipment slot: the slot's name (the game's EquipmentIndex), an item id ("" for empty), and its modifier.</summary>
public sealed class CharacterSlot
{
    public string Slot { get; set; } = "";
    public string Item { get; set; } = "";
    public string Modifier { get; set; } = "";
}

/// <summary>
/// A character as exported: everything that makes up a player's hero, by game id, plus the mod list of the world it came
/// from. Nothing in it points at a particular world (no party, clan, settlement or relation), so it can be brought into
/// another one; <see cref="CharacterSanitizer"/> then drops whatever that world does not have.
/// <para>No game types here: the launcher's tests compile this file.</para>
/// </summary>
public sealed class CharacterFile
{
    public const int CurrentSchema = 1;
    public const string Kind = "ModderLords character";

    public int Schema { get; set; } = CurrentSchema;
    public string ExportedAt { get; set; } = "";
    public List<KeyValuePair<string, string>> Modules { get; set; } = new List<KeyValuePair<string, string>>();

    public string Name { get; set; } = "";
    public string FirstName { get; set; } = "";
    public bool Female { get; set; }
    public double Age { get; set; }
    public string Culture { get; set; } = "";
    /// <summary>The game's own BodyProperties string (face, build, weight).</summary>
    public string Body { get; set; } = "";
    /// <summary>For information only: a level comes from experience, so it is never imported.</summary>
    public int Level { get; set; }

    public int Gold { get; set; }
    public int UnspentAttributePoints { get; set; }
    public int UnspentFocusPoints { get; set; }
    public Dictionary<string, int> Attributes { get; set; } = new Dictionary<string, int>(StringComparer.Ordinal);
    public Dictionary<string, int> Skills { get; set; } = new Dictionary<string, int>(StringComparer.Ordinal);
    public Dictionary<string, int> Focus { get; set; } = new Dictionary<string, int>(StringComparer.Ordinal);
    public Dictionary<string, int> Traits { get; set; } = new Dictionary<string, int>(StringComparer.Ordinal);
    public List<string> Perks { get; set; } = new List<string>();
    public List<CharacterSlot> Battle { get; set; } = new List<CharacterSlot>();
    public List<CharacterSlot> Civilian { get; set; } = new List<CharacterSlot>();

    public Dictionary<string, object?> ToJsonObject() => new Dictionary<string, object?>
    {
        ["kind"] = Kind,
        ["schema"] = Schema,
        ["exportedAt"] = ExportedAt,
        ["modules"] = Modules.Select(m => (object?)new Dictionary<string, object?> { ["id"] = m.Key, ["version"] = m.Value }).ToList(),
        ["name"] = Name,
        ["firstName"] = FirstName,
        ["female"] = Female,
        ["age"] = Age,
        ["culture"] = Culture,
        ["body"] = Body,
        ["level"] = Level,
        ["gold"] = Gold,
        ["unspentAttr"] = UnspentAttributePoints,
        ["unspentFocus"] = UnspentFocusPoints,
        ["attributes"] = Numbers(Attributes),
        ["skills"] = Numbers(Skills),
        ["focus"] = Numbers(Focus),
        ["traits"] = Numbers(Traits),
        ["perks"] = Perks.Cast<object?>().ToList(),
        ["battle"] = Slots(Battle),
        ["civilian"] = Slots(Civilian),
    };

    public string ToJson() => MiniJson.Serialize(ToJsonObject());

    /// <summary>Reads a file. Throws FormatException, with a reason a person can act on, for anything else.</summary>
    public static CharacterFile Parse(string json)
    {
        Dictionary<string, object?> o;
        try { o = MiniJson.ParseObject(json); }
        catch (FormatException ex) { throw new FormatException("This is not a character file (it is not valid JSON: " + ex.Message + ")."); }
        if (MiniJson.GetString(o, "kind") != Kind) throw new FormatException("This is not a ModderLords character file.");
        var schema = Int(o, "schema");
        if (schema < 1 || schema > CurrentSchema)
            throw new FormatException($"This character file is format {schema}; this version of ModderLords reads up to {CurrentSchema}. Update ModderLords.");

        return new CharacterFile
        {
            Schema = schema,
            ExportedAt = MiniJson.GetString(o, "exportedAt") ?? "",
            Modules = (MiniJson.GetArray(o, "modules") ?? new List<object?>()).OfType<Dictionary<string, object?>>()
                .Select(m => new KeyValuePair<string, string>(MiniJson.GetString(m, "id") ?? "", MiniJson.GetString(m, "version") ?? "")).ToList(),
            Name = MiniJson.GetString(o, "name") ?? "",
            FirstName = MiniJson.GetString(o, "firstName") ?? "",
            Female = o.TryGetValue("female", out var f) && f is bool b && b,
            Age = Num(o, "age"),
            Culture = MiniJson.GetString(o, "culture") ?? "",
            Body = MiniJson.GetString(o, "body") ?? "",
            Level = Int(o, "level"),
            Gold = Int(o, "gold"),
            UnspentAttributePoints = Int(o, "unspentAttr"),
            UnspentFocusPoints = Int(o, "unspentFocus"),
            Attributes = NumberMap(o, "attributes"),
            Skills = NumberMap(o, "skills"),
            Focus = NumberMap(o, "focus"),
            Traits = NumberMap(o, "traits"),
            Perks = (MiniJson.GetArray(o, "perks") ?? new List<object?>()).OfType<string>().ToList(),
            Battle = SlotList(o, "battle"),
            Civilian = SlotList(o, "civilian"),
        };
    }

    private static Dictionary<string, object?> Numbers(Dictionary<string, int> map) =>
        map.ToDictionary(kv => kv.Key, kv => (object?)kv.Value, StringComparer.Ordinal);

    private static List<object?> Slots(List<CharacterSlot> slots) =>
        slots.Select(s => (object?)new Dictionary<string, object?> { ["slot"] = s.Slot, ["item"] = s.Item, ["modifier"] = s.Modifier }).ToList();

    private static List<CharacterSlot> SlotList(Dictionary<string, object?> o, string key) =>
        (MiniJson.GetArray(o, key) ?? new List<object?>()).OfType<Dictionary<string, object?>>().Select(s => new CharacterSlot
        {
            Slot = MiniJson.GetString(s, "slot") ?? "",
            Item = MiniJson.GetString(s, "item") ?? "",
            Modifier = MiniJson.GetString(s, "modifier") ?? "",
        }).ToList();

    private static Dictionary<string, int> NumberMap(Dictionary<string, object?> o, string key)
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        if (MiniJson.GetObject(o, key) is { } obj)
            foreach (var kv in obj)
                if (ToDouble(kv.Value) is { } d) map[kv.Key] = Clamp(d);
        return map;
    }

    private static double Num(Dictionary<string, object?> o, string key) => o.TryGetValue(key, out var v) ? ToDouble(v) ?? 0 : 0;
    private static int Int(Dictionary<string, object?> o, string key) => Clamp(Num(o, key));
    private static int Clamp(double d) => d >= int.MaxValue ? int.MaxValue : d <= int.MinValue ? int.MinValue : (int)Math.Round(d);

    private static double? ToDouble(object? v) => v switch
    {
        long l => l,
        double d => d,
        int i => i,
        string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var p) => p,
        _ => null,
    };
}

/// <summary>What the world a character is going into has, as the sanitizer needs it. The server builds one from the game.</summary>
public interface ICharacterWorld
{
    bool HasAttribute(string id);
    bool HasSkill(string id);
    /// <summary>The trait's (min, max), or null when the world has no such trait.</summary>
    (int Min, int Max)? Trait(string id);
    bool HasPerk(string id);
    bool HasItem(string id);
    bool HasItemModifier(string id);
    bool HasSlot(string slot);
    bool HasCulture(string id);
    int MaxAttribute { get; }
    int MaxFocus { get; }
    int MaxSkill { get; }
}

/// <summary>
/// Cleans a character down to what the world it is going into can hold: anything from a mod that world does not have
/// (a skill, trait, perk, item, modifier) is dropped, anything over that world's limits is brought within them, and
/// each change is said, so the host sees exactly what will not come across before anything is applied.
/// </summary>
public static class CharacterSanitizer
{
    public const int MaxNameLength = 40;

    public static CharacterFile Sanitize(CharacterFile source, ICharacterWorld world, List<string> notes)
    {
        var clean = new CharacterFile
        {
            Schema = source.Schema,
            ExportedAt = source.ExportedAt,
            Modules = source.Modules.ToList(),
            Name = Name(source.Name, "name", notes),
            FirstName = Name(source.FirstName, "first name", notes),
            Female = source.Female,
            Body = source.Body,
            Level = source.Level,
            Gold = Within("gold", source.Gold, 0, int.MaxValue, notes),
            UnspentAttributePoints = Within("unspent attribute points", source.UnspentAttributePoints, 0, 1000, notes),
            UnspentFocusPoints = Within("unspent focus points", source.UnspentFocusPoints, 0, 1000, notes),
        };
        clean.Age = source.Age < 18 || source.Age > 120 ? Clamped("age", source.Age, Math.Min(120, Math.Max(18, source.Age)), notes) : source.Age;
        if (source.Culture.Length > 0 && !world.HasCulture(source.Culture)) notes.Add($"culture {source.Culture}: not in this world (the character keeps theirs)");
        else clean.Culture = source.Culture;

        foreach (var kv in source.Attributes)
        {
            if (!world.HasAttribute(kv.Key)) { notes.Add($"attribute {kv.Key}: not in this world, dropped"); continue; }
            clean.Attributes[kv.Key] = Within("attribute " + kv.Key, kv.Value, 0, world.MaxAttribute, notes);
        }
        foreach (var kv in source.Skills)
        {
            if (!world.HasSkill(kv.Key)) { notes.Add($"skill {kv.Key}: not in this world, dropped"); continue; }
            clean.Skills[kv.Key] = Within("skill " + kv.Key, kv.Value, 0, world.MaxSkill, notes);
        }
        foreach (var kv in source.Focus)
        {
            if (!world.HasSkill(kv.Key)) continue; // said once, under its skill
            clean.Focus[kv.Key] = Within("focus " + kv.Key, kv.Value, 0, world.MaxFocus, notes);
        }
        foreach (var kv in source.Traits)
        {
            if (world.Trait(kv.Key) is not { } range) { notes.Add($"trait {kv.Key}: not in this world, dropped"); continue; }
            clean.Traits[kv.Key] = Within("trait " + kv.Key, kv.Value, range.Min, range.Max, notes);
        }
        var missingPerks = source.Perks.Where(p => !world.HasPerk(p)).ToList();
        clean.Perks = source.Perks.Where(world.HasPerk).Distinct(StringComparer.Ordinal).ToList();
        if (missingPerks.Count > 0) notes.Add($"{missingPerks.Count} perk(s) not in this world, dropped: {string.Join(", ", missingPerks)}");

        clean.Battle = Gear(source.Battle, "battle", world, notes);
        clean.Civilian = Gear(source.Civilian, "civilian", world, notes);
        return clean;
    }

    private static List<CharacterSlot> Gear(List<CharacterSlot> slots, string set, ICharacterWorld world, List<string> notes)
    {
        var kept = new List<CharacterSlot>();
        foreach (var s in slots)
        {
            if (!world.HasSlot(s.Slot)) { notes.Add($"{set} slot {s.Slot}: not a slot in this game, dropped"); continue; }
            if (s.Item.Length > 0 && !world.HasItem(s.Item))
            {
                notes.Add($"{set} {s.Slot}: item {s.Item} not in this world (the slot keeps what the character has)");
                continue;
            }
            var modifier = s.Modifier;
            if (modifier.Length > 0 && !world.HasItemModifier(modifier))
            {
                notes.Add($"{set} {s.Slot}: modifier {modifier} not in this world, the plain item is used");
                modifier = "";
            }
            kept.Add(new CharacterSlot { Slot = s.Slot, Item = s.Item, Modifier = s.Item.Length > 0 ? modifier : "" });
        }
        return kept;
    }

    /// <summary>A name the game shows as typed: braces would make it a text template, so they go, and so do control characters.</summary>
    private static string Name(string name, string what, List<string> notes)
    {
        var clean = new string(name.Where(c => c != '{' && c != '}' && !char.IsControl(c)).ToArray()).Trim();
        if (clean.Length > MaxNameLength) clean = clean.Substring(0, MaxNameLength).Trim();
        if (clean != name) notes.Add($"{what}: \"{name}\" becomes \"{clean}\"");
        return clean;
    }

    private static int Within(string what, int value, int min, int max, List<string> notes)
    {
        if (value >= min && value <= max) return value;
        var clamped = Math.Min(max, Math.Max(min, value));
        notes.Add($"{what}: {value} is outside {min} to {max}, set to {clamped}");
        return clamped;
    }

    private static double Clamped(string what, double value, double clamped, List<string> notes)
    {
        notes.Add($"{what}: {value.ToString("0.#", CultureInfo.InvariantCulture)} brought to {clamped.ToString("0.#", CultureInfo.InvariantCulture)}");
        return clamped;
    }
}
