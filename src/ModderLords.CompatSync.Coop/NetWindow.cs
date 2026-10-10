using System.Globalization;

namespace ModderLords.CompatSync.Coop;

/// <summary>
/// Turns the network library's running totals into one line per window for the launcher's Performance tab, e.g.
/// <c>[ModderLords] perf net upKbps=412.6 downKbps=38.1 peers=3 sentMb=120.4 receivedMb=11.2 windowSec=9.0</c>
/// <para>
/// The totals count what the library hands to the socket, so each packet is short of what crosses the wire by its
/// IPv4 and UDP headers. <see cref="HeaderBytes"/> per packet is added back, which makes the figure comparable to
/// what a router or Task Manager shows. Free of game and Coop types so the tests can drive it.
/// </para>
/// </summary>
public sealed class NetWindow
{
    /// <summary>IPv4 (20) plus UDP (8). An IPv6 packet carries 20 more; the difference is a few percent at most.</summary>
    public const int HeaderBytes = 28;

    private bool _started;
    private long _firstSent, _firstReceived;
    private long _lastSent, _lastReceived;

    /// <summary>
    /// One reading of the running totals. Returns the line for the window that just ended, or null for the first
    /// reading (nothing to compare with yet), a window with no length, or totals that went backwards (a new session:
    /// the next reading starts again from there).
    /// </summary>
    public string? Read(long bytesSent, long packetsSent, long bytesReceived, long packetsReceived, double seconds, int peers)
    {
        var sent = bytesSent + packetsSent * HeaderBytes;
        var received = bytesReceived + packetsReceived * HeaderBytes;
        if (!_started || sent < _lastSent || received < _lastReceived)
        {
            _started = true;
            (_firstSent, _firstReceived, _lastSent, _lastReceived) = (sent, received, sent, received);
            return null;
        }

        var up = sent - _lastSent;
        var down = received - _lastReceived;
        (_lastSent, _lastReceived) = (sent, received);
        if (seconds <= 0) return null;

        return string.Format(CultureInfo.InvariantCulture,
            "[ModderLords] perf net upKbps={0:0.0} downKbps={1:0.0} peers={2} sentMb={3:0.0} receivedMb={4:0.0} windowSec={5:0.0}",
            up * 8 / 1000.0 / seconds, down * 8 / 1000.0 / seconds, peers,
            (sent - _firstSent) / 1e6, (received - _firstReceived) / 1e6, seconds);
    }
}
