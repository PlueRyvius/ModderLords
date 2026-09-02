using System;
using System.Collections.Generic;
using Common.Messaging;
using Common.Network;
using LiteNetLib;
using ModularCoop.CompatSync;
using ModularCoop.CompatSync.Messages;

// Coop discovers server-side handlers by namespace prefix (Coop.Core.Server.*) and constructs them from its
// container when a hosting session starts. The namespace is a discovery convention, nothing more.
namespace Coop.Core.Server.Services.ModularCoopCompat.Handlers;

/// <summary>Server: answers snapshot requests and re-broadcasts a settings object when its values change on the host.</summary>
public sealed class ServerSettingsHandler : IHandler
{
    public const int ProtocolVersion = 1;

    private readonly IMessageBroker broker;
    private readonly INetwork network;
    private readonly bool active;
    private readonly Dictionary<string, string> lastSent = new Dictionary<string, string>(StringComparer.Ordinal);

    public ServerSettingsHandler(IMessageBroker broker, INetwork network)
    {
        this.broker = broker;
        this.network = network;
        if (!CoopProbe.Present) { Log.Warn("settings sync (server) disabled: " + CoopProbe.Report); return; }
        if (!McmBridge.Present) { Log.Info("settings sync (server): MCM not installed, nothing to sync"); return; }
        active = true;
        Wire();
    }

    private void Wire()
    {
        broker.Subscribe<NetworkRequestSettingsSnapshots>(HandleRequest);
        Current = this;
        Log.Info("settings sync (server) armed");
    }

    private void HandleRequest(MessagePayload<NetworkRequestSettingsSnapshots> payload)
    {
        if (payload.Who is not NetPeer peer) return;
        var snapshots = McmBridge.Capture();
        foreach (var s in snapshots)
        {
            lastSent[s.SettingsId] = s.Payload;
            network.Send(peer, new NetworkSettingsSnapshot { SettingsId = s.SettingsId, Payload = s.Payload, ProtocolVersion = ProtocolVersion });
        }
        Log.Info($"settings sync: sent {snapshots.Count} settings object(s) to a joining client");
    }

    /// <summary>Called from the submodule tick on the server: broadcasts any settings object whose values changed.</summary>
    public void BroadcastChanges()
    {
        if (!active) return;
        foreach (var s in McmBridge.Capture())
        {
            if (lastSent.TryGetValue(s.SettingsId, out var prev) && prev == s.Payload) continue;
            lastSent[s.SettingsId] = s.Payload;
            network.SendAll(new NetworkSettingsSnapshot { SettingsId = s.SettingsId, Payload = s.Payload, ProtocolVersion = ProtocolVersion });
            Log.Info("settings sync: broadcast changed settings '" + s.SettingsId + "'");
        }
    }

    public void Dispose()
    {
        if (!active) return;
        broker.Unsubscribe<NetworkRequestSettingsSnapshots>(HandleRequest);
        if (ReferenceEquals(Current, this)) Current = null;
    }

    /// <summary>The live instance (set while armed) so the submodule's tick can drive change broadcasts without owning Coop's lifetime.</summary>
    public static ServerSettingsHandler? Current { get; private set; }
}
