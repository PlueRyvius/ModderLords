using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using TaleWorlds.CampaignSystem;

namespace BellumCivileCoopCompat
{
    // Server: Bellum's pre-check of an automatic treaty leaves a player's realm to its player, as in single player.
    //
    // Before Bellum puts a treaty forward (ForeignTreatyBehavior.CanSubmitAutomaticDraft), it asks whether each realm's
    // council would accept it, except the player's own realm, which only needs a draft within the war score's budget:
    // the player decides in the parley. On a dedicated server "the player's realm" is the placeholder's, so a realm a
    // player rules was judged as a lord's: its "council" was the player's own hero, assessed by Bellum's AI. A treaty
    // that hero would refuse (a losing war it would rather fight on, a tribute that would burden the house) never reached
    // the player, and TreatyVote never put it to their vote.
    //
    // Here, in that one check, a realm a connected player rules counts as the player's. The draft then goes forward, and
    // TreatyVote puts it to the realm as a kingdom vote: the player decides, as in single player.
    internal static class TreatyDraftGate
    {
        public const string Check = "CanSubmitAutomaticDraft";

        private static readonly MethodInfo PlayerClanGetter = AccessTools.PropertyGetter(typeof(Clan), nameof(Clan.PlayerClan));
        private static readonly MethodInfo RulingClanGetter = AccessTools.PropertyGetter(typeof(Kingdom), nameof(Kingdom.RulingClan));
        private static int _replaced, _asked;

        public static int Asked => _asked;

        public static void Install(Harmony harmony)
        {
            var treaty = AccessTools.TypeByName(BellumPeaceTargets.Treaty);
            // The check's own body and the local function the compiler made for its per-realm test.
            var methods = treaty == null ? new List<MethodInfo>()
                : new[] { treaty }.Concat(treaty.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
                    .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                    .Where(m => m.Name.Contains(Check) && !m.ContainsGenericParameters && m.GetMethodBody() != null)
                    .ToList();
            var total = 0;
            foreach (var method in methods)
            {
                _replaced = 0;
                harmony.Patch(method, transpiler: new HarmonyMethod(typeof(TreatyDraftGate), nameof(Transpiler)));
                total += _replaced;
            }
            if (total == 0)
                SubModule.Log("Treaty draft gate: Bellum's pre-check of a treaty has changed shape (" + methods.Count + " methods, no player test found); a treaty a player's hero would refuse may never reach their vote.");
            else
                SubModule.Log("Treaty draft gate: on the server, a treaty for a realm a connected player rules goes forward to that player (" + total + " checks).");
        }

        // realm.RulingClan != Clan.PlayerClan  ->  realm.RulingClan != PlayerClanFor(realm.RulingClan)
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            CodeInstruction previous = null;
            foreach (var code in instructions)
            {
                if (code.operand is MethodInfo called && called == PlayerClanGetter
                    && previous?.operand is MethodInfo before && before == RulingClanGetter)
                {
                    _replaced++;
                    var dup = new CodeInstruction(OpCodes.Dup);
                    dup.labels.AddRange(code.labels);
                    dup.blocks.AddRange(code.blocks);
                    yield return dup;
                    yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(TreatyDraftGate), nameof(PlayerClanFor)));
                    previous = null;
                    continue;
                }
                previous = code;
                yield return code;
            }
        }

        /// <summary>The ruling clan itself when a connected player leads it (on the server), else the game's player clan.</summary>
        private static Clan PlayerClanFor(Clan ruling)
        {
            if (Coop.OnServer && ruling != null && Coop.IsConnectedPlayer(ruling.Leader))
            {
                _asked++;
                return ruling;
            }
            return Clan.PlayerClan;
        }
    }
}
