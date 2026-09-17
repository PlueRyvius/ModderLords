using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TaleWorlds.Core;
using Coop.Core;
using Coop.Core.Common.Configuration;
using ModderLords.Operations;
using TaleWorlds.MountAndBlade;
using Host = Coop.Core.Server.Services.ModderLordsCompat.Handlers.OperationServerHandler;
using Client = Coop.Core.Client.Services.ModderLordsCompat.Handlers.OperationClientHandler;

namespace ModderLords.OperationFixture;

/// <summary>Original test-only mod; never packaged with the launcher and never alters campaign objects.</summary>
public sealed class FixtureDriver
{
    private Client? bound;
    private bool sent;
    private bool campaignSnapshotRequested;
    private bool registered;
    private bool connectionAttempted;
    private bool menuReported;
    private string lastState = "";
    private static void Checkpoint(string message)
    {
        Console.WriteLine(message);
        var path = Environment.GetEnvironmentVariable("MODDERLORDS_FIXTURE_LOG");
        if (!string.IsNullOrEmpty(path)) File.AppendAllText(path, DateTime.UtcNow.ToString("O") + " " + message + Environment.NewLine);
    }
    private void TryConnect()
    {
        if (connectionAttempted || !(GameStateManager.Current?.ActiveState is InitialState)) return;
        if (!menuReported) { FixtureCheckpoint.Emit("menu.ready"); menuReported = true; }
        var configPath = Environment.GetEnvironmentVariable("MODDERLORDS_FIXTURE_CONNECTION");
        if (string.IsNullOrEmpty(configPath)) return;
        var coopType = AppDomain.CurrentDomain.GetAssemblies().SingleOrDefault(a => a.GetName().Name == "Coop")?.GetType("Coop.CoopMod");
        var experience = coopType?.GetField("Coop")?.GetValue(null) as CoopartiveMultiplayerExperience;
        if (experience == null) return;
        connectionAttempted = true;
        var lines = File.ReadAllLines(configPath);
        if (lines.Length != 2 || !int.TryParse(lines[0], out var port) || port < 1024 || port > 65535 || lines[1].Length > 128)
            throw new InvalidOperationException("Invalid local fixture connection file");
        Checkpoint("[operation-fixture] connecting to loopback");
        FixtureCheckpoint.Emit("connection.started");
        var started = experience.StartAsClient(new NetworkConfig { Address = "127.0.0.1", Port = port, Token = lines[1] });
        Checkpoint("[operation-fixture] connection started=" + started);
        if (!started) FixtureCheckpoint.Emit("connection.failed");
    }
    private string epochPayload = "";
    public void Tick()
    {
        if (Environment.GetEnvironmentVariable("MODDERLORDS_OPERATION_FIXTURE") != "1") return;
        if (Common.ModInformation.IsServer)
        {
            if (!registered) { Host.Register(new Counter()); registered = true; Checkpoint("[operation-fixture] server operation registered"); FixtureCheckpoint.Emit("server.registered"); }
            return;
        }
        TryConnect();
        var client = Client.Current;
        if (client == null) return;
        if (client != bound)
        {
            bound = client; sent = false; campaignSnapshotRequested = false;
            client.BindSnapshot("fixture.counter", () => true, value => { epochPayload = value; FixtureCheckpoint.Emit("snapshot.applied", value: value); }, () => Checkpoint("[operation-fixture] snapshot=" + epochPayload));
            client.ResultReceived += result =>
            {
                Checkpoint("[operation-fixture] result=" + result.State + " revision=" + result.Revision + " value=" + result.Payload);
                FixtureCheckpoint.Emit(result.State == RequestState.Completed ? "result.completed" : "result.rejected", result.RequestId, result.Payload, result.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture));
            };
        }
        var campaignReady = GameInterface.ContainerProvider.TryResolve<Coop.Core.Client.IClientLogic>(out var logic)
            && logic.State is Coop.Core.Client.States.CampaignState && logic.Player != null;
        var state = logic?.State?.GetType().Name ?? "unavailable";
        if (lastState != state)
        {
            if (state == "MainMenuState" && lastState != "" && lastState != "MainMenuState")
                FixtureCheckpoint.Emit("connection.ended");
            lastState = state;
            Checkpoint("[operation-fixture] client state=" + state);
            if (state == "CharacterCreationState") FixtureCheckpoint.Emit("character.required");
            if (campaignReady) FixtureCheckpoint.Emit("campaign.ready");
        }
        if (campaignReady && !campaignSnapshotRequested)
        { client.RequestSnapshot("fixture.counter"); campaignSnapshotRequested = true; }
        if (!sent && Environment.GetEnvironmentVariable("MODDERLORDS_FIXTURE_OBSERVE_ONLY") != "1"
            && campaignReady && client.Submit("fixture.counter", "increment", out var request))
        { sent = true; Checkpoint("[operation-fixture] pending request=" + request); FixtureCheckpoint.Emit("command.pending", request); }
    }
    private sealed class Counter : ISnapshotOperation
    {
        private readonly Dictionary<string, int> counters = new Dictionary<string, int>();
        public string Id => "fixture.counter";
        public int MaxPayloadBytes => 16;
        public long SnapshotRevision { get; private set; }
        public bool Validate(Actor actor, string payload, out string reason) { reason = "Invalid request"; return actor.ControllerId.Length > 0 && payload == "increment"; }
        public bool CanReadSnapshot(Actor actor) => actor.ControllerId.Length > 0;
        public string Execute(Actor actor, string payload)
        { counters.TryGetValue(actor.ControllerId, out var value); counters[actor.ControllerId] = value + 1; SnapshotRevision++; FixtureCheckpoint.Emit("counter.executed", value: (value + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)); return CaptureSnapshot(actor); }
        public string CaptureSnapshot(Actor actor) { counters.TryGetValue(actor.ControllerId, out var value); return value.ToString(); }
    }
}
