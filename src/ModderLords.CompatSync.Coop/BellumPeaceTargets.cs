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
