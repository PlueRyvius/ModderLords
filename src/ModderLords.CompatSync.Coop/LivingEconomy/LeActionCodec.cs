using System;
using System.Collections.Generic;
using System.Globalization;

namespace ModderLords.CompatSync.Coop.LivingEconomy;

/// <summary>
/// How a Living Economy action's arguments travel, free of game and Coop types so the tests compile it in.
///
/// A relayed action is one of the mod's own methods, called again on the server with the same arguments. Each
/// argument is sent as text: numbers and flags in invariant culture, the mod's enums (policies, projects, estate
/// actions) as their underlying number, and game objects (the settlement) as their StringId, which the layer resolves.
/// The player's hero is never sent: the server always substitutes the sender's own hero.
/// </summary>
public static class LeActionCodec
{
    /// <summary>Encodes one primitive or enum value. False for anything else (game objects are the layer's job).</summary>
    public static bool TryEncodeValue(object? value, out string text)
    {
        text = "";
        switch (value)
        {
            case null: return false;
            case bool b: text = b ? "1" : "0"; return true;
            case int i: text = i.ToString(CultureInfo.InvariantCulture); return true;
            case long l: text = l.ToString(CultureInfo.InvariantCulture); return true;
            case float f: text = f.ToString("R", CultureInfo.InvariantCulture); return true;
            case string s: text = s; return true;
        }
        var type = value.GetType();
        if (type.IsEnum)
        {
            text = Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
            return true;
        }
        return false;
    }

    /// <summary>Decodes text back into a value of <paramref name="type"/> (primitive, string or enum). An enum value must be defined.</summary>
    public static bool TryDecodeValue(string text, Type type, out object? value)
    {
        value = null;
        if (type == typeof(string)) { value = text; return true; }
        if (type == typeof(bool)) { value = text == "1"; return text == "1" || text == "0"; }
        if (type == typeof(int))
        {
            if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)) return false;
            value = i;
            return true;
        }
        if (type == typeof(long))
        {
            if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)) return false;
            value = l;
            return true;
        }
        if (type == typeof(float))
        {
            if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var f)) return false;
            value = f;
            return true;
        }
        if (type.IsEnum)
        {
            if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) return false;
            var boxed = Enum.ToObject(type, n);
            if (!Enum.IsDefined(type, boxed)) return false;
            value = boxed;
            return true;
        }
        return false;
    }

    /// <summary>The op sent for a method: "Type::Method". Overloads are told apart by the spec table, not the id.</summary>
    public static string OpFor(string declaringType, string method) => declaringType + "::" + method;

    /// <summary>
    /// Sanity bounds for gold amounts a player sends. The mod checks affordability itself; this only refuses values no
    /// menu of the mod ever offers (negative, or beyond any purse).
    /// </summary>
    public static bool IsPlausibleAmount(int amount) => amount > 0 && amount <= 10_000_000;

    /// <summary>Joins message lines for the result's Data list: "colour|text".</summary>
    public static string Line(uint color, string text) => color.ToString(CultureInfo.InvariantCulture) + "|" + text;

    public static bool TrySplitLine(string line, out uint color, out string text)
    {
        color = 0;
        text = "";
        var bar = line.IndexOf('|');
        if (bar <= 0 || !uint.TryParse(line.Substring(0, bar), NumberStyles.Integer, CultureInfo.InvariantCulture, out color)) return false;
        text = line.Substring(bar + 1);
        return true;
    }

    /// <summary>Keeps at most <paramref name="max"/> lines so one action can never flood a player's screen.</summary>
    public static List<string> Cap(List<string> lines, int max = 6) => lines.Count <= max ? lines : lines.GetRange(0, max);
}
