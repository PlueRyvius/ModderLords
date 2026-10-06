using ModderLords.App.ViewModels;
using ModderLords.Core.Logs;
using ModderLords.Core.Perf;
using Xunit;

namespace ModderLords.App.Tests;

public sealed class PerformanceNetTests
{
    private static PerfSample Net(double up, double down, int peers, double sent = 1, double received = 1) =>
        PerfLineParser.TryParse(FormattableString.Invariant(
            $"[ModderLords] perf net upKbps={up:0.0} downKbps={down:0.0} peers={peers} sentMb={sent:0.0} receivedMb={received:0.0} windowSec=9.0"),
            DateTimeOffset.Now)!;

    [Fact]
    public void A_net_line_fills_upload_download_players_and_the_session_totals()
    {
        var vm = new PerformanceViewModel();

        vm.Apply(Net(1250, 96.4, 3, sent: 1840, received: 12.3));

        Assert.Equal("1.25 Mbit/s", vm.Upload.Value);
        Assert.Equal("96 kbit/s", vm.Download.Value);
        Assert.Equal("3", vm.Players);
        Assert.Equal("1.84 GB sent, 12.3 MB received", vm.DataTotals);
    }

    [Fact]
    public void An_empty_server_shows_zero_without_making_zero_the_normal_range()
    {
        var vm = new PerformanceViewModel();
        for (var i = 0; i < 40; i++) vm.Apply(Net(0, 0, 0));

        Assert.Equal("0 kbit/s", vm.Upload.Value);
        Assert.Equal("Nobody connected.", vm.Upload.Status);
        Assert.Empty(vm.Upload.History);
    }

    [Fact]
    public void A_rise_in_traffic_is_shown_but_never_raises_the_warning()
    {
        var vm = new PerformanceViewModel();
        for (var i = 0; i < BaselineTracker.WarmUpSamples + 20; i++) vm.Apply(Net(200, 20, 1));
        for (var i = 0; i < 10; i++) vm.Apply(Net(5000, 500, 6));

        Assert.Equal("5.00 Mbit/s", vm.Upload.Value);
        Assert.False(vm.Upload.Departed);
        Assert.DoesNotContain("Upload", vm.SessionState);
    }
}
