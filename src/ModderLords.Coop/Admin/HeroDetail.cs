using System.Globalization;
using System.Text.Json;

namespace ModderLords.Coop.Admin;

public sealed record HeroAttribute(string Id, string Name, int Value);
public sealed record HeroSkill(string Id, string Name, string Attribute, int Value, int Focus);
/// <summary>A relative in the editor's family strip, or a hero the editor's search found.</summary>
public sealed record HeroRelative(string HeroId, string Name, bool Female, int Age, bool Alive, string Clan)
{
    public string Display => Name + (Alive ? $", {Age}" : ", dead") + (Clan.Length > 0 ? $" ({Clan})" : "");
    public override string ToString() => Display;

    /// <summary>The <c>list</c> of a <c>modderlords.findheroes</c> reply.</summary>
    public static IReadOnlyList<HeroRelative> ListFrom(AdminReply reply) =>
        reply.Root.TryGetProperty("list", out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Select(HeroDetail.RelativeFrom).Where(x => x is not null).Select(x => x!).ToList()
            : [];
}
public sealed record HeroTrait(string Id, string Name, int Value, int Min, int Max, bool Personality);

/// <summary>A player's hero as the server's <c>modderlords.hero</c> / <c>modderlords.edit</c> report it.</summary>
public sealed record HeroDetail
{
    public string SteamId { get; init; } = "";
    public string HeroId { get; init; } = "";
    public string Name { get; init; } = "";
    public string Culture { get; init; } = "";
    public int Age { get; init; }
    public int MaxAge { get; init; }
    public bool Female { get; init; }
    public int Level { get; init; }
    public int MaxLevel { get; init; }
    public int Gold { get; init; }
    public string Clan { get; init; } = "";
    /// <summary>The hero's clan's renown; -1 when the hero has no clan or the server is too old to say.</summary>
    public int Renown { get; init; } = -1;
    public int MaxRenown { get; init; }
    public int HitPoints { get; init; }
    public int MaxHitPoints { get; init; }
    public int UnspentAttributePoints { get; init; }
    public int UnspentFocusPoints { get; init; }
    public int MaxAttribute { get; init; }
    public int MaxFocus { get; init; }
    public int MaxSkill { get; init; }
    public string Busy { get; init; } = "";
    public int Perks { get; init; }
    /// <summary>False for a dead hero (an old server that does not say counts as alive).</summary>
    public bool Alive { get; init; } = true;
    /// <summary>The game's BodyProperties text (appearance DNA); empty from an old server.</summary>
    public string Body { get; init; } = "";
    public HeroRelative? Father { get; init; }
    public HeroRelative? Mother { get; init; }
    public HeroRelative? Spouse { get; init; }
    public IReadOnlyList<HeroRelative> Children { get; init; } = [];
    public IReadOnlyList<HeroRelative> Siblings { get; init; } = [];
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
            MaxAge = Int(r, "maxAge"),
            Female = r.TryGetProperty("female", out var f) && f.ValueKind == JsonValueKind.True,
            Level = Int(r, "level"),
            MaxLevel = Int(r, "maxLevel"),
            Gold = Int(r, "gold"),
            Clan = Str(r, "clan"),
            Renown = r.TryGetProperty("renown", out _) ? Int(r, "renown") : -1,
            MaxRenown = Int(r, "maxRenown"),
            HitPoints = Int(r, "hp"),
            MaxHitPoints = Int(r, "maxHp"),
            UnspentAttributePoints = Int(r, "unspentAttr"),
            UnspentFocusPoints = Int(r, "unspentFocus"),
            MaxAttribute = Int(r, "maxAttribute"),
            MaxFocus = Int(r, "maxFocus"),
            MaxSkill = Int(r, "maxSkill"),
            Busy = Str(r, "busy"),
            Perks = Int(r, "perks"),
            Alive = !r.TryGetProperty("alive", out var alive) || alive.ValueKind != JsonValueKind.False,
            Body = Str(r, "body"),
            Father = Relative(r, "father"),
            Mother = Relative(r, "mother"),
            Spouse = Relative(r, "spouse"),
            Children = Items(r, "children").Select(Relative).Where(x => x is not null).Select(x => x!).ToList(),
            Siblings = Items(r, "siblings").Select(Relative).Where(x => x is not null).Select(x => x!).ToList(),
            Attributes = Items(r, "attributes").Select(a => new HeroAttribute(Str(a, "id"), Str(a, "name"), Int(a, "value"))).ToList(),
            Skills = Items(r, "skills").Select(s => new HeroSkill(Str(s, "id"), Str(s, "name"), Str(s, "attribute"), Int(s, "value"), Int(s, "focus"))).ToList(),
            Traits = Items(r, "traits").Select(t => new HeroTrait(Str(t, "id"), Str(t, "name"), Int(t, "value"), Int(t, "min"), Int(t, "max"),
                t.TryGetProperty("personality", out var p) && p.ValueKind == JsonValueKind.True)).ToList(),
            Changes = Items(r, "changes").Where(c => c.ValueKind == JsonValueKind.String).Select(c => c.GetString()!).ToList(),
            NotApplied = Items(r, "notApplied").Where(c => c.ValueKind == JsonValueKind.String).Select(c => c.GetString()!).ToList(),
        };
    }

    private static HeroRelative? Relative(JsonElement o, string name) => o.TryGetProperty(name, out var v) ? Relative(v) : null;

    internal static HeroRelative? RelativeFrom(JsonElement v) => Relative(v);

    private static HeroRelative? Relative(JsonElement v) =>
        v.ValueKind != JsonValueKind.Object || Str(v, "heroId").Length == 0 ? null
        : new HeroRelative(Str(v, "heroId"), Str(v, "name"), v.TryGetProperty("female", out var f) && f.ValueKind == JsonValueKind.True,
            Int(v, "age"), !v.TryGetProperty("alive", out var a) || a.ValueKind != JsonValueKind.False, Str(v, "clan"));

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
    public static string Age(int value) => "age=" + N(value);
    public static string Level(int value) => "level=" + N(value);
    public static string Gold(int value) => "gold=" + N(value);
    public static string ClanRenown(int value) => "renown=" + N(value);
    public static string Female(bool female) => female ? "female=1" : "female=0";
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

