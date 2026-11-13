using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace ModderLords.Core.Testing;

/// <summary>
/// Notices a game or server crashing during a test run. A hard crash leaves the process alive: the engine's watchdog
/// first asks whether to gather logs, writes the crash report folder (with a dump) only once someone answers, and the
/// process exits after that. Waiting on the process would hold an unattended run for a step's whole timeout (15
/// minutes to make a world), so the run watches the watchdog instead:
/// <list type="bullet">
/// <item>its log (<c>logs\watchdog_log_&lt;pid&gt;.txt</c>) says "Crash occurred" the moment it catches one;</item>
/// <item>a crash report folder (<c>crashes\&lt;date&gt;</c>) appears if someone answered the dialog, and names the
/// exception.</item>
/// </list>
/// </summary>
public sealed class CrashWatch
{
    /// <summary>The watchdog's line when it catches a crash, before it asks about the dump.</summary>
    public const string CrashLine = "Crash occurred";

    /// <summary>How often <see cref="Check"/> looks.</summary>
    public TimeSpan Every { get; init; } = TimeSpan.FromSeconds(2);

    private readonly string _crashesDir;
    private readonly string _logsDir;
    private readonly HashSet<string> _foldersBefore;
    private readonly HashSet<string> _crashedBefore;
    private readonly Stopwatch _sinceCheck = Stopwatch.StartNew();
    private string? _crash;

    /// <summary>
    /// Starts watching. Crash folders already there, and watchdog logs that already say they caught a crash, are
    /// earlier runs' and are ignored.
    /// </summary>
    public CrashWatch(string crashesDir, string? logsDir = null)
    {
        _crashesDir = crashesDir;
        _logsDir = logsDir ?? Path.Combine(Path.GetDirectoryName(crashesDir) ?? crashesDir, "logs");
        _foldersBefore = Folders().ToHashSet(StringComparer.OrdinalIgnoreCase);
        _crashedBefore = WatchdogLogs().Where(f => Read(f)?.Contains(CrashLine, StringComparison.Ordinal) == true).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _sinceCheck.Restart();
    }

    /// <summary>The crashed process's watchdog log, once <see cref="Check"/> reported a crash.</summary>
    public string? WatchdogLog { get; private set; }

    /// <summary>The crash report folder, when the watchdog got to write one.</summary>
    public string? Folder { get; private set; }

    /// <summary>Whether a crash was reported.</summary>
    public bool Crashed => _crash is not null;

    /// <summary>
    /// Null while nothing crashed; otherwise why the run stops: what the game was doing, the exception when the report
    /// names it, and where the logs are. Looks at most every <see cref="Every"/>, so it can be called on every poll.
    /// </summary>
    public string? Check()
    {
        if (_crash is not null) return _crash;
        if (_sinceCheck.Elapsed < Every) return null;
        _sinceCheck.Restart();

        if (Folders().FirstOrDefault(f => !_foldersBefore.Contains(f)) is { } folder)
        {
            Folder = folder;
            WatchdogLog = Directory.EnumerateFiles(folder, "watchdog_log_*.txt").FirstOrDefault();
            return _crash = Describe(Read(WatchdogLog), LastLine(folder), folder);
        }
        foreach (var log in WatchdogLogs().Where(f => !_crashedBefore.Contains(f)))
        {
            var text = Read(log);
            if (text?.Contains(CrashLine, StringComparison.Ordinal) != true) continue;
            WatchdogLog = log;
            return _crash = Describe(text, LastLine(_logsDir, PidOf(log)), _logsDir);
        }
        return null;
    }

