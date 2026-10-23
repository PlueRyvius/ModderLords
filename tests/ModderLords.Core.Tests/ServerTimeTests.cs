using ModderLords.Core.Logs;

namespace ModderLords.Core.Tests;

public sealed class ServerTimeTests
{
    private static string Applied(string requested, string effective) =>
        $"[Coop] Applying server time control: current=\"Pause\" requested={requested} effective={effective} liveTestFastForwardBypass=False";

    private static string Players(params (int Id, string State)[] list) =>
        "@DS@{\"ev\":\"players\",\"list\":[" + string.Join(",", list.Select(p => $"{{\"id\":{p.Id},\"name\":\"P{p.Id}\",\"state\":\"{p.State}\",\"addr\":\"x\"}}")) + "]}";

    [Fact]
    public void ReadsRequestedAndEffective()
    {
        var t = new ServerTime();
        Assert.Null(t.Observe(Applied("Play_1x", "Pause")));
        Assert.Equal("Play_1x", t.Requested);
        Assert.Equal("Pause", t.Effective);
    }

    [Fact]
    public void PausesWhileJoining_ThenRestoresRequestedSpeed()
    {
        var t = new ServerTime { PauseOnJoin = true };
        t.Observe(Applied("Play_2x", "Play_2x"));
        Assert.Equal("coop.debug.set_time_mode Pause", t.Observe(Players((0, "handshake"))));
        Assert.True(t.Holding);
        // Our own pause is logged too; it must not become the speed to go back to.
        t.Observe(Applied("Pause", "Pause"));
        Assert.Null(t.Observe(Players((0, "creating character"))));
        Assert.Equal("coop.debug.set_time_mode Play_2x", t.Observe(Players((0, "on map"))));
        Assert.False(t.Holding);
    }

    [Fact]
    public void PlayerAlreadyOnMap_InBattle_DoesNotPause()
    {
        var t = new ServerTime { PauseOnJoin = true };
        t.Observe(Applied("Play_1x", "Play_1x"));
        t.Observe(Players((0, "on map")));
        Assert.Null(t.Observe(Players((0, "in mission"))));
        Assert.False(t.Holding);
    }

    [Fact]
    public void DisconnectedJoinerEndsHold_AndReusedIdJoinsAgain()
    {
        var t = new ServerTime { PauseOnJoin = true };
        t.Observe(Applied("Play_1x", "Play_1x"));
        t.Observe(Players((0, "on map")));
        Assert.NotNull(t.Observe(Players((0, "on map"), (1, "loading"))));
        Assert.Equal("coop.debug.set_time_mode Play_1x", t.Observe(Players((0, "on map"))));
        t.Observe(Players());
        Assert.NotNull(t.Observe(Players((0, "handshake"))));
    }

    [Fact]
    public void Off_NeverPauses_AndTurningOffReleases()
    {
        var t = new ServerTime();
        t.Observe(Applied("Play_1x", "Play_1x"));
        Assert.Null(t.Observe(Players((0, "handshake"))));
        t.PauseOnJoin = true;
        Assert.NotNull(t.Reconsider());
        t.PauseOnJoin = false;
        Assert.Equal("coop.debug.set_time_mode Play_1x", t.Reconsider());
    }

    [Fact]
    public void HostPausedBeforeJoin_StaysPaused()
    {
        var t = new ServerTime { PauseOnJoin = true };
        t.Chosen(ServerTime.Pause);
        Assert.NotNull(t.Observe(Players((0, "handshake"))));
        Assert.Null(t.Observe(Players((0, "on map"))));
    }
}
