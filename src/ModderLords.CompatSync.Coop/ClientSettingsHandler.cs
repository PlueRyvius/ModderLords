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
        broker.Subscribe<NetworkRelayResult>(HandleRelayResult);
        // Generic relays are diagnostic only; the operation channel owns validated commands.
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
        // Asked again here. The request made when this handler arms goes out before the connection exists and is lost:
        // on 2026-09-14 the client armed and "Attempting connection" in the same second, and the server never logged
        // sending recipes. Applying a recipe twice is harmless (gates are idempotent, pending postfixes get retried).
        network.SendAll(new NetworkRequestCompatRecipes { ProtocolVersion = Bridge.ProtocolVersion });
        Log.Info("settings sync: requested the server's settings and recipes");
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

    private int relaySequence;

    /// <summary>From the relay prefix, on the game thread: the player just ran a relayed method; ask the server to run it as them.</summary>
    private void SendRelay(string method, object?[] args)
    {
        var kinds = new List<string>();
        var values = new List<string>();
        if (!RelayCodec.TryEncode(args, kinds, values, out var why))
        {
            Log.Warn("relay not sent " + method + ": " + why);
            return;
        }
        relays.Add(method, kinds, values, DateTime.UtcNow);
    }

    private readonly RelayCoalescer relays = new RelayCoalescer(TimeSpan.FromMilliseconds(300));

    /// <summary>From the submodule tick on clients: sends each relayed call whose control has settled (latest value only).</summary>
    public void FlushRelays()
    {
        if (relays.PendingCount == 0) return;
        foreach (var (method, kinds, values) in relays.Due(DateTime.UtcNow))
        {
            var seq = ++relaySequence;
            network.SendAll(new NetworkRelayInvoke { Method = method, Kinds = kinds, Values = values, Sequence = seq, ProtocolVersion = Bridge.ProtocolVersion });
            Log.Info($"relay sent {method} #{seq} ({string.Join(", ", values)})");
        }
    }

    private void HandleRelayResult(MessagePayload<NetworkRelayResult> payload)
    {
        var r = payload.What;
        Log.Info($"relay {(r.Ran ? "ran on the server" : "rejected by the server")}: {r.Method} #{r.Sequence} ({r.Reason})");
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
        broker.Unsubscribe<NetworkRelayResult>(HandleRelayResult);
    }
}
