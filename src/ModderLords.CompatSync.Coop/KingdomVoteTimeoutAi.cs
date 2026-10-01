using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using ModderLords.CompatSync;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Election;

namespace ModderLords.CompatSync.Coop;

/// <summary>
/// Server only: when a kingdom vote's round runs out, a player who did not vote is decided for by the AI, as if they
/// were a lord, instead of abstaining.
/// <para>
/// Coop gives every eligible player clan a timed voting round (KingdomDecisionVoteManager, 60 s). On the deadline its
/// ApplyMissingAbstentions removes the support of every clan that has not voted, so an absent or offline player counts
/// for nothing. The server had already given those clans AI support when the election was set up (on a dedicated
/// server "the player" is the placeholder hero, so vanilla treats real players' clans as lords); this puts that AI
/// decision back, recomputed at the deadline. A ruler who does not choose was already left to the AI by Coop.
/// </para>
/// </summary>
public static class KingdomVoteTimeoutAi
{
    private static readonly Harmony Harmony = new Harmony("ModderLords.Compat.KingdomVoteTimeoutAi");
    private static bool _installTried;
    private static PropertyInfo? _eligible, _finalVotes, _decision, _election;
    private static MethodInfo? _tryGetClan;

    /// <summary>Patches Coop's deadline handling once, on the server. Safe to call every tick.</summary>
    public static void EnsureInstalled()
    {
        if (_installTried) return;
        _installTried = true;
        if (!IsServer()) return;
        try
        {
            var manager = AccessTools.TypeByName("GameInterface.Services.Kingdoms.KingdomDecisionVoteManager");
            var target = manager == null ? null : AccessTools.Method(manager, "ApplyMissingAbstentions");
            var state = target?.GetParameters() is { Length: 1 } p ? p[0].ParameterType : null;
            _eligible = state == null ? null : AccessTools.Property(state, "EligibleClanIds");
            _finalVotes = state == null ? null : AccessTools.Property(state, "FinalVotes");
            _decision = state == null ? null : AccessTools.Property(state, "Decision");
            _election = state == null ? null : AccessTools.Property(state, "Election");
            _tryGetClan = manager == null ? null : AccessTools.Method(manager, "TryGetClan");
            if (target == null || _eligible == null || _finalVotes == null || _decision == null || _election == null || _tryGetClan == null)
            {
                Log.Warn("kingdom vote timeout: Coop's vote manager has changed shape; players who do not vote still abstain");
                return;
            }
            Harmony.Patch(target, postfix: new HarmonyMethod(typeof(KingdomVoteTimeoutAi), nameof(Postfix)));
            Log.Info("kingdom vote timeout: a player who does not vote before the deadline is decided for by the AI");
        }
        catch (Exception ex) { Log.Warn("kingdom vote timeout not installed: " + ex.GetBaseException().Message); }
    }

    private static void Postfix(object __instance, object state)
    {
        try
        {
            var decision = (KingdomDecision)_decision!.GetValue(state, null);
            var election = (KingdomElection)_election!.GetValue(state, null);
            var finalVotes = (IDictionary)_finalVotes!.GetValue(state, null);
            if (decision?.Kingdom == null || election == null) return;

            var decided = new List<string>();
            foreach (var clanId in (IEnumerable<string>)_eligible!.GetValue(state, null))
            {
                if (finalVotes.Contains(clanId)) continue;
                var args = new object?[] { clanId, decision.Kingdom, null };
                if (!(bool)_tryGetClan!.Invoke(__instance, args) || args[2] is not Clan clan) continue;

                var supporter = new Supporter(clan);
                var outcome = decision.DetermineSupportOption(supporter, election.PossibleOutcomes, out var weight, calculateRelationshipEffect: true);
                if (outcome == null) continue;
                supporter.SupportWeight = weight;
                outcome.AddSupport(supporter);
                decided.Add(clan.StringId);
            }
            if (decided.Count == 0) return;
            election.DetermineOfficialSupport();
            Log.Info($"kingdom vote timeout: AI decided for {string.Join(", ", decided)} on {decision.GetType().Name} in {decision.Kingdom.StringId}");
        }
        catch (Exception ex) { Log.Warn("kingdom vote timeout: could not let the AI decide: " + ex.GetBaseException().Message); }
    }

    private static bool IsServer()
    {
        try { return Common.ModInformation.IsServer; } catch { return false; }
    }
}
