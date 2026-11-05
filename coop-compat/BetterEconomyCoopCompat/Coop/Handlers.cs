using System;
using System.Collections.Generic;
using System.Linq;
using BetterEconomyCoopCompat;
using BetterEconomyCoopCompat.Messages;
using Common;
using Common.Messaging;
using Common.Network;
using GameInterface;
using GameInterface.Services.GameState.Messages;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.Players;
using LiteNetLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;

// Coop discovers handlers by namespace prefix (Coop.Core.Client.* / Coop.Core.Server.*) and builds them from its
// container when a session starts; the rest of each name is this module's own. Both exist in every Coop session where
// this module is loaded, and do nothing without Living Economy (Net.LayerActive).
namespace Coop.Core.Client.Services.BetterEconomyCoopCompat.Handlers
{
    /// <summary>Player's PC: says hello, and once the server accepts, relays clicks and loads the server's records.</summary>
    public sealed class LeClientHandler : IHandler
    {
        private readonly IMessageBroker broker;
        private readonly INetwork network;
        private int sequence;

        public LeClientHandler(IMessageBroker broker, INetwork network)
        {
            this.broker = broker;
            this.network = network;
            Net.SessionEnded();   // a fresh session starts from nothing
            broker.Subscribe<CampaignReady>(HandleCampaignReady);
            broker.Subscribe<LeHelloReply>(HandleHelloReply);
            broker.Subscribe<LeActionResult>(HandleActionResult);
            broker.Subscribe<LeState>(HandleState);
            broker.Subscribe<LeNotice>(HandleNotice);
        }

        public void Dispose()
        {
            broker.Unsubscribe<CampaignReady>(HandleCampaignReady);
            broker.Unsubscribe<LeHelloReply>(HandleHelloReply);
            broker.Unsubscribe<LeActionResult>(HandleActionResult);
            broker.Unsubscribe<LeState>(HandleState);
            broker.Unsubscribe<LeNotice>(HandleNotice);
            Net.SessionEnded();
        }

        private void HandleCampaignReady(MessagePayload<CampaignReady> payload)
        {
            if (!Net.LayerActive) return;
            network.SendAll(new LeHello { ProtocolVersion = Net.ProtocolVersion });
        }

        private void HandleHelloReply(MessagePayload<LeHelloReply> payload)
        {
            var reply = payload.What;
            if (!reply.Accepted || reply.ProtocolVersion != Net.ProtocolVersion)
            {
                Log.Warn($"the server did not accept this PC's relaying ({reply.Detail}; server protocol {reply.ProtocolVersion}, here {Net.ProtocolVersion}). "
                    + "Living Economy runs on this PC as it does alone");
                return;
            }
            Net.SendAction = (op, args) => network.SendAll(new LeAction
            {
                Op = op, Args = new List<string>(args), Sequence = ++sequence, ProtocolVersion = Net.ProtocolVersion,
            });
            Net.RequestFull = () => network.SendAll(new LeStateRequest { ProtocolVersion = Net.ProtocolVersion });
            Net.Ready = true;
            Log.Info($"the server accepted protocol {Net.ProtocolVersion}: the economy runs on the server and its records follow it here");
            Net.RequestFull();
        }

        private void HandleActionResult(MessagePayload<LeActionResult> payload)
        {
            var r = payload.What;
            if (r.ProtocolVersion != Net.ProtocolVersion) return;
            GameThread.RunSafe(() => Net.ShowLines(r.Lines ?? new List<string>()), false, "Living Economy Coop Compat action result");
        }

        private void HandleState(MessagePayload<LeState> payload)
        {
            var m = payload.What;
            if (m.ProtocolVersion != Net.ProtocolVersion || !Net.Ready) return;
            GameThread.RunSafe(() => Net.ApplyState(m.Behaviour, m.Payload), false, "Living Economy Coop Compat state");
        }

        private void HandleNotice(MessagePayload<LeNotice> payload)
        {
            var m = payload.What;
            if (m.ProtocolVersion != Net.ProtocolVersion) return;
            GameThread.RunSafe(() => Net.ShowNotice(m.Text, m.Color), false, "Living Economy Coop Compat notice");
        }
    }
}

namespace Coop.Core.Server.Services.BetterEconomyCoopCompat.Handlers
{
    /// <summary>
    /// Server: accepts players whose protocol matches, carries out their clicks as them, and sends the records to the
    /// accepted ones only. A player who isn't accepted gets nothing and has nothing run for them.
    /// </summary>
    public sealed class LeServerHandler : IHandler
    {
        private readonly IMessageBroker broker;
        private readonly INetwork network;
        private readonly HashSet<NetPeer> accepted = new HashSet<NetPeer>();
        private readonly Dictionary<NetPeer, Queue<DateTime>> recent = new Dictionary<NetPeer, Queue<DateTime>>();

        /// <summary>A backstop against a flood: a player's menu never sends more than a few clicks a second.</summary>
        private const int ActionsPerSecond = 10;

