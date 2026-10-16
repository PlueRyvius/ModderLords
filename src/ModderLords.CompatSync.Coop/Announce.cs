using System.Collections.Generic;
using Common;
using Common.Messaging;
using Common.Network;
using ModderLords.CompatSync;
using ModderLords.CompatSync.Coop;
using ModderLords.CompatSync.Coop.Admin;
using ModderLords.CompatSync.Messages;
using TaleWorlds.Library;

namespace ModderLords.CompatSync.Coop.Admin
{
    internal static class AnnounceAdmin
    {
        /// <summary><c>modderlords.announce &lt;req&gt; &lt;hex&gt;</c> (<see cref="AnnounceWire"/>): shows the text on every connected player's screen.</summary>
        [CommandLineFunctionality.CommandLineArgumentFunction("announce", AdminWire.CommandGroup)]
        public static string AnnounceCommand(List<string> args) => PlayerAdmin.Run("announce", args, req =>
        {
            if (args.Count < 2 || !AnnounceWire.TryDecode(args[1], out var text))
                return AdminWire.Reply("announce", req, "Usage: modderlords.announce <request id> <hex of the text>");
            if (global::Coop.Core.Server.Services.ModderLordsCompat.Handlers.AnnounceServerHandler.Current is not { } handler)
                return AdminWire.Reply("announce", req, "No Coop server session is running.");
            handler.Send(text);
            Log.Info("announcement sent to players: " + text);
            return AdminWire.Reply("announce", req, null);
        });
    }
}

// Coop discovers handlers by namespace prefix; see ServerSettingsHandler.
namespace Coop.Core.Server.Services.ModderLordsCompat.Handlers
{
    /// <summary>Server: sends host announcements to every client.</summary>
    public sealed class AnnounceServerHandler : IHandler
    {
        public const int ProtocolVersion = 1;
        private readonly INetwork network;

        public static AnnounceServerHandler? Current { get; private set; }

        public AnnounceServerHandler(INetwork network)
        {
            this.network = network;
            if (!CoopProbe.Present) return;
            Current = this;
        }

        public void Send(string text) => network.SendAll(new NetworkAnnouncement { Text = text, ProtocolVersion = ProtocolVersion });

        public void Dispose()
        {
            if (ReferenceEquals(Current, this)) Current = null;
        }
    }
}

namespace Coop.Core.Client.Services.ModderLordsCompat.Handlers
{
    /// <summary>Client: shows host announcements on screen.</summary>
    public sealed class AnnounceClientHandler : IHandler
    {
        private readonly IMessageBroker broker;
        private readonly bool active;

        public AnnounceClientHandler(IMessageBroker broker)
        {
            this.broker = broker;
            if (!CoopProbe.Present) return;
            active = true;
            broker.Subscribe<NetworkAnnouncement>(Handle);
        }

        public void Dispose()
        {
            if (active) broker.Unsubscribe<NetworkAnnouncement>(Handle);
        }

        private static void Handle(MessagePayload<NetworkAnnouncement> payload)
        {
            var text = payload.What.Text;
            GameThread.RunSafe(() => InformationManager.DisplayMessage(new InformationMessage("[Server] " + text, Colors.Yellow)),
                false, "ModderLords announcement");
        }
    }
}
