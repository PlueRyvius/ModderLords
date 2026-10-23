using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Election;

namespace BellumCivileCoopCompat
{
    // Server: a Bellum treaty for a realm a connected player rules is put to that realm as a kingdom vote.
    //
    // On a dedicated server no kingdom is "the player's", so Bellum ratifies every treaty with its AI
    // councils. A player's clan gets no council seat, so a realm of players refuses every draft; a
    // host's logs of 2026-10-11 show one refused about once a game day until the war score forced
    // terms. In single player Bellum would have opened its parley for the ruler, which PeaceInCoop
    // turns off on players' PCs.
    //
    // So when the server's Bellum is about to ratify a treaty that is not forced, and a connected
    // player rules one of its realms, ratification is held. That realm gets a vanilla peace vote,
    // through Coop's own voting round, with the treaty's tribute on it. The vote applies nothing
    // itself: its result stands in for that realm's council, and Bellum then applies or rejects its
    // own treaty with all its terms. After a refusal or no answer the realm is not asked again about
    // that war for AskAgainDays days. No messages of its own: the vote is vanilla, and Coop syncs it.
    //
    // Left to Bellum: forced treaties (total defeat), realms a lord rules, and realms whose ruling
    // player is not connected.
    internal static class TreatyVote
    {
        public const int AskAgainDays = 14;

        private static readonly TimeSpan GiveUpAfter = TimeSpan.FromMinutes(5);

        private enum Answer { Waiting, Accept, Refuse, NoAnswer }

        private sealed class Side
        {
            public Side(Held held, Kingdom realm, Kingdom other) { Held = held; Realm = realm; Other = other; }
            public Held Held { get; }
            public Kingdom Realm { get; }
            public Kingdom Other { get; }
            public MakePeaceKingdomDecision Vote { get; set; }
            public Answer Answer { get; set; }
        }

        private sealed class Held
        {
            public Held(object behavior, object war, object proposal, string id, string warKey)
            { Behavior = behavior; War = war; Proposal = proposal; Id = id; WarKey = warKey; }
            public object Behavior { get; }
            public object War { get; }
            public object Proposal { get; }
            public string Id { get; }
            public string WarKey { get; }
            public List<Side> Sides { get; } = new List<Side>();
            public DateTime OpenedUtc { get; } = DateTime.UtcNow;
        }

        private static readonly Dictionary<string, Held> HeldById = new Dictionary<string, Held>();
        private static readonly Dictionary<KingdomDecision, Side> ByVote = new Dictionary<KingdomDecision, Side>();
        private static readonly HashSet<KingdomDecision> RulerSilent = new HashSet<KingdomDecision>();
        private static readonly Dictionary<string, double> AskAgainAfter = new Dictionary<string, double>();

        private static bool _installed;
        private static MethodInfo _resolve, _summary;
        private static Held _resolving;
        private static int _asked, _answered;

        public static string Summary() => _asked > 0 ? "; treaties put to players " + _asked + ", answered " + _answered : "";

        public static void Install(Harmony harmony)
        {
            var treaty = AccessTools.TypeByName(BellumPeaceTargets.Treaty);
            _resolve = AccessTools.Method(treaty, BellumPeaceTargets.Resolve);
            var ratify = AccessTools.Method(treaty, BellumPeaceTargets.Ratify);
            var apply = AccessTools.Method(treaty, BellumPeaceTargets.Apply);
            _summary = AccessTools.Method(treaty, BellumPeaceTargets.Summary);
            var missing = Missing(_resolve, BellumPeaceTargets.ResolveParameters) ?? Missing(ratify, BellumPeaceTargets.RatifyParameters);
            if (missing != null)
            {
                SubModule.Log("Treaty vote: " + missing + "; Bellum's councils still decide treaties for realms players rule.");
                return;
            }
            harmony.Patch(_resolve, prefix: new HarmonyMethod(typeof(TreatyVote), nameof(ResolvePrefix)));
            harmony.Patch(ratify, postfix: new HarmonyMethod(typeof(TreatyVote), nameof(RatifyPostfix)));
            if (Missing(apply, BellumPeaceTargets.ApplyParameters) == null)
                harmony.Patch(apply, postfix: new HarmonyMethod(typeof(TreatyVote), nameof(ApplyPostfix)));
            _installed = true;
            SubModule.Log("Treaty vote: on the server, a treaty for a realm a connected player rules is put to that realm as a kingdom vote.");
        }

