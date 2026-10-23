using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace ModderLords.CompatSync.Coop.Fourberie;

/// <summary>Game objects and structs the book codec cannot write by itself: heroes, parties, settlements by id, times as ticks.</summary>
public interface IBookRefs
{
    /// <summary>Writes <paramref name="value"/> when its type is one the game side knows; false otherwise.</summary>
    bool TryEncode(object value, out JToken token);

    /// <summary>
    /// Reads a value of <paramref name="type"/>; false when the type is not one the game side knows. A known type whose
    /// object cannot be found (a party destroyed since) returns true with a null value and a problem.
    /// </summary>
    bool TryDecode(Type type, JToken token, out object? value, out string? problem);
}

/// <summary>
/// A book's persisted fields as one JSON object: field name to value. Dictionaries become JSON objects keyed by the
/// dictionary key, so <see cref="LivingEconomy.LeMirrorDelta"/> can send only the rows that changed, in both directions.
/// Only an explicit set of shapes is written (scalars, strings, enums, List&lt;T&gt;, Dictionary&lt;string|integer, T&gt;, and
/// whatever <see cref="IBookRefs"/> knows); anything else is reported and left out, never serialised by reflection.
///
/// Free of game and Coop types so the tests compile it in.
/// </summary>
public static class FbBookCodec
{
    public const int MaxCollection = 20000;

    public static JObject Encode(IEnumerable<(string Name, Type Type, object? Value)> fields, IBookRefs refs, ICollection<string> problems)
    {
        var json = new JObject();
        foreach (var (name, type, value) in fields)
        {
            if (TryEncode(type, value, refs, out var token, out var problem)) json[name] = token;
            else problems.Add(name + ": " + problem);
        }
        return json;
    }

    /// <summary>
    /// Values for <paramref name="fields"/> from <paramref name="json"/>. A field missing from the JSON, or one that cannot
    /// be read, gets <paramref name="fallback"/>(index) and a problem line; collection entries that cannot be read are
    /// dropped with a problem line.
    /// </summary>
    public static object?[] Decode(JObject json, IReadOnlyList<(string Name, Type Type)> fields, IBookRefs refs,
        ICollection<string> problems, Func<int, object?> fallback)
    {
        var values = new object?[fields.Count];
        for (var i = 0; i < fields.Count; i++)
        {
            var (name, type) = fields[i];
            var token = json[name];
            if (token == null)
            {
                values[i] = fallback(i);
                continue;
            }
            if (TryDecode(type, token, refs, problems, name, out var value)) values[i] = value;
            else values[i] = fallback(i);
        }
        return values;
    }

    /// <summary>One value of <paramref name="type"/> as JSON (relayed method arguments); false with a reason when it cannot be written.</summary>
    public static bool TryEncodeValue(Type type, object? value, IBookRefs refs, out JToken token, out string? problem) =>
        TryEncode(type, value, refs, out token, out problem);

    /// <summary>One value of <paramref name="type"/> from JSON; false (with a problem line) when it cannot be read.</summary>
    public static bool TryDecodeValue(Type type, JToken token, IBookRefs refs, ICollection<string> problems, string where, out object? value) =>
        TryDecode(type, token, refs, problems, where, out value);

    // ---- encode ------------------------------------------------------------------------------------------------

