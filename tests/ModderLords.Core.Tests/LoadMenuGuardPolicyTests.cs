using ModderLords.CompatSync.Coop;
using Xunit;

namespace ModderLords.Core.Tests;

/// <summary>
/// The rule behind the load menu guard. Reported 2026-10-13: a save whose stored menu was town_outside, with the
/// party in no settlement, crashed every game that loaded it - the server on boot and every joining player.
/// </summary>
public class LoadMenuGuardPolicyTests
{
    [Theory]
    [InlineData("town_outside")]          // the reported crash
    [InlineData("castle_outside")]
    [InlineData("village_outside")]
    [InlineData("castle_guard")]
    [InlineData("fortification_crime_rating")]
    [InlineData("menu_sneak_into_town_succeeded")]
    [InlineData("disguise_first_time")]
    [InlineData("town")]
    [InlineData("town_backstreet")]
    [InlineData("castle_dungeon")]
    [InlineData("village")]
    public void A_settlement_menu_with_no_settlement_is_dropped(string menu)
    {
        Assert.True(LoadMenuGuardPolicy.NeedsSettlement(menu));
        Assert.True(LoadMenuGuardPolicy.ShouldDrop(menu, hasCurrentSettlement: false));
    }

    [Theory]
    [InlineData("town_outside")]
    [InlineData("castle_dungeon")]
    public void A_settlement_menu_is_left_alone_when_there_is_a_settlement(string menu) =>
        Assert.False(LoadMenuGuardPolicy.ShouldDrop(menu, hasCurrentSettlement: true));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("encounter")]
    [InlineData("army_wait")]
    [InlineData("menu_settlement_taken")]
    [InlineData("townsfolk")]             // prefix alone is not enough: only "town" or "town_..."
    [InlineData("Town_outside")]          // menu ids are case-sensitive
    public void Other_menus_are_never_touched(string? menu)
    {
        Assert.False(LoadMenuGuardPolicy.NeedsSettlement(menu));
        Assert.False(LoadMenuGuardPolicy.ShouldDrop(menu, hasCurrentSettlement: false));
    }
}
