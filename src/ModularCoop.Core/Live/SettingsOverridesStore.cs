using System.Text.Json;
using System.Text.Json.Serialization;
using ModularCoop.Core.Profiles;

namespace ModularCoop.Core.Live;

/// <summary>Host overrides for one profile: values the launcher re-applies at every launch (and shows while the server is off).</summary>
public sealed class SettingsOverrides
{
    public int SchemaVersion { get; set; } = 1;
    public DateTime? UpdatedAt { get; set; }
    /// <summary>SettingsId → propId → text.</summary>
    public Dictionary<string, Dictionary<string, string>> Objects { get; set; } = new(StringComparer.Ordinal);

    public int Count => Objects.Sum(o => o.Value.Count);
    public bool IsEmpty => Objects.Count == 0;

    public string? Get(string settingsId, string propId) =>
        Objects.TryGetValue(settingsId, out var o) && o.TryGetValue(propId, out var v) ? v : null;

    public void Set(string settingsId, IReadOnlyDictionary<string, string> values)
    {
        if (!Objects.TryGetValue(settingsId, out var o)) Objects[settingsId] = o = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var kv in values) o[kv.Key] = kv.Value;
        UpdatedAt = DateTime.UtcNow;
    }

    public void Remove(string settingsId, string propId)
    {
        if (!Objects.TryGetValue(settingsId, out var o)) return;
        o.Remove(propId);
        if (o.Count == 0) Objects.Remove(settingsId);
        UpdatedAt = DateTime.UtcNow;
    }

    public void Clear(string settingsId)
    {
        if (Objects.Remove(settingsId)) UpdatedAt = DateTime.UtcNow;
    }
}

/// <summary>The module's answer after applying staged overrides (ack-overrides.json).</summary>
public sealed class OverridesAck
{
    public bool Ok { get; set; }
    public List<LiveApplyAck> Applied { get; set; } = new();
    public List<string> Pending { get; set; } = new();
}

/// <summary>
/// Per-profile files next to the profile JSON: &lt;name&gt;.settings.json (overrides) and, under cache\, the last
/// settings.json the server reported so the tab can show and edit values while the server is off.
/// </summary>
public static class SettingsOverridesStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public static string OverridesPath(string profileName) => Path.Combine(ProfileStore.ProfilesDir, ProfileStore.Safe(profileName) + ".settings.json");
    public static string CachePath(string profileName) => Path.Combine(ProfileStore.RootDir, "cache", ProfileStore.Safe(profileName) + ".settings-cache.json");

    public static SettingsOverrides Load(string profileName) => LoadFrom(OverridesPath(profileName));

    public static SettingsOverrides LoadFrom(string path)
    {
        if (!File.Exists(path)) return new SettingsOverrides();
        try { return JsonSerializer.Deserialize<SettingsOverrides>(File.ReadAllText(path), Json) ?? new SettingsOverrides(); }
        catch (JsonException) { return new SettingsOverrides(); }
    }

    public static void Save(string profileName, SettingsOverrides overrides) => SaveTo(OverridesPath(profileName), overrides);

    /// <summary>Atomic write; an empty set deletes the file.</summary>
    public static void SaveTo(string path, SettingsOverrides overrides)
    {
        if (overrides.IsEmpty) { if (File.Exists(path)) File.Delete(path); return; }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(overrides, Json));
        File.Move(tmp, path, overwrite: true);
    }

    public static void Delete(string profileName)
    {
        foreach (var p in new[] { OverridesPath(profileName), CachePath(profileName) })
            if (File.Exists(p)) File.Delete(p);
    }

    public static LiveSettingsDocument? LoadCache(string profileName)
    {
        var p = CachePath(profileName);
        if (!File.Exists(p)) return null;
        try { return JsonSerializer.Deserialize<LiveSettingsDocument>(File.ReadAllText(p), Json); }
        catch (JsonException) { return null; }
    }

    public static void SaveCache(string profileName, LiveSettingsDocument doc)
    {
        var p = CachePath(profileName);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        var tmp = p + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(doc, Json));
        File.Move(tmp, p, overwrite: true);
    }

    /// <summary>Stages the overrides into the live dir for the module (the same JSON shape; the module reads it with MiniJson).</summary>
    public static void WriteToLiveDir(string liveDir, SettingsOverrides overrides)
    {
        var path = Path.Combine(liveDir, LiveProtocol.OverridesFileName);
        if (overrides.IsEmpty) { if (File.Exists(path)) File.Delete(path); return; }
        Directory.CreateDirectory(liveDir);
        File.WriteAllText(path, JsonSerializer.Serialize(overrides, Json));
    }

    public static OverridesAck? ReadAck(string liveDir)
    {
        var path = Path.Combine(liveDir, LiveProtocol.OverridesAckFileName);
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<OverridesAck>(File.ReadAllText(path), Json); }
        catch (IOException) { return null; }
        catch (JsonException) { return null; }
    }
}
