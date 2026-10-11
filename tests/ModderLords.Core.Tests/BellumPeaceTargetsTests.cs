using ModderLords.CompatSync.Coop;
using Xunit;

namespace ModderLords.Core.Tests;

/// <summary>
/// The list of Bellum Civile 1.3.1 methods answered on a co-op player's game, pinned: every way Bellum queues or opens
/// its Peace Parley for "the player", and each of its patches on vanilla's peace vote.
/// </summary>
public sealed class BellumPeaceTargetsTests
{
    [Fact]
    public void Every_parley_entry_point_answers_no()
    {
        var no = BellumPeaceTargets.All.Where(t => t.Answer == BellumPeaceAnswer.No).Select(t => t.Method).ToList();
        Assert.Equal(new[]
        {
            "TryQueuePlayerParley", "TryQueueDeferredPlayerParleyOpen", "TryOpenPlayerParley", "TryShowForcedPlayerParleyInquiry",
            "Show", "TryQueueAndRetire",
        }, no);
    }

    [Fact]
    public void Bellums_patches_on_the_vanilla_peace_vote_let_vanilla_run()
    {
        var prefixes = BellumPeaceTargets.All.Where(t => t.Answer == BellumPeaceAnswer.RunOriginal).ToList();
        var postfixes = BellumPeaceTargets.All.Where(t => t.Answer == BellumPeaceAnswer.Nothing).ToList();
        Assert.All(prefixes, t => Assert.Equal("Prefix", t.Method));
        Assert.All(postfixes, t => Assert.Equal("Postfix", t.Method));
        // The one that looped the kingdom screen, and the one that turned the diplomacy button into a parley.
        Assert.Contains(prefixes, t => t.Type.EndsWith(".KingdomPeaceDecisionScreenFallbackPatch"));
        Assert.Contains(prefixes, t => t.Type.EndsWith(".KingdomDiplomacyPeaceParleyPatch"));
        Assert.Equal(4, prefixes.Count);
        Assert.Equal(2, postfixes.Count);
    }

    [Fact]
    public void Nothing_is_listed_twice_and_everything_is_Bellums()
    {
        Assert.Equal(BellumPeaceTargets.All.Count, BellumPeaceTargets.All.Select(t => t.Type + "::" + t.Method).Distinct().Count());
        Assert.All(BellumPeaceTargets.All, t => Assert.StartsWith("BellumCivile.", t.Type));
    }
}