    private static bool TryEncode(Type type, object? value, IBookRefs refs, out JToken token, out string? problem)
    {
        problem = null;
        token = JValue.CreateNull();
        if (value == null) return true;
        var t = value.GetType();
        if (t == typeof(string) || t == typeof(bool) || t == typeof(int) || t == typeof(long) || t == typeof(short)
            || t == typeof(byte) || t == typeof(uint) || t == typeof(ulong) || t == typeof(ushort))
        {
            token = new JValue(value);
            return true;
        }
        if (t == typeof(float) || t == typeof(double))
        {
            var d = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            if (double.IsNaN(d) || double.IsInfinity(d)) { problem = "not a finite number"; return false; }
            token = new JValue(d);
            return true;
        }
        if (t.IsEnum)
        {
            token = new JValue(Convert.ToInt64(value, CultureInfo.InvariantCulture));
            return true;
        }
        if (refs.TryEncode(value, out token)) return true;
        if (IsDictionary(t, out var keyType, out var valueType))
        {
            if (!IsKey(keyType)) { problem = "dictionary key type " + keyType.Name + " is not supported"; return false; }
            var dict = (IDictionary)value;
            if (dict.Count > MaxCollection) { problem = "more than " + MaxCollection + " entries"; return false; }
            var obj = new JObject();
            foreach (DictionaryEntry e in dict)
            {
                if (!TryEncode(valueType, e.Value, refs, out var v, out var p)) { problem = "[" + e.Key + "] " + p; return false; }
                obj[KeyString(e.Key)] = v;
            }
            token = obj;
            return true;
        }
        if (IsList(t, out var elementType))
        {
            var list = (IList)value;
            if (list.Count > MaxCollection) { problem = "more than " + MaxCollection + " entries"; return false; }
            var arr = new JArray();
            foreach (var item in list)
            {
                if (!TryEncode(elementType, item, refs, out var v, out var p)) { problem = "item " + p; return false; }
                arr.Add(v);
            }
            token = arr;
            return true;
        }
        problem = "type " + t.Name + " is not part of the book format";
        return false;
    }

    // ---- decode ------------------------------------------------------------------------------------------------

    private static bool TryDecode(Type type, JToken token, IBookRefs refs, ICollection<string> problems, string where, out object? value)
    {
        value = null;
        if (token.Type == JTokenType.Null)
        {
            value = type.IsValueType && Nullable.GetUnderlyingType(type) == null ? Activator.CreateInstance(type) : null;
            return true;
        }
        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        try
        {
            if (underlying == typeof(string)) { if (token.Type != JTokenType.String) return Bad(problems, where, "not a string"); value = (string?)token; return true; }
            if (underlying == typeof(bool)) { if (token.Type != JTokenType.Boolean) return Bad(problems, where, "not a boolean"); value = (bool)token; return true; }
            if (underlying.IsEnum)
            {
                if (token.Type != JTokenType.Integer) return Bad(problems, where, "not an enum value");
                value = Enum.ToObject(underlying, (long)token);
                return true;
            }
            if (underlying == typeof(int) || underlying == typeof(long) || underlying == typeof(short) || underlying == typeof(byte)
                || underlying == typeof(uint) || underlying == typeof(ulong) || underlying == typeof(ushort))
            {
                if (token.Type != JTokenType.Integer) return Bad(problems, where, "not an integer");
                value = Convert.ChangeType(token.ToObject<long>(), underlying, CultureInfo.InvariantCulture);
                return true;
            }
            if (underlying == typeof(float) || underlying == typeof(double))
            {
                if (token.Type is not (JTokenType.Float or JTokenType.Integer)) return Bad(problems, where, "not a number");
                var d = (double)token;
                if (double.IsNaN(d) || double.IsInfinity(d)) return Bad(problems, where, "not a finite number");
                value = Convert.ChangeType(d, underlying, CultureInfo.InvariantCulture);
                return true;
            }
        }
        catch (Exception ex) when (ex is OverflowException or FormatException or InvalidCastException)
        {
            return Bad(problems, where, "out of range");
        }

        if (refs.TryDecode(underlying, token, out value, out var refProblem))
        {
            if (refProblem != null) problems.Add(where + ": " + refProblem);
            return true;
        }

        if (IsDictionary(underlying, out var keyType, out var valueType))
        {
            if (token is not JObject obj) return Bad(problems, where, "not an object");
            if (!IsKey(keyType)) return Bad(problems, where, "dictionary key type " + keyType.Name + " is not supported");
            if (obj.Count > MaxCollection) return Bad(problems, where, "more than " + MaxCollection + " entries");
            var dict = (IDictionary)NewCollection(underlying, typeof(Dictionary<,>));
            foreach (var prop in obj.Properties())
            {
                if (!TryKey(keyType, prop.Name, out var key)) { problems.Add(where + "[" + prop.Name + "]: bad key"); continue; }
                if (!TryDecode(valueType, prop.Value, refs, problems, where + "[" + prop.Name + "]", out var v)) continue;
                // A row whose object no longer exists is dropped rather than kept as a null the mod never wrote.
                if (v == null && prop.Value.Type != JTokenType.Null) continue;
                dict[key!] = v;
            }
            value = dict;
            return true;
        }
        if (IsList(underlying, out var elementType))
        {
            if (token is not JArray arr) return Bad(problems, where, "not an array");
            if (arr.Count > MaxCollection) return Bad(problems, where, "more than " + MaxCollection + " entries");
            var list = (IList)NewCollection(underlying, typeof(List<>));
            var i = 0;
            foreach (var item in arr)
            {
                if (TryDecode(elementType, item, refs, problems, where + "#" + i, out var v) && (v != null || item.Type == JTokenType.Null)) list.Add(v);
                i++;
            }
            value = list;
            return true;
        }
        return Bad(problems, where, "type " + underlying.Name + " is not part of the book format");
    }

