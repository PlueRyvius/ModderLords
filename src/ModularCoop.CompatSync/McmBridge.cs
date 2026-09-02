using System.Collections.Generic;

namespace ModularCoop.CompatSync;

/// <summary>Thin facade over SettingsSources kept for the existing call sites; new code should use SettingsSources directly.</summary>
public static class McmBridge
{
    public static bool Present => SettingsSources.Mcm.Present;

    public static List<SettingsSnapshot> Capture() => SettingsSources.Capture();

    public static int Apply(string settingsId, string payload, out string report) => SettingsSources.Apply(settingsId, payload, out report);

    public static int Apply(string settingsId, IDictionary<string, string> values, out string report) => SettingsSources.Apply(settingsId, values, out report);

    public static string Save(string settingsId) => SettingsSources.Save(settingsId);

    public static List<object?> Describe() => SettingsSources.Describe();
}
