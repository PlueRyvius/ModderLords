using System.Text.Json;
using ModderLords.Core.Logs;

namespace ModderLords.Coop.Launch;

/// <summary>
/// Captures the part of a TAOM launch that happens before the ordinary server starts.
///
/// World creation used to be invisible to the launch log: the GUI only subscribed to the filtered phase lines and
/// the normal log was attached to the later serving process. A native stall could therefore leave the user with a
/// status message and an empty log. This writer keeps the raw streams, classified lines, phase history and the exact
/// redacted launch plan together in one uniquely named session.
/// </summary>
public sealed class CreationDiagnostics : IDisposable
{
    private readonly object _gate = new();
    private readonly CappedLogWriter _combined;
    private readonly CappedLogWriter _stdout;
    private readonly CappedLogWriter _stderr;
    private readonly LaunchPlan _plan;
    private readonly List<string> _phases = new();
    private DateTimeOffset? _completedAt;
    private int? _processId;
    private int? _exitCode;
    private bool _timedOut;
    private bool _saveExists;
    private int _expectedMissingAnimationWarnings;
    private int _otherWarnings;
    private int _messageboxPrompts;
    private string? _actualMapSceneType;
    private bool _disposed;

    public string SessionId { get; }
    public string SaveName { get; }
    public DateTimeOffset StartedAt { get; }
    public string LogPath { get; }
    public string StdoutPath { get; }
    public string StderrPath { get; }
    public string ManifestPath { get; }
    public IReadOnlyList<string> Phases
    {
        get { lock (_gate) return _phases.ToArray(); }
    }

    private CreationDiagnostics(string sessionId, string saveName, DateTimeOffset startedAt, string logPath,
        string stdoutPath, string stderrPath, string manifestPath, LaunchPlan plan)
    {
        SessionId = sessionId;
        SaveName = saveName;
        StartedAt = startedAt;
        LogPath = logPath;
        StdoutPath = stdoutPath;
        StderrPath = stderrPath;
        ManifestPath = manifestPath;
        _plan = plan;
        _combined = new CappedLogWriter(logPath);
        _stdout = new CappedLogWriter(stdoutPath);
        _stderr = new CappedLogWriter(stderrPath);
        WriteHeader();
        WriteManifest();
    }

    public static CreationDiagnostics Start(string directory, LaunchPlan plan, string saveName)
    {
        if (string.IsNullOrWhiteSpace(directory)) throw new ArgumentException("A diagnostics directory is required.", nameof(directory));
        if (string.IsNullOrWhiteSpace(saveName)) throw new ArgumentException("A save name is required.", nameof(saveName));
        Directory.CreateDirectory(directory);
        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss-fff");
        var sessionId = $"{stamp}-{Guid.NewGuid():N}";
        var prefix = Path.Combine(directory, $"creation-{sessionId}");
        return new CreationDiagnostics(sessionId, saveName, DateTimeOffset.UtcNow,
            prefix + ".log", prefix + ".stdout.log", prefix + ".stderr.log", prefix + ".json", plan);
    }

