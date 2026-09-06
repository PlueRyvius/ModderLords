using System.Text.Json;
using System.Text.Json.Serialization;

using ModderLords.Core.Compat;
using ModderLords.Core.Config;
using ModderLords.Core.Export;
using ModderLords.Core.Launch;
using ModderLords.Core.Logs;
using ModderLords.Core.Modules;
using ModderLords.Core.Overlay;
using ModderLords.Core.Perf;
using ModderLords.Core.Profiles;
using ModderLords.Core.Saves;
using ModderLords.Coop.Compat;
using ModderLords.Coop.Config;
using ModderLords.Coop.Launch;
using ModderLords.Coop.Live;
using ModderLords.Coop.Saves;

namespace ModderLords.Coop.Live;

// The launcher's half of the live-settings channel. File shapes are produced by ModderLords.CompatSync.LiveSettings
// (MiniJson) on the engine side; keep the property names in step.

public sealed class LiveSettingsDocument
{
    public int SchemaVersion { get; set; } = 1;
    public DateTime WrittenAt { get; set; }
    public List<LiveSettingsObject> Objects { get; set; } = new();
}

public sealed class LiveSettingsObject
{
    public string SettingsId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string? Folder { get; set; }
    public List<LiveSettingsGroup> Groups { get; set; } = new();
    [JsonIgnore] public int PropertyCount => Groups.Sum(g => g.Properties.Count);
}

public sealed class LiveSettingsGroup
{
    public string Name { get; set; } = "";
    public List<LiveSettingsProperty> Properties { get; set; } = new();
}

/// <summary>Kind is one of bool / int / float / string / enum, or the CLR type name for unsupported (Editable=false) properties.</summary>
public sealed class LiveSettingsProperty
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string? Hint { get; set; }
    public string Kind { get; set; } = "string";
    public string? Value { get; set; }
    public double? Min { get; set; }
    public double? Max { get; set; }
    public List<string>? Choices { get; set; }
    public bool Editable { get; set; }
    public bool RequireRestart { get; set; }
}

public sealed class LiveApplyRequest
{
    public string SettingsId { get; set; } = "";
    public Dictionary<string, string> Values { get; set; } = new();
}

public sealed class LiveApplyAck
{
    public bool Ok { get; set; }
    public string? SettingsId { get; set; }
    public int Changed { get; set; }
    public string Report { get; set; } = "";
    public string? Persisted { get; set; }
}

/// <summary>File names and the request/ack round trip; pure functions over a directory, used by the client and the tests.</summary>
public static class LiveProtocol
{
    public const string EnvVar = "MODDERLORDS_LIVE_DIR";
    public const string SettingsFileName = "settings.json";
    public const string RequestPrefix = "apply-";
    public const string AckPrefix = "ack-";
    public const string OverridesFileName = "overrides.json";
    public const string OverridesAckFileName = "ack-overrides.json";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    /// <summary>A token that sorts in creation order as a file name (zero-padded UTC ticks).</summary>
    public static string NewToken() => DateTime.UtcNow.Ticks.ToString("D19");
    public static string RequestFileName(string token) => RequestPrefix + token + ".json";
    public static string AckFileName(string token) => AckPrefix + token + ".json";

    public static string LiveDirFor(string profileName) => Path.Combine(ProfileStore.RootDir, "live", ProfileStore.Safe(profileName));

    /// <summary>Fresh directory per launch so no stale settings.json or ack from a previous run is mistaken for live data.</summary>
    public static void Reset(string dir)
    {
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        Directory.CreateDirectory(dir);
    }

    public static LiveSettingsDocument? ReadSettings(string dir)
    {
        var path = Path.Combine(dir, SettingsFileName);
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<LiveSettingsDocument>(File.ReadAllText(path), Json); }
        catch (IOException) { return null; }         // mid-move; the next poll sees it
        catch (JsonException) { return null; }
    }

    /// <summary>Writes apply-&lt;token&gt;.json atomically and returns the token.</summary>
    public static string WriteRequest(string dir, LiveApplyRequest request)
    {
        Directory.CreateDirectory(dir);
        var token = NewToken();
        var path = Path.Combine(dir, RequestFileName(token));
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(request, Json));
        File.Move(tmp, path, overwrite: true);
        return token;
    }

    /// <summary>The ack for a token, deleting it once read; null while the module has not answered.</summary>
    public static LiveApplyAck? TakeAck(string dir, string token)
    {
        var path = Path.Combine(dir, AckFileName(token));
        if (!File.Exists(path)) return null;
        try
        {
            var ack = JsonSerializer.Deserialize<LiveApplyAck>(File.ReadAllText(path), Json);
            File.Delete(path);
            return ack;
        }
        catch (IOException) { return null; }
        catch (JsonException) { File.Delete(path); return new LiveApplyAck { Ok = false, Report = "unreadable ack" }; }
    }

    public static bool RequestPending(string dir, string token) => File.Exists(Path.Combine(dir, RequestFileName(token)));
}

/// <summary>
/// Watches one live directory: raises Changed (on a thread-pool thread) when settings.json is rewritten, with a 1 s
/// poll as the fallback for missed watcher events. Apply writes a request and waits for its ack.
/// </summary>
public sealed class LiveSettingsClient : IDisposable
{
    private readonly FileSystemWatcher? _watcher;
    private readonly Timer _poll;
    private DateTime _lastWrite;
    private bool _disposed;

    public string Dir { get; }
    public event Action<LiveSettingsDocument?>? Changed;

    public LiveSettingsClient(string dir)
    {
        Dir = dir;
        Directory.CreateDirectory(dir);
        try
        {
            _watcher = new FileSystemWatcher(dir, LiveProtocol.SettingsFileName) { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size };
            _watcher.Changed += (_, _) => Check();
            _watcher.Created += (_, _) => Check();
            _watcher.Renamed += (_, _) => Check();
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception) { _watcher = null; }
        _poll = new Timer(_ => Check(), null, 1000, 1000);
    }

    public LiveSettingsDocument? Read() => LiveProtocol.ReadSettings(Dir);

    private void Check()
    {
        if (_disposed) return;
        try
        {
            var path = Path.Combine(Dir, LiveProtocol.SettingsFileName);
            if (!File.Exists(path)) return;
            var stamp = File.GetLastWriteTimeUtc(path);
            if (stamp == _lastWrite) return;
            var doc = LiveProtocol.ReadSettings(Dir);
            if (doc is null) return;    // mid-write; try again on the next event/poll
            _lastWrite = stamp;
            Changed?.Invoke(doc);
        }
        catch (Exception) { /* transient IO; next poll */ }
    }

    /// <summary>Drops the request and waits for the module's ack (it polls every 3 s). Null on timeout.</summary>
    public async Task<LiveApplyAck?> ApplyAsync(string settingsId, IReadOnlyDictionary<string, string> values, TimeSpan timeout, CancellationToken ct = default)
    {
        var token = LiveProtocol.WriteRequest(Dir, new LiveApplyRequest { SettingsId = settingsId, Values = new Dictionary<string, string>(values) });
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var ack = LiveProtocol.TakeAck(Dir, token);
            if (ack is not null) return ack;
            await Task.Delay(250, ct).ConfigureAwait(false);
        }
        return null;
    }

    public void Dispose()
    {
        _disposed = true;
        _poll.Dispose();
        _watcher?.Dispose();
    }
}
