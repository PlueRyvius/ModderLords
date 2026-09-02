using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ModularCoop.CompatSync;

/// <summary>
/// The host-side live channel: a directory the launcher names in MODULARCOOP_LIVE_DIR. On each tick the module
/// (1) applies any apply-*.json requests the launcher dropped there, answering each with ack-*.json, and
/// (2) rewrites settings.json whenever the described MCM settings changed. The existing broadcast tick then fans
/// the new values out to players. Absent env var = feature off. Everything here is best-effort and never throws
/// into the engine.
/// </summary>
public static class LiveSettings
{
    public const string EnvVar = "MODULARCOOP_LIVE_DIR";
    public const string SettingsFileName = "settings.json";
    public const string RequestPrefix = "apply-";
    public const string AckPrefix = "ack-";
    public const int SchemaVersion = 1;

    private static string? _dir;
    private static bool _initTried;
    private static string? _lastBody;
    private static int _describeFailures;

    public static bool Enabled { get { Init(); return _dir != null; } }
    public static string? Dir { get { Init(); return _dir; } }

    public static void Init()
    {
        if (_initTried) return;
        _initTried = true;
        try
        {
            var dir = Environment.GetEnvironmentVariable(EnvVar);
            if (string.IsNullOrWhiteSpace(dir)) return;
            Directory.CreateDirectory(dir);
            _dir = dir;
            Log.Info("live settings dir: " + dir);
        }
        catch (Exception ex) { Log.Warn("live settings disabled: " + ex.GetBaseException().Message); }
    }

    /// <summary>Called from the 3 s tick before the broadcast tick, so an applied change goes out on the same tick.</summary>
    public static void Poll()
    {
        if (!Enabled) return;
        try { ProcessRequests(); }
        catch (Exception ex) { Log.Warn("live settings: request pass failed: " + ex.GetBaseException().Message); }
        try { WriteDescriptionIfChanged(); }
        catch (Exception ex)
        {
            if (_describeFailures++ < 3) Log.Warn("live settings: describe failed: " + ex.GetBaseException().Message);
        }
    }

    // ---- launcher -> engine ------------------------------------------------------------------------------------

    private static void ProcessRequests()
    {
        var files = Directory.GetFiles(_dir!, RequestPrefix + "*.json");
        if (files.Length == 0) return;
        Array.Sort(files, StringComparer.Ordinal);   // zero-padded ticks: name order == request order
        foreach (var file in files)
        {
            var token = Path.GetFileNameWithoutExtension(file).Substring(RequestPrefix.Length);
            var ack = new Dictionary<string, object?>();
            try
            {
                var req = MiniJson.ParseObject(File.ReadAllText(file));
                var settingsId = MiniJson.GetString(req, "SettingsId") ?? "";
                var values = new Dictionary<string, string>(StringComparer.Ordinal);
                var obj = MiniJson.GetObject(req, "Values");
                if (obj != null) foreach (var kv in obj) values[kv.Key] = MiniJson.GetString(obj, kv.Key) ?? "";

                var changed = McmBridge.Apply(settingsId, values, out var report);
                var persisted = changed > 0 ? McmBridge.Save(settingsId) : "nothing to persist";
                ack["Ok"] = true;
                ack["SettingsId"] = settingsId;
                ack["Changed"] = changed;
                ack["Report"] = report;
                ack["Persisted"] = persisted;
                Log.Info($"live apply {settingsId}: {report}; {persisted}");
            }
            catch (Exception ex)
            {
                ack["Ok"] = false;
                ack["Changed"] = 0;
                ack["Report"] = ex.GetBaseException().Message;
                Log.Warn("live apply failed: " + ex.GetBaseException().Message);
            }
            WriteAtomic(Path.Combine(_dir!, AckPrefix + token + ".json"), MiniJson.Serialize(ack));
            try { File.Delete(file); } catch { }
            _lastBody = null;   // force a fresh settings.json after any apply, even if the values match what we last wrote
        }
    }

    // ---- engine -> launcher ------------------------------------------------------------------------------------

    private static void WriteDescriptionIfChanged()
    {
        if (!McmBridge.Present) return;
        var objects = McmBridge.Describe();
        var body = MiniJson.Serialize(objects);
        if (body == _lastBody) return;
        var doc = new Dictionary<string, object?>
        {
            ["SchemaVersion"] = SchemaVersion,
            ["WrittenAt"] = DateTime.UtcNow.ToString("o"),
            ["Objects"] = objects,
        };
        WriteAtomic(Path.Combine(_dir!, SettingsFileName), MiniJson.Serialize(doc));
        if (_lastBody == null) Log.Info($"live settings: described {objects.Count} settings object(s)");
        _lastBody = body;
    }

    private static void WriteAtomic(string path, string text)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, text);
        if (File.Exists(path)) File.Delete(path);
        File.Move(tmp, path);
    }
}
