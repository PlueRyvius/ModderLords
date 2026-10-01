using System;
using System.Globalization;

namespace ModderLords.CompatSync.Coop.Admin;

/// <summary>What one edit changes.</summary>
public enum HeroEditKind
{
    Gold,
    HitPoints,
    UnspentAttributePoints,
    UnspentFocusPoints,
    Attribute,
    Focus,
    Skill,
    Trait,
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
/// <c>gold=N</c>, <c>hp=N</c>, <c>unspent_attr=N</c>, <c>unspent_focus=N</c>, <c>attr.&lt;id&gt;=N</c>,
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
            HeroEditKind.Gold => "gold=" + v,
            HeroEditKind.HitPoints => "hp=" + v,
            HeroEditKind.UnspentAttributePoints => "unspent_attr=" + v,
            HeroEditKind.UnspentFocusPoints => "unspent_focus=" + v,
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
            case "gold": edit = new HeroEdit(HeroEditKind.Gold, "", value); return true;
            case "hp": edit = new HeroEdit(HeroEditKind.HitPoints, "", value); return true;
            case "unspent_attr": edit = new HeroEdit(HeroEditKind.UnspentAttributePoints, "", value); return true;
            case "unspent_focus": edit = new HeroEdit(HeroEditKind.UnspentFocusPoints, "", value); return true;
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
