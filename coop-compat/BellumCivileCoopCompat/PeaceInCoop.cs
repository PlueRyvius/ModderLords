using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace BellumCivileCoopCompat
{
    // Players' PCs: Bellum's Peace Parley is off and vanilla's peace vote is left alone.
    //
    // Peace in co-op is the kingdom vote: Coop runs it as a timed round, counts each player's vote
    // on the server and applies the result there. Bellum retires the vanilla vote and opens its own
    // parley, and Sign Treaty ends in MakePeaceAction, which Coop refuses from a player's PC. So the
    // treaty was signed on that one PC and nowhere else. Reported 2026-10-11 three ways: a forced
    // parley nobody could leave, a war that stayed on after the treaty was signed twice, and the
    // kingdom screen opening on the same peace vote over and over.
    //
    // What players lose: drafting a treaty's terms themselves. The server's Bellum is untouched; no
    // kingdom there is "the player's". VotePeace and TreatyVote complete this on the server.
    internal static class PeaceInCoop
    {
        private static bool _told;
        private static int _answered;

        public static string Summary() => _answered > 0 ? "; parley steps answered " + _answered : "";

        public static void Install(Harmony harmony)
        {
            var missing = new List<string>();
            var patched = 0;
            foreach (var target in BellumPeaceTargets.All)
            {
                var type = AccessTools.TypeByName(target.Type);
                var methods = type == null
                    ? new List<MethodInfo>()
                    : type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                        .Where(m => m.Name == target.Method && Fits(m, target.Answer)).ToList();
                if (methods.Count == 0)
                {
                    missing.Add(Short(target));
                    continue;
                }
                foreach (var method in methods)
                {
                    try
                    {
                        harmony.Patch(method, prefix: new HarmonyMethod(typeof(PeaceInCoop), PrefixFor(target.Answer)) { priority = Priority.First });
                        patched++;
                    }
                    catch (Exception ex) { missing.Add(Short(target) + " (" + ex.GetBaseException().Message + ")"); }
                }
            }
            SubModule.Log("Peace: on a player's PC peace goes through the kingdom vote; Bellum's parley is off (" + patched + " methods).");
            if (missing.Count > 0)
                SubModule.Log("Not found in this Bellum version, so its parley may still open: " + string.Join(", ", missing));
        }

        private static bool Fits(MethodInfo method, BellumPeaceAnswer answer) =>
            answer == BellumPeaceAnswer.Nothing ? method.ReturnType == typeof(void) : method.ReturnType == typeof(bool);

        private static string PrefixFor(BellumPeaceAnswer answer) =>
            answer == BellumPeaceAnswer.No ? nameof(No) : answer == BellumPeaceAnswer.RunOriginal ? nameof(RunOriginal) : nameof(Nothing);

        private static string Short(BellumPeaceTargets.Target target) => target.Type.Substring(target.Type.LastIndexOf('.') + 1) + "." + target.Method;

        private static bool No(ref bool __result)
        {
            if (!Coop.OnPlayerPc)
                return true;
            __result = false;
            _answered++;
            if (!_told)
            {
                _told = true;
                SubModule.Tell("Bellum Civile's peace parley isn't available in co-op. Peace for your realm is decided by the kingdom vote.");
                SubModule.Log("Bellum tried to open a peace parley on this player's PC; answered no (said once).");
            }
            return false;
        }

        private static bool RunOriginal(ref bool __result)
        {
            if (!Coop.OnPlayerPc)
                return true;
            __result = true;
            _answered++;
            return false;
        }

        private static bool Nothing()
        {
            if (!Coop.OnPlayerPc)
                return true;
            _answered++;
            return false;
        }
    }
}
