using Common.Messaging;
using Common.Network;
using GameInterface.Services.GameState.Messages;
using ModularCoop.CompatSync;
using ModularCoop.CompatSync.Messages;

// Coop discovers client-side handlers by namespace prefix (Coop.Core.Client.*); see the server handler.
namespace Coop.Core.Client.Services.ModularCoopCompat.Handlers;

/// <summary>Client: asks the server for its settings when the campaign is ready and applies every snapshot it receives.</summary>
public sealed class ClientSettingsHandler : IHandler
{
    private readonly IMessageBroker broker;
    private readonly INetwork network;
    private readonly bool active;

    public ClientSettingsHandler(IMessageBroker broker, INetwork network)
    {
        this.broker = broker;
        this.network = network;
        if (!CoopProbe.Present) { Log.Warn("settings sync (client) disabled: " + CoopProbe.Report); return; }
        if (!McmBridge.Present) { Log.Info("settings sync (client): MCM not installed, nothing to apply"); return; }
        active = true;
        Wire();
    }

    private void Wire()
    {
        broker.Subscribe<NetworkSettingsSnapshot>(HandleSnapshot);
        broker.Subscribe<CampaignReady>(HandleCampaignReady);
        Log.Info("settings sync (client) armed");
    }

    private void HandleCampaignReady(MessagePayload<CampaignReady> payload)
    {
        network.SendAll(new NetworkRequestSettingsSnapshots { ProtocolVersion = ServerSettingsHandlerVersion.Value });
        Log.Info("settings sync: requested the server's settings");
    }

    private void HandleSnapshot(MessagePayload<NetworkSettingsSnapshot> payload)
    {
        var msg = payload.What;
        var changed = McmBridge.Apply(msg.SettingsId, msg.Payload, out var report);
        Log.Info($"settings sync: '{msg.SettingsId}' from server: {report}");
    }

    public void Dispose()
    {
        if (!active) return;
        broker.Unsubscribe<NetworkSettingsSnapshot>(HandleSnapshot);
        broker.Unsubscribe<CampaignReady>(HandleCampaignReady);
    }
}

internal static class ServerSettingsHandlerVersion
{
    public const int Value = 1;
}