    private static bool Bad(ICollection<string> problems, string where, string problem)
    {
        problems.Add(where + ": " + problem);
        return false;
    }

    // ---- shapes ------------------------------------------------------------------------------------------------

    /// <summary>
    /// Dictionary&lt;K,V&gt;, or a type derived from it. The engine's save loader hands back its own collection types
    /// (MBList) for fields declared as the plain ones, so the base type is what counts.
    /// </summary>
    private static bool IsDictionary(Type t, out Type key, out Type value)
    {
        key = value = typeof(object);
        var d = GenericBase(t, typeof(Dictionary<,>));
        if (d == null) return false;
        var args = d.GetGenericArguments();
        key = args[0];
        value = args[1];
        return true;
    }

    /// <summary>List&lt;T&gt;, or a type derived from it (MBList&lt;T&gt;).</summary>
    private static bool IsList(Type t, out Type element)
    {
        element = typeof(object);
        var l = GenericBase(t, typeof(List<>));
        if (l == null) return false;
        element = l.GetGenericArguments()[0];
        return true;
    }

    private static Type? GenericBase(Type? t, Type definition)
    {
        for (; t != null && t != typeof(object); t = t.BaseType)
            if (t.IsGenericType && t.GetGenericTypeDefinition() == definition) return t;
        return null;
    }

    /// <summary>A new, empty collection the field can hold: the declared type when it can be made, else its generic base.</summary>
    private static object NewCollection(Type declared, Type definition) =>
        !declared.IsAbstract && !declared.IsInterface && declared.GetConstructor(Type.EmptyTypes) != null
            ? Activator.CreateInstance(declared)!
            : Activator.CreateInstance(GenericBase(declared, definition)!)!;

    private static bool IsKey(Type t) => t == typeof(string) || t == typeof(int) || t == typeof(long) || t.IsEnum;

    private static string KeyString(object key) => key is IFormattable f && key is not Enum
        ? f.ToString(null, CultureInfo.InvariantCulture)
        : key is Enum ? Convert.ToInt64(key, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture) : key.ToString()!;

    private static bool TryKey(Type type, string text, out object? key)
    {
        key = null;
        if (type == typeof(string)) { key = text; return true; }
        if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) return false;
        try
        {
            key = type.IsEnum ? Enum.ToObject(type, n) : Convert.ChangeType(n, type, CultureInfo.InvariantCulture);
            return true;
        }
        catch (OverflowException) { return false; }
    }

    /// <summary>The persisted fields of a schema as (name, type) pairs, in schema order.</summary>
    public static IReadOnlyList<(string Name, Type Type)> PersistedShape(StaticBookSchema schema) =>
        schema.Persisted.Select(i => (schema.Fields[i].Name, schema.Fields[i].FieldType)).ToList();
}
