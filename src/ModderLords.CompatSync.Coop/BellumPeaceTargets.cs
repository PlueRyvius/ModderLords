using System.Collections.Generic;

namespace ModderLords.CompatSync.Coop;

/// <summary>What a Bellum method is made to do on a co-op player's game. See <see cref="BellumPeaceTargets"/>.</summary>
public enum BellumPeaceAnswer
{
    /// <summary>A parley entry point: answers "no, not opened" and does nothing.</summary>
    No,
    /// <summary>A Harmony prefix of Bellum's that would replace a vanilla peace step: answers "run the original".</summary>
    RunOriginal,
    /// <summary>A Harmony postfix of Bellum's that would retire a vanilla peace vote: does nothing.</summary>
    Nothing,
}

/// <summary>
/// The Bellum Civile 1.3.1 methods that turn vanilla peace into its own Peace Parley. Kept free of game types so the
/// list can be checked in tests.
///
/// <para>Two kinds. The <b>entry points</b> are where Bellum queues or opens a parley for "the player"; with those
/// answering no, every caller Bellum has falls back to vanilla or stops. The <b>patches</b> are Bellum's own Harmony
/// patches on the vanilla peace vote, courier offer and diplomacy button; they are answered too, because the
/// diplomacy button's patch shows a failure instead of falling back.</para>
/// </summary>
public static class BellumPeaceTargets
{
    public sealed class Target
    {
        public Target(string type, string method, BellumPeaceAnswer answer) { Type = type; Method = method; Answer = answer; }
        public string Type { get; }
        public string Method { get; }
        public BellumPeaceAnswer Answer { get; }
    }

    public const string Treaty = "BellumCivile.Behaviors.ForeignTreatyBehavior";
    private const string Patches = "BellumCivile.Patches.";

    /// <summary>Bellum's mark for a peace it makes itself, and the counter behind it. See BellumVotePeace.</summary>
    public const string TreatyMark = "BellumCivile.BellumPeaceResolutionContext";
    public const string TreatyMarkDepth = "_bellumPeaceDepth";
    /// <summary>Bellum's guard on <c>MakePeaceAction</c>, which lets a marked peace through.</summary>
    public const string PeaceGuard = Patches + "CivilWarMakePeacePatch";

    // ---- what BellumTreatyVote reads by name on the server (checked against the installed Bellum in tests) ----

    public const string Proposal = "BellumCivile.TreatyProposalRecord";
    public const string Term = "BellumCivile.TreatyTermRecord";
    /// <summary>Bellum's rule that a war must last a set number of days before peace: <c>TryGetPeaceProposalBlock</c>.</summary>
    public const string MinimumDuration = "BellumCivile.WarPeaceMinimumDurationHelper";
    public const string MinimumDurationCheck = "TryGetPeaceProposalBlock";

    /// <summary>Where the server's Bellum ratifies a pending treaty: both councils are asked, then it is applied or rejected.</summary>
    public const string Resolve = "ResolvePendingParley";
    /// <summary>The councils' verdicts; its out parameters are bound by name.</summary>
    public const string Ratify = "EvaluateRatification";
    public const string Apply = "ApplyTreaty";
    public const string Summary = "BuildTreatyTermsSummary";

    public static readonly IReadOnlyList<string> ResolveParameters = new[] { "war", "proposal", "allowPlayerVassal" };
    public static readonly IReadOnlyList<string> RatifyParameters = new[]
        { "proposal", "winner", "loser", "winnerAccepts", "loserAccepts", "winnerUsesOverride", "loserUsesOverride" };
    public static readonly IReadOnlyList<string> ApplyParameters = new[] { "proposal", "winner", "loser" };
    public static readonly IReadOnlyList<string> ProposalMembers = new[]
        { "ProposalId", "WarKey", "WinnerKingdomId", "LoserKingdomId", "IsForced", "State", "Terms" };
    public static readonly IReadOnlyList<string> TermMembers = new[] { "Type", "FromKingdomId", "ToKingdomId", "DailyGold", "DurationDays" };

    /// <summary>The state name of a proposal that is still waiting to be ratified.</summary>
    public const string PendingState = "ParleyPending";
    public const string TributeTerm = "Tribute";
    public const string WhitePeaceTerm = "WhitePeace";

    /// <summary>One tribute term of a treaty: who pays whom, how much a day, for how long.</summary>
    public readonly struct Tribute
    {
        public Tribute(string from, string to, int daily, int days) { From = from; To = to; Daily = daily; Days = days; }
        public string From { get; }
        public string To { get; }
        public int Daily { get; }
        public int Days { get; }
    }

    /// <summary>
    /// What a kingdom pays a day under a treaty's tribute terms (negative: it is paid), and for how many days. The
    /// vote shown to that kingdom's players carries these numbers, the way a vanilla peace vote does.
    /// </summary>
    public static (int Daily, int Days) TributePaidBy(string kingdomId, IEnumerable<Tribute> tributes)
    {
        int daily = 0, days = 0;
        foreach (var t in tributes)
        {
            if (t.Daily <= 0 || t.Days <= 0) continue;
            if (t.From == kingdomId) daily += t.Daily;
            else if (t.To == kingdomId) daily -= t.Daily;
            else continue;
            if (t.Days > days) days = t.Days;
        }
        return daily == 0 ? (0, 0) : (daily, days);
    }

    public static readonly IReadOnlyList<Target> All = new[]
    {
        new Target(Treaty, "TryQueuePlayerParley", BellumPeaceAnswer.No),
        new Target(Treaty, "TryQueueDeferredPlayerParleyOpen", BellumPeaceAnswer.No),
        new Target(Treaty, "TryOpenPlayerParley", BellumPeaceAnswer.No),
        new Target(Treaty, "TryShowForcedPlayerParleyInquiry", BellumPeaceAnswer.No),
        new Target("BellumCivile.UI.Parley.PeaceParleyInterface", "Show", BellumPeaceAnswer.No),
        new Target(Patches + "PeaceParleyDecisionRedirector", "TryQueueAndRetire", BellumPeaceAnswer.No),

        new Target(Patches + "KingdomDiplomacyPeaceParleyPatch", "Prefix", BellumPeaceAnswer.RunOriginal),
        new Target(Patches + "RulerPeaceOfferParleyPatch", "Prefix", BellumPeaceAnswer.RunOriginal),
        new Target(Patches + "KingdomPeaceVoteNotificationParleyPatch", "Prefix", BellumPeaceAnswer.RunOriginal),
        new Target(Patches + "KingdomPeaceDecisionScreenFallbackPatch", "Prefix", BellumPeaceAnswer.RunOriginal),
        new Target(Patches + "KingdomPeaceVoteNotificationQueuedParleyPatch", "Postfix", BellumPeaceAnswer.Nothing),
        new Target(Patches + "KingdomPeaceDecisionAddedParleyCleanupPatch", "Postfix", BellumPeaceAnswer.Nothing),
    };
}
