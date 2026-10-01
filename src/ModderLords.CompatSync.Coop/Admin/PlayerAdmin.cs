using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GameInterface;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.Players;
using LiteNetLib;
using ModderLords.CompatSync;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Library;

namespace ModderLords.CompatSync.Coop.Admin;

/// <summary>
/// Server only: the console commands the launcher's Characters tab drives (<see cref="AdminWire"/>), and the ban list
/// they enforce. A banned player is refused as they connect (<c>PlayerAdminHandler</c>) and swept off every tick in
/// case the list changed while they were on.
/// <para>
/// The commands are ordinary engine console functions. The dedicated server hands any stdin line with a dot in its
/// first word to <see cref="CommandLineFunctionality"/>, which collects them once at startup, before this assembly
/// is loaded; <see cref="ServerTick"/> collects again so ours are found. Collecting skips names it already has.
/// </para>
/// </summary>
internal static class PlayerAdmin
{
    private static readonly object Gate = new object();
    private static BanList _bans = BanList.Empty;
    private static DateTime _bansWritten = DateTime.MinValue;
    private static bool _commandsRegistered;

    /// <summary>The ban file under the server's user folder, or null when the launcher did not say where that is.</summary>
    internal static string? BansPath { get; } =
        Environment.GetEnvironmentVariable("BANNERLORD_USER_DIR") is { Length: > 0 } dir ? Path.Combine(dir, AdminWire.BansFile) : null;

    /// <summary>Server, every tick: registers the commands once, then rereads the ban list when the file changed.</summary>
    internal static void ServerTick()
    {
        if (global::Coop.Core.Server.Services.ModderLordsCompat.Handlers.PlayerAdminHandler.Current is null) return;
        if (!_commandsRegistered)
        {
            _commandsRegistered = true;
            CommandLineFunctionality.CollectCommandLineFunctions();
            var found = CommandLineFunctionality.HasFunctionForCommand(AdminWire.CommandGroup + ".players");
            var bans = "; ban list " + (BansPath is null ? "unavailable (no BANNERLORD_USER_DIR)" : BansPath);
            if (found) Log.Info("server admin commands ready" + bans);
            else Log.Warn("server admin commands could not be registered; the Characters tab will get no answers" + bans);
        }
        if (ReloadBans(force: false)) Sweep();
    }

    /// <summary>The ban that refuses this player, or null. Safe from the network thread.</summary>
    internal static BanEntry? BanFor(string? steamId, string? ip)
    {
        lock (Gate) return _bans.Match(steamId, ip);
    }

    /// <summary>Rereads the file when it changed (or always, when forced). True when the list was replaced.</summary>
    private static bool ReloadBans(bool force)
    {
        if (BansPath is null) return false;
        try
        {
            var written = File.Exists(BansPath) ? File.GetLastWriteTimeUtc(BansPath) : DateTime.MinValue;
            if (!force && written == _bansWritten) return false;
            var list = written == DateTime.MinValue ? BanList.Empty : BanList.Parse(File.ReadAllText(BansPath));
            lock (Gate) { _bans = list; _bansWritten = written; }
            Log.Info($"ban list loaded: {list.Entries.Count} entr{(list.Entries.Count == 1 ? "y" : "ies")}");
            return true;
        }
        catch (Exception ex)
        {
            // Keep the list we had: a half-written or hand-broken file must not unban everyone.
            Log.Warn("ban list could not be read, keeping the previous one: " + ex.GetBaseException().Message);
            return false;
        }
    }

    /// <summary>Disconnects every connected player the current list bans. Returns their names.</summary>
    private static List<string> Sweep()
    {
        var removed = new List<string>();
        if (!ContainerProvider.TryResolve<IPlayerManager>(out var players)) return removed;
        foreach (var player in players.Players.ToList())
        {
            if (!players.TryGetPeer(player.ControllerId, out var peer) || peer is null) continue;
            if (BanFor(player.ControllerId, IpOf(peer)) is not { } ban) continue;
            var name = PlayerHeroes.HeroFor(player.HeroId)?.Name?.ToString() ?? player.ControllerId;
            Log.Info($"disconnecting banned player {name} ({player.ControllerId}){(ban.Reason.Length > 0 ? ": " + ban.Reason : "")}");
            peer.Disconnect();
            removed.Add(name);
        }
        return removed;
    }

    internal static string IpOf(NetPeer peer) => BanList.NormalizeIp(peer.Address?.ToString());

    // ---- console commands --------------------------------------------------------------------------------------

