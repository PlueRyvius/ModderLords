using System;
using System.Collections.Generic;
using GameInterface.Policies;
using HarmonyLib;
using ModderLords.CompatSync;
using ModderLords.CompatSync.Coop.Taom;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Election;

namespace ModderLords.CompatSync.Coop;

/// <summary>
/// On a co-op player's game: kingdom decisions stay the server's, and a decision the game cannot settle never locks the
/// player in the Kingdom screen.
///
/// <para>Reported 2026-10-13 (support bundles 20261014-014618-c4081a5d, 20261014-014836-402edadf): after a policy vote
/// in a kingdom two players belonged to, and after a fief redistribution, the ruler was locked in the Kingdom screen
/// and had to kill the game. Both votes reached the server and it counted 2/2; the policy passed. On the ruler's game a
/// decision was still pending, and when its time ran out the map screen reopened the Kingdom screen every frame
/// (<c>MapScreen</c> opens it for any decision with <c>NeedsPlayerResolution</c>), with a "you need to resolve" prompt
/// that has no Cancel.</para>
///
/// <para>The lists had come apart. Minutes before, the other player's own copy of Bellum Civile had opened a privy
/// council ballot on their game (proposer an AI clan). Coop's client added it to that game's kingdom and then failed to
/// send it ("PrivyCouncilAppointmentDecision is not supported"). Coop addresses decisions by their position in the
/// kingdom's list, and from then on that player's game called the policy vote "decision 1" while the server called it
/// "decision 0"; their vote for decision 1 was dropped by the server. Our 30 Sep Bellum notes recorded the same stale
/// decision without a cause.</para>
///
/// <list type="bullet">
/// <item><see cref="AddDecisionPrefix"/>: a decision some other clan proposed is not added on a player's game. The
/// server makes its own and Coop brings it to every player.</item>
/// <item><see cref="NeedsPlayerResolutionPostfix"/>: a player's game never demands that its player settle a decision.
/// The server settles them all; the player still votes, and can still open a decision from the Kingdom screen.</item>
/// </list>
/// The server is untouched, and so is a game outside co-op.
/// </summary>
public static class ClientKingdomDecisions
{
    private static readonly Harmony Harmony = new Harmony("ModderLords.Compat.ClientKingdomDecisions");
    private static bool _installTried, _waivedTold;
    private static readonly HashSet<string> RefusedTold = new HashSet<string>(StringComparer.Ordinal);
    private static int _refused;

    /// <summary>A player's game in a co-op session.</summary>
    private static bool Active => !Operations.OperationProcessSide.IsServer && TaomActions.IsCoopClient;

    /// <summary>Patches once, on a player's game. Safe to call every tick.</summary>
    public static void EnsureInstalled()
    {
        if (_installTried) return;
        _installTried = true;
        if (Operations.OperationProcessSide.IsServer) return;
        try
        {
            var add = AccessTools.Method(typeof(Kingdom), nameof(Kingdom.AddDecision));
            var needs = AccessTools.PropertyGetter(typeof(KingdomDecision), nameof(KingdomDecision.NeedsPlayerResolution));
            if (add == null || needs == null)
            {
                Log.Warn("kingdom decisions on players' games: the game has changed shape; a stale decision can still lock the Kingdom screen");
                return;
            }
            Harmony.Patch(add, prefix: new HarmonyMethod(typeof(ClientKingdomDecisions), nameof(AddDecisionPrefix)) { priority = Priority.First });
            Harmony.Patch(needs, postfix: new HarmonyMethod(typeof(ClientKingdomDecisions), nameof(NeedsPlayerResolutionPostfix)));
            Log.Info("kingdom decisions on players' games: only the player's own proposals are added here, and the server settles every decision");
        }
        catch (Exception ex) { Log.Warn("kingdom decisions on players' games not installed: " + ex.GetBaseException().Message); }
    }

    public static bool AddDecisionPrefix(Kingdom __instance, KingdomDecision kingdomDecision)
    {
        try
        {
            if (kingdomDecision == null) return true;
            if (!ClientKingdomDecisionsPolicy.RefuseAdd(Active, CallOriginalPolicy.IsOriginalAllowed(), kingdomDecision.ProposerClan == Clan.PlayerClan))
                return true;
            _refused++;
            var type = kingdomDecision.GetType().Name;
            if (RefusedTold.Add(type))
                Log.Info($"kingdom decisions on players' games: {type} proposed by '{kingdomDecision.ProposerClan?.StringId}' in "
                       + $"'{__instance?.StringId}' was made by this game's own AI; not added (the server's arrives through Coop; said once per type)");
            return false;
        }
        catch (Exception ex)
        {
            Log.Warn("kingdom decisions on players' games: check failed, leaving the decision to Coop: " + ex.GetBaseException().Message);
            return true;
        }
    }

    public static void NeedsPlayerResolutionPostfix(KingdomDecision __instance, ref bool __result)
    {
        // Read every frame by the map screen: the common case (false) costs one test.
        if (!__result || !ClientKingdomDecisionsPolicy.WaiveResolution(Active, __result)) return;
        __result = false;
        if (_waivedTold) return;
        _waivedTold = true;
        Log.Info($"kingdom decisions on players' games: {__instance.GetType().Name} in '{__instance.Kingdom?.StringId}' asked this player to settle it; "
               + "the server settles it, so the Kingdom screen is not forced open (said once)");
    }

    public static string Summary() => _refused == 0 && !_waivedTold ? ""
        : $"; kingdom decisions: AI proposals not added here {_refused}" + (_waivedTold ? ", settle demand waived" : "");
}