    /// <summary>Associates the diagnostics with the process that owns the captured streams.</summary>
    public void Attach(int? processId)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _processId = processId;
            WriteBoth($"event process-started pid={processId?.ToString() ?? "unknown"}");
            WriteManifest();
        }
    }

    /// <summary>Writes a launcher-side event even when the engine never emits a line.</summary>
    public void RecordEvent(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        lock (_gate)
        {
            if (_disposed) return;
            WriteBoth($"event {message}");
        }
    }

    public void Record(EngineLine line)
    {
        lock (_gate)
        {
            if (_disposed) return;
            var classification = LogClassifier.Classify(line.Text).Category;
            var expectedAnimation = classification == LogCategory.Warning &&
                line.Text.Contains("Could not find animation:", StringComparison.OrdinalIgnoreCase);
            if (expectedAnimation) _expectedMissingAnimationWarnings++;
            else if (classification == LogCategory.Warning) _otherWarnings++;
            if (line.Text.Contains("Messagebox [Always Ignore?]", StringComparison.OrdinalIgnoreCase)) _messageboxPrompts++;
            var category = expectedAnimation ? "Warning(ExpectedMissingAnimation)" : classification.ToString();
            var text = $"{line.At:O} {line.Stream,-6} {category,-28} {line.Text}";
            _combined.WriteLine(text);
            (line.Stream == OutputStream.Stdout ? _stdout : _stderr).WriteLine(text);
            TrackPhase(line.Text);
            TrackMapScene(line.Text);
        }
    }

    public void Complete(int exitCode, bool timedOut, bool saveExists)
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (_completedAt is not null) return;
            _completedAt = DateTimeOffset.UtcNow;
            _exitCode = exitCode;
            _timedOut = timedOut;
            _saveExists = saveExists;
            WriteBoth($"event process-exited code={exitCode} timedOut={timedOut} saveExists={saveExists} elapsedSeconds={(int)(_completedAt.Value - StartedAt).TotalSeconds}");
            WriteManifest();
        }
    }

    private void TrackPhase(string text)
    {
        var marker = "worldcreate: phase=";
        var failMarker = "worldcreate: fail phase=";
        var index = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        var start = index >= 0 ? index + marker.Length : -1;
        if (start < 0)
        {
            index = text.IndexOf(failMarker, StringComparison.OrdinalIgnoreCase);
            start = index >= 0 ? index + failMarker.Length : -1;
        }
        if (start < 0) return;
        var value = text[start..].Trim();
        var end = value.IndexOfAny([' ', '\t', '\r', '\n', ';', ',']);
        if (end >= 0) value = value[..end];
        if (value.Length == 0 || _phases.Contains(value, StringComparer.OrdinalIgnoreCase)) return;
        _phases.Add(value);
        WriteManifest();
    }

    private void TrackMapScene(string text)
    {
        const string marker = "worldcreate: map scene=";
        var index = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0) return;
        var value = text[(index + marker.Length)..].Trim();
        var end = value.IndexOfAny([' ', '\t', '\r', '\n', ';', ',']);
        if (end >= 0) value = value[..end];
        if (value.Length == 0 || string.Equals(_actualMapSceneType, value, StringComparison.Ordinal)) return;
        _actualMapSceneType = value;
        WriteManifest();
    }

    private void WriteHeader()
    {
        lock (_gate)
        {
            WriteBoth($"session={SessionId}");
            WriteBoth($"startedUtc={StartedAt:O}");
            WriteBoth($"saveName={SaveName}");
            WriteBoth("plan-begin");
            WriteBoth($"cwd : {_plan.Paths.ServerBin}");
            WriteBoth($"exe : {_plan.Paths.DotnetExe}");
            WriteBoth("args: " + string.Join(' ', RedactedArguments().Select(FormatArgument)));
            foreach (var kv in _plan.Environment()) WriteBoth($"env : {kv.Key}={Redact(kv.Key, kv.Value)}");
            WriteBoth("plan-end");
            var worldLog = _plan.ExtraEnvironment.TryGetValue("MODDERLORDS_CREATE_WORLD_LOG", out var value) ? value : "";
            WriteBoth($"worldLog={worldLog}");
        }
    }

    private void WriteBoth(string line)
    {
        _combined.WriteLine(line);
        _stdout.WriteLine(line);
        _stderr.WriteLine(line);
    }

    private void WriteManifest()
    {
        var environment = _plan.Environment().ToDictionary(k => k.Key, v => Redact(v.Key, v.Value), StringComparer.OrdinalIgnoreCase);
        var arguments = RedactedArguments();
        environment.TryGetValue("MODDERLORDS_HEADLESS_MAP", out var mapScene);
        environment.TryGetValue("MODDERLORDS_HEADLESS_MAP_MODULE", out var mapSceneModule);
        var manifest = new
        {
            schema = 1,
            sessionId = SessionId,
            saveName = SaveName,
            startedUtc = StartedAt,
            completedUtc = _completedAt,
            processId = _processId,
            exitCode = _exitCode,
            timedOut = _timedOut,
            saveExists = _saveExists,
            elapsedSeconds = (_completedAt is null ? (DateTimeOffset.UtcNow - StartedAt) : _completedAt.Value - StartedAt).TotalSeconds,
            phases = _phases.ToArray(),
            warnings = new { expectedMissingAnimation = _expectedMissingAnimationWarnings, other = _otherWarnings, messageboxPrompts = _messageboxPrompts },
            worldCreation = new
            {
                mapScene,
                mapSceneModule,
                actualMapSceneType = _actualMapSceneType,
                savePath = Path.Combine(_plan.Paths.SavesDir, SaveName + ".sav"),
            },
            files = new { log = LogPath, stdout = StdoutPath, stderr = StderrPath, manifest = ManifestPath },
            plan = new
            {
                serverRoot = _plan.Paths.DedicatedServerRoot,
                dataDir = _plan.Paths.DataDir,
                coopDataDir = _plan.Paths.CoopDataDir,
                arguments,
                modules = _plan.ModuleIds.ToArray(),
                enginePort = _plan.EnginePort,
                region = _plan.Region,
                environment,
            },
        };
        var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
        var temp = ManifestPath + ".tmp";
        File.WriteAllText(temp, json);
        File.Move(temp, ManifestPath, overwrite: true);
    }

    private static string Redact(string key, string value) =>
        key.Contains("password", StringComparison.OrdinalIgnoreCase) ||
        key.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
        key.Contains("token", StringComparison.OrdinalIgnoreCase)
            ? "****" : value;

    private string[] RedactedArguments()
    {
        var arguments = _plan.Arguments().ToArray();
        for (var i = 1; i < arguments.Length; i++)
            if (arguments[i - 1].Equals("/cooppassword", StringComparison.OrdinalIgnoreCase)) arguments[i] = "****";
        return arguments;
    }

    private static string FormatArgument(string value) => value.Contains(' ') ? $"\"{value}\"" : value;

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _combined.Dispose();
            _stdout.Dispose();
            _stderr.Dispose();
        }
    }
}
