using ModderLords.Operations;

namespace ModderLords.Core.Tests;

public sealed class ResourceAwardFixtureTests
{
    // Original engine-independent fixture: exercises the same compiled gate while keeping award policy in its caller.
    private sealed class ClanState(string id) { public string Id = id; public int Gold; public float Influence; }
    private static void Award(ResourceAdderPolicy gate, object campaign, long day, bool coop, bool server,
        ClanState[] clans, int goldThreshold, int goldAmount, float influenceThreshold, float influenceAmount)
    {
        if (!gate.TryBeginAward(coop, server, campaign, day)) return;
        foreach (var clan in clans.Where(c => gate.IsAiClan(c.Id, coop, c.Id != "local")))
        {
            if (clan.Gold < goldThreshold) clan.Gold += goldAmount;
            if (clan.Influence < influenceThreshold) clan.Influence += influenceAmount;
        }
    }
    [Fact] public void OneAuthorityAwardPreservesConfiguredAmountsAndExcludesDisconnectedOwners()
    {
        var gate = new ResourceAdderPolicy(); var campaign = new object();
        gate.UpdateOwners(["local", "disconnected"]);
        ClanState[] clans = [new("local"), new("disconnected"), new("ai"), new("rich") { Gold = 101, Influence = 21 }];
        Award(gate, campaign, 1, true, false, clans, 100, 37, 20, 3.5f);
        Award(gate, campaign, 1, true, true, clans, 100, 37, 20, 3.5f);
        Award(gate, campaign, 1, true, true, clans, 100, 37, 20, 3.5f);
        Assert.Equal([0, 0, 37, 101], clans.Select(c => c.Gold));
        Assert.Equal([0f, 0f, 3.5f, 21f], clans.Select(c => c.Influence));
    }
    [Fact] public void UnresolvedOwnershipSuspendsWithoutConsumingAwardAndOwnershipChangesAreHonored()
    {
        var gate = new ResourceAdderPolicy(); var campaign = new object();
        gate.UpdateOwners(["local", null]);
        Assert.False(gate.TryBeginAward(true, true, campaign, 1));
        gate.UpdateOwners(["local", "new-clan"]);
        Assert.True(gate.TryBeginAward(true, true, campaign, 1));
        Assert.False(gate.IsAiClan("new-clan", true, true));
        Assert.True(gate.IsAiClan("old-clan", true, false));
        Assert.True(gate.TryBeginAward(true, true, new object(), 1));
    }
    [Fact] public void SinglePlayerKeepsOriginalPredicateAndCallbackFrequency()
    {
        var gate = new ResourceAdderPolicy(); var campaign = new object();
        ClanState[] clans = [new("local"), new("other")];
        Award(gate, campaign, 1, false, false, clans, 100, 7, 20, 2);
        Award(gate, campaign, 1, false, false, clans, 100, 7, 20, 2);
        Assert.Equal(0, clans[0].Gold); Assert.Equal(14, clans[1].Gold); Assert.Equal(4, clans[1].Influence);
    }
}
