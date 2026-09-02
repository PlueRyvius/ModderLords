using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;

namespace ModularCoop.CompatSync;

/// <summary>
/// Reads and writes MCM v5 settings by reflection (MCM is an optional dependency, so nothing here binds to its types at
/// compile time). A settings object becomes a snapshot of "propertyId\tvalue" lines for its primitive, string and enum
/// properties; applying a snapshot sets those properties through MCM's own property references so its change
/// notifications fire.
/// </summary>
public static class McmBridge
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
        var wanted = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in payload.Split('\n'))
        {
            var tab = line.IndexOf('\t');
            if (tab > 0) wanted[line.Substring(0, tab)] = line.Substring(tab + 1);
        }
        return Apply(settingsId, wanted, out report);
    }

    /// <summary>Applies property values by id; returns the number changed. Unknown ids are ignored, unsupported kinds reported.</summary>
    public static int Apply(string settingsId, IDictionary<string, string> wanted, out string report)
    {
        var provider = Provider();
        report = "";
        if (provider == null) { report = "MCM not present"; return 0; }
        var target = FindDefinition(provider, settingsId);
        if (target == null) { report = "settings '" + settingsId + "' not installed on this side"; return 0; }

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

    private static object? FindDefinition(object provider, string settingsId)
    {
        var defs = provider.GetType().GetProperty("SettingsDefinitions")?.GetValue(provider) as IEnumerable;
        if (defs == null) return null;
        foreach (var def in defs)
            if ((def.GetType().GetProperty("SettingsId")?.GetValue(def) as string) == settingsId) return def;
        return null;
    }

    /// <summary>
    /// Persists a settings object through MCM's own provider (GetSettings + SaveSettings, found by name) so the value
    /// survives a restart. Returns a one-line result; never throws.
    /// </summary>
    public static string Save(string settingsId)
    {
        try
        {
            var provider = Provider();
            if (provider == null) return "not persisted: MCM not present";
            var type = provider.GetType();
            var get = type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(m => m.Name == "GetSettings" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(string));
            var save = type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(m => m.Name == "SaveSettings" && m.GetParameters().Length == 1);
            if (get == null || save == null) return "not persisted: provider has no GetSettings/SaveSettings";
            var settings = get.Invoke(provider, new object[] { settingsId });
            if (settings == null) return "not persisted: GetSettings returned null";
            save.Invoke(provider, new[] { settings });
            return "persisted";
        }
        catch (Exception ex) { return "not persisted: " + ex.GetBaseException().Message; }
    }

    // ---- description for the host-side editor --------------------------------------------------------------

    /// <summary>
    /// Every settings object with display names, groups, kinds, ranges and choices, as plain dictionaries for
    /// MiniJson. Unsupported kinds (dropdowns, colours, buttons) are listed with Editable=false. All best-effort:
    /// a member MCM renames simply reads as null.
    /// </summary>
    public static List<object?> Describe()
    {
        var result = new List<object?>();
        var provider = Provider();
        if (provider == null) return result;
        var defs = provider.GetType().GetProperty("SettingsDefinitions")?.GetValue(provider) as IEnumerable;
        if (defs == null) return result;
        foreach (var def in defs)
        {
            var id = Str(def, "SettingsId");
            if (string.IsNullOrEmpty(id)) continue;
            var obj = new Dictionary<string, object?>
            {
                ["SettingsId"] = id,
                ["DisplayName"] = Str(def, "DisplayName") ?? id,
                ["Folder"] = Str(def, "FolderName"),
            };
            var groups = new List<object?>();
            var groupDefs = def.GetType().GetProperty("SettingPropertyGroups")?.GetValue(def) as IEnumerable;
            if (groupDefs != null) foreach (var g in groupDefs) DescribeGroup(g, "", groups);
            obj["Groups"] = groups;
            result.Add(obj);
        }
        return result;
    }

    private static void DescribeGroup(object group, string prefix, List<object?> into)
    {
        var name = Str(group, "GroupName") ?? Str(group, "DisplayGroupName") ?? "General";
        var full = prefix.Length == 0 ? name : prefix + " / " + name;
        var props = new List<object?>();
        var propDefs = group.GetType().GetProperty("SettingProperties")?.GetValue(group) as IEnumerable;
        if (propDefs != null)
            foreach (var p in propDefs)
            {
                var d = DescribeProperty(p);
                if (d != null) props.Add(d);
            }
        if (props.Count > 0) into.Add(new Dictionary<string, object?> { ["Name"] = full, ["Properties"] = props });
        var subs = group.GetType().GetProperty("SubGroups")?.GetValue(group) as IEnumerable;
        if (subs != null) foreach (var s in subs) DescribeGroup(s, full, into);
    }

    private static Dictionary<string, object?>? DescribeProperty(object p)
    {
        var id = Str(p, "Id");
        var reference = p.GetType().GetProperty("PropertyReference")?.GetValue(p);
        if (string.IsNullOrEmpty(id) || reference == null) return null;
        var type = reference.GetType().GetProperty("Type")?.GetValue(reference) as Type
                   ?? reference.GetType().GetProperty("Value")?.PropertyType;
        var kind = "unsupported";
        var editable = false;
        List<string>? choices = null;
        if (type == typeof(bool)) { kind = "bool"; editable = true; }
        else if (type == typeof(string)) { kind = "string"; editable = true; }
        else if (type != null && type.IsEnum) { kind = "enum"; editable = true; choices = new List<string>(Enum.GetNames(type)); }
        else if (type == typeof(int) || type == typeof(long) || type == typeof(short) || type == typeof(byte) || type == typeof(uint)) { kind = "int"; editable = true; }
        else if (type == typeof(float) || type == typeof(double) || type == typeof(decimal)) { kind = "float"; editable = true; }
        else if (type != null) kind = type.Name;

        var value = ReadValue(reference);
        if (value == null)
        {
            try { value = reference.GetType().GetProperty("Value")?.GetValue(reference)?.ToString(); } catch { }
        }
        var d = new Dictionary<string, object?>
        {
            ["Id"] = id,
            ["DisplayName"] = Str(p, "DisplayName") ?? id,
            ["Hint"] = Str(p, "HintText"),
            ["Kind"] = kind,
            ["Value"] = value,
            ["Editable"] = editable,
        };
        if (kind == "int" || kind == "float")
        {
            var min = Num(p, "MinValue");
            var max = Num(p, "MaxValue");
            if (min != null && max != null && (min != 0 || max != 0)) { d["Min"] = min; d["Max"] = max; }
        }
        if (choices != null) d["Choices"] = choices;
        var restart = p.GetType().GetProperty("RequireRestart")?.GetValue(p);
        if (restart is bool rb && rb) d["RequireRestart"] = true;
        return d;
    }

    private static string? Str(object o, string prop)
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

    private static double? Num(object o, string prop)
    {
        try
        {
            var v = o.GetType().GetProperty(prop)?.GetValue(o);
            return v == null ? (double?)null : Convert.ToDouble(v, CultureInfo.InvariantCulture);
        }
        catch { return null; }
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
