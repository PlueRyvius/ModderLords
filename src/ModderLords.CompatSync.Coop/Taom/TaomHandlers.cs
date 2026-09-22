using System;
using Common;
using Common.Messaging;
using Common.Network;
using GameInterface;
using GameInterface.Services.GameState.Messages;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.Players;
using LiteNetLib;
using ModderLords.CompatSync;
using ModderLords.CompatSync.Coop;
using ModderLords.CompatSync.Coop.Taom;
using ModderLords.CompatSync.Messages;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;

// Coop discovers handlers by namespace prefix; see ServerSettingsHandler. Both handlers exist in every Coop session and
// do nothing unless the TAOM layer's join-grant component bound (TAOM loaded, its members present).
namespace Coop.Core.Client.Services.ModderLordsCompat.Handlers
{
    /// <summary>Client: once the joined campaign is ready, sends the player's TAOM character-creation choices, once.</summary>
    public sealed class TaomClientHandler : IHandler
    {
        public const int ProtocolVersion = 1;

        private readonly IMessageBroker broker;
        private readonly INetwork network;
        private readonly bool active;

        public TaomClientHandler(IMessageBroker broker, INetwork network)
        {
            this.broker = broker;
            this.network = network;
            if (!CoopProbe.Present) return;
            active = true;
            broker.Subscribe<CampaignReady>(HandleCampaignReady);
            broker.Subscribe<NetworkTaomJoinResult>(HandleResult);
        }

        public void Dispose()
        {
            if (!active) return;
            broker.Unsubscribe<CampaignReady>(HandleCampaignReady);
            broker.Unsubscribe<NetworkTaomJoinResult>(HandleResult);
        }

        private void HandleCampaignReady(MessagePayload<CampaignReady> payload)
        {
            var pending = TaomJoinGrant.Pending;
            if (pending == null) return;
            network.SendAll(new NetworkTaomJoinChoices
            {
                HeroId = pending.HeroId,
                CultureId = pending.CultureId,
                RaceId = pending.RaceId,
                CareerId = pending.CareerId ?? "",
                ProtocolVersion = ProtocolVersion,
            });
            Log.Info("TAOM layer: join-grant sent this player's character-creation choices to the server (" + pending + ")");
        }

        private void HandleResult(MessagePayload<NetworkTaomJoinResult> payload)
        {
            // A non-final answer means the server did not know this player yet (a send from the local
            // character-creation campaign, before the join); keep the choices for the next CampaignReady.
            if (payload.What.Final) TaomJoinGrant.Answered();
            Log.Info("TAOM layer: join-grant server answer: " + payload.What.Detail + (payload.What.Final ? "" : " (will send again)"));
        }
    }
}

namespace Coop.Core.Server.Services.ModderLordsCompat.Handlers
{
    /// <summary>Server: applies a joining player's TAOM character-creation package to that player's hero.</summary>
    public sealed class TaomServerHandler : IHandler
    {
        private readonly IMessageBroker broker;
        private readonly INetwork network;
        private readonly bool active;

        public TaomServerHandler(IMessageBroker broker, INetwork network)
        {
            this.broker = broker;
            this.network = network;
            if (!CoopProbe.Present) return;
            active = true;
            broker.Subscribe<NetworkTaomJoinChoices>(HandleChoices);
        }

        public void Dispose()
        {
            if (active) broker.Unsubscribe<NetworkTaomJoinChoices>(HandleChoices);
        }

        private void HandleChoices(MessagePayload<NetworkTaomJoinChoices> payload)
        {
            if (payload.Who is not NetPeer peer) return;
            var msg = payload.What;
            void Reply(bool applied, string detail, bool final = true)
            {
                Log.Info("TAOM layer: join-grant: " + detail);
                network.Send(peer, new NetworkTaomJoinResult
                {
                    Applied = applied, Detail = detail, Final = final,
                    ProtocolVersion = Coop.Core.Client.Services.ModderLordsCompat.Handlers.TaomClientHandler.ProtocolVersion,
                });
            }

            if (msg.ProtocolVersion != Coop.Core.Client.Services.ModderLordsCompat.Handlers.TaomClientHandler.ProtocolVersion) { Reply(false, "protocol version mismatch; update ModderLords on both sides"); return; }
            if (string.IsNullOrEmpty(msg.CultureId)) { Reply(false, "no culture in the request"); return; }
            if (!ContainerProvider.TryResolve<IPlayerManager>(out var players) || !players.TryGetPlayer(peer, out var player) || player is null)
            {
                Reply(false, "the sender is not a known player yet", final: false);
                return;
            }
            var choices = new JoinChoices(msg.HeroId, msg.CultureId, msg.RaceId, string.IsNullOrEmpty(msg.CareerId) ? null : msg.CareerId);
            GameThread.RunSafe(() =>
            {
                try
                {
                    if (!ContainerProvider.TryResolve<IObjectManager>(out var objects) || !objects.TryGetObject<Hero>(player.HeroId, out var hero) || hero is null)
                    {
                        Reply(false, "the player's hero was not found on the server", final: false);
                        return;
                    }
                    objects.TryGetObject<MobileParty>(player.MobilePartyId, out var party);
                    var (applied, detail) = TaomJoinGrant.ServerApply(hero, party, choices);
                    Reply(applied, detail);
                }
                catch (Exception ex) { Reply(false, "failed: " + ex.GetBaseException().Message); }
            }, false, "ModderLords TAOM join-grant");
        }
    }
}
