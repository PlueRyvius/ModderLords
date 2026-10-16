using System;
using System.Collections.Generic;
using System.Linq;
using Common;
using Common.Messaging;
using Common.Network;
using GameInterface;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.Players;
using LiteNetLib;
using ModderLords.CompatSync;
using ModderLords.CompatSync.Coop;
using ModderLords.CompatSync.Messages;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Library;

namespace ModderLords.CompatSync.Coop
{
    /// <summary>
    /// <c>modderlords.give &lt;player name&gt; &lt;amount&gt;</c>, typed in a player's own console: sends that much of the
    /// player's gold to another player. Not a cheat: it only moves gold the player already has, and the server checks
    /// that, so it works with cheats off.
    /// </summary>
    internal static class GiveGoldCommand
    {
        /// <summary>Set by the client handler while connected.</summary>
        internal static Action<string, int>? Send;

        [CommandLineFunctionality.CommandLineArgumentFunction("give", Admin.AdminWire.CommandGroup)]
        public static string Give(List<string> args)
        {
            const string usage = "Usage: modderlords.give <player name> <amount>";
            if (args.Count < 2 || !int.TryParse(args[args.Count - 1], out var amount)) return usage;
            if (amount <= 0) return "The amount must be more than 0.";
            var name = string.Join(" ", args.Take(args.Count - 1));
            if (Send is not { } send) return "This only works while connected to a co-op server.";
            send(name, amount);
            return $"Asking the server to give {amount} gold to {name}...";
        }
    }
}

// Coop discovers handlers by namespace prefix; see ServerSettingsHandler.
namespace Coop.Core.Client.Services.ModderLordsCompat.Handlers
{
    /// <summary>Client: sends gold gifts typed in the console and shows the server's answers.</summary>
    public sealed class GiveGoldClientHandler : IHandler
    {
        public const int ProtocolVersion = 1;

        private readonly IMessageBroker broker;
        private readonly bool active;

        public GiveGoldClientHandler(IMessageBroker broker, INetwork network)
        {
            this.broker = broker;
            if (!CoopProbe.Present) return;
            active = true;
            broker.Subscribe<NetworkGiveGoldNotice>(HandleNotice);
            GiveGoldCommand.Send = (name, amount) =>
                network.SendAll(new NetworkGiveGold { Recipient = name, Amount = amount, ProtocolVersion = ProtocolVersion });
        }

        public void Dispose()
        {
            if (!active) return;
            GiveGoldCommand.Send = null;
            broker.Unsubscribe<NetworkGiveGoldNotice>(HandleNotice);
        }

        private static void HandleNotice(MessagePayload<NetworkGiveGoldNotice> payload)
        {
            var msg = payload.What;
            GameThread.RunSafe(() => InformationManager.DisplayMessage(
                new InformationMessage(msg.Message, msg.Ok ? Colors.Green : Colors.Yellow)), false, "ModderLords give gold notice");
        }
    }

    /// <summary>
    /// Server: moves gold between two players' heroes with Hero.ChangeHeroGold, which Coop replicates to every client.
    /// Refuses anything the giver cannot afford.
    /// </summary>
    public sealed class GiveGoldServerHandler : IHandler
    {
        private readonly IMessageBroker broker;
        private readonly INetwork network;
        private readonly bool active;

        public GiveGoldServerHandler(IMessageBroker broker, INetwork network)
        {
            this.broker = broker;
            this.network = network;
            if (!CoopProbe.Present) return;
            active = true;
            broker.Subscribe<NetworkGiveGold>(HandleGive);
        }

        public void Dispose()
        {
            if (!active) return;
            broker.Unsubscribe<NetworkGiveGold>(HandleGive);
        }

        private void Notify(NetPeer peer, bool ok, string message) =>
            network.Send(peer, new NetworkGiveGoldNotice { Ok = ok, Message = message, ProtocolVersion = GiveGoldClientHandler.ProtocolVersion });

        private void HandleGive(MessagePayload<NetworkGiveGold> payload)
        {
            if (payload.Who is not NetPeer peer) return;
            var msg = payload.What;
            if (!ContainerProvider.TryResolve<IPlayerManager>(out var players) || !players.TryGetPlayer(peer, out var player) || player is null)
            {
                Notify(peer, false, "The server does not know you yet; try again in a moment.");
                return;
            }
            GameThread.RunSafe(() =>
            {
                try
                {
                    if (!ContainerProvider.TryResolve<IObjectManager>(out var objects) || !objects.TryGetObject<Hero>(player.HeroId, out var giver) || giver is null)
                    {
                        Notify(peer, false, "Your hero was not found on the server.");
                        return;
                    }
                    var name = msg.Recipient.Trim();
                    var matches = players.Players
                        .Select(p => (Player: p, Hero: objects.TryGetObject<Hero>(p.HeroId, out var h) ? h : null))
                        .Where(x => x.Hero is not null && string.Equals(x.Hero.Name?.ToString(), name, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    if (matches.Count == 0) { Notify(peer, false, $"No player's hero is called \"{name}\"."); return; }
                    if (matches.Count > 1) { Notify(peer, false, $"More than one player's hero is called \"{name}\"."); return; }
                    var (target, receiver) = matches[0];
                    if (ReferenceEquals(receiver, giver)) { Notify(peer, false, "You cannot give gold to yourself."); return; }
                    if (msg.Amount <= 0) { Notify(peer, false, "The amount must be more than 0."); return; }
                    if (giver.Gold < msg.Amount) { Notify(peer, false, $"You have only {giver.Gold} gold."); return; }

                    giver.ChangeHeroGold(-msg.Amount);
                    receiver!.ChangeHeroGold(msg.Amount);
                    Log.Info($"gold gift: {giver.Name} gave {msg.Amount} to {receiver.Name}");
                    Notify(peer, true, $"You gave {msg.Amount} gold to {receiver.Name}.");
                    if (players.TryGetPeer(target.ControllerId, out var to) && to != null)
                        Notify(to, true, $"{giver.Name} gave you {msg.Amount} gold.");
                }
                catch (Exception ex)
                {
                    Notify(peer, false, "The server could not do that: " + ex.GetBaseException().Message);
                }
            }, false, "ModderLords give gold");
        }
    }
}
