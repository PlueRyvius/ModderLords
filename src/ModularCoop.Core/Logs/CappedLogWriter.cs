using System.Text;

namespace ModularCoop.Core.Logs;

/// <summary>
/// A launch log that cannot eat the disk.
///
/// The engine can emit hundreds of thousands of lines a second when a trace switch is on
/// (`[tick]`/`[party]` spam), and the previous writer took every one of them with AutoFlush on:
/// one session produced a 9.5 GB file, and rotation that kept the newest 20 files by count let the
/// folder reach 40 GB. Both limits here are on bytes, because a line count says nothing about size.
///
/// Once the cap is reached the writer stops, having written one final line saying so, rather than
/// silently truncating: a reader must be able to tell a complete log from a cut-off one.
/// </summary>
public sealed class CappedLogWriter : IDisposable
{
    public const long DefaultMaxBytes = 256L * 1024 * 1024;

    private readonly StreamWriter _writer;
    private readonly long _maxBytes;
    private long _written;
    private int _sinceFlush;

    /// <summary>Bytes written so far, as counted against the cap.</summary>
    public long BytesWritten => _written;

    /// <summary>True once the cap was hit and further lines are being discarded.</summary>
    public bool IsCapped { get; private set; }

    /// <summary>Lines discarded after the cap was reached.</summary>
    public long DroppedLines { get; private set; }

    public CappedLogWriter(string path, long maxBytes = DefaultMaxBytes)
    {
        _maxBytes = maxBytes;
        // AutoFlush stays off: flushing per line is what made the engine's trace firehose an I/O
        // problem as well as a disk one. Flush() runs on the UI's console tick and on Dispose.
        _writer = new StreamWriter(path, append: false) { AutoFlush = false };
    }

    public void WriteLine(string line)
    {
        if (IsCapped) { DroppedLines++; return; }

        var size = Encoding.UTF8.GetByteCount(line) + Environment.NewLine.Length;
        if (_written + size > _maxBytes)
        {
            IsCapped = true;
            DroppedLines++;
            _writer.WriteLine($"--- log capped at {_maxBytes / (1024 * 1024)} MB; further lines are not being written. " +
                              "If you turned on a Trace switch on the Server tab, turn it off: the engine emits " +
                              "hundreds of thousands of lines a second with it on. ---");
            _writer.Flush();
            return;
        }

        _writer.WriteLine(line);
        _written += size;
        if (++_sinceFlush >= 500) Flush();
    }

    public void Flush()
    {
        _sinceFlush = 0;
        try { _writer.Flush(); } catch (ObjectDisposedException) { }
    }

    public void Dispose()
    {
        try { _writer.Flush(); } catch (ObjectDisposedException) { }
        _writer.Dispose();
    }
}