        private static string Missing(MethodInfo method, IReadOnlyList<string> parameters)
        {
            if (method == null)
                return "a Bellum method is not in this version";
            var names = method.GetParameters().Select(p => p.Name).ToList();
            var lost = parameters.Where(p => !names.Contains(p)).ToList();
            return lost.Count == 0 ? null : method.Name + " no longer has " + string.Join(", ", lost);
        }

        // ---- Bellum is about to ratify a pending treaty ----

        private static bool ResolvePrefix(object __instance, object war, object proposal)
        {
            if (!Coop.OnServer || _resolving != null)
                return true;   // single player, a player's PC, or our own call with the players' answers in hand
            try
            {
                if (proposal == null || war == null)
                    return true;
                var id = Text(proposal, "ProposalId");
                if (id.Length == 0)
                    return true;
                if (HeldById.ContainsKey(id))
                    return false;   // the realm is still voting
                if (Flag(proposal, "IsForced") || Get(proposal, "State")?.ToString() != BellumPeaceTargets.PendingState)
                    return true;

                var winner = KingdomById(Text(proposal, "WinnerKingdomId"));
                var loser = KingdomById(Text(proposal, "LoserKingdomId"));
                if (winner == null || loser == null || winner == loser || !winner.IsAtWarWith(loser))
                    return true;

                var held = new Held(__instance, war, proposal, id, Text(proposal, "WarKey"));
                foreach (var pair in new[] { (winner, loser), (loser, winner) })
                {
                    if (!RulerIsConnected(pair.Item1) || Today() < AskAgain(held.WarKey, pair.Item1))
                        continue;
                    var side = new Side(held, pair.Item1, pair.Item2);
                    if (OpenVote(side))
                        held.Sides.Add(side);
                }
                if (held.Sides.Count == 0)
                    return true;

                HeldById[id] = held;
                _asked++;
                var terms = Terms(proposal);
                foreach (var side in held.Sides)
                {
                    Tell(side.Realm, "Peace terms are before your realm in the war with " + side.Other.Name + ". " + terms
                        + " Vote on them in the kingdom screen now. If your ruler doesn't answer, the realm's council decides.");
                    SubModule.Log("Treaty " + id + " for war " + held.WarKey + " put to " + side.Realm.StringId + "; " + terms);
                }
                return false;
            }
            catch (Exception ex)
            {
                SubModule.Log("Could not put a treaty to its players, Bellum's councils decide it: " + ex.GetBaseException().Message);
                return true;
            }
        }

        // A vanilla peace decision that applies nothing (applyResults off), marked as an offer so
        // Coop keeps it pending for the round instead of letting the proposer reconsider it.
        private static bool OpenVote(Side side)
        {
            var tribute = BellumPeaceTargets.TributePaidBy(side.Realm.StringId, Tributes(side.Held.Proposal));
            var vote = new MakePeaceKingdomDecision(side.Realm.RulingClan, side.Other, tribute.Daily, tribute.Days, applyResults: false, isProposedByOpponent: true);
            side.Vote = vote;
            ByVote[vote] = side;
            side.Realm.AddDecision(vote, true);
            if (side.Realm.UnresolvedDecisions.Contains(vote) && side.Answer == Answer.Waiting)
                return true;
            // Coop found nobody to ask and resolved it on the spot, or refused it: not the players' answer.
            ByVote.Remove(vote);
            RulerSilent.Remove(vote);
            return false;
        }

        // ---- the vote's result ----

