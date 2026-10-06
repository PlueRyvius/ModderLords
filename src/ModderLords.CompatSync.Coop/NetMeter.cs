using System;
using System.Diagnostics;
using System.Reflection;
using LiteNetLib;
using ModderLords.CompatSync;

namespace ModderLords.CompatSync.Coop;

/// <summary>
/// Server: reports how much network traffic the session uses, once every ten seconds.
/// <para>
/// Nothing here watches packets. The network library already keeps running totals of bytes and packets when its
/// statistics switch is on (one interlocked add per packet inside the library); this reads those four numbers on the
/// 3-second tick and prints one line per window. The launcher cannot measure it from outside without administrator
/// rights, because Windows only exposes per-process network counters through kernel tracing.
/// </para>
/// </summary>
internal static class NetMeter
{
    public const double ReportPeriodSeconds = 10;

    private static readonly Stopwatch Clock = new Stopwatch();
    private static NetWindow _window = new NetWindow();
    private static object? _network;
    private static NetManager? _manager;
    private static bool _noField;

    internal static void ServerTick()
    {
        var network = global::Coop.Core.Server.Services.ModderLordsCompat.Handlers.ServerSettingsHandler.Current?.Network;
        if (network is null) return;
        if (!ReferenceEquals(network, _network))
        {
            // A new session has its own network object and its own totals.
            (_network, _manager, _noField, _window) = (network, null, false, new NetWindow());
            Clock.Reset();
        }
        if (_manager is null && (_noField || (_manager = Find(network)) is null)) return;

        if (!_manager.EnableStatistics) _manager.EnableStatistics = true;
        if (Clock.IsRunning && Clock.Elapsed.TotalSeconds < ReportPeriodSeconds - 1.5) return;   // the tick is every 3 s: report on the one nearest ten

        var seconds = Clock.Elapsed.TotalSeconds;
        Clock.Restart();
        var stats = _manager.Statistics;
        if (_window.Read(stats.BytesSent, stats.PacketsSent, stats.BytesReceived, stats.PacketsReceived, seconds, _manager.ConnectedPeersCount) is { } line)
            Console.WriteLine(line);
    }

    /// <summary>
    /// Coop's server keeps its <see cref="NetManager"/> in a private field. Found by type, not by name, so a rename
    /// on Coop's side does not break it; null until Coop has started the network.
    /// </summary>
    private static NetManager? Find(object network)
    {
        var found = false;
        for (var type = network.GetType(); type != null; type = type.BaseType)
            foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (field.FieldType != typeof(NetManager)) continue;
                found = true;
                if (field.GetValue(network) is NetManager manager) return manager;
            }
        if (!found)
        {
            _noField = true;
            Log.Warn("network meter off: this Coop version keeps no LiteNetLib NetManager on its server network object");
        }
        return null;
    }
}
