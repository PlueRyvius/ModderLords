using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;

namespace ModderLords.CompatSync.Coop;

/// <summary>
/// An object's instance fields, its base types' included, as (field, kind, value) triples: s string, v primitive or
/// enum (invariant culture), plus whatever kinds the caller adds for game types. Fields are keyed "DeclaringType.name"
/// so a base and a subclass field of the same name stay apart; auto-property backing fields are included. TaleWorlds-
/// free so it can be tested; see ModKingdomDecisions.
/// </summary>
public static class DecisionFieldCodec
{
    public static IEnumerable<FieldInfo> Fields(Type type)
    {
        for (var t = type; t != null && t != typeof(object); t = t.BaseType)
            foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                if (!f.IsLiteral && (!f.Name.Contains("<") || f.Name.EndsWith(">k__BackingField", StringComparison.Ordinal)))
                    yield return f;
    }

    private static string Key(FieldInfo f) => f.DeclaringType!.Name + "." + f.Name;

    /// <summary>Null values are left out; values neither kind can carry are named in <paramref name="skipped"/>.</summary>
    public static List<string> Encode(object value, out List<string> skipped, Func<FieldInfo, object, (string Kind, string Value)?> other)
    {
        var result = new List<string>();
        skipped = new List<string>();
        foreach (var f in Fields(value.GetType()))
        {
            var v = f.GetValue(value);
            if (v == null) continue;
            var key = Key(f);
            if (v is string s) result.AddRange(new[] { key, "s", s });
            else if (f.FieldType.IsPrimitive || f.FieldType.IsEnum)
                result.AddRange(new[] { key, "v", Convert.ToString(f.FieldType.IsEnum ? Convert.ToInt64(v) : v, CultureInfo.InvariantCulture)! });
            else if (other(f, v) is { } kv) result.AddRange(new[] { key, kv.Kind, kv.Value });
            else skipped.Add(f.Name);
        }
        return result;
    }

    /// <summary>An uninitialised <paramref name="type"/> with the given fields set; unknown names are ignored.</summary>
    public static object Decode(Type type, IList<string> fields, Func<FieldInfo, string, string, object?> other)
    {
        var result = FormatterServices.GetUninitializedObject(type);
        var byKey = Fields(type).ToDictionary(Key, StringComparer.Ordinal);
        for (var i = 0; i + 2 < fields.Count; i += 3)
        {
            if (!byKey.TryGetValue(fields[i], out var f)) continue;
            var raw = fields[i + 2];
            object? value = fields[i + 1] switch
            {
                "s" => raw,
                "v" when f.FieldType.IsEnum => Enum.ToObject(f.FieldType, long.Parse(raw, CultureInfo.InvariantCulture)),
                "v" => Convert.ChangeType(raw, f.FieldType, CultureInfo.InvariantCulture),
                var kind => other(f, kind, raw),
            };
            if (value != null) f.SetValue(result, value);
        }
        return result;
    }

    /// <summary>T when <paramref name="t"/> is a list type of T that can be created empty.</summary>
    public static Type? ListElement(Type t) =>
        t.IsGenericType && t.GetGenericArguments().Length == 1 && typeof(IList).IsAssignableFrom(t) && t.GetConstructor(Type.EmptyTypes) != null
            ? t.GetGenericArguments()[0] : null;
}
