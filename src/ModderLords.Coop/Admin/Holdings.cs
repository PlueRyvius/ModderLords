using System.Text;
using System.Text.Json;

namespace ModderLords.Coop.Admin;

/// <summary>A hero of a player's clan other than the player, as the server's <c>modderlords.companions</c> reports them.</summary>
public sealed record AdminCompanion(string HeroId, string Name, string Kind, string Owner, string Clan, int Level, int Age, string Where, string Busy)
{
    /// <summary>The companions in a <c>companions</c> reply.</summary>
    public static IReadOnlyList<AdminCompanion> From(AdminReply reply) =>
        Json.Items(reply.Root, "list").Select(c => new AdminCompanion(
            Json.Str(c, "heroId"), Json.Str(c, "name"), Json.Str(c, "kind"), Json.Str(c, "owner"), Json.Str(c, "clan"),
            Json.Int(c, "level"), Json.Int(c, "age"), Json.Str(c, "where"), Json.Str(c, "busy"))).ToList();
}

/// <summary>A town or castle and who owns it.</summary>
public sealed record Holding(string Id, string Name, string Type, string ClanId, string Clan, string Kingdom, int Villages, string Busy);

/// <summary>A clan that can own a town or castle.</summary>
public sealed record HoldingClan(string Id, string Name, string Kingdom, string Leader, bool Player);

/// <summary>What the server's <c>modderlords.holdings</c> / <c>modderlords.setowner</c> report.</summary>
public sealed record HoldingsList(IReadOnlyList<Holding> Settlements, IReadOnlyList<HoldingClan> Clans, string Changed)
{
    public static HoldingsList From(AdminReply reply) => new(
        Json.Items(reply.Root, "settlements").Select(s => new Holding(
            Json.Str(s, "id"), Json.Str(s, "name"), Json.Str(s, "type"), Json.Str(s, "clanId"), Json.Str(s, "clan"), Json.Str(s, "kingdom"),
            Json.Int(s, "villages"), Json.Str(s, "busy"))).ToList(),
        Json.Items(reply.Root, "clans").Select(c => new HoldingClan(
            Json.Str(c, "id"), Json.Str(c, "name"), Json.Str(c, "kingdom"), Json.Str(c, "leader"),
            c.TryGetProperty("player", out var p) && p.ValueKind == JsonValueKind.True)).ToList(),
        Json.Str(reply.Root, "changed"));
}

/// <summary>
/// Which hero a command is about (the module's HeroTargetWire; a test checks the two agree): a player's Steam id for
/// their own hero, or <c>hero:&lt;id&gt;</c> for a companion or family member.
/// </summary>
public static class HeroTargets
{
    public static string Hero(string heroId) =>
        heroId.Length == 0 || heroId.Any(char.IsWhiteSpace) ? throw new ArgumentException($"Not a usable hero id: \"{heroId}\"") : "hero:" + heroId;
}

/// <summary>
/// A hero's new name as the one console argument <c>modderlords.rename</c> takes (the module's HeroNameWire; a test
/// checks the two agree). The console splits arguments on spaces, so the name travels as the hex of its UTF-8 bytes.
/// </summary>
public static class HeroNames
{
    public const int MaxLength = 40;

    /// <summary>Why this name cannot be used, or null when it can.</summary>
    public static string? Problem(string name)
    {
        if (name.Trim().Length == 0) return "A name cannot be empty.";
        if (name.Trim().Length > MaxLength) return $"A name can be at most {MaxLength} characters.";
        return name.Any(c => char.IsControl(c) || c is '{' or '}') ? "A name cannot contain line breaks, tabs or { }." : null;
    }

    public static string Encode(string name) =>
        Problem(name) is { } problem ? throw new ArgumentException(problem) : Convert.ToHexString(Encoding.UTF8.GetBytes(name.Trim())).ToLowerInvariant();
}

internal static class Json
{
    public static IEnumerable<JsonElement> Items(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object) : [];

    public static string Str(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    public static int Int(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : 0;
}
