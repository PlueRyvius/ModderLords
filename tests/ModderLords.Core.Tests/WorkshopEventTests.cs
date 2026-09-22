using ModderLords.Core.Workshop;
using Xunit;

namespace ModderLords.Core.Tests;

/// <summary>
/// The Workshop helper runs in its own process and reports on stdout, so the line format is the whole contract, and
/// the id parser decides which missing mods can be subscribed at all.
/// </summary>
public class WorkshopEventTests
{
    [Theory]
    [InlineData("https://steamcommunity.com/sharedfiles/filedetails/?id=2859188632", 2859188632UL)]
    [InlineData("https://steamcommunity.com/workshop/filedetails/?id=2859188632&searchtext=", 2859188632UL)]
    [InlineData("steam://url/CommunityFilePage/3770450698", 3770450698UL)]
    [InlineData("  3770450698 ", 3770450698UL)]
    public void Parses_Workshop_links(string link, ulong id) => Assert.Equal(id, WorkshopEvent.ParseWorkshopId(link));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("https://www.nexusmods.com/mountandblade2bannerlord/mods/2006?id=5")]
    [InlineData("https://steamcommunity.com/sharedfiles/filedetails/?id=")]
    [InlineData("0")]
    public void Anything_else_has_no_Workshop_id(string? link) => Assert.Null(WorkshopEvent.ParseWorkshopId(link));

    [Fact]
    public void Events_round_trip_through_one_line()
    {
        var e = new WorkshopEvent(3000000001, WorkshopEventKind.Downloading, null, 0.25, Parent: 2859188632);
        var line = e.ToLine();
        Assert.DoesNotContain('\n', line);
        Assert.Equal(e, WorkshopEvent.TryParse(line));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("[S_API] SteamAPI_Init(): Loaded local 'steamclient64.dll'")]
    [InlineData("{ not json")]
    public void Stray_output_is_not_an_event(string? line) => Assert.Null(WorkshopEvent.TryParse(line));
}
