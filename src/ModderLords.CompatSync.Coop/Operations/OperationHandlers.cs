using System;
using System.Collections.Generic;
using System.Linq;
using Common;
using Common.Messaging;
using Common.Network;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.Players;
using LiteNetLib;
using ModderLords.Operations;
using ModderLords.CompatSync.Coop.Operations;

namespace Coop.Core.Server.Services.ModderLordsCompat.Handlers
{
    public sealed class OperationServerHandler : IHandler, IDisposable
    {
        private readonly IMessageBroker broker;
        private readonly INetwork network;
        private readonly IPlayerManager players;
        private readonly IObjectManager objects;
        private readonly object admissionGate = new object();
        private readonly Dictionary<NetPeer, string> admitted = new Dictionary<NetPeer, string>();
        private readonly Dictionary<string, long> broadcastRevisions = new Dictionary<string, long>(StringComparer.Ordinal);
        private readonly string epoch = Guid.NewGuid().ToString("N");
        private CommandDispatcher? dispatcher;
        // A compiled adapter may register its domain operations before the session dispatcher is frozen.
        private static readonly List<IServerOperation> registered = new List<IServerOperation>();
        public static void Register(IServerOperation operation) { if (Current?.dispatcher != null) throw new InvalidOperationException("Operation registry is frozen"); registered.Add(operation); }
        public static void Unregister(IServerOperation operation) { if (Current?.dispatcher == null) registered.Remove(operation); }
        public static OperationServerHandler? Current { get; private set; }
        public OperationServerHandler(IMessageBroker broker, INetwork network, IPlayerManager players, IObjectManager objects)
        {
            this.broker = broker; this.network = network; this.players = players; this.objects = objects; Current = this;
            ClansResourceAdderAdapter.Players = players; ClansResourceAdderAdapter.Objects = objects;
            OperationRuntime.InitializeFromEnvironment();
            if (OperationRuntime.JoinBarrier != null) OperationRuntime.JoinBarrier.SendPlan = SendPlan;
            broker.Subscribe<OperationHelloV1>(Hello); broker.Subscribe<OperationPlanAckV1>(Ack); broker.Subscribe<OperationCommandV1>(Command);
            broker.Subscribe<OperationSnapshotRequestV1>(SnapshotRequest);
        }
        private void Hello(MessagePayload<OperationHelloV1> payload)
        {
            if (!OperationRuntime.SessionActive || payload.Who is not NetPeer peer || payload.What?.Version != 1) return;
            if (!IsAdmitted(peer) && OperationRuntime.JoinBarrier?.Begin(peer) != true) { peer.Disconnect(); return; }
            SendPlan(peer);
        }
        private bool IsAdmitted(NetPeer peer) { lock (admissionGate) return peer.ConnectionState == ConnectionState.Connected && admitted.ContainsKey(peer); }
        public void RemoveDisconnectedPeers()
        {
            lock (admissionGate) foreach (var peer in admitted.Keys.Where(p => p.ConnectionState != ConnectionState.Connected).ToArray()) admitted.Remove(peer);
        }
        private void SendPlan(NetPeer peer) => network.Send(peer, new OperationPlanV1 { Json = OperationRuntime.PlanJson, Epoch = epoch });
        private void Ack(MessagePayload<OperationPlanAckV1> payload)
        {
            if (payload.Who is not NetPeer peer || payload.What == null) return;
            var digest = payload.What.Digest; var acknowledgementEpoch = payload.What.Epoch;
            GameThread.RunSafe(() =>
            {
                if (peer.ConnectionState != ConnectionState.Connected || acknowledgementEpoch != epoch || !OperationRuntime.Activation.Admit(digest)
                    || !OperationRuntime.CheckReadiness()) { peer.Disconnect(); return; }
                try { dispatcher ??= new CommandDispatcher(OperationRuntime.Activation.Digest!, epoch, registered); }
                catch { peer.Disconnect(); return; }
                if (OperationRuntime.JoinBarrier?.Acknowledge(peer) != true) { peer.Disconnect(); return; }
                lock (admissionGate) admitted[peer] = digest;
                ModderLords.CompatSync.Log.Info("operation plan agreed peer=" + peer.Id);
            });
        }
        private void Command(MessagePayload<OperationCommandV1> payload)
        {
            if (payload.Who is not NetPeer peer || payload.What == null || !IsAdmitted(peer)) return;
            var request = payload.What;
            if (request.Payload == null || request.Payload.Length > 256 * 1024 || request.OperationId == null || request.OperationId.Length > 128 || request.RequestId?.Length != 32) return;
            // Re-authenticate on the game thread; ownership may have changed while this request was queued.
            GameThread.RunSafe(() =>
            {
                if (!OperationRuntime.CheckReadiness()) { peer.Disconnect(); return; }
                var actor = ResolveActor(peer);
                if (actor == null)
                {
                    network.Send(peer, new OperationResultV1 { Epoch = epoch, RequestId = request.RequestId,
                        State = (int)RequestState.Rejected, Detail = "Actor ownership is unavailable" });
                    return;
                }
                dispatcher ??= new CommandDispatcher(OperationRuntime.Activation.Digest!, epoch, registered);
                var result = dispatcher.Execute(new OperationCommand(request.Digest, request.Epoch, request.RequestId, request.OperationId, request.Payload), actor);
                network.Send(peer, new OperationResultV1 { Epoch = epoch, RequestId = result.RequestId, State = (int)result.State, Detail = result.Detail, Payload = result.Payload, Revision = result.Revision });
                SendSnapshot(peer, request.OperationId, actor);
            });
        }
        private void SnapshotRequest(MessagePayload<OperationSnapshotRequestV1> payload)
        {
            if (payload.Who is not NetPeer peer || payload.What == null || !IsAdmitted(peer) || payload.What.Epoch != epoch || !OperationRuntime.Activation.Admit(payload.What.Digest)) return;
            GameThread.RunSafe(() =>
            {
                if (!OperationRuntime.CheckReadiness()) { peer.Disconnect(); return; }
                var actor = ResolveActor(peer);
                if (actor != null) SendSnapshot(peer, payload.What.OperationId, actor);
            });
        }
        private void SendSnapshot(NetPeer peer, string operationId, Actor actor)
        {
            var operation = registered.OfType<ISnapshotOperation>().SingleOrDefault(o => o.Id == operationId);
            if (operation == null || !operation.CanReadSnapshot(actor)) return;
            var snapshot = operation.CaptureSnapshot(actor);
            if (snapshot == null) return;
            SendSnapshotPayload(peer, operationId, operation.SnapshotRevision, snapshot);
        }
        private void SendSnapshotPayload(NetPeer peer, string operationId, long revision, string snapshot)
        {
            IReadOnlyList<string> chunks;
            try { chunks = SnapshotTransfer.Split(snapshot); }
            catch (ArgumentException ex) { ModderLords.CompatSync.Log.Warn("operation snapshot refused: " + ex.Message); return; }
            for (var i = 0; i < chunks.Count; i++)
                network.Send(peer, new OperationSnapshotV1 { Epoch = epoch, OperationId = operationId, Revision = revision,
                    Payload = chunks[i], ChunkIndex = i, ChunkCount = chunks.Count });
        }
        /// <summary>
        /// Captures one actor-independent snapshot and broadcasts it to every currently admitted actor. Calls are
        /// expected on the game thread. Revisions already broadcast are suppressed, including when a join request
        /// happened to capture the same state first.
        /// </summary>
        public bool BroadcastSharedSnapshot(string operationId)
        {
            var operation = registered.OfType<ISharedSnapshotOperation>().SingleOrDefault(o => o.Id == operationId);
            if (operation == null) return false;
            NetPeer[] peers;
            lock (admissionGate) peers = admitted.Keys.Where(p => p.ConnectionState == ConnectionState.Connected).ToArray();
            var authorized = peers.Select(peer => new { Peer = peer, Actor = ResolveActor(peer) })
                .Where(x => x.Actor != null && operation.CanReadSnapshot(x.Actor)).ToArray();
            if (authorized.Length == 0) return false;
            var snapshot = operation.CaptureSharedSnapshot();
            var revision = operation.SnapshotRevision;
            if (broadcastRevisions.TryGetValue(operationId, out var sent) && revision <= sent) return true;
            IReadOnlyList<string> chunks;
            try { chunks = SnapshotTransfer.Split(snapshot); }
            catch (ArgumentException ex) { ModderLords.CompatSync.Log.Warn("shared operation snapshot refused: " + ex.Message); return false; }
            foreach (var target in authorized)
                for (var i = 0; i < chunks.Count; i++)
                    network.Send(target.Peer, new OperationSnapshotV1 { Epoch = epoch, OperationId = operationId,
                        Revision = revision, Payload = chunks[i], ChunkIndex = i, ChunkCount = chunks.Count });
            broadcastRevisions[operationId] = revision;
            ModderLords.CompatSync.Log.Info("operation snapshot broadcast: " + operationId + " revision=" + revision
                + " bytes=" + System.Text.Encoding.UTF8.GetByteCount(snapshot) + " peers=" + authorized.Length);
            return true;
        }
        private Actor? ResolveActor(NetPeer peer)
        {
            GameInterface.Services.Players.Data.Player player;
            NetPeer currentPeer;
            TaleWorlds.CampaignSystem.Hero hero;
            if (!IsAdmitted(peer) || !players.TryGetPlayer(peer, out player) || !players.TryGetPeer(player.ControllerId, out currentPeer) || !ReferenceEquals(peer, currentPeer)
                || !objects.TryGetObject<TaleWorlds.CampaignSystem.Hero>(player.HeroId, out hero) || hero?.Clan == null) return null;
            return new Actor(player.ControllerId, player.HeroId, hero.Clan.StringId);
        }
        public void Dispose()
        {
            broker.Unsubscribe<OperationHelloV1>(Hello); broker.Unsubscribe<OperationPlanAckV1>(Ack); broker.Unsubscribe<OperationCommandV1>(Command);
            broker.Unsubscribe<OperationSnapshotRequestV1>(SnapshotRequest);
            lock (admissionGate) admitted.Clear(); broadcastRevisions.Clear(); registered.Clear(); Current = null; OperationRuntime.EndSession();
        }
    }
}
namespace Coop.Core.Client.Services.ModderLordsCompat.Handlers
{
    public sealed class OperationClientHandler : IHandler, IDisposable
    {
        private readonly IMessageBroker broker;
        private readonly INetwork network;
        private NetPeer? server;
        private string epoch = "";
        private int planQueued;
        private sealed class SnapshotSink
        {
            public ClientOperationState State = new ClientOperationState();
            public SnapshotReassembler Reassembler = new SnapshotReassembler();
            public Func<bool> Ready = null!;
            public Action<string> Apply = null!;
            public Action Refresh = null!;
        }
        private readonly Dictionary<string, SnapshotSink> snapshots = new Dictionary<string, SnapshotSink>();
        public ClientOperationState State { get; } = new ClientOperationState();
        public event Action<OperationResult>? ResultReceived;
        public static OperationClientHandler? Current { get; private set; }
        public OperationClientHandler(IMessageBroker broker, INetwork network)
        {
            this.broker = broker; this.network = network; Current = this;
            OperationRuntime.InitializeFromEnvironment();
            broker.Subscribe<OperationPlanV1>(Plan); broker.Subscribe<OperationResultV1>(Result);
            broker.Subscribe<OperationSnapshotV1>(Snapshot);
            broker.Subscribe<global::Coop.Core.Client.Messages.NetworkConnected>(Connected);
            broker.Subscribe<global::Coop.Core.Client.Messages.NetworkDisconnected>(Disconnected);
            network.SendAll(new OperationHelloV1());
        }
        private void Connected(MessagePayload<global::Coop.Core.Client.Messages.NetworkConnected> payload)
            => network.SendAll(new OperationHelloV1());
        private void Disconnected(MessagePayload<global::Coop.Core.Client.Messages.NetworkDisconnected> payload)
        {
            State.Disconnect();
            if (OperationRuntime.JoinBarrier != null) OperationRuntime.JoinBarrier.ClientAgreed = false;
            foreach (var sink in snapshots.Values) { sink.State.Disconnect(); sink.Reassembler.Disconnect(); }
            server = null; epoch = "";
            // Preserve the frozen campaign plan. A reconnect may agree with it, never replace it.
        }
        private void Plan(MessagePayload<OperationPlanV1> payload)
        {
            if (payload.Who is not NetPeer peer || payload.What?.Version != 1) return;
            var json = payload.What.Json; var incomingEpoch = payload.What.Epoch;
            if (json == null || json.Length > 1024 * 1024 || !Guid.TryParseExact(incomingEpoch, "N", out _)) { peer.Disconnect(); return; }
            if (System.Threading.Interlocked.CompareExchange(ref planQueued, 1, 0) != 0) return;
            GameThread.RunSafe(() =>
            {
                try
                {
                    if (peer.ConnectionState != ConnectionState.Connected || server != null && !ReferenceEquals(server, peer)) return;
                    if (!OperationRuntime.TryActivate(json, out _)) { peer.Disconnect(); return; }
                    server = peer;
                    OperationRuntime.JoinBarrier!.ClientAgreed = true;
                    if (epoch != incomingEpoch) { epoch = incomingEpoch; State.BeginSession(epoch); foreach (var sink in snapshots.Values) { sink.State.BeginSession(epoch); sink.Reassembler.BeginSession(epoch); } }
                    network.Send(peer, new OperationPlanAckV1 { Digest = OperationRuntime.Activation.Digest!, Epoch = epoch });
                    foreach (var operationId in snapshots.Keys) RequestSnapshot(operationId);
                }
                finally { System.Threading.Interlocked.Exchange(ref planQueued, 0); }
            });
        }

