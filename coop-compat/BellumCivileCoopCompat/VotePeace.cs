using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Election;

namespace BellumCivileCoopCompat
{
    // Server: a peace that comes out of a kingdom vote in a kingdom with players gets past Bellum's guard.
    //
    // Bellum's prefixes on MakePeaceAction refuse every peace between two ordinary kingdoms that
    // Bellum did not make itself. In single player the player's peace goes through Bellum's parley;
    // in co-op the parley is off (PeaceInCoop) and peace is the kingdom vote, which the server
    // applies. So the vote passed and was thrown away. Reported 2026-10-11: a players' kingdom voted
    // for peace and stayed at war until Bellum's war score forced a treaty.
    //
    // Bellum marks its own treaties with BellumPeaceResolutionContext, and its guard lets a marked
    // peace through after its claim-feud and civil-war checks, which still apply. The vote's outcome
    // is applied inside the same mark. Votes in kingdoms with no players are left to Bellum.
    internal static class VotePeace
    {
        private static FieldInfo _depth;
        private static MethodInfo _tooEarly;
        private static int _marked;

        public static string Summary() => _marked > 0 ? "; voted peace applied " + _marked : "";

        public static void Install(Harmony harmony)
        {
            var context = AccessTools.TypeByName(BellumPeaceTargets.TreatyMark);
            _depth = context == null ? null : AccessTools.Field(context, BellumPeaceTargets.TreatyMarkDepth);
            var target = AccessTools.Method(typeof(MakePeaceKingdomDecision), nameof(MakePeaceKingdomDecision.ApplyChosenOutcome));
            if (_depth == null || _depth.FieldType != typeof(int) || !_depth.IsStatic || target == null)
            {
                SubModule.Log("Bellum's treaty mark has changed shape; a peace voted in a players' kingdom may still be blocked.");
                return;
            }
            var minimum = AccessTools.TypeByName(BellumPeaceTargets.MinimumDuration);
            _tooEarly = minimum == null ? null : AccessTools.Method(minimum, BellumPeaceTargets.MinimumDurationCheck);
            if (_tooEarly == null || _tooEarly.GetParameters().Length != 5)
            {
                _tooEarly = null;
                SubModule.Log("Bellum's minimum war length check was not found; a voted peace is not held to it.");
            }
            harmony.Patch(target,
                prefix: new HarmonyMethod(typeof(VotePeace), nameof(Prefix)) { priority = Priority.First },
                finalizer: new HarmonyMethod(typeof(VotePeace), nameof(Finalizer)));
            SubModule.Log("Voted peace: on the server, a peace voted in a kingdom with players is applied as a Bellum treaty.");
        }

        private static void Prefix(MakePeaceKingdomDecision __instance, DecisionOutcome chosenOutcome, out bool __state)
        {
            __state = false;
            if (!Coop.OnServer)
                return;
            try
            {
                // A vote that stands in for a Bellum treaty applies nothing itself; Bellum applies its own terms afterwards.
                if (TreatyVote.OnOutcome(__instance, chosenOutcome))
                    return;
                if (!(chosenOutcome is MakePeaceKingdomDecision.MakePeaceDecisionOutcome outcome) || !outcome.ShouldPeaceBeDeclared)
                    return;
                var realm = __instance?.Kingdom;
                if (realm == null || !realm.Clans.Any(Coop.IsPlayerClan))
                    return;
                var other = __instance.FactionToMakePeaceWith;
                if (TooEarly(realm, other as Kingdom, out var reason))
                {
                    SubModule.Log("The peace vote of " + realm.StringId + " with " + other?.StringId + " is left to Bellum, which refuses it: " + reason);
                    TreatyVote.Tell(realm, "The vote for peace with " + other?.Name + " passed, but the war is too young to end: " + reason);
                    return;
                }
                _depth.SetValue(null, (int)_depth.GetValue(null) + 1);
                __state = true;
                _marked++;
                SubModule.Log("Applying the peace vote of " + realm.StringId + " with " + other?.StringId + " as a Bellum treaty.");
            }
            catch (Exception ex) { SubModule.Log("Could not mark a peace vote: " + ex.GetBaseException().Message); }
        }

        // Bellum's own rule that a war lasts a minimum number of days before peace, which its parley enforced.
        private static bool TooEarly(Kingdom realm, Kingdom other, out string reason)
        {
            reason = "";
            if (other == null || _tooEarly == null)
                return false;
            var args = new object[] { realm, other, null, 0, 0 };
            if (!(_tooEarly.Invoke(null, args) is true))
                return false;
            reason = args[2]?.ToString() ?? "";
            return true;
        }

        private static void Finalizer(bool __state)
        {
            if (!__state)
                return;
            try { _depth.SetValue(null, Math.Max(0, (int)_depth.GetValue(null) - 1)); }
            catch (Exception ex) { SubModule.Log("Could not clear the treaty mark: " + ex.GetBaseException().Message); }
        }
    }
}
