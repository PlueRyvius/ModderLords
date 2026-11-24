using System;
using System.Linq;
using BellumCivileCoopCompat;
using BellumCivileCoopCompat.Net;
using BellumCivileCoopCompat.Net.Messages;
using Common.Messaging;
using Common.Network;
using GameInterface.Services.Players;
using GameInterface.Services.Players.Data;
using LiteNetLib;
using TaleWorlds.CampaignSystem;

// Coop finds handlers by namespace (Coop.Core.Server.* and Coop.Core.Client.*) and builds them from its container when a
// session starts. The namespaces are a discovery convention, nothing more. Constructors take only Coop's messaging and
// player list, and do nothing that could fail: an exception here would break Coop's session.
namespace Coop.Core.Server.Services.BellumCivileCompat.Handlers
{
    /// <summary>Server: receives players' Bellum requests and sends Bellum messages to one player.</summary>
    public sealed class BellumAgencyServerHandler : IHandler, IAgencyChannel
    {
        private readonly IMessageBroker _broker;
        private readonly INetwork _network;
        private readonly IPlayerManager _players;

        public BellumAgencyServerHandler(IMessageBroker broker, INetwork network, IPlayerManager players)
        {
            _broker = broker;
            _network = network;
            _players = players;
            try
            {
                _broker.Subscribe<BellumAgencyRequest>(OnRequest);
                AgencyNet.Attach(this, "server");
            }
            catch (Exception ex) { AgencyNet.Fail("server", ex); }
        }

        private void OnRequest(MessagePayload<BellumAgencyRequest> payload)
        {
            try
            {
                // The sender is the hero Coop says this peer controls, never one named in the message.
                Hero sender = null;
                if (payload.Who is NetPeer peer && _players.TryGetPlayer(peer, out Player player)
                    && _players.TryGetPeer(player.ControllerId, out NetPeer current) && ReferenceEquals(current, peer))
                    _players.TryGetSenderHero(peer, out sender);
                var message = payload.What;
                BellumCivileCoopCompat.Agency.FromPlayer(sender, message.Protocol, message.Kind ?? "", message.Args?.ToArray() ?? new string[0]);
            }
            catch (Exception ex) { AgencyNet.Fail("server request", ex); }
        }

        public void ToServer(string kind, string[] args) { }

        public bool ToPlayer(Hero hero, string kind, string[] args)
        {
            if (!_players.TryGetPlayer(hero, out Player player) || !_players.TryGetPeer(player.ControllerId, out NetPeer peer) || peer == null)
                return false;
            _network.Send(peer, new BellumAgencyMessage { Protocol = BellumCivileCoopCompat.Agency.Protocol, Kind = kind, Args = args.ToList() });
            return true;
        }

        public void Dispose()
        {
            try { _broker.Unsubscribe<BellumAgencyRequest>(OnRequest); }
            catch { }
            AgencyNet.Detach(this);
        }
    }
}

namespace Coop.Core.Client.Services.BellumCivileCompat.Handlers
{
    /// <summary>Player's PC: sends Bellum requests to the server and receives the server's Bellum messages.</summary>
    public sealed class BellumAgencyClientHandler : IHandler, IAgencyChannel
    {
        private readonly IMessageBroker _broker;
        private readonly INetwork _network;

        public BellumAgencyClientHandler(IMessageBroker broker, INetwork network)
        {
            _broker = broker;
            _network = network;
            try
            {
                _broker.Subscribe<BellumAgencyMessage>(OnMessage);
                AgencyNet.Attach(this, "player");
            }
            catch (Exception ex) { AgencyNet.Fail("player", ex); }
        }

        private void OnMessage(MessagePayload<BellumAgencyMessage> payload)
        {
            try
            {
                var message = payload.What;
                BellumCivileCoopCompat.Agency.FromServer(message.Protocol, message.Kind ?? "", message.Args?.ToArray() ?? new string[0]);
            }
            catch (Exception ex) { AgencyNet.Fail("player message", ex); }
        }

        public void ToServer(string kind, string[] args) =>
            _network.SendAll(new BellumAgencyRequest { Protocol = BellumCivileCoopCompat.Agency.Protocol, Kind = kind, Args = args.ToList() });

        public bool ToPlayer(Hero hero, string kind, string[] args) => false;

        public void Dispose()
        {
            try { _broker.Unsubscribe<BellumAgencyMessage>(OnMessage); }
            catch { }
            AgencyNet.Detach(this);
        }
    }
}
