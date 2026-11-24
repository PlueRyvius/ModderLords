using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using TaleWorlds.CampaignSystem;

namespace BellumCivileCoopCompat
{
    // Player agency, claim feuds (docs/BELLUM-COOP-COVERAGE.md, stage 3, A1): Bellum's feud questions to "the player",
    // put to the player they concern.
    //
    // On the server Bellum's "is this the player?" tests meet only the server's placeholder player, so a feud that
    // involves a player was settled by Bellum's AI on their behalf. Now:
    // - A petition (ResolvePetition) where a player is the claimant or the holder runs as that player: the ruler's ruling
    //   comes to them to accept or refuse (and fight). Unanswered: they accept.
    // - A petition in a realm a player rules runs as the ruler: Bellum's judgment list (rule for the claimant, for the
    //   holder, refuse to intervene, enforce the realm's peace) comes to them. Unanswered: Bellum's AI judges, as for a
    //   lord ruler, and that feud is not put to them again.
    // - A feud's call for supporters reaches each connected player in the realm Bellum would call (its own check).
    //   Unanswered: nothing (neither pledged nor refused).
    // - A lord's demand that a player surrender a title he claims comes to that player to submit or refuse. Unanswered:
    //   nothing (Bellum's "decide later"). An absent player is not asked, and the demand isn't made then.
    // A disconnected claimant, holder or ruler: as before, Bellum's AI answers.
    public static class Feuds
    {
        private const string Behavior = "BellumCivile.Behaviors.ClaimFeudBehavior";

        /// <summary>Feuds whose ruler judgment went unanswered: Bellum's AI judges them, the ruler isn't asked again.</summary>
        private static readonly HashSet<string> AiJudged = new HashSet<string>();

        [ThreadStatic] private static Hero _revocationPlayer;
        private static int _revocationRewrites, _supportCalls;
        private static MethodInfo _support, _resolve, _setState;
        private static FieldInfo _shownJudgments;
        private static object _petitionReady;

        public static string Summary() => _supportCalls > 0 ? "; feud support calls put to players " + _supportCalls : "";

        public static readonly List<Prompts.Site> Sites = new List<Prompts.Site>
        {
            // A petition: the claimant or holder (a player) answers the ruling; a player ruler judges.
            new Prompts.Site(Behavior, "ResolvePetition", (i, a) => PetitionPlayer(a, 1, 2, 3), Prompts.Fallback.Yes, "a ruling on a claim feud")
                { Unanswered = JudgeAsAi },
            // The same questions, still waiting from an earlier day.
            new Prompts.Site(Behavior, "TryShowPendingPlayerRulingInquiry", (i, a) => PartyPlayer(a.ElementAtOrDefault(1) as Clan, a.ElementAtOrDefault(2) as Clan),
                Prompts.Fallback.Yes, "a ruling on a claim feud", quietWhenAbsent: true),
            new Prompts.Site(Behavior, "TryShowPendingPlayerRulerJudgmentInquiry", (i, a) => RulerPlayer(a.ElementAtOrDefault(1), a.ElementAtOrDefault(2) as Clan, a.ElementAtOrDefault(3) as Clan),
                Prompts.Fallback.Nothing, "a claim feud to judge") { Unanswered = JudgeAsAi },
            // A lord demands that a player surrender a title he claims.
            new Prompts.Site(Behavior, "ShowPlayerRevocationDemandInquiry", (i, a) => _revocationPlayer, Prompts.Fallback.Nothing,
                "a demand to surrender a title", quietWhenAbsent: true),
        };

        public static void Install(Harmony harmony)
        {
            var missing = new List<string>();
            var behavior = AccessTools.TypeByName(Behavior);
            var record = AccessTools.TypeByName("BellumCivile.ClaimFeudRecord");
            var state = AccessTools.TypeByName("BellumCivile.ClaimFeudState");
            _resolve = behavior == null ? null : AccessTools.Method(behavior, "ResolvePetition");
            _support = behavior == null ? null : AccessTools.Method(behavior, "TryShowPlayerSupportInvitation");
            _shownJudgments = behavior == null ? null : AccessTools.Field(behavior, "_shownPlayerRulerJudgmentInquiries");
            _setState = record == null ? null : AccessTools.Method(record, "SetState");
            try { _petitionReady = state == null ? null : Enum.Parse(state, "PetitionReady"); }
            catch { _petitionReady = null; }
            if (_resolve == null || _setState == null || _petitionReady == null || _shownJudgments == null)
                missing.Add("the feud petition (ClaimFeudBehavior.ResolvePetition, ClaimFeudRecord.SetState, ClaimFeudState.PetitionReady)");

            Prompts.Sites.AddRange(Sites);

            if (_support == null)
                missing.Add("ClaimFeudBehavior.TryShowPlayerSupportInvitation");
            else
                harmony.Patch(_support, prefix: new HarmonyMethod(typeof(Feuds), nameof(SupportPrefix)) { priority = Priority.First });

            var revocation = behavior == null ? null : AccessTools.Method(behavior, "EvaluateClanForClaimedRevocation");
            if (revocation == null)
                missing.Add("ClaimFeudBehavior.EvaluateClanForClaimedRevocation");
            else
            {
                harmony.Patch(revocation, transpiler: new HarmonyMethod(typeof(Feuds), nameof(RevocationTranspiler)));
                if (_revocationRewrites != 1)
                    missing.Add("the player test in EvaluateClanForClaimedRevocation (found " + _revocationRewrites + ")");
            }

            SubModule.Log("Feuds: Bellum's claim feud rulings, judgments, calls and title demands go to the players they concern.");
            if (missing.Count > 0)
                SubModule.Log("Feuds: not found in this Bellum version: " + string.Join("; ", missing));
        }

