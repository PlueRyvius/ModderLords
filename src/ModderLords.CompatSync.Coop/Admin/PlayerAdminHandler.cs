using System;
using Common.Messaging;
using Coop.Core.Server.Connections.Messages;
using LiteNetLib;
using ModderLords.CompatSync;
using ModderLords.CompatSync.Coop;
using ModderLords.CompatSync.Coop.Admin;

// Coop discovers server-side handlers by namespace prefix (Coop.Core.Server.*) and constructs them from its
// container when a hosting session starts. The namespace is a discovery convention, nothing more.
namespace Coop.Core.Server.Services.ModderLordsCompat.Handlers;

/// <summary>
/// Server: refuses a banned player as they connect. The address is known the moment the connection opens; the Steam
/// id only once the game sends it to be validated, so each is checked as soon as it is known.
/// Its existence is also how <see cref="PlayerAdmin"/> knows it is running on a hosting server.
/// </summary>
public sealed class PlayerAdminHandler : IHandler
{
    private readonly IMessageBroker broker;

    public static PlayerAdminHandler? Current { get; private set; }

    public PlayerAdminHandler(IMessageBroker broker)
    {
        this.broker = broker;
        if (!CoopProbe.Present) { Log.Warn("server admin disabled: " + CoopProbe.Report); return; }
        broker.Subscribe<PlayerConnected>(HandleConnected);
        broker.Subscribe<NetworkClientValidate>(HandleValidate);
        Current = this;
    }

    public void Dispose()
    {
        broker.Unsubscribe<PlayerConnected>(HandleConnected);
        broker.Unsubscribe<NetworkClientValidate>(HandleValidate);
        if (ReferenceEquals(Current, this)) Current = null;
    }

    private void HandleConnected(MessagePayload<PlayerConnected> payload) =>
        Refuse(payload.What.PlayerPeer, steamId: null);

    private void HandleValidate(MessagePayload<NetworkClientValidate> payload)
    {
        if (payload.Who is NetPeer peer) Refuse(peer, payload.What.PlayerId);
    }

    private static void Refuse(NetPeer? peer, string? steamId)
    {
        if (peer is null) return;
        try
        {
            var ip = PlayerAdmin.IpOf(peer);
            if (PlayerAdmin.BanFor(steamId, ip) is not { } ban) return;
            Log.Info($"refused banned player {(ban.Name.Length > 0 ? ban.Name : steamId ?? ip)} (steam id {steamId ?? "not sent yet"}, address {ip})");
            peer.Disconnect();
        }
        catch (Exception ex)
        {
            Log.Warn("ban check failed: " + ex.GetBaseException().Message);
        }
    }
}
