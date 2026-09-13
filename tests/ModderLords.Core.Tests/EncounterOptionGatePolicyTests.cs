using ModderLords.CompatSync.Coop;

namespace ModderLords.Core.Tests;

/// <summary>
/// Pins when client encounter options are held back. The live failure: on a Coop client the encounter menu opened
/// before the server's MapEvent arrived, Coop dropped the surrender request for want of a MapEvent id, and latched its
/// own flag so no later click did anything either.
/// </summary>
public class EncounterOptionGatePolicyTests
{
    private const string Surrender = "game_menu_encounter_surrender_on_condition";

    [Fact]
    public void Surrender_is_gated_and_attack_is_not()
    {
        Assert.True(EncounterOptionGatePolicy.IsGated(Surrender));
        Assert.Equal("Surrender", EncounterOptionGatePolicy.LabelFor(Surrender));
        // Attack is built server-side and was measured working in the same window.
        Assert.False(EncounterOptionGatePolicy.IsGated("game_menu_encounter_attack_on_condition"));
    }

    [Fact]
    public void A_client_in_an_encounter_with_no_battle_yet_is_gated()
        => Assert.True(EncounterOptionGatePolicy.ShouldGate(isClient: true, optionShown: true, hasEncounter: true, mapEventResolved: false));

    [Fact]
    public void Once_the_battle_has_arrived_the_option_is_left_alone()
        => Assert.False(EncounterOptionGatePolicy.ShouldGate(isClient: true, optionShown: true, hasEncounter: true, mapEventResolved: true));

    [Fact]
    public void The_server_is_never_gated()
        => Assert.False(EncounterOptionGatePolicy.ShouldGate(isClient: false, optionShown: true, hasEncounter: true, mapEventResolved: false));

    [Fact]
    public void An_option_vanilla_hid_is_not_touched()
        => Assert.False(EncounterOptionGatePolicy.ShouldGate(isClient: true, optionShown: false, hasEncounter: true, mapEventResolved: false));

    [Fact]
    public void Without_an_encounter_there_is_nothing_to_wait_for()
        => Assert.False(EncounterOptionGatePolicy.ShouldGate(isClient: true, optionShown: true, hasEncounter: false, mapEventResolved: false));

    [Theory]
    [InlineData(true, true, true, true)]
    [InlineData(true, true, false, false)]
    [InlineData(true, false, true, false)]
    [InlineData(false, true, true, false)]
    public void The_menu_refreshes_only_for_the_same_gated_encounter_once_its_battle_arrives(bool gated, bool same, bool resolved, bool expected)
        => Assert.Equal(expected, EncounterOptionGatePolicy.ShouldRefresh(gated, same, resolved));

    [Theory]
    [InlineData(0.0, false)]
    [InlineData(20.0, false)]
    [InlineData(20.1, true)]
    public void A_wait_past_the_limit_is_reported_as_stuck(double seconds, bool expected)
        => Assert.Equal(expected, EncounterOptionGatePolicy.IsStuck(seconds));

    [Fact]
    public void Labels_are_listed_once_in_order()
        => Assert.Equal("Leave, Surrender", EncounterOptionGatePolicy.Describe(["Surrender", "Leave", "Surrender"]));
}
