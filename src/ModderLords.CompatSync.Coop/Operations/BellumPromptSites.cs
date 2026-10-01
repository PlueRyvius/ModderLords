using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using GameInterface;
using GameInterface.Services.Players;
using HarmonyLib;
using TaleWorlds.CampaignSystem;

namespace ModderLords.CompatSync.Coop.Operations;

/// <summary>
/// The Bellum 1.3.1 decisions that reach the player they concern (BellumPrompts), each with Bellum's own AI decision for
/// an absent player. First set: claim feuds (ClaimFeudBehavior), the state machine where a vassal presses a claim, the
/// crown judges it, the parties accept or fight, and kin are called to arms.
/// </summary>
internal static class BellumPromptSites
{
    /// <summary>
    /// Methods where Bellum asks "is this the player's?" meaning "players choose for themselves": rewritten to any
    /// player, so a player's clan is not enlisted, nor made to revoke titles or fabricate claims, by Bellum's AI.
    /// </summary>
    internal static readonly string[] AnyPlayerChecks =
    {
        "BellumCivile.Behaviors.ClaimFeudBehavior::BuildFeudSide",
        "BellumCivile.Behaviors.ClaimFeudBehavior::ResolveFeudCallsToArms",
        "BellumCivile.Behaviors.ClaimFeudBehavior::EvaluateClanForClaimedRevocation",
        "BellumCivile.Behaviors.ClaimFeudBehavior::TryRunAutonomousLandedAmbitionEvaluationForClan",
    };

    /// <summary>Crown judgments that timed out: Bellum's AI judges these feuds, so the ruler is not asked again.</summary>
    private static readonly HashSet<string> AiJudged = new HashSet<string>(StringComparer.Ordinal);

    internal static IEnumerable<BellumPromptSite> All() => new[]
    {
        // A petition reaches the crown. A player ruler judges it (multi-selection); otherwise the AI judges and a player
        // claimant or holder is asked to accept or fight.
        new BellumPromptSite
        {
            Method = "BellumCivile.Behaviors.ClaimFeudBehavior::ResolvePetition",
            Players = (_, a) => PetitionPlayers(a[1], a[2] as Clan, a[3] as Clan),
            Ai = DecideFeudPrompt,
        },
        // Daily re-show of a judgment still waiting on the crown.
        new BellumPromptSite
        {
            Method = "BellumCivile.Behaviors.ClaimFeudBehavior::TryShowPendingPlayerRulerJudgmentInquiry",
            Players = (_, a) => RulerPlayer(a[1], a[2] as Clan, a[3] as Clan),
            Ai = DecideFeudPrompt,
        },
        // Daily re-show of a ruling still waiting on the claimant's or holder's answer.
        new BellumPromptSite
        {
            Method = "BellumCivile.Behaviors.ClaimFeudBehavior::TryShowPendingPlayerRulingInquiry",
            Players = (_, a) => Single(BellumPrompts.PlayerOf(a[1] as Clan) ?? BellumPrompts.PlayerOf(a[2] as Clan)),
            Ai = DecideFeudPrompt,
        },
        // A feud passes 66%: kin are called to arms. Every player in the realm who would answer is asked.
        new BellumPromptSite
        {
            Method = "BellumCivile.Behaviors.ClaimFeudBehavior::TryShowPlayerSupportInvitation",
            Players = (_, a) => RealmPlayers(a[5] as Kingdom, a[2] as Clan, a[3] as Clan),
            // Bellum only calls a clan that would support a side, and a lord in that position answers the call.
            Ai = p => Choose(p, yes: true),
        },
        // A lord demands a title the player holds be revoked.
        new BellumPromptSite
        {
            Method = "BellumCivile.Behaviors.ClaimFeudBehavior::ShowPlayerRevocationDemandInquiry",
            Players = (_, a) => Single(BellumPrompts.PlayerOf(Get<Clan>(a[3], "HolderClan"))),
            Ai = DecideRevocationDemand,
        },
    };

    // ---- who each call is for -------------------------------------------------------------------------------------

    private static IReadOnlyList<Hero> PetitionPlayers(object? record, Clan? claimant, Clan? holder)
    {
        var ruler = RulerPlayer(record, claimant, holder);
        if (ruler.Count > 0 && ruler[0].Clan != claimant && ruler[0].Clan != holder) return ruler;
        return Single(BellumPrompts.PlayerOf(claimant) ?? BellumPrompts.PlayerOf(holder));
    }

    private static IReadOnlyList<Hero> RulerPlayer(object? record, Clan? claimant, Clan? holder)
    {
        if (record == null || AiJudged.Contains(Get<string>(record, "RecordId") ?? "")) return Array.Empty<Hero>();
        return Single(BellumPrompts.PlayerOf((claimant?.Kingdom ?? holder?.Kingdom)?.RulingClan));
    }

    private static IReadOnlyList<Hero> RealmPlayers(Kingdom? kingdom, Clan? claimant, Clan? holder)
    {
        if (kingdom == null || !ContainerProvider.TryResolve<IPlayerManager>(out var players)) return Array.Empty<Hero>();
        return players.Players.Select(p => PlayerHeroes.HeroFor(p.HeroId))
            .Where(h => h is { IsAlive: true } && h.Clan?.Kingdom == kingdom && h.Clan != claimant && h.Clan != holder && h.Clan?.Leader == h)
            .Select(h => h!).Distinct().ToList();
    }

    private static IReadOnlyList<Hero> Single(Hero? hero) => hero == null ? Array.Empty<Hero>() : new[] { hero };

    // ---- what Bellum's AI does instead ----------------------------------------------------------------------------

