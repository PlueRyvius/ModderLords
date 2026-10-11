using System;
using System.Reflection;
using HarmonyLib;
using ModderLords.CompatSync;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Election;

namespace ModderLords.CompatSync.Coop;

/// <summary>
/// Server only: a peace that comes out of a kingdom vote in a kingdom with players is let past Bellum Civile's guard.
///
/// <para>While Bellum's war-and-peace revamp is on, its prefixes on <c>MakePeaceAction.Apply</c> and
/// <c>ApplyByKingdomDecision</c> refuse every peace between two ordinary kingdoms that Bellum did not make itself
/// ("Blocked non-Bellum international peace"). In single player that is safe, because the player's peace goes through
/// Bellum's parley. In co-op the parley is off on a player's game (<see cref="BellumPeaceInCoop"/>) and peace is the
/// kingdom vote, which the server applies; so the vote ran, passed, and was thrown away there.</para>
///
/// <para>Reported 2026-10-11 from a host's logs: a players' kingdom voted for peace and stayed at war until Bellum's
/// own war score reached 100 and forced a treaty.</para>
///
/// <para>Bellum marks its own treaties with <c>BellumPeaceResolutionContext</c>, and its guard lets a marked peace
/// through after the claim-feud and civil-war checks, which still apply. The vote's outcome is applied inside the same
/// mark. Bellum closes its war score for the war from the game's peace event, as it does for its own treaties. A vote
/// in a kingdom with no players is left to Bellum.</para>
/// </summary>
public static class BellumVotePeace
{
    private static readonly Harmony Harmony = new Harmony("ModderLords.Compat.BellumVotePeace");
    private static bool _installTried;
    private static FieldInfo? _depth;
    private static MethodInfo? _tooEarly;
    private static int _marked;

    /// <summary>How many vote outcomes were applied inside Bellum's mark this session, for the verification line.</summary>
    public static int Marked => _marked;

    /// <summary>Patches the peace vote's outcome once Bellum is loaded, on the server. Safe to call every tick.</summary>
    public static void EnsureInstalled()
    {
        if (_installTried) return;
        _installTried = true;
        if (!Operations.OperationProcessSide.IsServer) return;
        var context = AccessTools.TypeByName(BellumPeaceTargets.TreatyMark);
        if (context == null) return;   // Bellum is not loaded
        try
        {
            _depth = AccessTools.Field(context, BellumPeaceTargets.TreatyMarkDepth);
            var target = AccessTools.Method(typeof(MakePeaceKingdomDecision), nameof(MakePeaceKingdomDecision.ApplyChosenOutcome));
            if (_depth == null || _depth.FieldType != typeof(int) || !_depth.IsStatic || target == null)
            {
                Log.Warn("Bellum vote peace: Bellum's treaty mark has changed shape; a peace voted in a players' kingdom may still be blocked by Bellum");
                return;
            }
            var minimum = AccessTools.TypeByName(BellumPeaceTargets.MinimumDuration);
            _tooEarly = minimum == null ? null : AccessTools.Method(minimum, BellumPeaceTargets.MinimumDurationCheck);
            if (_tooEarly == null || _tooEarly.GetParameters().Length != 5)
            {
                _tooEarly = null;
                Log.Warn("Bellum vote peace: Bellum's minimum war duration check was not found; a voted peace is not held to it");
            }
            Harmony.Patch(target,
                prefix: new HarmonyMethod(typeof(BellumVotePeace), nameof(Prefix)) { priority = Priority.First },
                finalizer: new HarmonyMethod(typeof(BellumVotePeace), nameof(Finalizer)));
            Log.Info("Bellum vote peace: a peace voted in a kingdom with players is applied as a Bellum treaty, so Bellum does not block it");
        }
        catch (Exception ex) { Log.Warn("Bellum vote peace not installed: " + ex.GetBaseException().Message); }
    }

    private static void Prefix(MakePeaceKingdomDecision __instance, DecisionOutcome chosenOutcome, out bool __state)
    {
        __state = false;
        try
        {
            // A vote that stands in for a Bellum treaty applies nothing itself; Bellum applies its own terms afterwards.
            if (BellumTreatyVote.OnOutcome(__instance, chosenOutcome)) return;
            if (chosenOutcome is not MakePeaceKingdomDecision.MakePeaceDecisionOutcome { ShouldPeaceBeDeclared: true }) return;
            if (!HasPlayers(__instance?.Kingdom)) return;
            var other = __instance!.FactionToMakePeaceWith;
            if (TooEarly(__instance.Kingdom, other as Kingdom, out var reason))
            {
                Log.Info($"Bellum vote peace: the peace vote of {__instance.Kingdom.StringId} with {other?.StringId} is left to Bellum, which refuses it: {reason}");
                BellumTreatyVote.Tell(__instance.Kingdom, $"The vote for peace with {other?.Name} passed, but the war is too young to end: {reason}");
                return;
            }
            _depth!.SetValue(null, (int)_depth.GetValue(null) + 1);
            __state = true;
            _marked++;
            Log.Info($"Bellum vote peace: applying the peace vote of {__instance.Kingdom.StringId} with {other?.StringId} as a Bellum treaty");
        }
        catch (Exception ex) { Log.Warn("Bellum vote peace: could not mark the vote: " + ex.GetBaseException().Message); }
    }

    /// <summary>Bellum's own rule that a war lasts a minimum number of days before peace, which its parley enforced for the player.</summary>
    private static bool TooEarly(Kingdom realm, Kingdom? other, out string reason)
    {
        reason = "";
        if (other == null || _tooEarly == null) return false;
        var args = new object?[] { realm, other, null, 0, 0 };
        if (_tooEarly.Invoke(null, args) is not true) return false;
        reason = args[2]?.ToString() ?? "";
        return true;
    }
    private static void Finalizer(bool __state)
    {
        if (!__state) return;
        try { _depth!.SetValue(null, Math.Max(0, (int)_depth.GetValue(null) - 1)); }
        catch (Exception ex) { Log.Warn("Bellum vote peace: could not clear the mark: " + ex.GetBaseException().Message); }
    }

    private static bool HasPlayers(Kingdom? kingdom)
    {
        if (kingdom == null) return false;
        foreach (var clan in kingdom.Clans)
            if (ServerPlayerChecks.IsAnyPlayerClan(clan)) return true;
        return false;
    }
}