/// <summary>
/// Appearance DNA as the one console argument <c>modderlords.body</c> takes (the module's HeroBodyWire; a test checks
/// the two agree): the game's BodyProperties text, as the hex of its UTF-8 bytes.
/// </summary>
public static class HeroBodies
{
    public const int MaxLength = 1000;

    /// <summary>Why this text is not appearance DNA, or null when it looks like it.</summary>
    public static string? Problem(string body)
    {
        var t = body.Trim();
        if (t.Length == 0) return "The appearance DNA is empty.";
        if (t.Length > MaxLength) return $"The appearance DNA can be at most {MaxLength} characters.";
        if (!t.StartsWith("<BodyProperties", StringComparison.Ordinal) || !t.Contains("key=\"", StringComparison.Ordinal))
            return "That is not appearance DNA: it starts with <BodyProperties and has a key=\"...\".";
        return t.Any(char.IsControl) ? "The appearance DNA must be on one line." : null;
    }

    public static string Encode(string body) =>
        Problem(body) is { } problem ? throw new ArgumentException(problem) : Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(body.Trim())).ToLowerInvariant();

    /// <summary>The DNA with line breaks and the spaces around them folded away, as pasted from a file or a forum post.</summary>
    public static string Normalize(string body) =>
        string.Join(" ", body.Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).Where(l => l.Length > 0));
}

/// <summary>
/// The arguments <c>modderlords.family</c> takes after the target (the module's FamilyWire; a test checks the two agree).
/// </summary>
public static class FamilyArgs
{
    public static string[] Marry(string heroId) => ["marry", HeroTargets.Hero(heroId)];
    public static string[] Divorce() => ["divorce"];
    public static string[] Father(string? heroId) => ["father", heroId is null ? "none" : HeroTargets.Hero(heroId)];
    public static string[] Mother(string? heroId) => ["mother", heroId is null ? "none" : HeroTargets.Hero(heroId)];
    public static string[] Clan(string clanId) =>
        clanId.Length == 0 || clanId.Any(char.IsWhiteSpace) ? throw new ArgumentException($"Not a usable clan id: \"{clanId}\"") : ["clan", clanId];
    public static string[] Adopt(string parentHeroId) => ["adopt", HeroTargets.Hero(parentHeroId)];

    /// <summary>Search text as the one console argument findheroes / findclans take: the hex of its UTF-8 bytes.</summary>
    public static string Search(string text) => Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(text.Trim())).ToLowerInvariant();
}
