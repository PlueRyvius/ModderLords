using System;
using System.Collections.Generic;
using Common.Messaging;
using Common.Network;
using LiteNetLib;
using ModderLords.CompatSync;
using ModderLords.CompatSync.Coop;
using ModderLords.CompatSync.Messages;

// Coop discovers server-side handlers by namespace prefix (Coop.Core.Server.*) and constructs them from its
// container when a hosting session starts. The namespace is a discovery convention, nothing more.
namespace Coop.Core.Server.Services.ModderLordsCompat.Handlers;

/// <summary>Server: answers snapshot requests and re-broadcasts a settings object when its values change on the host.</summary>
public sealed class ServerSettingsHandler : IHandler
{
    public const int ProtocolVersion = 1;

    private readonly IMessageBroker broker;
    private readonly INetwork network;
    private readonly bool active;
    private string sessionRecipes = "{\"SchemaVersion\":1,\"Mods\":[]}";
    private readonly Dictionary<string, string> lastSent = new Dictionary<string, string>(StringComparer.Ordinal);
    private readonly Dictionary<string, int> broadcastCount = new Dictionary<string, int>(StringComparer.Ordinal);

    public ServerSettingsHandler(IMessageBroker broker, INetwork network)
    {
        this.broker = broker;
        this.network = network;
        if (!CoopProbe.Present) { Log.Warn("settings sync (server) disabled: " + CoopProbe.Report); return; }
        active = true;
        Wire();
    }

    private void Wire()
    {
        broker.Subscribe<NetworkRequestSettingsSnapshots>(HandleRequest);
        broker.Subscribe<NetworkRequestCompatRecipes>(HandleRecipeRequest);
        Current = this;
        var recipes = BehaviorGate.ReadLocalRecipes();
        sessionRecipes = recipes ?? sessionRecipes;
        Log.Info("settings sync (server) armed" + (recipes is null ? "; no recipes.json (no server-only behaviours)" : "; recipes.json loaded"));
        // Generated transformations remain diagnostic; settings, scene exclusions and opt-in tracing remain available.
        if (recipes is not null)
        {
            try { Log.Info("battle scene pick: " + BattleScenePick.Load(recipes)); }
            catch (Exception ex) { Log.Warn("battle scene pick failed to load: " + ex.GetBaseException().Message); }
            // Ground truth for the classifier: only present when the launcher was asked to trace a mod.
            try { if (BehaviorGate.InstallTrace(recipes) is { } trace) Log.Info(trace); }
            catch (Exception ex) { Log.Warn("trace failed to install: " + ex.GetBaseException().Message); }
        }
    }

    private void HandleRecipeRequest(MessagePayload<NetworkRequestCompatRecipes> payload)
    {
        if (payload.Who is not NetPeer peer) return;
        var json = sessionRecipes;
        network.Send(peer, new NetworkCompatRecipes { Json = json, ProtocolVersion = ProtocolVersion });
        Log.Info("recipes sent to a joining client");
    }

    private void HandleRequest(MessagePayload<NetworkRequestSettingsSnapshots> payload)
    {
        if (payload.Who is not NetPeer peer) return;
        SettingsSources.Refresh();
        var snapshots = SettingsSources.Capture();
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
        foreach (var s in SettingsSources.Capture())
        {
            if (lastSent.TryGetValue(s.SettingsId, out var prev) && prev == s.Payload) continue;
            lastSent[s.SettingsId] = s.Payload;
            network.SendAll(new NetworkSettingsSnapshot { SettingsId = s.SettingsId, Payload = s.Payload, ProtocolVersion = ProtocolVersion });
            // A mod that rewrites its own settings every tick would flood the log; cap per id.
            broadcastCount.TryGetValue(s.SettingsId, out var n);
            broadcastCount[s.SettingsId] = n + 1;
            if (n < 5) Log.Info("settings sync: broadcast changed settings '" + s.SettingsId + "'" + (n == 4 ? " (further broadcasts of this id not logged)" : ""));
        }
    }

    public void Dispose()
    {
        if (!active) return;
        broker.Unsubscribe<NetworkRequestSettingsSnapshots>(HandleRequest);
        broker.Unsubscribe<NetworkRequestCompatRecipes>(HandleRecipeRequest);

        if (ReferenceEquals(Current, this)) Current = null;
    }

    /// <summary>The live instance (set while armed) so the submodule's tick can drive change broadcasts without owning Coop's lifetime.</summary>
    public static ServerSettingsHandler? Current { get; private set; }
}
