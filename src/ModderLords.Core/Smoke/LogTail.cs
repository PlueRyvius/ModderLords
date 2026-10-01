using System.Text;

namespace ModderLords.Core.Smoke;

/// <summary>
/// Reads what a log file gains after a point in time, while another process holds it open for writing.
/// <para>
/// The game's logs are rewritten per launch (Coop_client.log, ModderLords.Compat-client.log), so the file a test
/// started watching is usually replaced by the game it starts. A replacement is seen two ways: the file is shorter than
/// what was already read, or its first bytes changed. Either way reading restarts at the top of the new file.
/// </para>
/// </summary>
public sealed class LogTail
{
    private const int HeadLength = 64;
    private long _offset;
    private byte[] _head;
    private readonly StringBuilder _partial = new();

    public string Path { get; }

    /// <summary>Everything read so far, for the report's log slice. Capped; the cap is noted when hit.</summary>
    public StringBuilder Captured { get; } = new();
    public long CaptureLimit { get; init; } = 16L * 1024 * 1024;
    private bool _capped;

    /// <param name="fromStart">True reads the file's current content too; false starts at its current end.</param>
    public LogTail(string path, bool fromStart = false)
    {
        Path = path;
        _head = ReadHead(path);
        _offset = fromStart ? 0 : Length(path);
    }

    /// <summary>The complete lines written since the last call. A trailing partial line waits for its newline.</summary>
    public IReadOnlyList<string> ReadNew()
    {
        if (!File.Exists(Path)) return [];
        var length = Length(Path);
        var head = ReadHead(Path);
        if (length < _offset || (_head.Length > 0 && !head.AsSpan().StartsWith(_head.AsSpan(0, Math.Min(_head.Length, head.Length)))))
        {
            _offset = 0;
            _partial.Clear();
        }
        _head = head;
        if (length == _offset) return [];

        string text;
        try
        {
            using var stream = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            stream.Seek(_offset, SeekOrigin.Begin);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            text = reader.ReadToEnd();
            _offset = stream.Position;
        }
        catch (IOException) { return []; }

        _partial.Append(text);
        var all = _partial.ToString();
        var lastNewline = all.LastIndexOf('\n');
        if (lastNewline < 0) return [];
        _partial.Clear().Append(all[(lastNewline + 1)..]);
        var lines = all[..lastNewline].Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        Capture(lines);
        return lines;
    }

    private void Capture(IEnumerable<string> lines)
    {
        if (_capped) return;
        foreach (var line in lines)
        {
            if (Captured.Length + line.Length > CaptureLimit)
            {
                Captured.AppendLine($"[smoke test: capture stopped at {CaptureLimit / (1024 * 1024)} MB; the full file is {Path}]");
                _capped = true;
                return;
            }
            Captured.AppendLine(line);
        }
    }

    private static long Length(string path)
    {
        try { return File.Exists(path) ? new FileInfo(path).Length : 0; }
        catch (IOException) { return 0; }
    }

    private static byte[] ReadHead(string path)
    {
        try
        {
            if (!File.Exists(path)) return [];
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buffer = new byte[HeadLength];
            var read = stream.Read(buffer, 0, buffer.Length);
            return buffer[..read];
        }
        catch (IOException) { return []; }
    }
}