        // ---- who is asked ----

        private static Hero PetitionPlayer(object[] args, int recordAt, int claimantAt, int holderAt)
        {
            var claimant = args.ElementAtOrDefault(claimantAt) as Clan;
            var holder = args.ElementAtOrDefault(holderAt) as Clan;
            return PartyPlayer(claimant, holder) ?? RulerPlayer(args.ElementAtOrDefault(recordAt), claimant, holder);
        }

        /// <summary>The claimant's or holder's leader when a player (connected first), else null.</summary>
        private static Hero PartyPlayer(Clan claimant, Clan holder)
        {
            var players = new[] { claimant?.Leader, holder?.Leader }.Where(Coop.IsPlayerHero).ToList();
            return players.FirstOrDefault(Prompts.CanAsk) ?? players.FirstOrDefault();
        }

        /// <summary>The realm's ruler when a player, unless that feud went to Bellum's AI after going unanswered.</summary>
        private static Hero RulerPlayer(object record, Clan claimant, Clan holder)
        {
            var ruler = (claimant?.Kingdom ?? holder?.Kingdom)?.RulingClan?.Leader;
            if (ruler == null || !Coop.IsPlayerHero(ruler))
                return null;
            var id = Id(record);
            lock (AiJudged) return id != null && AiJudged.Contains(id) ? null : ruler;
        }

        private static string Id(object record) => record == null ? null : AccessTools.Property(record.GetType(), "RecordId")?.GetValue(record, null) as string;

        // ---- a judgment nobody gave ----

        // Server: the ruler did not judge in time. Bellum's AI judges, as for a lord ruler: the petition goes back to
        // "ready", and Bellum's own ResolvePetition runs again, now not put to the ruler.
        private static void JudgeAsAi(object behavior, object[] args)
        {
            var record = args.ElementAtOrDefault(1);
            var id = Id(record);
            if (behavior == null || record == null || id == null || _resolve == null)
                return;
            lock (AiJudged) AiJudged.Add(id);
            try
            {
                (_shownJudgments?.GetValue(behavior) as HashSet<string>)?.Remove(id);
                _setState.Invoke(record, new[] { _petitionReady });
                var resolveArgs = args.Length == 5 ? args : new[] { args[0], args[1], args[2], args[3], args[4] };
                _resolve.Invoke(behavior, resolveArgs);
            }
            catch (Exception ex) { SubModule.Log("Feuds: Bellum could not judge " + id + " for the ruler: " + ex.GetBaseException().Message); }
        }

        // ---- the call for supporters ----

        // Server: Bellum's call for supporters, for the placeholder player, becomes the same call to each connected player
        // in the realm, on the next tick. As that player, Bellum's own method decides whether the call applies to them.
        private static bool SupportPrefix(object __instance, object[] __args)
        {
            if (!Coop.OnServer || AsPlayer.Current != null || __args == null || __args.Length < 6)
                return true;
            var claimant = __args[2] as Clan;
            var holder = __args[3] as Clan;
            var kingdom = __args[5] as Kingdom;
            if (kingdom == null)
                return false;
            var args = (object[])__args.Clone();
            foreach (var clan in kingdom.Clans.ToList())
            {
                var hero = clan.Leader;
                if (clan == claimant || clan == holder || clan == kingdom.RulingClan || !Prompts.CanAsk(hero))
                    continue;
                Prompts.Later(() =>
                {
                    if (!Prompts.CanAsk(hero) || hero.Clan?.Kingdom != kingdom)
                        return;
                    _supportCalls++;
                    Prompts.InFrame(hero, SupportCall, () => _support.Invoke(__instance, args));
                });
            }
            return false;   // the server's own placeholder player is never called
        }

        private static readonly Prompts.Site SupportCall = new Prompts.Site("", "", null, Prompts.Fallback.Nothing, "a call to support a claim feud");

        // ---- a demand to surrender a title ----

        // preview.HolderClan == Clan.PlayerClan  ->  preview.HolderClan == HolderClan(preview.HolderClan)
        private static IEnumerable<CodeInstruction> RevocationTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            var playerClan = AccessTools.PropertyGetter(typeof(Clan), nameof(Clan.PlayerClan));
            CodeInstruction previous = null;
            foreach (var code in instructions)
            {
                if (code.operand is MethodInfo called && called == playerClan
                    && previous?.operand is MethodInfo before && before.Name == "get_HolderClan" && before.ReturnType == typeof(Clan))
                {
                    _revocationRewrites++;
                    var dup = new CodeInstruction(OpCodes.Dup);
                    dup.labels.AddRange(code.labels);
                    dup.blocks.AddRange(code.blocks);
                    yield return dup;
                    yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(Feuds), nameof(HolderClan)));
                    previous = null;
                    continue;
                }
                previous = code;
                yield return code;
            }
        }

        /// <summary>The holder's clan itself when a player leads it (on the server), else the game's player clan.</summary>
        private static Clan HolderClan(Clan holder)
        {
            _revocationPlayer = null;
            if (Coop.OnServer && holder != null && Coop.IsPlayerHero(holder.Leader))
            {
                _revocationPlayer = holder.Leader;
                return holder;
            }
            return Clan.PlayerClan;
        }
    }
}
