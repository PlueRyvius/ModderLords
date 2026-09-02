using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace ModularCoop.CompatSync;

/// <summary>
/// MCM v5 settings by reflection (MCM is an optional dependency, so nothing here binds to its types at compile
/// time). SettingsIds are MCM's own; values go through MCM's PropertyReference so its change notifications fire.
/// </summary>
public sealed class McmSettingsSource : ISettingsSource
{
    private bool _probed;
    private Type? _providerType;

    public string Name => "MCM";
    public bool Present { get { Probe(); return _providerType != null; } }
    public bool Owns(string settingsId) => !settingsId.Contains(":") && Present && Provider() is { } p && FindDefinition(p, settingsId) != null;
    public void Refresh() { }

    private void Probe()
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

    private object? Provider()
    {
        Probe();
        return _providerType?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
    }

    private static IEnumerable Definitions(object provider) =>
        provider.GetType().GetProperty("SettingsDefinitions")?.GetValue(provider) as IEnumerable ?? Array.Empty<object>();

    private static object? FindDefinition(object provider, string settingsId)
    {
        foreach (var def in Definitions(provider))
            if ((def.GetType().GetProperty("SettingsId")?.GetValue(def) as string) == settingsId) return def;
        return null;
    }

    public List<SettingsSnapshot> Capture()
    {
        var result = new List<SettingsSnapshot>();
        var provider = Provider();
        if (provider == null) return result;
        foreach (var def in Definitions(provider))
        {
            var id = def.GetType().GetProperty("SettingsId")?.GetValue(def) as string;
            if (string.IsNullOrEmpty(id)) continue;
            result.Add(new SettingsSnapshot { SettingsId = id!, Payload = ValueConverter.Payload(Properties(def).Select(p => p.reference)) });
        }
        return result;
    }

    public int Apply(string settingsId, IDictionary<string, string> values, out string report)
    {
        var provider = Provider();
        if (provider == null) { report = "MCM not present"; return 0; }
        var target = FindDefinition(provider, settingsId);
        if (target == null) { report = "settings '" + settingsId + "' not installed on this side"; return 0; }
        return ValueConverter.ApplyTo(Properties(target).Select(p => p.reference), values, out report);
    }

    /// <summary>GetSettings + SaveSettings on MCM's provider, found by name. Never throws.</summary>
    public string Save(string settingsId)
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

    public List<object?> Describe()
    {
        var result = new List<object?>();
        var provider = Provider();
        if (provider == null) return result;
        foreach (var def in Definitions(provider))
        {
            var id = ValueConverter.Str(def, "SettingsId");
            if (string.IsNullOrEmpty(id)) continue;
            var obj = new Dictionary<string, object?>
            {
                ["SettingsId"] = id,
                ["DisplayName"] = ValueConverter.Str(def, "DisplayName") ?? id,
                ["Folder"] = ValueConverter.Str(def, "FolderName"),
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
        var name = ValueConverter.Str(group, "GroupName") ?? ValueConverter.Str(group, "DisplayGroupName") ?? "General";
        var full = prefix.Length == 0 ? name : prefix + " / " + name;
        var props = new List<object?>();
        var propDefs = group.GetType().GetProperty("SettingProperties")?.GetValue(group) as IEnumerable;
        if (propDefs != null)
            foreach (var p in propDefs)
            {
                var id = p.GetType().GetProperty("Id")?.GetValue(p) as string;
                var reference = p.GetType().GetProperty("PropertyReference")?.GetValue(p);
                if (string.IsNullOrEmpty(id) || reference == null) continue;
                var r = new McmPropertyRef(id!, reference);
                var restart = p.GetType().GetProperty("RequireRestart")?.GetValue(p) is bool rb && rb;
                props.Add(ValueConverter.Describe(r, ValueConverter.Str(p, "DisplayName") ?? id!, ValueConverter.Str(p, "HintText"),
                    ValueConverter.Num(p, "MinValue"), ValueConverter.Num(p, "MaxValue"), restart));
            }
        if (props.Count > 0) into.Add(new Dictionary<string, object?> { ["Name"] = full, ["Properties"] = props });
        var subs = group.GetType().GetProperty("SubGroups")?.GetValue(group) as IEnumerable;
        if (subs != null) foreach (var s in subs) DescribeGroup(s, full, into);
    }

    // ---- reflection over MCM's definition tree -------------------------------------------------------------

    private static IEnumerable<(string id, PropertyRef reference)> Properties(object settingsDefinition)
    {
        var groups = settingsDefinition.GetType().GetProperty("SettingPropertyGroups")?.GetValue(settingsDefinition) as IEnumerable;
        if (groups == null) yield break;
        foreach (var g in groups)
            foreach (var p in GroupProperties(g)) yield return p;
    }

    private static IEnumerable<(string id, PropertyRef reference)> GroupProperties(object group)
    {
        var props = group.GetType().GetProperty("SettingProperties")?.GetValue(group) as IEnumerable;
        if (props != null)
            foreach (var p in props)
            {
                var id = p.GetType().GetProperty("Id")?.GetValue(p) as string;
                var reference = p.GetType().GetProperty("PropertyReference")?.GetValue(p);
                if (!string.IsNullOrEmpty(id) && reference != null) yield return (id!, new McmPropertyRef(id!, reference));
            }
        var subs = group.GetType().GetProperty("SubGroups")?.GetValue(group) as IEnumerable;
        if (subs != null)
            foreach (var s in subs)
                foreach (var p in GroupProperties(s)) yield return p;
    }
}