    /// <summary>
    /// Copies the crashed process's engine logs (watchdog, rgl log, rgl errors) into <paramref name="runFolder"/>\crash,
    /// so the run folder holds the evidence. Never the dump. Returns the files copied.
    /// </summary>
    public IReadOnlyList<string> CopyLogs(string runFolder)
    {
        if (WatchdogLog is null) return [];
        var dir = Path.GetDirectoryName(WatchdogLog)!;
        var pid = PidOf(WatchdogLog);
        var copied = new List<string>();
        var to = Directory.CreateDirectory(Path.Combine(runFolder, "crash")).FullName;
        foreach (var name in new[] { Path.GetFileName(WatchdogLog), $"rgl_log_{pid}.txt", $"rgl_log_errors_{pid}.txt" })
        {
            var from = Path.Combine(dir, name);
            if (!File.Exists(from)) continue;
            try
            {
                using var source = Open(from);
                using var target = File.Create(Path.Combine(to, name));
                source.CopyTo(target);
                copied.Add(Path.Combine(to, name));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return copied;
    }

    /// <summary>"the game crashed (MissingMethodException) during: Initializing new game begin...; logs: DIR".</summary>
    public static string Describe(string? watchdogLog, string? lastLine, string where)
    {
        var what = watchdogLog is null ? null : ExceptionName(watchdogLog);
        return "the game crashed" + (what is null ? "" : $" ({what})") + (lastLine is null ? "" : $" during: {lastLine}") + $"; logs: {where}";
    }

    /// <summary>
    /// What the watchdog's log says crashed: a .NET exception by name where its HRESULT is a common one, otherwise the
    /// codes. Null when the log names no exception yet (it does only once the crash dialog is answered).
    /// </summary>
    public static string? ExceptionName(string watchdogLog)
    {
        var code = Regex.Match(watchdogLog, @"ExceptionCode:\s*0x([0-9A-Fa-f]+)");
        if (!code.Success) return null;
        // 0xE0434352 is the CLR's: a managed exception, whose HRESULT is the first parameter.
        if (!code.Groups[1].Value.Equals("E0434352", StringComparison.OrdinalIgnoreCase)) return "native exception 0x" + code.Groups[1].Value.ToUpperInvariant();
        var param = Regex.Match(watchdogLog, @"Parameter-0:\s*0x([0-9A-Fa-f]+)");
        if (!param.Success) return "a .NET exception";
        var hex = param.Groups[1].Value;
        var hresult = hex.Length > 8 ? hex[^8..] : hex;
        return HResults.TryGetValue(uint.Parse(hresult, NumberStyles.HexNumber, CultureInfo.InvariantCulture), out var name)
            ? name
            : "a .NET exception, HRESULT 0x" + hresult.ToUpperInvariant();
    }

    private static readonly Dictionary<uint, string> HResults = new()
    {
        [0x80131513] = "MissingMethodException",
        [0x80131511] = "MissingFieldException",
        [0x80131522] = "TypeLoadException",
        [0x80004003] = "NullReferenceException",
        [0x80004002] = "InvalidCastException",
        [0x80131508] = "IndexOutOfRangeException",
        [0x80131509] = "InvalidOperationException",
        [0x80070057] = "ArgumentException",
        [0x80131577] = "KeyNotFoundException",
        [0x80131500] = "Exception",
        [0x800703E9] = "StackOverflowException",
        [0x8007000E] = "OutOfMemoryException",
    };

    /// <summary>The last non-empty line of the process's rgl log, with its time stamp taken off: what the engine was doing.</summary>
    private static string? LastLine(string dir, string? pid = null)
    {
        var file = pid is null ? Directory.EnumerateFiles(dir, "rgl_log_*.txt").FirstOrDefault(f => !Path.GetFileName(f).StartsWith("rgl_log_errors", StringComparison.Ordinal))
                               : Path.Combine(dir, $"rgl_log_{pid}.txt");
        var text = file is null ? null : Read(file);
        var last = text?.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0);
        return last is null ? null : Regex.Replace(last, @"^\[\d\d:\d\d:\d\d\.\d+\]\s*", "");
    }

    private static string? PidOf(string watchdogLog) =>
        Regex.Match(Path.GetFileNameWithoutExtension(watchdogLog), @"_(\d+)$") is { Success: true } m ? m.Groups[1].Value : null;

    private static FileStream Open(string file) => new(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    private static string? Read(string? file)
    {
        if (file is null) return null;
        try
        {
            // The watchdog and the engine still have their logs open.
            using var reader = new StreamReader(Open(file));
            return reader.ReadToEnd();
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private IEnumerable<string> WatchdogLogs() => Files(_logsDir, "watchdog_log_*.txt");

    private IEnumerable<string> Folders()
    {
        try { return Directory.Exists(_crashesDir) ? Directory.GetDirectories(_crashesDir) : []; }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }

    private static IEnumerable<string> Files(string dir, string pattern)
    {
        try { return Directory.Exists(dir) ? Directory.GetFiles(dir, pattern) : []; }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }
}
