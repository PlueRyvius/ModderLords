using System.Collections.Generic;
using System.IO;

namespace ModderLords.CompatSync;

/// <summary>
/// Optional per-mod hints for the static-settings discovery, read once from recipes.json in the module folder
/// (written by the launcher from the compat record): Mods[].Settings.Include / Exclude, full type names or
/// trailing-* globs. Absent file or key = no hints. Parsed with MiniJson so this works without Coop.
/// </summary>
public static class SettingsHints
{
    public static string RecipesPath()
    {
        var bin = Path.GetDirectoryName(typeof(SettingsHints).Assembly.Location) ?? ".";
        return Path.GetFullPath(Path.Combine(bin, "..", "..", "recipes.json"));
    }

    public static void Load()
    {
        var path = RecipesPath();
        if (!File.Exists(path)) return;
        var json = File.ReadAllText(path);
        if (!LegacyRecipePolicy.Accept(json, out var refusal)) { Log.Warn(refusal); return; }
        var root = MiniJson.ParseObject(json);
        var mods = MiniJson.GetArray(root, "Mods");
        if (mods == null) return;
        var inc = 0; var exc = 0;
        foreach (var m in mods)
        {
            if (m is not Dictionary<string, object?> mod) continue;
            var settings = MiniJson.GetObject(mod, "Settings");
            if (settings == null) continue;
            foreach (var s in MiniJson.GetArray(settings, "Include") ?? new List<object?>()) if (s is string i) { StaticSettingsSource.Include.Add(i); inc++; }
            foreach (var s in MiniJson.GetArray(settings, "Exclude") ?? new List<object?>()) if (s is string e) { StaticSettingsSource.Exclude.Add(e); exc++; }
        }
        if (inc + exc > 0) Log.Info($"settings hints: {inc} include, {exc} exclude pattern(s) from recipes.json");
    }
}
