using System;
using System.Globalization;

namespace ModderLords.CompatSync.Coop.Admin;

/// <summary>What one edit changes.</summary>
public enum HeroEditKind
{
    Age,
    Level,
    Gold,
    HitPoints,
    UnspentAttributePoints,
    UnspentFocusPoints,
    Attribute,
    Focus,
    Skill,
    Trait,
    ClanRenown,
    /// <summary>1 = female, 0 = male.</summary>
    Female,
}

/// <summary>One requested change: set <see cref="Kind"/> (of <see cref="Id"/>, for the per-attribute/skill/trait kinds) to <see cref="Value"/>.</summary>
public sealed class HeroEdit
{
    public HeroEditKind Kind { get; }
    public string Id { get; }
    public int Value { get; }

    public HeroEdit(HeroEditKind kind, string id, int value) { Kind = kind; Id = id; Value = value; }

    public override string ToString() => HeroEditWire.Token(Kind, Id, Value);
}

/// <summary>
/// The edits <c>modderlords.edit &lt;req&gt; &lt;steam id&gt; &lt;token&gt;...</c> takes, one token each:
/// <c>age=N</c>, <c>level=N</c>, <c>gold=N</c>, <c>hp=N</c>, <c>unspent_attr=N</c>, <c>unspent_focus=N</c>, <c>renown=N</c> (the hero's clan), <c>female=0|1</c>, <c>attr.&lt;id&gt;=N</c>,
/// <c>focus.&lt;skill id&gt;=N</c>, <c>skill.&lt;skill id&gt;=N</c>, <c>trait.&lt;id&gt;=N</c>. Every value is the new value,
/// never a difference, so sending the same edit twice does the same thing.
/// <para>No game types here: the launcher's tests compile this file to check both ends agree.</para>
/// </summary>
public static class HeroEditWire
{
    public static string Token(HeroEditKind kind, string id, int value)
    {
        var v = value.ToString(CultureInfo.InvariantCulture);
        return kind switch
        {
            HeroEditKind.Age => "age=" + v,
            HeroEditKind.Level => "level=" + v,
            HeroEditKind.Gold => "gold=" + v,
            HeroEditKind.HitPoints => "hp=" + v,
            HeroEditKind.UnspentAttributePoints => "unspent_attr=" + v,
            HeroEditKind.UnspentFocusPoints => "unspent_focus=" + v,
            HeroEditKind.ClanRenown => "renown=" + v,
            HeroEditKind.Female => "female=" + v,
            HeroEditKind.Attribute => "attr." + id + "=" + v,
            HeroEditKind.Focus => "focus." + id + "=" + v,
            HeroEditKind.Skill => "skill." + id + "=" + v,
            HeroEditKind.Trait => "trait." + id + "=" + v,
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
    }

    /// <summary>Reads one token. False, with the reason, for anything that is not exactly one of the forms above.</summary>
    public static bool TryParse(string token, out HeroEdit? edit, out string error)
    {
        edit = null;
        error = "";
        var eq = token.IndexOf('=');
        if (eq <= 0 || eq == token.Length - 1) { error = $"\"{token}\" is not key=value."; return false; }
        var key = token.Substring(0, eq);
        if (!int.TryParse(token.Substring(eq + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value))
        {
            error = $"\"{token}\": the value must be a whole number.";
            return false;
        }

        switch (key)
        {
            case "age": edit = new HeroEdit(HeroEditKind.Age, "", value); return true;
            case "level": edit = new HeroEdit(HeroEditKind.Level, "", value); return true;
            case "gold": edit = new HeroEdit(HeroEditKind.Gold, "", value); return true;
            case "hp": edit = new HeroEdit(HeroEditKind.HitPoints, "", value); return true;
            case "unspent_attr": edit = new HeroEdit(HeroEditKind.UnspentAttributePoints, "", value); return true;
            case "unspent_focus": edit = new HeroEdit(HeroEditKind.UnspentFocusPoints, "", value); return true;
            case "renown": edit = new HeroEdit(HeroEditKind.ClanRenown, "", value); return true;
            case "female": edit = new HeroEdit(HeroEditKind.Female, "", value); return true;
        }

        var dot = key.IndexOf('.');
        var id = dot > 0 ? key.Substring(dot + 1) : "";
        if (id.Length == 0) { error = $"\"{token}\": unknown edit \"{key}\"."; return false; }
        HeroEditKind kind;
        switch (key.Substring(0, dot))
        {
            case "attr": kind = HeroEditKind.Attribute; break;
            case "focus": kind = HeroEditKind.Focus; break;
            case "skill": kind = HeroEditKind.Skill; break;
            case "trait": kind = HeroEditKind.Trait; break;
            default: error = $"\"{token}\": unknown edit \"{key}\"."; return false;
        }
        edit = new HeroEdit(kind, id, value);
        return true;
    }
}

/// <summary>
/// Which hero a command is about: a player's Steam id (their own hero), or <c>hero:&lt;hero string id&gt;</c> for any
/// other hero in the world, living or dead (a companion, a relative, someone who married out of the clan).
/// </summary>
public static class HeroTargetWire
{
    public const string HeroPrefix = "hero:";

    public static string ForHero(string heroStringId) => HeroPrefix + heroStringId;

    /// <summary>True, with the hero's string id, when the target names a hero rather than a player.</summary>
    public static bool IsHero(string target, out string heroStringId)
    {
        var yes = target.StartsWith(HeroPrefix, StringComparison.Ordinal) && target.Length > HeroPrefix.Length;
        heroStringId = yes ? target.Substring(HeroPrefix.Length) : "";
        return yes;
    }
}

/// <summary>
/// A hero's new name as one console argument for <c>modderlords.rename</c>. The console splits its arguments on
/// spaces, so the name travels as the hex of its UTF-8 bytes.
/// </summary>
public static class HeroNameWire
{
    public const int MaxLength = 40;

    /// <summary>Why this name cannot be used, or null when it can. Braces are the game's own text markup.</summary>
    public static string? Problem(string name)
    {
        if (name.Trim().Length == 0) return "A name cannot be empty.";
        if (name.Trim().Length > MaxLength) return $"A name can be at most {MaxLength} characters.";
        foreach (var c in name)
            if (char.IsControl(c) || c == '{' || c == '}') return "A name cannot contain line breaks, tabs or { }.";
        return null;
    }

    public static string Encode(string name) => Hex(name.Trim());

    internal static string Hex(string value)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(value);
        var text = new System.Text.StringBuilder(bytes.Length * 2);
        foreach (var b in bytes) text.Append(b.ToString("x2", CultureInfo.InvariantCulture));
        return text.ToString();
    }

    internal static bool TryUnhex(string hex, out string value)
    {
        value = "";
        if (hex.Length == 0 || hex.Length % 2 != 0) return false;
        var bytes = new byte[hex.Length / 2];
        for (var i = 0; i < bytes.Length; i++)
            if (!byte.TryParse(hex.Substring(i * 2, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out bytes[i])) return false;
        try { value = new System.Text.UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes); }
        catch (ArgumentException) { return false; }
        return true;
    }

    /// <summary>Reads an encoded name. False, with the reason, when it is not hex of valid UTF-8 or not a usable name.</summary>
    public static bool TryDecode(string hex, out string name, out string error)
    {
        error = "That is not a name the launcher sent.";
        if (!TryUnhex(hex, out name)) { name = ""; return false; }
        if (Problem(name) is { } problem) { error = problem; name = ""; return false; }
        error = "";
        return true;
    }
}

/// <summary>
/// A hero's appearance DNA for <c>modderlords.body</c>: the game's own BodyProperties text
/// (<c>&lt;BodyProperties version="4" age=".." weight=".." build=".." key="..." /&gt;</c>, what its character editor copies).
/// It travels as one console argument, as the hex of its UTF-8 bytes.
/// </summary>
public static class HeroBodyWire
{
    public const int MaxLength = 1000;

    /// <summary>Why this text is not appearance DNA, or null when it looks like it. The game itself reads it afterwards.</summary>
    public static string? Problem(string body)
    {
        var t = body.Trim();
        if (t.Length == 0) return "The appearance DNA is empty.";
        if (t.Length > MaxLength) return $"The appearance DNA can be at most {MaxLength} characters.";
        if (!t.StartsWith("<BodyProperties", StringComparison.Ordinal) || t.IndexOf("key=\"", StringComparison.Ordinal) < 0)
            return "That is not appearance DNA: it starts with <BodyProperties and has a key=\"...\".";
        foreach (var c in t)
            if (char.IsControl(c)) return "The appearance DNA must be on one line.";
        return null;
    }

    public static string Encode(string body) => HeroNameWire.Hex(body.Trim());

    /// <summary>Reads an encoded DNA string. False, with the reason, when it is not hex of valid UTF-8 or not DNA.</summary>
    public static bool TryDecode(string hex, out string body, out string error)
    {
        body = "";
        if (!HeroNameWire.TryUnhex(hex, out var text)) { error = "That is not appearance DNA the launcher sent."; return false; }
        if (Problem(text) is { } problem) { error = problem; return false; }
        body = text.Trim();
        error = "";
        return true;
    }
}
