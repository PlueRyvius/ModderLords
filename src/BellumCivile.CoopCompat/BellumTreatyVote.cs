using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using GameInterface;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.Players;
using HarmonyLib;
using ModderLords.CompatSync;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Election;
using TaleWorlds.Library;

namespace ModderLords.CompatSync.Coop;

/// <summary>
/// Server only: a Bellum Civile treaty for a realm a player rules is put to that realm as a kingdom vote.
///
/// <para>On a dedicated server no kingdom is "the player's", so Bellum ratifies every negotiated treaty with its AI
/// councils. A player's clan is a minor faction and gets no council seat, so a realm of players has an empty council
/// and refuses every draft; a host's logs of 2026-10-11 show one refused about once a game day until the war score
/// reached 100 and Bellum forced terms. In single player Bellum would have opened its Peace Parley for the ruler,
/// which <see cref="BellumPeaceInCoop"/> switches off on a player's game because nothing signed there reaches the
/// server.</para>
///
/// <para>Here, when the server's Bellum is about to ratify a treaty that is not forced and the ruler of one of its
/// realms is a connected player, ratification is held. That realm gets a peace vote through Coop's own voting round,
/// with the treaty's tribute on it and Bellum's wording of the terms sent to its players as a message. The vote
/// applies nothing itself: its result stands in for that realm's council, and Bellum then applies or rejects its own
/// treaty with all its terms. If the ruler does not answer, Bellum's council decides, as it would for a lord's realm.
/// After a refusal or no answer the realm is not asked again about that war for <see cref="AskAgainDays"/> days.</para>
///
/// <para>Left to Bellum as before: forced treaties (total defeat), realms a lord rules, and realms whose ruling player
/// is not connected. Players of either realm are told the terms of any Bellum treaty that is applied.</para>
/// </summary>
public static class BellumTreatyVote
{
    /// <summary>Days before a realm that refused, or did not answer, is asked again about the same war.</summary>
    public const int AskAgainDays = 14;

    private static readonly TimeSpan GiveUpAfter = TimeSpan.FromMinutes(5);

    private enum Answer { Waiting, Accept, Refuse, NoAnswer }

    private sealed class Side
    {
        public Side(Held held, Kingdom realm, Kingdom other) { Held = held; Realm = realm; Other = other; }
        public Held Held { get; }
        public Kingdom Realm { get; }
        public Kingdom Other { get; }
        public MakePeaceKingdomDecision? Vote { get; set; }
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

    private static readonly Harmony Harmony = new Harmony("ModderLords.Compat.BellumTreatyVote");
    private static readonly Dictionary<string, Held> HeldById = new Dictionary<string, Held>();
    private static readonly Dictionary<KingdomDecision, Side> ByVote = new Dictionary<KingdomDecision, Side>();
    private static readonly HashSet<KingdomDecision> RulerSilent = new HashSet<KingdomDecision>();
    private static readonly Dictionary<string, double> AskAgainAfter = new Dictionary<string, double>();

    private static bool _installTried, _installed;
    private static MethodInfo? _resolve, _summary;
    private static Held? _resolving;
    private static int _asked, _answered;

    /// <summary>True once the patches are in; the peace vote's outcome and deadline are then offered to this class first.</summary>
    public static bool Installed => _installed;

    public static string Summary() => _asked > 0 ? $"; Bellum treaties put to players {_asked}, answered {_answered}" : "";