    /// <summary><c>modderlords.players &lt;req&gt;</c>: every registered player, connected or not.</summary>
    [CommandLineFunctionality.CommandLineArgumentFunction("players", AdminWire.CommandGroup)]
    public static string Players(List<string> args) => Run("players", args, req =>
    {
        if (!ContainerProvider.TryResolve<IPlayerManager>(out var players)) return AdminWire.Reply("players", req, "Coop's player registry is not available yet.");
        ContainerProvider.TryResolve<IObjectManager>(out var objects);
        var list = new List<object?>();
        foreach (var player in players.Players)
        {
            if (player.ControllerId == "Server") continue;
            var hero = PlayerHeroes.HeroFor(player.HeroId);
            MobileParty? party = null;
            objects?.TryGetObject(player.MobilePartyId, out party);
            var online = players.TryGetPeer(player.ControllerId, out var peer) && peer is not null && players.IsConnected(player);
            list.Add(new Dictionary<string, object?>
            {
                ["steamId"] = player.ControllerId,
                ["heroId"] = player.HeroId,
                ["name"] = hero?.Name?.ToString() ?? "",
                ["clan"] = hero?.Clan?.Name?.ToString() ?? "",
                ["level"] = hero?.Level ?? 0,
                ["gold"] = hero?.Gold ?? 0,
                ["online"] = online,
                ["peerId"] = online ? peer!.Id : -1,
                ["ip"] = online ? IpOf(peer!) : "",
                ["busy"] = BusyReason(party),
            });
        }
        return AdminWire.Reply("players", req, null, new Dictionary<string, object?> { ["list"] = list });
    });

    /// <summary><c>modderlords.kick &lt;req&gt; &lt;steam id&gt;</c>: disconnects that player. Their character stays in the world.</summary>
    [CommandLineFunctionality.CommandLineArgumentFunction("kick", AdminWire.CommandGroup)]
    public static string Kick(List<string> args) => Run("kick", args, req =>
    {
        if (args.Count < 2) return AdminWire.Reply("kick", req, "Usage: modderlords.kick <request id> <steam id>");
        if (!ContainerProvider.TryResolve<IPlayerManager>(out var players)) return AdminWire.Reply("kick", req, "Coop's player registry is not available yet.");
        var id = args[1];
        if (!players.TryGetPlayer(id, out var player)) return AdminWire.Reply("kick", req, $"No player with Steam id {id} is registered on this server.");
        if (!players.TryGetPeer(id, out var peer) || peer is null) return AdminWire.Reply("kick", req, "That player is not connected.");
        var name = PlayerHeroes.HeroFor(player.HeroId)?.Name?.ToString() ?? id;
        Log.Info($"kicking {name} ({id}) at the host's request");
        peer.Disconnect();
        return AdminWire.Reply("kick", req, null, new Dictionary<string, object?> { ["steamId"] = id, ["name"] = name });
    });

    /// <summary><c>modderlords.bans &lt;req&gt;</c>: rereads the ban list now and disconnects anyone it bans.</summary>
    [CommandLineFunctionality.CommandLineArgumentFunction("bans", AdminWire.CommandGroup)]
    public static string Bans(List<string> args) => Run("bans", args, req =>
    {
        if (BansPath is null) return AdminWire.Reply("bans", req, "This server was started without BANNERLORD_USER_DIR, so it has no ban list.");
        ReloadBans(force: true);
        var removed = Sweep();
        int count;
        lock (Gate) count = _bans.Entries.Count;
        return AdminWire.Reply("bans", req, null, new Dictionary<string, object?>
        {
            ["count"] = count,
            ["disconnected"] = removed.Cast<object?>().ToList(),
        });
    });

    internal static string BusyReason(MobileParty? party)
    {
        if (party?.Party?.MapEvent is not null) return "in a battle";
        if (party?.BesiegerCamp is not null) return "besieging";
        return "";
    }

    /// <summary>A command's body, with any exception turned into an error reply rather than a console stack trace.</summary>
    internal static string Run(string ev, List<string> args, Func<string, string> body)
    {
        var req = AdminWire.RequestId(args);
        try
        {
            if (global::Coop.Core.Server.Services.ModderLordsCompat.Handlers.PlayerAdminHandler.Current is null)
                return AdminWire.Reply(ev, req, "No Coop server session is running.");
            if (Campaign.Current is null) return AdminWire.Reply(ev, req, "No campaign is loaded.");
            return body(req);
        }
        catch (Exception ex)
        {
            Log.Warn($"admin command {ev} failed: {ex}");
            return AdminWire.Reply(ev, req, ex.GetBaseException().Message);
        }
    }
}
