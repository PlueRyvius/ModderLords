using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace ModderLords.CompatSync;

/// <summary>Detects a new campaign (fresh start or a save loaded) by the identity of Campaign.Current, by reflection.</summary>
public static class CampaignWatch
{
    private static WeakReference? _last;
    private static bool _hadOne;

    /// <summary>Bumps when Campaign.Current becomes a different object; 0 until a campaign exists.</summary>
    public static int Generation { get; private set; }

    public static void Tick()
    {
        object? current;
        try
        {
            var t = Type.GetType("TaleWorlds.CampaignSystem.Campaign, TaleWorlds.CampaignSystem", false);
            current = t?.GetProperty("Current", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
        }
        catch { return; }
        if (current == null) { _hadOne = false; return; }
        var last = _last != null && _last.IsAlive ? _last.Target : null;
        if (ReferenceEquals(last, current) && _hadOne) return;
        _last = new WeakReference(current);
        _hadOne = true;
        Generation++;
    }
}

/// <summary>
/// Host overrides staged by the launcher in &lt;live dir&gt;\overrides.json: { Objects: { settingsId: { propId: text } } }.
/// Applied once each settings object exists (sources discover lazily), persisted best-effort, re-applied when a
/// campaign is (re)loaded in case a mod restores its own values then. Result in ack-overrides.json.
/// </summary>
public static class Overrides
{
    public const string FileName = "overrides.json";
    public const string AckFileName = "ack-overrides.json";

    private static readonly Dictionary<string, Dictionary<string, string>> _wanted = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
    private static readonly HashSet<string> _applied = new HashSet<string>(StringComparer.Ordinal);
    private static readonly List<object?> _results = new List<object?>();
    private static bool _loaded;
    private static int _generation;
    private static string? _dir;

    public static int Pending => _wanted.Count - _applied.Count;

    public static void Load(string dir)
    {
        if (_loaded) return;
        _loaded = true;
        _dir = dir;
        var path = Path.Combine(dir, FileName);
        if (!File.Exists(path)) return;
        var root = MiniJson.ParseObject(File.ReadAllText(path));
        var objects = MiniJson.GetObject(root, "Objects");
        if (objects == null) return;
        foreach (var kv in objects)
        {
            if (kv.Value is not Dictionary<string, object?> values) continue;
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var v in values) d[v.Key] = MiniJson.GetString(values, v.Key) ?? "";
            if (d.Count > 0) _wanted[kv.Key] = d;
        }
        Log.Info($"overrides: {_wanted.Count} settings object(s) staged by the launcher");
        WriteAck();
    }

    /// <summary>Called from the tick after discovery: applies what can be applied now; re-applies everything on a new campaign.</summary>
    public static void Poll()
    {
        if (_wanted.Count == 0) return;
        if (CampaignWatch.Generation != _generation)
        {
            if (_generation != 0 && _applied.Count > 0) Log.Info("overrides: campaign changed, re-applying " + _applied.Count + " settings object(s)");
            _generation = CampaignWatch.Generation;
            _applied.Clear();
            _results.Clear();
        }
        var changedAny = false;
        foreach (var kv in _wanted)
        {
            if (_applied.Contains(kv.Key)) continue;
            if (SettingsSources.Owner(kv.Key) is null) continue;
            var changed = SettingsSources.Apply(kv.Key, kv.Value, out var report);
            if (report.Contains("not created yet")) continue;       // static object known but instance still null
            var persisted = changed > 0 ? SettingsSources.Save(kv.Key) : "nothing to persist";
            _applied.Add(kv.Key);
            _results.Add(new Dictionary<string, object?> { ["SettingsId"] = kv.Key, ["Changed"] = changed, ["Report"] = report, ["Persisted"] = persisted });
            Log.Info($"override {kv.Key}: {report}; {persisted}");
            changedAny = true;
        }
        if (changedAny)
        {
            Log.Info($"{_applied.Count} override(s) applied at start ({Pending} pending)");
            WriteAck();
        }
    }

    private static void WriteAck()
    {
        if (_dir == null) return;
        try
        {
            var ack = new Dictionary<string, object?>
            {
                ["Ok"] = true,
                ["Applied"] = _results.ToList(),
                ["Pending"] = _wanted.Keys.Where(k => !_applied.Contains(k)).Cast<object?>().ToList(),
            };
            var path = Path.Combine(_dir, AckFileName);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, MiniJson.Serialize(ack));
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }
        catch (Exception ex) { Log.Warn("overrides: ack write failed: " + ex.GetBaseException().Message); }
    }
}
