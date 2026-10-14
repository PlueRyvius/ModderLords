using ModderLords.CompatSync.Coop;
using Xunit;

namespace ModderLords.Core.Tests;

/// <summary>
/// The rules behind ClientKingdomDecisions. Reported 2026-10-13: a player's own Bellum opened a council ballot that
/// only their game held, the kingdom's decision lists stopped lining up, and after a policy vote the ruler was locked in
/// the Kingdom screen by a decision the server had already settled.
/// </summary>
public class ClientKingdomDecisionsPolicyTests
{
    [Fact]
    public void A_decision_another_clan_proposed_is_not_added_on_a_players_game()
    {
        Assert.True(ClientKingdomDecisionsPolicy.RefuseAdd(coopClient: true, coopApplying: false, proposedByPlayerClan: false));
    }

    [Fact]
    public void The_players_own_proposal_goes_to_Coop_as_before()
    {
        Assert.False(ClientKingdomDecisionsPolicy.RefuseAdd(coopClient: true, coopApplying: false, proposedByPlayerClan: true));
    }

    [Fact]
    public void A_decision_Coop_is_applying_from_the_server_is_left_alone()
    {
        Assert.False(ClientKingdomDecisionsPolicy.RefuseAdd(coopClient: true, coopApplying: true, proposedByPlayerClan: false));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_server_and_a_game_outside_coop_are_untouched(bool proposedByPlayerClan)
    {
        Assert.False(ClientKingdomDecisionsPolicy.RefuseAdd(coopClient: false, coopApplying: false, proposedByPlayerClan));
        Assert.False(ClientKingdomDecisionsPolicy.WaiveResolution(coopClient: false, needsPlayerResolution: true));
    }

    [Fact]
    public void A_players_game_never_demands_its_player_settle_a_decision()
    {
        Assert.True(ClientKingdomDecisionsPolicy.WaiveResolution(coopClient: true, needsPlayerResolution: true));
        Assert.False(ClientKingdomDecisionsPolicy.WaiveResolution(coopClient: true, needsPlayerResolution: false));
    }
}