        public static bool OnDeadline(KingdomDecision decision, bool rulerVoted)
        {
            if (decision == null || !ByVote.ContainsKey(decision))
                return false;
            if (!rulerVoted)
                RulerSilent.Add(decision);
            return true;
        }

        public static bool OnOutcome(MakePeaceKingdomDecision decision, DecisionOutcome outcome)
        {
            if (decision == null || !ByVote.TryGetValue(decision, out var side))
                return false;
            side.Answer = RulerSilent.Remove(decision) ? Answer.NoAnswer
                : outcome is MakePeaceKingdomDecision.MakePeaceDecisionOutcome peace && peace.ShouldPeaceBeDeclared ? Answer.Accept
                : Answer.Refuse;
            return true;
        }

        // Every 3 seconds: once every realm asked has answered, Bellum ratifies with those answers.
        public static void Tick()
        {
            if (!_installed || HeldById.Count == 0)
                return;
            foreach (var held in HeldById.Values.ToList())
            {
                // Coop's round lasts a minute; a vote still open long after that is not going to be answered.
                var overdue = DateTime.UtcNow - held.OpenedUtc > GiveUpAfter;
                foreach (var side in held.Sides)
                    if (side.Answer == Answer.Waiting && (overdue || side.Vote == null || !side.Realm.UnresolvedDecisions.Contains(side.Vote)))
                        side.Answer = Answer.NoAnswer;   // cancelled or expired without an outcome
                if (held.Sides.All(s => s.Answer != Answer.Waiting))
                    Finish(held);
            }
        }

        private static void Finish(Held held)
        {
            HeldById.Remove(held.Id);
            foreach (var side in held.Sides)
            {
                if (side.Vote != null) { ByVote.Remove(side.Vote); RulerSilent.Remove(side.Vote); }
                if (side.Answer != Answer.Accept) AskAgainAfter[Key(held.WarKey, side.Realm)] = Today() + AskAgainDays;
                if (side.Answer != Answer.NoAnswer) _answered++;
            }
            _resolving = held;
            try { _resolve.Invoke(held.Behavior, new[] { held.War, held.Proposal, (object)false }); }
            catch (Exception ex) { SubModule.Log("Bellum could not ratify treaty " + held.Id + ": " + ex.GetBaseException().Message); }
            finally { _resolving = null; }

            var state = Get(held.Proposal, "State")?.ToString() ?? "?";
            SubModule.Log("Treaty " + held.Id + " for war " + held.WarKey + ": "
                + string.Join(", ", held.Sides.Select(s => s.Realm.StringId + " " + s.Answer)) + "; Bellum's result: " + state);
            if (state == "Applied")
                return;   // ApplyPostfix has told both realms the terms
            // Bellum left it pending (it does when it cannot prepare the draft): don't ask the same realms again tomorrow.
            if (state == BellumPeaceTargets.PendingState)
                foreach (var side in held.Sides)
                    AskAgainAfter[Key(held.WarKey, side.Realm)] = Today() + AskAgainDays;
            foreach (var side in held.Sides)
                Tell(side.Realm,
                    side.Answer == Answer.Refuse ? "Your realm refused the peace terms. The war with " + side.Other.Name + " goes on."
                    : side.Answer == Answer.Accept ? "Your realm accepted the peace terms, but they weren't ratified. The war with " + side.Other.Name + " goes on."
                    : "Your ruler didn't answer the peace terms, so the realm's council decided. The war with " + side.Other.Name + " goes on.");
        }

        // The councils have spoken; a realm that voted is answered for by its vote instead.
        private static void RatifyPostfix(object proposal, Kingdom winner, Kingdom loser,
            ref bool winnerAccepts, ref bool loserAccepts, ref bool winnerUsesOverride, ref bool loserUsesOverride)
        {
            var held = _resolving;
            if (held == null || !ReferenceEquals(held.Proposal, proposal))
                return;
            foreach (var side in held.Sides)
            {
                if (side.Answer != Answer.Accept && side.Answer != Answer.Refuse)
                    continue;
                var accepts = side.Answer == Answer.Accept;
                if (side.Realm == winner) { winnerAccepts = accepts; winnerUsesOverride = false; }
                else if (side.Realm == loser) { loserAccepts = accepts; loserUsesOverride = false; }
            }
        }