        public bool Submit(string operationId, string payload, out string requestId)
        {
            requestId = Guid.NewGuid().ToString("N");
            if (server == null || epoch.Length == 0 || snapshots.Values.Any(s => s.State.ApplyingSnapshot) || !State.BeginRequest(requestId)) return false;
            network.Send(server, new OperationCommandV1 { Digest = OperationRuntime.Activation.Digest!, Epoch = epoch, RequestId = requestId, OperationId = operationId, Payload = payload });
            return true; // Submission only. UI must await ResultReceived before reporting success.
        }
        private void Result(MessagePayload<OperationResultV1> payload)
        {
            if (!ReferenceEquals(server, payload.Who) || payload.What == null) return;
            var m = payload.What;
            if (!Enum.IsDefined(typeof(RequestState), m.State)) return;
            GameThread.RunSafe(() =>
            {
                var result = new OperationResult(m.RequestId, (RequestState)m.State, m.Detail, m.Payload, m.Revision);
                if (State.AcceptResult(m.Epoch, result)) ResultReceived?.Invoke(result);
            });
        }
        public void BindSnapshot(string operationId, Func<bool> ready, Action<string> apply, Action refresh)
        {
            var sink = new SnapshotSink { Ready = ready, Apply = apply, Refresh = refresh };
            if (epoch.Length > 0) { sink.State.BeginSession(epoch); sink.Reassembler.BeginSession(epoch); }
            snapshots.Add(operationId, sink); RequestSnapshot(operationId);
        }
        public void UnbindSnapshot(string operationId) => snapshots.Remove(operationId);
        public void RequestSnapshot(string operationId)
        {
            if (server != null && epoch.Length > 0) network.Send(server, new OperationSnapshotRequestV1 { Digest = OperationRuntime.Activation.Digest!, Epoch = epoch, OperationId = operationId });
        }
        private void Snapshot(MessagePayload<OperationSnapshotV1> payload)
        {
            if (!ReferenceEquals(server, payload.Who) || payload.What == null || payload.What.Payload == null ||
                System.Text.Encoding.UTF8.GetByteCount(payload.What.Payload) > SnapshotTransfer.MaxChunkBytes) return;
            var message = payload.What;
            GameThread.RunSafe(() =>
            {
                if (snapshots.TryGetValue(message.OperationId, out var sink))
                {
                    var count = message.ChunkCount == 0 ? 1 : message.ChunkCount;
                    if (sink.Reassembler.Offer(message.Epoch, message.Revision, message.ChunkIndex, count, message.Payload, out var complete))
                        sink.State.OfferSnapshot(message.Epoch, message.Revision, complete, sink.Ready, sink.Apply, sink.Refresh);
                }
            });
        }
        public void ApplyPendingSnapshots()
        {
            foreach (var sink in snapshots.Values) sink.State.ApplyPending(sink.Ready, sink.Apply, sink.Refresh);
        }
        public void Dispose()
        {
            broker.Unsubscribe<OperationPlanV1>(Plan); broker.Unsubscribe<OperationResultV1>(Result);
            broker.Unsubscribe<OperationSnapshotV1>(Snapshot);
            broker.Unsubscribe<global::Coop.Core.Client.Messages.NetworkConnected>(Connected);
            broker.Unsubscribe<global::Coop.Core.Client.Messages.NetworkDisconnected>(Disconnected);
            foreach (var sink in snapshots.Values) { sink.State.Disconnect(); sink.Reassembler.Disconnect(); } snapshots.Clear();
            State.Disconnect(); server = null; Current = null; OperationRuntime.EndSession();
        }
    }
}

