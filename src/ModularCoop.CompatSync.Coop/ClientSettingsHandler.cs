using Common.Messaging;
using Common.Network;
using GameInterface.Services.GameState.Messages;
using ModularCoop.CompatSync;
using ModularCoop.CompatSync.Coop;
using ModularCoop.CompatSync.Messages;

// Coop discovers client-side handlers by namespace prefix (Coop.Core.Client.*); see the server handler.
namespace Coop.Core.Client.Services.ModularCoopCompat.Handlers;

/// <summary>
/// Client: asks for the behaviour recipes as soon as the session exists (before the campaign loads, so the gates are
/// in place when behaviours register), asks for settings when the campaign is ready, applies both.
/// </summary>
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
        active = true;
        Wire();
    }

    private void Wire()
    {
        broker.Subscribe<NetworkSettingsSnapshot>(HandleSnapshot);
        broker.Subscribe<NetworkCompatRecipes>(HandleRecipes);
        broker.Subscribe<CampaignReady>(HandleCampaignReady);
        Log.Info("settings sync (client) armed" + (McmBridge.Present ? "" : " (MCM absent: settings will not be applied)"));
        // A copy of the recipe may already ship with the module; apply it now, then ask the server for the live one.
        var local = BehaviorGate.ReadLocalRecipes();
        if (local is not null) Log.Info("local recipes: " + BehaviorGate.Apply(local));
        network.SendAll(new NetworkRequestCompatRecipes { ProtocolVersion = Bridge.ProtocolVersion });
    }

    private void HandleRecipes(MessagePayload<NetworkCompatRecipes> payload)
    {
        Log.Info("server recipes: " + BehaviorGate.Apply(payload.What.Json));
    }

    private void HandleCampaignReady(MessagePayload<CampaignReady> payload)
    {
        if (!McmBridge.Present) return;
        network.SendAll(new NetworkRequestSettingsSnapshots { ProtocolVersion = Bridge.ProtocolVersion });
        Log.Info("settings sync: requested the server's settings");
    }

    private void HandleSnapshot(MessagePayload<NetworkSettingsSnapshot> payload)
    {
        var msg = payload.What;
        McmBridge.Apply(msg.SettingsId, msg.Payload, out var report);
        Log.Info($"settings sync: '{msg.SettingsId}' from server: {report}");
    }

    public void Dispose()
    {
        if (!active) return;
        broker.Unsubscribe<NetworkSettingsSnapshot>(HandleSnapshot);
        broker.Unsubscribe<NetworkCompatRecipes>(HandleRecipes);
        broker.Unsubscribe<CampaignReady>(HandleCampaignReady);
    }
}
