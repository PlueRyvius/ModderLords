using System.Collections.Concurrent;
using ModderLords.CompatSync.Coop.Admin;
using ModderLords.Coop.Admin;
using ModderLords.Core.Logs;
using Xunit;

namespace ModderLords.Core.Tests;

/// <summary>
/// The Characters tab's two channels, each checked end to end without a server: the console commands (the module's
/// AdminWire writes the reply, the launcher's AdminClient reads it) and the ban file (the launcher's BanStore writes
/// it, the module's BanList enforces it).
/// </summary>
public class ServerAdminTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ml-admin-" + Guid.NewGuid().ToString("N"));

    public ServerAdminTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { /* temp dir */ } }

    private static string PlayersReply(string req) => AdminWire.Reply("players", req, null, new Dictionary<string, object?>
    {
        ["list"] = new List<object?>
        {
            new Dictionary<string, object?>
            {
                ["steamId"] = "76561198000000001", ["heroId"] = "Hero_Player", ["name"] = "Aldric \"the Bold\"", ["clan"] = "Vlandian Lions",
                ["level"] = 14, ["gold"] = 25300, ["online"] = true, ["peerId"] = 3, ["ip"] = "203.0.113.7", ["busy"] = "in a battle",
            },
            new Dictionary<string, object?>
            {
                ["steamId"] = "76561198000000002", ["heroId"] = "Hero_Player_2", ["name"] = "Mira", ["clan"] = "",
                ["level"] = 2, ["gold"] = 0, ["online"] = false, ["peerId"] = -1, ["ip"] = "", ["busy"] = "",
            },
        },
    });

    [Fact]
    public void A_reply_is_one_console_line_the_launcher_reads_back()
    {
        var line = PlayersReply("r7");
        Assert.DoesNotContain('\n', line);
        Assert.StartsWith(AdminClient.Marker, line);

        // The engine prints it as is, but the launcher may see it after a log prefix; either way it is found.
        var reply = AdminClient.Parse("[12:00:01] " + line)!;
        Assert.Equal("players", reply.Event);
        Assert.Equal("r7", reply.RequestId);
        Assert.True(reply.Ok);
        var players = AdminClient.Players(reply);
        Assert.Equal(2, players.Count);
        Assert.Equal(new AdminPlayer("76561198000000001", "Hero_Player", "Aldric \"the Bold\"", "Vlandian Lions", 14, 25300, true, 3, "203.0.113.7", "in a battle"), players[0]);
        Assert.False(players[1].Online);
        Assert.Equal(-1, players[1].PeerId);
    }

    [Fact]
    public void An_error_reply_carries_its_message()
    {
        var reply = AdminClient.Parse(AdminWire.Reply("kick", "r2", "That player is not connected."))!;
        Assert.False(reply.Ok);
        Assert.Equal("That player is not connected.", reply.Error);
    }

    [Fact]
    public void Both_ends_use_the_same_command_group_and_marker()
    {
        Assert.Equal(AdminWire.Marker, AdminClient.Marker);
        Assert.Equal(AdminWire.CommandGroup, AdminClient.CommandGroup);
        Assert.Equal(AdminWire.BansFile, BanStore.RelativePath);
    }

    [Fact]
    public void A_line_that_is_not_a_reply_is_ignored()
    {
        Assert.Null(AdminClient.Parse("@DS@{\"ev\":\"players\",\"list\":[]}"));
        Assert.Null(AdminClient.Parse("@ML@{broken"));
        Assert.Null(AdminClient.Parse("[DedicatedServer] > modderlords.players r1"));
    }

    [Fact]
    public async Task A_request_is_answered_by_the_reply_with_its_id_only()
    {
        var sent = new ConcurrentQueue<string>();
        var client = new AdminClient(line => { sent.Enqueue(line); return Task.CompletedTask; });
        var request = client.RequestAsync("kick", ["76561198000000001"], TimeSpan.FromSeconds(5));
        Assert.True(sent.TryDequeue(out var line));
        Assert.Equal("modderlords.kick r1 76561198000000001", line);

        client.Observe(AdminWire.Reply("kick", "r99", null)); // someone else's
        Assert.False(request.IsCompleted);
        client.Observe(AdminWire.Reply("kick", "r1", null, new Dictionary<string, object?> { ["name"] = "Aldric" }));
        var reply = await request;
        Assert.True(reply.Ok);
        Assert.Equal("Aldric", reply.Root.GetProperty("name").GetString());
    }

    [Fact]
    public async Task A_request_nobody_answers_times_out_with_a_reason()
    {
        var client = new AdminClient(_ => Task.CompletedTask);
        var ex = await Assert.ThrowsAsync<TimeoutException>(() => client.RequestAsync("players", [], TimeSpan.FromMilliseconds(50)));
        Assert.Contains("compat module", ex.Message);
    }

    [Fact]
    public async Task An_argument_with_a_space_is_refused_before_it_is_sent()
    {
        var sent = 0;
        var client = new AdminClient(_ => { sent++; return Task.CompletedTask; });
        await Assert.ThrowsAsync<ArgumentException>(() => client.RequestAsync("kick", ["two words"], TimeSpan.FromSeconds(1)));
        Assert.Equal(0, sent);
    }

    [Fact]
    public void A_ban_the_launcher_writes_is_enforced_by_the_server_by_steam_id_and_by_address()
    {
        var store = new BanStore(_dir);
        store.Add("76561198000000001", "::ffff:203.0.113.7", "Aldric", "griefing");

        var server = BanList.Parse(File.ReadAllText(store.Path));
        Assert.Equal("Aldric", server.Match("76561198000000001", null)?.Name);
        // Same person from another network, and someone else on the banned network (either form of the address).
        Assert.NotNull(server.Match("76561198000000001", "198.51.100.1"));
        Assert.NotNull(server.Match("76561198000000099", "203.0.113.7"));
        Assert.NotNull(server.Match(null, "::ffff:203.0.113.7"));
        Assert.Null(server.Match("76561198000000099", "198.51.100.1"));
        Assert.Equal("griefing", store.Load().Single().Reason);
    }

    [Fact]
    public void Nobody_is_banned_by_the_shared_default_id_or_by_this_PCs_own_address()
    {
        var store = new BanStore(_dir);
        Assert.Throws<InvalidOperationException>(() => store.Add("DefaultId", "127.0.0.1", "Local", ""));

        // Only the parts that identify someone are kept.
        var kept = store.Add("DefaultId", "203.0.113.7", "NoSteam", "");
        Assert.Equal("", kept.SteamId);
        var server = BanList.Parse(File.ReadAllText(store.Path));
        Assert.Null(server.Match("DefaultId", "198.51.100.1"));

        // A hand-edited file cannot ban everyone without Steam, or the host, either.
        var edited = BanList.Parse("{\"schema\":1,\"bans\":[{\"steamId\":\"DefaultId\",\"ip\":\"127.0.0.1\"},{\"steamId\":\"\",\"ip\":\"::1\"}]}");
        Assert.Empty(edited.Entries);
        Assert.Null(edited.Match("DefaultId", "127.0.0.1"));
    }

    [Fact]
    public void Banning_the_same_player_again_replaces_their_ban_and_unban_removes_it()
    {
        var store = new BanStore(_dir);
        store.Add("76561198000000001", "203.0.113.7", "Aldric", "first");
        store.Add("76561198000000001", "198.51.100.1", "Aldric", "second");
        var ban = Assert.Single(store.Load());
        Assert.Equal("second", ban.Reason);

        store.Remove(ban);
        Assert.Empty(store.Load());
        Assert.Empty(BanList.Parse(File.ReadAllText(store.Path)).Entries);
        Assert.False(File.Exists(store.Path + ".tmp"));
    }

    [Fact]
    public void No_ban_file_means_no_bans()
    {
        Assert.Empty(new BanStore(_dir).Load());
    }

    [Fact]
    public void Replies_show_in_the_console_with_the_commands_that_asked_for_them()
    {
        Assert.Equal(LogCategory.CommandReply, LogClassifier.Classify(PlayersReply("r1")).Category);
    }
}
