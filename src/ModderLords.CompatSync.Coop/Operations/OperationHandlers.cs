using System;
using System.Collections.Generic;
using System.Linq;
using Common;
using Common.Messaging;
using Common.Network;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.Players;
using LiteNetLib;
using Newtonsoft.Json.Linq;
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
        // Shared snapshots are sent as a diff from what each player already has (SnapshotDelta). Per operation: the
        // revision each admitted player was last sent, and a parsed copy of every revision some player still holds.
        private readonly object deltaGate = new object();
        private readonly Dictionary<string, Dictionary<NetPeer, long>> sentRevision = new Dictionary<string, Dictionary<NetPeer, long>>(StringComparer.Ordinal);
        private readonly Dictionary<string, Dictionary<long, JObject>> sentCopies = new Dictionary<string, Dictionary<long, JObject>>(StringComparer.Ordinal);
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
            NetPeer[] gone;
            lock (admissionGate)
            {
                gone = admitted.Keys.Where(p => p.ConnectionState != ConnectionState.Connected).ToArray();
                foreach (var peer in gone) admitted.Remove(peer);
            }
            if (gone.Length == 0) return;
            lock (deltaGate)
                foreach (var operationId in sentRevision.Keys.ToArray())
                {
                    foreach (var peer in gone) sentRevision[operationId].Remove(peer);
                    PruneCopies(operationId);
                }
        }
        private void SendPlan(NetPeer peer)
        {
            // Coop drops ordinary world traffic while a newly accepted peer is still in its pre-save
            // phase.  The operation plan is part of that connection handshake, so it must bypass the
            // loading queue just like Coop's own validation/save-transfer messages.
            network.SendImmediate(peer, new OperationPlanV1 { Json = OperationRuntime.PlanJson, Epoch = epoch });
        }
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
                if (result.State == RequestState.Rejected)
                    ModderLords.CompatSync.Log.Warn("operation command rejected: " + request.OperationId + " for " + actor.ControllerId
                        + " (hero " + actor.HeroId + ", clan " + actor.ClanId + "): " + result.Detail);
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
            var revision = operation.SnapshotRevision;
            JObject parsed;
            try { parsed = SnapshotDelta.Parse(snapshot); }
            catch (Exception ex) { ModderLords.CompatSync.Log.Warn("operation snapshot refused: " + ex.GetBaseException().Message); return; }
            if (!SendSnapshotPayload(peer, operationId, revision, SnapshotDelta.Pack(snapshot), SnapshotTransfer.GzipFull, 0, SnapshotDelta.Fingerprint(parsed))) return;
            // A full copy is where this player's diffs start from. Every shared revision is sent as a diff from it.
            if (operation is ISharedSnapshotOperation)
                lock (deltaGate) Remember(operationId, peer, revision, parsed);
        }
        private bool SendSnapshotPayload(NetPeer peer, string operationId, long revision, string payload, int encoding, long baseRevision, string fingerprint)
        {
            IReadOnlyList<string> chunks;
            try { chunks = SnapshotTransfer.Split(payload); }
            catch (ArgumentException ex) { ModderLords.CompatSync.Log.Warn("operation snapshot refused: " + ex.Message); return false; }
            for (var i = 0; i < chunks.Count; i++)
                network.Send(peer, new OperationSnapshotV1 { Epoch = epoch, OperationId = operationId, Revision = revision,
                    Payload = chunks[i], ChunkIndex = i, ChunkCount = chunks.Count, Encoding = encoding, BaseRevision = baseRevision, Fingerprint = fingerprint });
            return true;
        }
        private void Remember(string operationId, NetPeer peer, long revision, JObject copy)
        {
            if (!sentRevision.TryGetValue(operationId, out var peers)) sentRevision[operationId] = peers = new Dictionary<NetPeer, long>();
            if (!sentCopies.TryGetValue(operationId, out var copies)) sentCopies[operationId] = copies = new Dictionary<long, JObject>();
            peers[peer] = revision;
            copies[revision] = copy;
            PruneCopies(operationId);
        }
        private void PruneCopies(string operationId)
        {
            if (!sentCopies.TryGetValue(operationId, out var copies)) return;
            var held = sentRevision.TryGetValue(operationId, out var peers) ? new HashSet<long>(peers.Values) : new HashSet<long>();
            foreach (var revision in copies.Keys.Where(r => !held.Contains(r)).ToArray()) copies.Remove(revision);
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
            JObject next;
            try { next = SnapshotDelta.Parse(snapshot); }
            catch (Exception ex) { ModderLords.CompatSync.Log.Warn("shared operation snapshot refused: " + ex.GetBaseException().Message); return false; }
            var fingerprint = SnapshotDelta.Fingerprint(next);
            string? full = null;
            var diffs = new Dictionary<long, string>();
            int fulls = 0, deltas = 0; long wireBytes = 0;
            lock (deltaGate)
            {
                sentRevision.TryGetValue(operationId, out var held);
                sentCopies.TryGetValue(operationId, out var copies);
                foreach (var target in authorized)
                {
                    bool ok;
                    string payload;
                    if (held != null && copies != null && held.TryGetValue(target.Peer, out var baseRevision) && copies.TryGetValue(baseRevision, out var from))
                    {
                        if (!diffs.TryGetValue(baseRevision, out payload!))
                            diffs[baseRevision] = payload = SnapshotDelta.Pack(SnapshotDelta.Canonical(SnapshotDelta.Create(from, next)));
                        ok = SendSnapshotPayload(target.Peer, operationId, revision, payload, SnapshotTransfer.GzipDelta, baseRevision, fingerprint);
                        if (ok) deltas++;
                    }
                    else
                    {
                        payload = full ??= SnapshotDelta.Pack(snapshot);
                        ok = SendSnapshotPayload(target.Peer, operationId, revision, payload, SnapshotTransfer.GzipFull, 0, fingerprint);
                        if (ok) fulls++;
                    }
                    if (!ok) continue;
                    wireBytes += payload.Length;
                    Remember(operationId, target.Peer, revision, next);
                }
            }
            if (fulls + deltas == 0) return false;
            broadcastRevisions[operationId] = revision;
            ModderLords.CompatSync.Log.Info("operation snapshot broadcast: " + operationId + " revision=" + revision
                + " bytes=" + System.Text.Encoding.UTF8.GetByteCount(snapshot) + " peers=" + authorized.Length
                + " sent=" + wireBytes + " (" + deltas + " diff(s), " + fulls + " full)");
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
            lock (deltaGate) { sentRevision.Clear(); sentCopies.Clear(); }
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
            // The last snapshot decoded (full or rebuilt from a diff), which the server's next diff applies to.
            public long LastRevision = -1;
            public JObject? Last;
            public string Wire = "";
            public DateTime FullAskedAt = DateTime.MinValue;
            public void Forget() { LastRevision = -1; Last = null; FullAskedAt = DateTime.MinValue; }
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
            foreach (var sink in snapshots.Values) { sink.State.Disconnect(); sink.Reassembler.Disconnect(); sink.Forget(); }
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
                    if (epoch != incomingEpoch) { epoch = incomingEpoch; State.BeginSession(epoch); foreach (var sink in snapshots.Values) { sink.State.BeginSession(epoch); sink.Reassembler.BeginSession(epoch); sink.Forget(); } }
                    // Keep the acknowledgement on Coop's connection-level path as well.  It is the
                    // reliable-ordered barrier that permits the server to resume character validation.
                    network.SendImmediate(peer, new OperationPlanAckV1 { Digest = OperationRuntime.Activation.Digest!, Epoch = epoch });
                    ModderLords.CompatSync.Log.Info("operation plan agreed with server: " + OperationRuntime.Activation.Digest);
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
            var sink = new SnapshotSink { Ready = ready, Refresh = refresh };
            sink.Apply = payload =>
            {
                apply(payload);
                ModderLords.CompatSync.Log.Info("operation snapshot applied: " + operationId
                    + " bytes=" + System.Text.Encoding.UTF8.GetByteCount(payload) + sink.Wire);
            };
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
                    if (!sink.Reassembler.Offer(message.Epoch, message.Revision, message.ChunkIndex, count, message.Payload, out var complete)) return;
                    if (!Decode(sink, message, complete, out var json, out var problem))
                    {
                        // Not usable here (a diff from a copy this game does not have, or a rebuild that does not match):
                        // allow the same revision again and ask for a full copy, at most every ten seconds.
                        sink.Reassembler.RewindTo(sink.LastRevision);
                        if (DateTime.UtcNow - sink.FullAskedAt > TimeSpan.FromSeconds(10))
                        {
                            sink.FullAskedAt = DateTime.UtcNow;
                            ModderLords.CompatSync.Log.Info("operation snapshot " + message.OperationId + " revision " + message.Revision + " not usable (" + problem + "); asking for a full copy");
                            RequestSnapshot(message.OperationId);
                        }
                        return;
                    }
                    sink.State.OfferSnapshot(message.Epoch, message.Revision, json, sink.Ready, sink.Apply, sink.Refresh);
                }
            });
        }
        private static bool Decode(SnapshotSink sink, OperationSnapshotV1 message, string complete, out string json, out string problem)
        {
            json = ""; problem = "";
            try
            {
                JObject decoded;
                switch (message.Encoding)
                {
                    case SnapshotTransfer.PlainJson:
                        json = complete; decoded = SnapshotDelta.Parse(json); sink.Wire = ""; break;
                    case SnapshotTransfer.GzipFull:
                        json = SnapshotDelta.Unpack(complete); decoded = SnapshotDelta.Parse(json);
                        sink.Wire = " (full, " + complete.Length + " bytes sent)"; break;
                    case SnapshotTransfer.GzipDelta:
                        if (sink.Last == null || message.BaseRevision != sink.LastRevision) { problem = "diff from revision " + message.BaseRevision + ", this game has " + sink.LastRevision; return false; }
                        decoded = SnapshotDelta.Apply(sink.Last, SnapshotDelta.Parse(SnapshotDelta.Unpack(complete)));
                        json = SnapshotDelta.Canonical(decoded);
                        sink.Wire = " (diff, " + complete.Length + " bytes sent)"; break;
                    default:
                        problem = "unknown encoding " + message.Encoding; return false;
                }
                if (message.Fingerprint.Length > 0 && SnapshotDelta.Fingerprint(decoded) != message.Fingerprint) { problem = "rebuilt snapshot does not match the server's"; return false; }
                sink.Last = decoded; sink.LastRevision = message.Revision;
                return true;
            }
            catch (Exception ex) { problem = ex.GetBaseException().Message; return false; }
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