    /// <summary>Patches Bellum's ratification once it is loaded, on the server. Safe to call every tick.</summary>
    public static void EnsureInstalled()
    {
        if (_installTried) return;
        _installTried = true;
        if (!Operations.OperationProcessSide.IsServer) return;
        var treaty = AccessTools.TypeByName(BellumPeaceTargets.Treaty);
        if (treaty == null) return;   // Bellum is not loaded
        try
        {
            _resolve = AccessTools.Method(treaty, BellumPeaceTargets.Resolve);
            var ratify = AccessTools.Method(treaty, BellumPeaceTargets.Ratify);
            var apply = AccessTools.Method(treaty, BellumPeaceTargets.Apply);
            _summary = AccessTools.Method(treaty, BellumPeaceTargets.Summary);
            var missing = Missing(_resolve, BellumPeaceTargets.ResolveParameters) ?? Missing(ratify, BellumPeaceTargets.RatifyParameters);
            if (missing != null)
            {
                Log.Warn("Bellum treaty vote: " + missing + "; Bellum's councils still decide treaties for realms players rule");
                return;
            }
            Harmony.Patch(_resolve, prefix: new HarmonyMethod(typeof(BellumTreatyVote), nameof(ResolvePrefix)));
            Harmony.Patch(ratify, postfix: new HarmonyMethod(typeof(BellumTreatyVote), nameof(RatifyPostfix)));
            if (Missing(apply, BellumPeaceTargets.ApplyParameters) == null)
                Harmony.Patch(apply, postfix: new HarmonyMethod(typeof(BellumTreatyVote), nameof(ApplyPostfix)));
            _installed = true;
            Log.Info("Bellum treaty vote: a treaty for a realm a connected player rules is put to that realm as a kingdom vote; "
                + "Bellum applies its own terms on the result");
        }
        catch (Exception ex) { Log.Warn("Bellum treaty vote not installed: " + ex.GetBaseException().Message); }
    }

    private static string? Missing(MethodInfo? method, IReadOnlyList<string> parameters)
    {
        if (method == null) return "a Bellum method is not in this version";
        var names = method.GetParameters().Select(p => p.Name).ToList();
        var lost = parameters.Where(p => !names.Contains(p)).ToList();
        return lost.Count == 0 ? null : method.Name + " no longer has " + string.Join(", ", lost);
    }

    // ---- Bellum is about to ratify a pending treaty -----------------------------------------------------

    private static bool ResolvePrefix(object __instance, object war, object proposal)
    {
        if (_resolving != null) return true;   // our own call, with the players' answers in hand
        try
        {
            if (proposal == null || war == null) return true;
            var id = Text(proposal, "ProposalId");
            if (id.Length == 0) return true;
            if (HeldById.ContainsKey(id)) return false;   // the realm is still voting
            if (Flag(proposal, "IsForced") || Get(proposal, "State")?.ToString() != BellumPeaceTargets.PendingState) return true;

            var winner = KingdomById(Text(proposal, "WinnerKingdomId"));
            var loser = KingdomById(Text(proposal, "LoserKingdomId"));
            if (winner == null || loser == null || winner == loser || !winner.IsAtWarWith(loser)) return true;

            var held = new Held(__instance, war, proposal, id, Text(proposal, "WarKey"));
            foreach (var (realm, other) in new[] { (winner, loser), (loser, winner) })
            {
                if (!RulerIsConnected(realm) || Today() < AskAgain(held.WarKey, realm)) continue;
                var side = new Side(held, realm, other);
                if (OpenVote(side)) held.Sides.Add(side);
            }
            if (held.Sides.Count == 0) return true;

            HeldById[id] = held;
            _asked++;
            var terms = Terms(proposal);
            foreach (var side in held.Sides)
            {
                Tell(side.Realm, $"Peace terms are before your realm in the war with {side.Other.Name}. {terms} "
                    + "Vote on them in the kingdom screen now. If your ruler does not answer, the realm's council decides.");
                Log.Info($"Bellum treaty vote: treaty {id} for war {held.WarKey} put to {side.Realm.StringId}; {terms}");
            }
            return false;
        }
        catch (Exception ex)
        {
            Log.Warn("Bellum treaty vote: could not put a treaty to its players, Bellum's councils decide it: " + ex.GetBaseException().Message);
            return true;
        }
    }

    /// <summary>
    /// The vote is a vanilla peace decision that applies nothing (<c>applyResults</c> off), marked as an offer so Coop
    /// keeps it pending for the round instead of letting the proposer reconsider it.
    /// </summary>
    private static bool OpenVote(Side side)
    {
        var (daily, days) = BellumPeaceTargets.TributePaidBy(side.Realm.StringId, Tributes(side.Held.Proposal));
        var vote = new MakePeaceKingdomDecision(side.Realm.RulingClan, side.Other, daily, days, applyResults: false, isProposedByOpponent: true);
        side.Vote = vote;
        ByVote[vote] = side;
        side.Realm.AddDecision(vote, true);
        if (side.Realm.UnresolvedDecisions.Contains(vote) && side.Answer == Answer.Waiting) return true;
        // Coop found nobody to ask and resolved it on the spot, or refused it: not the players' answer.
        ByVote.Remove(vote);
        RulerSilent.Remove(vote);
        return false;
    }