        // Any Bellum treaty that is applied: both realms' players are told its terms, which Bellum only shows on the server.
        private static void ApplyPostfix(object proposal, Kingdom winner, Kingdom loser, bool __result)
        {
            if (!__result || !Coop.OnServer)
                return;
            try
            {
                var terms = Terms(proposal);
                Tell(winner, "A treaty ends the war with " + loser?.Name + ". " + terms);
                Tell(loser, "A treaty ends the war with " + winner?.Name + ". " + terms);
            }
            catch (Exception ex) { SubModule.Log("Could not tell players a treaty's terms: " + ex.GetBaseException().Message); }
        }

        // ---- reading Bellum's records ----

        private static object Get(object target, string member) => AccessTools.Property(target.GetType(), member)?.GetValue(target, null);
        private static string Text(object target, string member) => Get(target, member) as string ?? "";
        private static bool Flag(object target, string member) => Get(target, member) is true;
        private static int Number(object target, string member) => Get(target, member) is int n ? n : 0;

        private static IEnumerable<object> TermsOf(object proposal) =>
            (Get(proposal, "Terms") as IEnumerable)?.Cast<object>().Where(t => t != null) ?? Enumerable.Empty<object>();

        private static IEnumerable<BellumPeaceTargets.Tribute> Tributes(object proposal) =>
            TermsOf(proposal).Where(t => Get(t, "Type")?.ToString() == BellumPeaceTargets.TributeTerm)
                .Select(t => new BellumPeaceTargets.Tribute(Text(t, "FromKingdomId"), Text(t, "ToKingdomId"), Number(t, "DailyGold"), Number(t, "DurationDays")));

        // Bellum's own sentence for the terms, with its "agreed" opening turned into "proposed" when it is the English one.
        private static string Terms(object proposal)
        {
            try
            {
                if (TermsOf(proposal).Any(t => Get(t, "Type")?.ToString() == BellumPeaceTargets.WhitePeaceTerm))
                    return "Terms: white peace; nothing changes hands.";
                var text = _summary?.Invoke(null, new[] { proposal, null })?.ToString();
                if (string.IsNullOrWhiteSpace(text))
                    return "The terms could not be listed.";
                return text.StartsWith("Agreed terms:", StringComparison.Ordinal) ? "Terms:" + text.Substring("Agreed terms:".Length) : text;
            }
            catch (Exception ex) { return "The terms could not be listed (" + ex.GetBaseException().Message + ")."; }
        }

        private static Kingdom KingdomById(string id) => id.Length == 0 ? null : Kingdom.All.FirstOrDefault(k => k != null && k.StringId == id);
        private static double Today() => CampaignTime.Now.ToDays;
        private static string Key(string warKey, Kingdom realm) => warKey + "|" + realm.StringId;
        private static double AskAgain(string warKey, Kingdom realm) => AskAgainAfter.TryGetValue(Key(warKey, realm), out var day) ? day : double.MinValue;

        private static bool RulerIsConnected(Kingdom realm)
        {
            if (realm.RulingClan == null || realm.IsEliminated)
                return false;
            try { return realm.RulingClan.Heroes.Any(Coop.IsConnectedPlayer); }
            catch { return false; }
        }

        // Only a launcher that can reach players fills Hooks.TellRealm; without one nobody is told.
        internal static void Tell(Kingdom realm, string text)
        {
            if (realm == null || Hooks.TellRealm == null)
                return;
            try { Hooks.TellRealm(realm, text); }
            catch (Exception ex) { SubModule.Log("The launcher could not tell " + realm.StringId + ": " + ex.GetBaseException().Message); }
        }
    }
}
