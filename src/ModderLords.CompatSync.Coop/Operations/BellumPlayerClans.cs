using System;
using HarmonyLib;
using TaleWorlds.CampaignSystem;

namespace ModderLords.CompatSync.Coop.Operations;

/// <summary>
/// Bellum validation mode, both sides: every player's clan is one of Bellum's noble houses, as the player's is in single
/// player.
/// <para>
/// The game defines the player's clan as a minor faction (SandBox spclans.xml: player_faction is_minor_faction="true"), and
/// nothing ever changes that, whatever the clan's tier or rank. Bellum's eligibility filters exclude minor factions
/// "except the player's clan" (<c>clan.IsMinorFaction &amp;&amp; clan != Clan.PlayerClan</c>), and its AI-only filters exclude
/// "the player's clan" (<c>clan != Clan.PlayerClan &amp;&amp; ... !clan.IsMinorFaction</c>). On a dedicated server Clan.PlayerClan
/// is the server's placeholder, so every real player's clan counted as a minor faction (no claim feuds, votes, council or
/// service roles) and Bellum's AI picked players' clans as if they were lords'.
/// </para>
/// <para>
/// In the methods below, only the player comparisons within a few instructions of an IsMinorFaction read are rewritten to
/// "any player's clan" (PlayerComparisonRewriter, near mode); other "the player at this game" checks in them (menu
/// conditions, tooltip text, a player ruler's refuge) stay as written. The list comes from an IL scan of Bellum 1.3.1:
/// every method reading both, without the cheat commands and the testing-only treason trigger, and without
/// ExpulsionDeliberationBehavior.OnDailyTick, which the authority contract owns (a method is pinned by one contract
/// only), so players are still not among the AI's expulsion candidates. Pinned in the bellum-civile.commands contract.
/// </para>
/// </summary>
internal static class BellumPlayerClans
{
    private static readonly Harmony Harmony = new Harmony("ModderLords.Compat.BellumCivile.PlayerClans");

    internal static readonly string[] Targets =
    {
        "BellumCivile.Behaviors.BellumMarriageStrategyHelper::IsStrategicMarriageInitiator",
        "BellumCivile.Behaviors.ClaimFeudBehavior::IsValidFeudClan",
        "BellumCivile.Behaviors.ClaimFeudWarBehavior::IsValidWarClan",
        "BellumCivile.Behaviors.DynamicRelationBehavior::ShouldAffectPair",
        "BellumCivile.Behaviors.ExiledClanRecoveryBehavior::IsRecoverableExileCandidate",
        "BellumCivile.Behaviors.ExiledClanRecoveryBehavior::TryMoveClanToRefuge",
        "BellumCivile.Behaviors.ExpulsionDeliberationBehavior::ExpulsionMenuEntryCondition",
        "BellumCivile.Behaviors.FeudalPoliticalOptionsBehavior::IsValidPoliticalActionClan",
        "BellumCivile.Behaviors.FeudalServiceBehavior::IsValidAiServiceRelationship",
        "BellumCivile.Behaviors.FeudalServiceBehavior::IsValidServiceLiege",
        "BellumCivile.Behaviors.FeudalTitleUsurpationBehavior::IsValidNpcClan",
        "BellumCivile.Behaviors.FiefDeliberationBehavior::GetNobleClanIdsFromSide",
        "BellumCivile.Behaviors.IdeologyBehavior+<>c::<OnDailyTick_Consolidated>b__182_0",
        "BellumCivile.Behaviors.IdeologyBehavior+<>c::<ProcessKingdomIdeologies>b__39_0",
        "BellumCivile.Behaviors.IdeologyBehavior+<>c__DisplayClass211_0::<OnDailyTick_Treason>b__0",
        "BellumCivile.Behaviors.IdeologyBehavior::CanRulerIndictClan",
        "BellumCivile.Behaviors.IdeologyBehavior::IsEligibleCourtRebellionLeader",
        "BellumCivile.ClanFiefDesireHelper::IsEligibleVassalForLandDemand",
        "BellumCivile.DynamicRelationBaselineHelper::IsLandlessNobleVassal",
        "BellumCivile.FactionObject+<>c::<BuildFiefRedistributionRecipients>b__110_0",
        "BellumCivile.FactionObject+<>c__DisplayClass122_0::<BuildFiefRedistributionDonors>b__0",
        "BellumCivile.FiefNominationHelper::IsValidCandidate",
        "BellumCivile.FiefNominationHelper::IsValidVoter",
        "BellumCivile.ForeignPolicyVoteEvaluator+<>c::<GetAverageVotingClanStrength>b__13_0",
        "BellumCivile.ForeignPolicyVoteEvaluator::IsValidVote",
        "BellumCivile.NobleClanEligibilityHelper::IsNonPlayerMinorClan",
        "BellumCivile.Patches.BlockCivilWarClanRecruitmentPatch::ShouldBlockVanillaDefection",
        "BellumCivile.Patches.FiefVoteAIPatch::Prefix",
        "BellumCivile.Patches.RevocationVoteAIPatch::FindBestInternalClaimant",
        "BellumCivile.TreatyAiDraftService::IsEligibleClan",
        "BellumCivile.TreatyRealmTransitionService::IsEligibleSettledClan",
        "BellumCivile.UI.FactionsWindowVM+<>c__DisplayClass81_0::<BuildProjectedLoyalistPowerTooltip>b__2",
        "BellumCivile.UI.FactionsWindowVM::GetLoyalistContributionText",
    };

    /// <summary>Comparisons the IL scan found beside an IsMinorFaction read in <see cref="Targets"/>.</summary>
    internal const int ExpectedComparisons = 36;

    /// <summary>
    /// "Is this any player's clan?" On a player's game this game's own clan is also Clan.PlayerClan, counted as well so it
    /// can never lose the standing it had before.
    /// </summary>
    public static bool IsPlayerClan(Clan? clan)
    {
        if (clan == null) return false;
        if (ServerPlayerChecks.IsAnyPlayerClan(clan)) return true;
        try { return !OperationProcessSide.IsServer && ReferenceEquals(clan, Clan.PlayerClan); }
        catch { return false; }
    }

    internal static string Install()
    {
        var t = typeof(ServerPlayerChecks);
        PlayerComparisonRewriter.SetHelpers(t.GetMethod(nameof(ServerPlayerChecks.IsAnyPlayerHero))!, typeof(BellumPlayerClans).GetMethod(nameof(IsPlayerClan))!,
            t.GetMethod(nameof(ServerPlayerChecks.IsAnyPlayerParty))!, t.GetMethod(nameof(ServerPlayerChecks.IsAnyPlayerPartyBase))!);
        var (methods, comparisons, missing) = PlayerComparisonRewriter.Apply(Harmony, Targets, m => Log.Warn("Bellum player clans: " + m), "get_IsMinorFaction");
        if (missing > 0) throw new MissingMethodException("Bellum player clans: " + missing + " method(s) not found");
        if (comparisons != ExpectedComparisons)
            Log.Warn($"Bellum player clans: {comparisons} comparison(s) rewritten, expected {ExpectedComparisons}; Bellum's code differs from the reviewed build");
        return $"players' clans are Bellum noble houses ({methods} method(s), {comparisons} check(s))";
    }
}
