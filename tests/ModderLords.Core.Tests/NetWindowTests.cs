using ModderLords.CompatSync.Coop;
using ModderLords.Core.Logs;
using Xunit;

namespace ModderLords.Core.Tests;

public sealed class NetWindowTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void The_first_reading_only_sets_the_starting_point()
        => Assert.Null(new NetWindow().Read(5_000_000, 4_000, 1_000_000, 3_000, 0, 2));

    [Fact]
    public void A_window_reports_rates_with_packet_headers_added_and_totals_since_the_first_reading()
    {
        var window = new NetWindow();
        window.Read(5_000_000, 4_000, 1_000_000, 3_000, 0, 2);

        // 1,000,000 bytes in 1,000 packets up, 100,000 bytes in 500 packets down, over 10 seconds.
        var line = window.Read(6_000_000, 5_000, 1_100_000, 3_500, 10, 2);

        var sample = PerfLineParser.TryParse(line!, At);
        Assert.Equal(PerfKind.Net, sample!.Kind);
        Assert.Equal((1_000_000 + 1_000 * NetWindow.HeaderBytes) * 8 / 1000.0 / 10, sample.Number("upKbps")!.Value, 1);
        Assert.Equal((100_000 + 500 * NetWindow.HeaderBytes) * 8 / 1000.0 / 10, sample.Number("downKbps")!.Value, 1);
        Assert.Equal(2, sample.Count("peers"));
        Assert.Equal(1.0, sample.Number("sentMb")!.Value, 1);
        Assert.Equal(0.1, sample.Number("receivedMb")!.Value, 1);
    }

    [Fact]
    public void Each_window_is_measured_from_the_one_before()
    {
        var window = new NetWindow();
        window.Read(0, 0, 0, 0, 0, 1);
        window.Read(250_000, 0, 0, 0, 10, 1);

        var sample = PerfLineParser.TryParse(window.Read(500_000, 0, 0, 0, 5, 1)!, At);

        Assert.Equal(400.0, sample!.Number("upKbps")!.Value, 1);   // 250,000 bytes in 5 s
        Assert.Equal(0.5, sample.Number("sentMb")!.Value, 1);
    }

    [Fact]
    public void Totals_that_go_backwards_start_a_new_count_instead_of_a_negative_rate()
    {
        var window = new NetWindow();
        window.Read(9_000_000, 100, 9_000_000, 100, 0, 1);

        Assert.Null(window.Read(1_000, 1, 1_000, 1, 10, 1));

        var sample = PerfLineParser.TryParse(window.Read(126_000, 1, 1_000, 1, 10, 1)!, At);
        Assert.Equal(100.0, sample!.Number("upKbps")!.Value, 1);
        Assert.Equal(0.0, sample.Number("downKbps")!.Value, 1);
    }

    [Fact]
    public void A_window_with_no_length_reports_nothing()
    {
        var window = new NetWindow();
        window.Read(0, 0, 0, 0, 0, 1);
        Assert.Null(window.Read(1_000, 1, 0, 0, 0, 1));
    }

    [Fact]
    public void The_line_uses_a_point_for_decimals_whatever_the_machine_s_language()
    {
        var before = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            var window = new NetWindow();
            window.Read(0, 0, 0, 0, 0, 1);
            Assert.Contains("upKbps=100.5 ", window.Read(125_625, 0, 0, 0, 10, 1));
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = before; }
    }
}
