using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace ModularCoop.CompatSync;

/// <summary>
/// Reads and writes MCM v5 settings by reflection (MCM is an optional dependency, so nothing here binds to its types at
/// compile time). A settings object becomes a snapshot of "propertyId\tvalue" lines for its primitive, string and enum
/// properties; applying a snapshot sets those properties through MCM's own property references so its change
/// notifications fire.
/// </summary>
internal static class McmBridge
{
    private static bool _probed;
    private static Type? _providerType;

    public static bool Present { get { Probe(); return _providerType != null; } }

    private static void Probe()
    {
        if (_probed) return;
        _probed = true;
        foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (a.IsDynamic || a.GetName().Name != "MCMv5") continue;
            _providerType = a.GetType("MCM.Abstractions.BaseSettingsProvider", false);
            if (_providerType != null) break;
        }
    }

    private static object? Provider()
    {
        Probe();
        return _providerType?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
    }

    public sealed class Snapshot
    {
        public string SettingsId = "";
        public string Payload = "";
    }

    /// <summary>All settings objects MCM knows about, serialised. Empty when MCM is absent.</summary>
    public static List<Snapshot> Capture()
    {
        var result = new List<Snapshot>();
        var provider = Provider();
        if (provider == null) return result;
        var defs = provider.GetType().GetProperty("SettingsDefinitions")?.GetValue(provider) as IEnumerable;
        if (defs == null) return result;
        foreach (var def in defs)
        {
            var id = def.GetType().GetProperty("SettingsId")?.GetValue(def) as string;
            if (string.IsNullOrEmpty(id)) continue;
            var sb = new StringBuilder();
            foreach (var prop in Properties(def))
            {
                var value = ReadValue(prop.reference);
                if (value == null) continue;
                sb.Append(prop.id).Append('\t').Append(value).Append('\n');
            }
            result.Add(new Snapshot { SettingsId = id!, Payload = sb.ToString() });
        }
        return result;
    }

    /// <summary>Applies a snapshot; returns the number of properties changed. Unknown properties are skipped.</summary>
    public static int Apply(string settingsId, string payload, out string report)
    {
        var provider = Provider();
        report = "";
        if (provider == null) return 0;
        var defs = provider.GetType().GetProperty("SettingsDefinitions")?.GetValue(provider) as IEnumerable;
        if (defs == null) return 0;
        object? target = null;
        foreach (var def in defs)
            if ((def.GetType().GetProperty("SettingsId")?.GetValue(def) as string) == settingsId) { target = def; break; }
        if (target == null) { report = "settings '" + settingsId + "' not installed on this side"; return 0; }

        var wanted = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in payload.Split('\n'))
        {
            var tab = line.IndexOf('\t');
            if (tab > 0) wanted[line.Substring(0, tab)] = line.Substring(tab + 1);
        }
        var changed = 0;
        var skipped = new List<string>();
        foreach (var prop in Properties(target))
        {
            if (!wanted.TryGetValue(prop.id, out var text)) continue;
            var current = ReadValue(prop.reference);
            if (current == text) continue;
            if (WriteValue(prop.reference, text)) changed++; else skipped.Add(prop.id);
        }
        report = changed + " changed" + (skipped.Count > 0 ? ", unsupported: " + string.Join(", ", skipped) : "");
        return changed;
    }

    // ---- reflection over MCM's definition tree -------------------------------------------------------------

    private static IEnumerable<(string id, object reference)> Properties(object settingsDefinition)
    {
        var groups = settingsDefinition.GetType().GetProperty("SettingPropertyGroups")?.GetValue(settingsDefinition) as IEnumerable;
        if (groups == null) yield break;
        foreach (var g in groups)
            foreach (var p in GroupProperties(g)) yield return p;
    }

    private static IEnumerable<(string id, object reference)> GroupProperties(object group)
    {
        var props = group.GetType().GetProperty("SettingProperties")?.GetValue(group) as IEnumerable;
        if (props != null)
            foreach (var p in props)
            {
                var id = p.GetType().GetProperty("Id")?.GetValue(p) as string;
                var reference = p.GetType().GetProperty("PropertyReference")?.GetValue(p);
                if (!string.IsNullOrEmpty(id) && reference != null) yield return (id!, reference);
            }
        var subs = group.GetType().GetProperty("SubGroups")?.GetValue(group) as IEnumerable;
        if (subs != null)
            foreach (var s in subs)
                foreach (var p in GroupProperties(s)) yield return p;
    }

    private static string? ReadValue(object reference)
    {
        try
        {
            var value = reference.GetType().GetProperty("Value")?.GetValue(reference);
            if (value == null) return null;
            switch (value)
            {
                case bool b: return b ? "true" : "false";
                case string s: return s;
                case Enum e: return e.ToString();
                case IFormattable f when IsNumeric(value): return f.ToString(null, CultureInfo.InvariantCulture);
                default: return null;   // dropdowns, colors, custom types: not carried
            }
        }
        catch { return null; }
    }

    private static bool WriteValue(object reference, string text)
    {
        try
        {
            var valueProp = reference.GetType().GetProperty("Value");
            var type = reference.GetType().GetProperty("Type")?.GetValue(reference) as Type ?? valueProp?.PropertyType;
            if (valueProp == null || type == null) return false;
            object converted;
            if (type == typeof(bool)) converted = string.Equals(text, "true", StringComparison.OrdinalIgnoreCase);
            else if (type == typeof(string)) converted = text;
            else if (type.IsEnum) converted = Enum.Parse(type, text);
            else if (IsNumericType(type)) converted = Convert.ChangeType(text, type, CultureInfo.InvariantCulture);
            else return false;
            valueProp.SetValue(reference, converted);
            return true;
        }
        catch { return false; }
    }

    private static bool IsNumeric(object v) => IsNumericType(v.GetType());
    private static bool IsNumericType(Type t) =>
        t == typeof(int) || t == typeof(float) || t == typeof(double) || t == typeof(decimal) || t == typeof(long) || t == typeof(short) || t == typeof(byte) || t == typeof(uint);
}