    /// <summary>The crown's judgment (multi-selection) or a party's answer to it (yes/no), whichever this prompt is.</summary>
    private static bool DecideFeudPrompt(BellumPrompts.Pending p)
    {
        var record = p.Args.FirstOrDefault(a => a?.GetType().Name == "ClaimFeudRecord");
        if (record == null) return false;
        return p.Multi != null ? JudgeAsAi(p, record) : AnswerRulingAsAi(p, record);
    }

    /// <summary>
    /// The ruler did not judge: mark the feud for the AI, put it back to "petition ready" and run Bellum's petition again,
    /// which now judges it as it would for a lord's realm (and asks a player claimant or holder, if there is one).
    /// </summary>
    private static bool JudgeAsAi(BellumPrompts.Pending p, object record)
    {
        var id = Get<string>(record, "RecordId");
        var behavior = p.Instance;
        var petition = behavior == null ? null : AccessTools.Method(behavior.GetType(), "ResolvePetition");
        var state = AccessTools.TypeByName("BellumCivile.ClaimFeudState");
        if (id == null || petition == null || state == null) return false;
        AiJudged.Add(id);
        (AccessTools.Field(behavior!.GetType(), "_shownPlayerRulerJudgmentInquiries")?.GetValue(behavior) as HashSet<string>)?.Remove(id);
        AccessTools.Method(record.GetType(), "SetState")?.Invoke(record, new[] { Enum.Parse(state, "PetitionReady") });
        var claimant = ClanById(Get<string>(record, "ClaimantClanId"));
        var holder = ClanById(Get<string>(record, "HolderClanId"));
        var title = p.Args.FirstOrDefault(a => a?.GetType().Name == "FeudalTitleRecord");
        var titles = p.Args.FirstOrDefault(a => a?.GetType().Name == "FeudalTitleBehavior");
        petition.Invoke(behavior, new[] { titles, record, claimant, holder, title });
        return true;
    }

    /// <summary>A player claimant or holder did not answer the crown's ruling: what Bellum's AI would have that clan do.</summary>
    private static bool AnswerRulingAsAi(BellumPrompts.Pending p, object record)
    {
        if (p.Inquiry is not { } q) return false;
        var claimant = ClanById(Get<string>(record, "ClaimantClanId"));
        var holder = ClanById(Get<string>(record, "HolderClanId"));
        var kingdom = claimant?.Kingdom ?? holder?.Kingdom;
        var responses = AccessTools.Method(AccessTools.TypeByName("BellumCivile.Behaviors.ClaimFeudBehavior"), "GetRulingResponses");
        var title = p.Args.FirstOrDefault(a => a?.GetType().Name == "FeudalTitleRecord");
        if (responses == null || kingdom?.RulingClan?.Leader == null || claimant == null || holder == null || title == null) return false;
        var args = new object?[]
        {
            Get<object>(record, "Judgment"), kingdom, kingdom.RulingClan.Leader, claimant, holder, title, Get<object>(record, "ClaimStrength"),
            Get<float>(record, "ClaimantSidePower"), Get<float>(record, "HolderSidePower"), null, null, null, null,
        };
        responses.Invoke(null, args);
        var response = (p.Hero.Clan == claimant ? args[9] : args[10])?.ToString();
        var defy = response == "Defy" && q.IsNegativeOptionShown;
        BellumPrompts.RunAsPlayer(p, () => (defy ? q.NegativeAction : q.AffirmativeAction)?.Invoke());
        return true;
    }

    /// <summary>The holder did not answer a revocation demand: Bellum's AI revocation, where the holder's defiance is its own call.</summary>
    private static bool DecideRevocationDemand(BellumPrompts.Pending p)
    {
        var revoker = p.Args.ElementAtOrDefault(1) as Clan;
        var title = p.Args.ElementAtOrDefault(2);
        var service = AccessTools.TypeByName("BellumCivile.FeudalTitlePlayerActionService");
        var execute = service == null ? null : AccessTools.Method(service, "TryExecuteRevocation");
        var key = AccessTools.Method(AccessTools.TypeByName("BellumCivile.Behaviors.ClaimFeudBehavior"), "BuildPlayerRevocationDemandKey");
        if (revoker == null || title == null || execute == null) return false;
        var args = new object?[] { revoker, title, null, null };
        execute.Invoke(null, args);
        // Bellum remembers the demand under the holder's clan; forget it so a later demand can be made.
        BellumPrompts.RunAsPlayer(p, () =>
        {
            if (key?.Invoke(null, new[] { revoker, title }) is string k)
                (AccessTools.Field(p.Instance?.GetType(), "_shownPlayerRevocationDemandInquiries")?.GetValue(p.Instance) as HashSet<string>)?.Remove(k);
        });
        Log.Info($"Bellum prompts: AI revocation by {revoker.Name}; holder defied={args[2]}; {args[3]}");
        return true;
    }

    private static bool Choose(BellumPrompts.Pending p, bool yes)
    {
        if (p.Inquiry is not { } q) return false;
        BellumPrompts.RunAsPlayer(p, () => (yes ? q.AffirmativeAction : q.NegativeAction)?.Invoke());
        return true;
    }

    // ---- reflection helpers ---------------------------------------------------------------------------------------

    private static T? Get<T>(object? instance, string property)
    {
        if (instance == null) return default;
        var value = AccessTools.Property(instance.GetType(), property)?.GetValue(instance, null);
        return value is T t ? t : default;
    }

    private static Clan? ClanById(string? id) => id == null ? null : Clan.All.FirstOrDefault(c => c.StringId == id);
}
