using System.Globalization;
using System.Text.Json;

namespace ModderLords.Coop.Admin;

public sealed record HeroAttribute(string Id, string Name, int Value);
public sealed record HeroSkill(string Id, string Name, string Attribute, int Value, int Focus);
public sealed record HeroTrait(string Id, string Name, int Value, int Min, int Max, bool Personality);

/// <summary>A player's hero as the server's <c>modderlords.hero</c> / <c>modderlords.edit</c> report it.</summary>
public sealed record HeroDetail
{
    public string SteamId { get; init; } = "";
    public string HeroId { get; init; } = "";
    public string Name { get; init; } = "";
    public string Culture { get; init; } = "";
    public int Age { get; init; }
    public bool Female { get; init; }
    public int Level { get; init; }
    public int Gold { get; init; }
    public int HitPoints { get; init; }
    public int MaxHitPoints { get; init; }
    public int UnspentAttributePoints { get; init; }
    public int UnspentFocusPoints { get; init; }
    public int MaxAttribute { get; init; }
    public int MaxFocus { get; init; }
    public int MaxSkill { get; init; }
    public string Busy { get; init; } = "";
    public int Perks { get; init; }
    public IReadOnlyList<HeroAttribute> Attributes { get; init; } = [];
    public IReadOnlyList<HeroSkill> Skills { get; init; } = [];
    public IReadOnlyList<HeroTrait> Traits { get; init; } = [];

    /// <summary>What an edit reply says it changed, and what it could not.</summary>
    public IReadOnlyList<string> Changes { get; init; } = [];
    public IReadOnlyList<string> NotApplied { get; init; } = [];

    public static HeroDetail From(AdminReply reply)
    {
        var r = reply.Root;
        return new HeroDetail
        {
            SteamId = Str(r, "steamId"),
            HeroId = Str(r, "heroId"),
            Name = Str(r, "name"),
            Culture = Str(r, "culture"),
            Age = Int(r, "age"),
            Female = r.TryGetProperty("female", out var f) && f.ValueKind == JsonValueKind.True,
            Level = Int(r, "level"),
            Gold = Int(r, "gold"),
            HitPoints = Int(r, "hp"),
            MaxHitPoints = Int(r, "maxHp"),
            UnspentAttributePoints = Int(r, "unspentAttr"),
            UnspentFocusPoints = Int(r, "unspentFocus"),
            MaxAttribute = Int(r, "maxAttribute"),
            MaxFocus = Int(r, "maxFocus"),
            MaxSkill = Int(r, "maxSkill"),
            Busy = Str(r, "busy"),
            Perks = Int(r, "perks"),
            Attributes = Items(r, "attributes").Select(a => new HeroAttribute(Str(a, "id"), Str(a, "name"), Int(a, "value"))).ToList(),
            Skills = Items(r, "skills").Select(s => new HeroSkill(Str(s, "id"), Str(s, "name"), Str(s, "attribute"), Int(s, "value"), Int(s, "focus"))).ToList(),
            Traits = Items(r, "traits").Select(t => new HeroTrait(Str(t, "id"), Str(t, "name"), Int(t, "value"), Int(t, "min"), Int(t, "max"),
                t.TryGetProperty("personality", out var p) && p.ValueKind == JsonValueKind.True)).ToList(),
            Changes = Items(r, "changes").Where(c => c.ValueKind == JsonValueKind.String).Select(c => c.GetString()!).ToList(),
            NotApplied = Items(r, "notApplied").Where(c => c.ValueKind == JsonValueKind.String).Select(c => c.GetString()!).ToList(),
        };
    }

    private static IEnumerable<JsonElement> Items(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() : [];

    private static string Str(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static int Int(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : 0;
}

/// <summary>
/// The edit tokens <c>modderlords.edit</c> takes (the module's HeroEditWire; a test checks the two agree). Each sets a
/// new value, never a difference.
/// </summary>
public static class HeroEdits
{
    public static string Gold(int value) => "gold=" + N(value);
    public static string HitPoints(int value) => "hp=" + N(value);
    public static string UnspentAttributePoints(int value) => "unspent_attr=" + N(value);
    public static string UnspentFocusPoints(int value) => "unspent_focus=" + N(value);
    public static string Attribute(string id, int value) => "attr." + Id(id) + "=" + N(value);
    public static string Focus(string skillId, int value) => "focus." + Id(skillId) + "=" + N(value);
    public static string Skill(string skillId, int value) => "skill." + Id(skillId) + "=" + N(value);
    public static string Trait(string id, int value) => "trait." + Id(id) + "=" + N(value);

    private static string N(int v) => v.ToString(CultureInfo.InvariantCulture);

    /// <summary>Game object ids never contain spaces or '='; one that did would split into another edit on the console.</summary>
    private static string Id(string id) =>
        id.Length == 0 || id.Any(c => char.IsWhiteSpace(c) || c == '=') ? throw new ArgumentException($"Not a usable game id: \"{id}\"") : id;
}
