using System;
using System.Collections.Generic;
using System.Globalization;

namespace ModularCoop.CompatSync;

/// <summary>
/// Text form of setting values shared by every source, the wire and the launcher: bool "true"/"false", enums by
/// name, numbers invariant, strings verbatim. Engine-free (also compiled into the launcher's tests).
/// </summary>
public static class ValueConverter
{
    public static bool IsNumericType(Type t) =>
        t == typeof(int) || t == typeof(float) || t == typeof(double) || t == typeof(decimal) || t == typeof(long)
        || t == typeof(short) || t == typeof(byte) || t == typeof(uint) || t == typeof(ulong) || t == typeof(ushort) || t == typeof(sbyte);

    public static bool IsIntegerType(Type t) =>
        t == typeof(int) || t == typeof(long) || t == typeof(short) || t == typeof(byte) || t == typeof(uint) || t == typeof(ulong) || t == typeof(ushort) || t == typeof(sbyte);

    public static bool IsSupported(Type? t) => t != null && (t == typeof(bool) || t == typeof(string) || t.IsEnum || IsNumericType(t));

    /// <summary>bool | int | float | string | enum, or the CLR type name for anything the bridge cannot carry.</summary>
    public static string Kind(Type? t)
    {
        if (t == null) return "unsupported";
        if (t == typeof(bool)) return "bool";
        if (t == typeof(string)) return "string";
        if (t.IsEnum) return "enum";
        if (IsIntegerType(t)) return "int";
        if (IsNumericType(t)) return "float";
        return t.Name;
    }

    /// <summary>Text for a value, or null when the value's type is not carried.</summary>
    public static string? Format(object? value)
    {
        switch (value)
        {
            case null: return null;
            case bool b: return b ? "true" : "false";
            case string s: return s;
            case Enum e: return e.ToString();
            case IFormattable f when IsNumericType(value.GetType()): return f.ToString(null, CultureInfo.InvariantCulture);
            default: return null;
        }
    }

    public static bool TryParse(string text, Type type, out object? value)
    {
        value = null;
        try
        {
            if (type == typeof(bool))
            {
                if (string.Equals(text, "true", StringComparison.OrdinalIgnoreCase)) { value = true; return true; }
                if (string.Equals(text, "false", StringComparison.OrdinalIgnoreCase)) { value = false; return true; }
                return false;
            }
            if (type == typeof(string)) { value = text; return true; }
            if (type.IsEnum) { value = Enum.Parse(type, text.Trim(), ignoreCase: true); return true; }
            if (IsNumericType(type)) { value = Convert.ChangeType(text.Trim(), type, CultureInfo.InvariantCulture); return true; }
            return false;
        }
        catch { return false; }
    }

    /// <summary>Reads a ref as text; null when unsupported or the getter throws.</summary>
    public static string? Read(PropertyRef r)
    {
        try { return Format(r.Get()); } catch { return null; }
    }

    /// <summary>Writes text into a ref; false when unsupported, unparsable or the setter throws.</summary>
    public static bool Write(PropertyRef r, string text)
    {
        try
        {
            var type = r.ValueType;
            if (type == null || !r.CanWrite || !TryParse(text, type, out var v) || v == null) return false;
            r.Set(v);
            return true;
        }
        catch { return false; }
    }

    /// <summary>The editor entry for one value (LiveSettingsProperty on the launcher side).</summary>
    public static Dictionary<string, object?> Describe(PropertyRef r, string displayName, string? hint, double? min, double? max, bool requireRestart, bool? editableOverride = null)
    {
        var type = r.ValueType;
        var kind = Kind(type);
        var editable = editableOverride ?? (IsSupported(type) && r.CanWrite);
        var value = Read(r);
        if (value == null)
        {
            try { value = r.Get()?.ToString(); } catch { value = null; }
        }
        var d = new Dictionary<string, object?>
        {
            ["Id"] = r.Id,
            ["DisplayName"] = displayName,
            ["Hint"] = hint,
            ["Kind"] = kind,
            ["Value"] = value,
            ["Editable"] = editable,
        };
        if ((kind == "int" || kind == "float") && min != null && max != null && (min != 0 || max != 0)) { d["Min"] = min; d["Max"] = max; }
        if (type != null && type.IsEnum) d["Choices"] = new List<string>(Enum.GetNames(type));
        if (requireRestart) d["RequireRestart"] = true;
        return d;
    }

    /// <summary>"propId\tvalue\n" lines → dictionary.</summary>
    public static Dictionary<string, string> ParsePayload(string payload)
    {
        var wanted = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in payload.Split('\n'))
        {
            var tab = line.IndexOf('\t');
            if (tab > 0) wanted[line.Substring(0, tab)] = line.Substring(tab + 1);
        }
        return wanted;
    }

    /// <summary>Applies values onto refs; the shared body of every source's Apply.</summary>
    public static int ApplyTo(IEnumerable<PropertyRef> refs, IDictionary<string, string> wanted, out string report)
    {
        var changed = 0;
        var skipped = new List<string>();
        foreach (var r in refs)
        {
            if (!wanted.TryGetValue(r.Id, out var text)) continue;
            var current = Read(r);
            if (current == text) continue;
            if (Write(r, text)) changed++; else skipped.Add(r.Id);
        }
        report = changed + " changed" + (skipped.Count > 0 ? ", unsupported: " + string.Join(", ", skipped) : "");
        return changed;
    }

    /// <summary>Snapshot payload of a set of refs (unsupported values are left out).</summary>
    public static string Payload(IEnumerable<PropertyRef> refs)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var r in refs)
        {
            var value = Read(r);
            if (value == null) continue;
            sb.Append(r.Id).Append('\t').Append(value).Append('\n');
        }
        return sb.ToString();
    }

    // ---- reflection helpers shared by the sources ----------------------------------------------------------

    public static string? Str(object o, string prop)
    {
        try
        {
            var v = o.GetType().GetProperty(prop)?.GetValue(o);
            if (v == null) return null;
            var s = v as string ?? v.ToString();
            return string.IsNullOrWhiteSpace(s) ? null : s;
        }
        catch { return null; }
    }

    public static double? Num(object o, string prop)
    {
        try
        {
            var v = o.GetType().GetProperty(prop)?.GetValue(o);
            return v == null ? (double?)null : Convert.ToDouble(v, CultureInfo.InvariantCulture);
        }
        catch { return null; }
    }
}
