using System;
using System.Collections.Generic;
using System.Linq;
using Common.Messaging;
using Common.Network;
using GameInterface.Services.GameState.Messages;
using ModderLords.CompatSync;
using ModderLords.CompatSync.Coop;
using ModderLords.CompatSync.Messages;

// Coop discovers client-side handlers by namespace prefix (Coop.Core.Client.*); see the server handler.
namespace Coop.Core.Client.Services.ModderLordsCompat.Handlers;

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
        Current = this;
        Log.Info("settings sync (client) armed; settings sources: " + SettingsSources.Summary());
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
        network.SendAll(new NetworkRequestSettingsSnapshots { ProtocolVersion = Bridge.ProtocolVersion });
        Log.Info("settings sync: requested the server's settings");
    }

    private void HandleSnapshot(MessagePayload<NetworkSettingsSnapshot> payload)
    {
        var msg = payload.What;
        received[msg.SettingsId] = msg.Payload;
        if (SettingsSources.Owner(msg.SettingsId) is null)
        {
            // A plain settings object the mod has not created yet: keep the snapshot and apply it once discovery finds it.
            pending.Add(msg.SettingsId);
            Log.Info($"settings sync: '{msg.SettingsId}' from server: not created here yet, will apply when it appears");
            return;
        }
        SettingsSources.Apply(msg.SettingsId, msg.Payload, out var report);
        Log.Info($"settings sync: '{msg.SettingsId}' from server: {report}");
    }

    private readonly Dictionary<string, string> received = new Dictionary<string, string>(StringComparer.Ordinal);
    private readonly HashSet<string> pending = new HashSet<string>(StringComparer.Ordinal);
    private int generation;

    /// <summary>Called from the submodule tick on clients: applies snapshots whose settings object appeared after they arrived.</summary>
    public void ApplyPending()
    {
        // A (re)loaded campaign may restore a mod's own values; push the server's snapshots again.
        if (CampaignWatch.Generation != generation)
        {
            generation = CampaignWatch.Generation;
            if (received.Count > 0)
            {
                foreach (var id in received.Keys) pending.Add(id);
                Log.Info("settings sync: campaign changed, re-applying " + received.Count + " settings object(s) from the server");
            }
        }
        if (pending.Count == 0) return;
        foreach (var id in pending.ToList())
        {
            if (SettingsSources.Owner(id) is null || !received.TryGetValue(id, out var payload)) continue;
            SettingsSources.Apply(id, payload, out var report);
            pending.Remove(id);
            Log.Info($"settings sync: '{id}' from server (deferred): {report}");
        }
    }

    public static ClientSettingsHandler? Current { get; private set; }

    public void Dispose()
    {
        if (!active) return;
        if (ReferenceEquals(Current, this)) Current = null;
        broker.Unsubscribe<NetworkSettingsSnapshot>(HandleSnapshot);
        broker.Unsubscribe<NetworkCompatRecipes>(HandleRecipes);
        broker.Unsubscribe<CampaignReady>(HandleCampaignReady);
    }
}
