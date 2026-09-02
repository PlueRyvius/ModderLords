using System;
using System.Collections.Generic;
using System.Linq;

namespace ModularCoop.CompatSync;

/// <summary>Every settings source in one place; the handlers and the live dir talk to this, never to a source directly.</summary>
public static class SettingsSources
{
    public static readonly McmSettingsSource Mcm = new McmSettingsSource();

    public static ISettingsSource[] All { get; private set; } = { Mcm };

    /// <summary>Adds a source (the static-settings source registers itself once it exists).</summary>
    public static void Register(ISettingsSource source)
    {
        if (All.Contains(source)) return;
        All = All.Concat(new[] { source }).ToArray();
    }

    public static bool AnyPresent => All.Any(s => Safe(() => s.Present, false));

    public static string Summary() => string.Join(", ", All.Select(s => s.Name + (Safe(() => s.Present, false) ? "" : " (absent)")));

    public static void Refresh()
    {
        foreach (var s in All) Safe(() => { s.Refresh(); return 0; }, 0);
    }

    public static List<SettingsSnapshot> Capture()
    {
        var result = new List<SettingsSnapshot>();
        foreach (var s in All)
            if (Safe(() => s.Present, false)) result.AddRange(Safe(() => s.Capture(), new List<SettingsSnapshot>()));
        return result;
    }

    public static ISettingsSource? Owner(string settingsId) => All.FirstOrDefault(s => Safe(() => s.Owns(settingsId), false));

    public static int Apply(string settingsId, IDictionary<string, string> values, out string report)
    {
        var owner = Owner(settingsId);
        if (owner == null) { report = "settings '" + settingsId + "' not installed on this side"; return 0; }
        try { return owner.Apply(settingsId, values, out report); }
        catch (Exception ex) { report = "apply failed: " + ex.GetBaseException().Message; return 0; }
    }

    public static int Apply(string settingsId, string payload, out string report) => Apply(settingsId, ValueConverter.ParsePayload(payload), out report);

    public static string Save(string settingsId)
    {
        var owner = Owner(settingsId);
        if (owner == null) return "not persisted: no source owns '" + settingsId + "'";
        try { return owner.Save(settingsId); }
        catch (Exception ex) { return "not persisted: " + ex.GetBaseException().Message; }
    }

    public static List<object?> Describe()
    {
        var result = new List<object?>();
        foreach (var s in All)
            if (Safe(() => s.Present, false)) result.AddRange(Safe(() => s.Describe(), new List<object?>()));
        return result;
    }

    private static T Safe<T>(Func<T> f, T fallback)
    {
        try { return f(); } catch { return fallback; }
    }
}