        public LeServerHandler(IMessageBroker broker, INetwork network)
        {
            this.broker = broker;
            this.network = network;
            Net.SessionEnded();
            broker.Subscribe<LeHello>(HandleHello);
            broker.Subscribe<LeAction>(HandleAction);
            broker.Subscribe<LeStateRequest>(HandleStateRequest);
            Net.Broadcast = (behaviour, data) =>
            {
                List<NetPeer> peers;
                lock (accepted) peers = accepted.ToList();
                foreach (var peer in peers)
                    network.Send(peer, new LeState { Behaviour = behaviour, Payload = data, ProtocolVersion = Net.ProtocolVersion });
            };
            Net.SendNotice = (hero, text, color) =>
            {
                if (PeerFor(hero) is { } peer)
                    network.Send(peer, new LeNotice { Text = text, Color = color, ProtocolVersion = Net.ProtocolVersion });
            };
        }

        public void Dispose()
        {
            broker.Unsubscribe<LeHello>(HandleHello);
            broker.Unsubscribe<LeAction>(HandleAction);
            broker.Unsubscribe<LeStateRequest>(HandleStateRequest);
            Net.SessionEnded();
        }

        private void HandleHello(MessagePayload<LeHello> payload)
        {
            if (payload.Who is not NetPeer peer) return;
            var ok = Net.LayerActive && payload.What.ProtocolVersion == Net.ProtocolVersion;
            var detail = !Net.LayerActive ? "Living Economy is not running on the server"
                : ok ? "accepted" : $"protocol {payload.What.ProtocolVersion} is not this server's {Net.ProtocolVersion}; update Living Economy Coop Compat";
            lock (accepted)
            {
                if (ok) accepted.Add(peer);
                else accepted.Remove(peer);
            }
            network.Send(peer, new LeHelloReply { ProtocolVersion = Net.ProtocolVersion, Accepted = ok, Detail = detail });
            Log.Info("a player said hello: " + detail);
        }

        private bool IsAccepted(NetPeer peer, int version)
        {
            if (version != Net.ProtocolVersion) return false;
            lock (accepted) return accepted.Contains(peer);
        }

        private void HandleStateRequest(MessagePayload<LeStateRequest> payload)
        {
            if (payload.Who is not NetPeer peer || !IsAccepted(peer, payload.What.ProtocolVersion)) return;
            GameThread.RunSafe(() =>
            {
                var all = Net.StateForJoin();
                foreach (var (behaviour, data) in all)
                    network.Send(peer, new LeState { Behaviour = behaviour, Payload = data, ProtocolVersion = Net.ProtocolVersion });
                if (all.Count > 0) Log.Info($"state mirror sent {all.Count - 1} book(s) to a player");
            }, false, "Living Economy Coop Compat state request");
        }

        private void HandleAction(MessagePayload<LeAction> payload)
        {
            if (payload.Who is not NetPeer peer) return;
            var msg = payload.What;
            void Reply(bool ok, List<string> lines) => network.Send(peer, new LeActionResult
            {
                Op = msg.Op, Sequence = msg.Sequence, Ok = ok, Lines = lines, ProtocolVersion = Net.ProtocolVersion,
            });
            static List<string> Line(string text) => new List<string> { ActionCodec.Line(TaleWorlds.Library.Colors.Yellow.ToUnsignedInteger(), text) };

            if (!IsAccepted(peer, msg.ProtocolVersion)) return;
            if (!RateOk(peer)) { Reply(false, Line("Too many requests; slow down a moment.")); return; }
            if (!ContainerProvider.TryResolve<IPlayerManager>(out var players) || !players.TryGetPlayer(peer, out var player) || player is null)
            {
                Reply(false, Line("The server does not know you yet; try again in a moment."));
                return;
            }
            GameThread.RunSafe(() =>
            {
                try
                {
                    if (!ContainerProvider.TryResolve<IObjectManager>(out var objects) || !objects.TryGetObject<Hero>(player.HeroId, out var hero) || hero is null)
                    {
                        Reply(false, Line("Your hero was not found on the server."));
                        return;
                    }
                    objects.TryGetObject<MobileParty>(player.MobilePartyId, out var party);
                    var (ok, lines) = Net.RunAction(hero, party, msg.Op, msg.Args ?? new List<string>());
                    Reply(ok, lines);
                }
                catch (Exception ex) { Reply(false, Line("The server could not do that: " + ex.GetBaseException().Message)); }
            }, false, "Living Economy Coop Compat action");
        }

        private bool RateOk(NetPeer peer)
        {
            lock (recent)
            {
                if (!recent.TryGetValue(peer, out var q)) recent[peer] = q = new Queue<DateTime>();
                var now = DateTime.UtcNow;
                while (q.Count > 0 && (now - q.Peek()).TotalSeconds > 1) q.Dequeue();
                if (q.Count >= ActionsPerSecond) return false;
                q.Enqueue(now);
                return true;
            }
        }

        /// <summary>
        /// The connection of the player who controls this hero. Coop's Player.HeroId is its own object id, not the hero's
        /// StringId, so the hero is found through Coop's object manager.
        /// </summary>
        private NetPeer? PeerFor(Hero hero)
        {
            if (!ContainerProvider.TryResolve<IPlayerManager>(out var players) || !ContainerProvider.TryResolve<IObjectManager>(out var objects)) return null;
            var player = players.Players.FirstOrDefault(p => objects.TryGetObject<Hero>(p.HeroId, out var h) && ReferenceEquals(h, hero));
            if (player == null || !players.TryGetPeer(player.ControllerId, out var peer) || peer == null) return null;
            lock (accepted) return accepted.Contains(peer) ? peer : null;
        }
    }
}