    // ---- the vote's result -------------------------------------------------------------------------------

    /// <summary>
    /// Coop's round ran out for this vote. True when it is one of ours: the layer's usual "the AI votes for a player who
    /// did not" is then skipped, because for a treaty the fallback is Bellum's council, not vanilla's peace score.
    /// </summary>
    public static bool OnDeadline(KingdomDecision decision, bool rulerVoted)
    {
        if (decision == null || !ByVote.ContainsKey(decision)) return false;
        if (!rulerVoted) RulerSilent.Add(decision);
        return true;
    }

    /// <summary>The peace vote's outcome is being applied. True when it is one of ours, which applies nothing itself.</summary>
    public static bool OnOutcome(MakePeaceKingdomDecision decision, DecisionOutcome? outcome)
    {
        if (decision == null || !ByVote.TryGetValue(decision, out var side)) return false;
        side.Answer = RulerSilent.Remove(decision) ? Answer.NoAnswer
            : outcome is MakePeaceKingdomDecision.MakePeaceDecisionOutcome { ShouldPeaceBeDeclared: true } ? Answer.Accept
            : Answer.Refuse;
        return true;
    }

    /// <summary>Server, every 3 s: once every realm asked has answered, Bellum ratifies with those answers.</summary>
    public static void ServerTick()
    {
        if (!_installed || HeldById.Count == 0) return;
        foreach (var held in HeldById.Values.ToList())
        {
            // Coop's round lasts a minute; a vote still open long after that is not going to be answered.
            var overdue = DateTime.UtcNow - held.OpenedUtc > GiveUpAfter;
            foreach (var side in held.Sides)
                if (side.Answer == Answer.Waiting && (overdue || side.Vote == null || !side.Realm.UnresolvedDecisions.Contains(side.Vote)))
                    side.Answer = Answer.NoAnswer;   // cancelled or expired without an outcome
            if (held.Sides.All(s => s.Answer != Answer.Waiting)) Finish(held);
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
        try { _resolve!.Invoke(held.Behavior, new[] { held.War, held.Proposal, (object)false }); }
        catch (Exception ex) { Log.Warn("Bellum treaty vote: Bellum could not ratify treaty " + held.Id + ": " + ex.GetBaseException().Message); }
        finally { _resolving = null; }

        var state = Get(held.Proposal, "State")?.ToString() ?? "?";
        Log.Info($"Bellum treaty vote: treaty {held.Id} for war {held.WarKey}: "
            + string.Join(", ", held.Sides.Select(s => s.Realm.StringId + " " + s.Answer)) + "; Bellum's result: " + state);
        if (state == "Applied") return;   // ApplyPostfix has told both realms the terms
        // Bellum left it pending (it does when it cannot prepare the draft): do not ask the same realms again tomorrow.
        if (state == BellumPeaceTargets.PendingState)
            foreach (var side in held.Sides) AskAgainAfter[Key(held.WarKey, side.Realm)] = Today() + AskAgainDays;
        foreach (var side in held.Sides)
            Tell(side.Realm, side.Answer switch
            {
                Answer.Refuse => $"Your realm refused the peace terms. The war with {side.Other.Name} goes on.",
                Answer.Accept => $"Your realm accepted the peace terms, but they were not ratified. The war with {side.Other.Name} goes on.",
                _ => $"Your ruler did not answer the peace terms, so the realm's council decided. The war with {side.Other.Name} goes on.",
            });
    }

    /// <summary>The councils have spoken; a realm that voted is answered for by its vote instead.</summary>
    private static void RatifyPostfix(object proposal, Kingdom winner, Kingdom loser,
        ref bool winnerAccepts, ref bool loserAccepts, ref bool winnerUsesOverride, ref bool loserUsesOverride)
    {
        var held = _resolving;
        if (held == null || !ReferenceEquals(held.Proposal, proposal)) return;
        foreach (var side in held.Sides)
        {
            if (side.Answer != Answer.Accept && side.Answer != Answer.Refuse) continue;
            var accepts = side.Answer == Answer.Accept;
            if (side.Realm == winner) { winnerAccepts = accepts; winnerUsesOverride = false; }
            else if (side.Realm == loser) { loserAccepts = accepts; loserUsesOverride = false; }
        }
    }

    /// <summary>Any Bellum treaty that is applied: the players of both realms are told its terms, which Bellum only shows on the server.</summary>
    private static void ApplyPostfix(object proposal, Kingdom winner, Kingdom loser, bool __result)
    {
        if (!__result) return;
        try
        {
            var terms = Terms(proposal);
            Tell(winner, $"A treaty ends the war with {loser?.Name}. {terms}");
            Tell(loser, $"A treaty ends the war with {winner?.Name}. {terms}");
        }
        catch (Exception ex) { Log.Warn("Bellum treaty vote: could not tell players a treaty's terms: " + ex.GetBaseException().Message); }
    }

    // ---- reading Bellum's records ------------------------------------------------------------------------

    private static object? Get(object target, string member) => AccessTools.Property(target.GetType(), member)?.GetValue(target, null);
    private static string Text(object target, string member) => Get(target, member) as string ?? "";
    private static bool Flag(object target, string member) => Get(target, member) is true;
    private static int Number(object target, string member) => Get(target, member) is int n ? n : 0;

    private static IEnumerable<object> TermsOf(object proposal) =>
        (Get(proposal, "Terms") as IEnumerable)?.Cast<object>().Where(t => t != null) ?? Enumerable.Empty<object>();

    private static IEnumerable<BellumPeaceTargets.Tribute> Tributes(object proposal) =>
        TermsOf(proposal).Where(t => Get(t, "Type")?.ToString() == BellumPeaceTargets.TributeTerm)
            .Select(t => new BellumPeaceTargets.Tribute(Text(t, "FromKingdomId"), Text(t, "ToKingdomId"), Number(t, "DailyGold"), Number(t, "DurationDays")));

    /// <summary>Bellum's own sentence for the terms, with its "agreed" opening turned into "proposed" when it is the English one.</summary>
    private static string Terms(object proposal)
    {
        try
        {
            if (TermsOf(proposal).Any(t => Get(t, "Type")?.ToString() == BellumPeaceTargets.WhitePeaceTerm))
                return "Terms: white peace; nothing changes hands.";
            var text = _summary?.Invoke(null, new[] { proposal, null })?.ToString();
            if (string.IsNullOrWhiteSpace(text)) return "The terms could not be listed.";
            return text!.StartsWith("Agreed terms:", StringComparison.Ordinal) ? "Terms:" + text.Substring("Agreed terms:".Length) : text;
        }
        catch (Exception ex) { return "The terms could not be listed (" + ex.GetBaseException().Message + ")."; }
    }

    private static Kingdom? KingdomById(string id) => id.Length == 0 ? null : Kingdom.All.FirstOrDefault(k => k != null && k.StringId == id);
    private static double Today() => CampaignTime.Now.ToDays;
    private static string Key(string warKey, Kingdom realm) => warKey + "|" + realm.StringId;
    private static double AskAgain(string warKey, Kingdom realm) => AskAgainAfter.TryGetValue(Key(warKey, realm), out var day) ? day : double.MinValue;

    // ---- players -----------------------------------------------------------------------------------------

    private static IEnumerable<(string ControllerId, Hero Hero)> Connected()
    {
        if (!ContainerProvider.TryResolve<IPlayerManager>(out var players) || !ContainerProvider.TryResolve<IObjectManager>(out var objects))
            yield break;
        foreach (var player in players.Players.ToList())
        {
            if (!players.TryGetPeer(player.ControllerId, out var peer) || peer == null) continue;
            if (objects.TryGetObject<Hero>(player.HeroId, out var hero) && hero != null) yield return (player.ControllerId, hero);
        }
    }

    private static bool RulerIsConnected(Kingdom realm) =>
        realm.RulingClan != null && !realm.IsEliminated && Connected().Any(p => p.Hero.Clan == realm.RulingClan);

    internal static void Tell(Kingdom? realm, string text)
    {
        if (realm == null || Taom.NoticeComponent.Send is not { } send) return;
        foreach (var (controllerId, hero) in Connected())
            if (hero.Clan?.Kingdom == realm)
                send(controllerId, text, Colors.Yellow.ToUnsignedInteger(), false);
    }
}
