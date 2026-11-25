using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using ModderLords.Testing;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.CharacterDevelopment;
using TaleWorlds.CampaignSystem.BarterSystem;
using TaleWorlds.CampaignSystem.Conversation;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.Core;
using TaleWorlds.Library;
using static ModderLords.Tests.Bellum.Steps;

namespace ModderLords.Tests.Bellum;

/// <summary>
/// Steps for Bellum's dialogue lines on a player's PC (docs/BELLUM-COOP-COVERAGE.md, stage 4): a real map conversation
/// driven by its line ids, and what the lines change (an estate, a client kingdom).
/// </summary>
public static class TalkSteps
{
    public static void Register()
    {
        InGameTests.Register("bellum.talk", StepSide.Client, Talk);
        InGameTests.Register("bellum.landlessLord", StepSide.Server, LandlessLord);
        InGameTests.Register("bellum.lordship", StepSide.Any, Lordship);
        InGameTests.Register("bellum.peacefulRealm", StepSide.Server, PeacefulRealm);
        InGameTests.Register("bellum.clientOf", StepSide.Any, ClientOf);
        InGameTests.Register("bellum.queuePolicyVote", StepSide.Server, QueuePolicyVote);
        InGameTests.Register("bellum.policyBribe", StepSide.Any, PolicyBribe);
        InGameTests.Register("bellum.bribableLord", StepSide.Server, BribableLord);
        InGameTests.Register("bellum.goldSpent", StepSide.Any, GoldSpent);
        InGameTests.Register("bellum.recruitTarget", StepSide.Server, RecruitTarget);
        InGameTests.Register("bellum.rebelFaction", StepSide.Any, RebelFaction);
        InGameTests.Register("bellum.ransomBroker", StepSide.Any, RansomBroker);
        InGameTests.Register("bellum.captiveKin", StepSide.Server, CaptiveKin);
        InGameTests.Register("bellum.ransomPrice", StepSide.Server, RansomPrice);
        InGameTests.Register("bellum.prisoner", StepSide.Any, Prisoner);
    }

    private static object? Behavior(string typeName)
    {
        var type = FindType(typeName);
        return type == null ? null : typeof(Campaign).GetMethod(nameof(Campaign.GetCampaignBehavior))!.MakeGenericMethod(type).Invoke(Campaign.Current, null);
    }

    private static object? Member(object? o, string name) =>
        o == null ? null : o.GetType().GetField(name, Any)?.GetValue(o) ?? o.GetType().GetProperty(name, Any)?.GetValue(o);

    private static Hero HeroById(string id) => Hero.FindFirst(h => h.StringId == id) ?? throw new StepFailedException($"no hero '{id}'");

    /// <summary>
    /// Player: talks to hero <c>heroId</c> in a map conversation and follows <c>path</c>, the ids of the player's lines to
    /// pick, in order ("a|b|c"). The other side's lines are let through; where the player has options and none is the next
    /// line on the path, a later one on the path is picked if offered (a greyed-out line gives way to a later one that can be
    /// picked), else the step fails with what was offered. After the
    /// last line the conversation is left (any "leave" line), or closed. Every line and option is in the
    /// <c>conversation</c> dump. Reports value (the last line said).
    /// With <c>barter: accept</c>, the path's last line opens a barter: the player offers gold (as little as is accepted, up
    /// to all they have) with what the barter already offers, and the barter is accepted as its Offer button does. Reports
    /// gold (what was offered).
    /// With <c>confirm</c> ("yes" or "no"), a yes/no question Bellum asks on the way (an inquiry) is answered so, as its
    /// buttons do; with <c>text</c>, a box Bellum asks to type in (a text inquiry, such as a new house's name) is answered
    /// with that text and its Done button. Either one asked for and never shown fails the step.
    /// </summary>
    private static IEnumerable<StepWait?> Talk(StepContext ctx)
    {
        yield return ctx.Until(() => MobileParty.MainParty != null && Campaign.Current?.ConversationManager != null, "this player's party");
        // A hero (heroId), or a character who is no hero (characterId: a ransom broker, a tavern keeper).
        var hero = ctx.HasArg("heroId") ? HeroById(ctx.Arg("heroId")) : null;
        var character = hero?.CharacterObject ?? TaleWorlds.ObjectSystem.MBObjectManager.Instance.GetObject<CharacterObject>(ctx.Arg("characterId"))
            ?? throw new StepFailedException($"no character '{ctx.Arg("characterId")}'");
        var path = ctx.Arg("path").Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries).ToList();
        var cm = Campaign.Current.ConversationManager;
        var transcript = new StringBuilder();
        BarterData? barter = null;
        var wantsBarter = ctx.Arg("barter", "") == "accept";
        BarterManager.BarterBeginEventDelegate began = data => barter = data;
        if (wantsBarter) BarterManager.Instance.BarterBegin += began;
        // With pick, a list Bellum opens on the way (a multi-selection inquiry) is answered with the entry whose title
        // contains pick, as its Confirm button does.
        MultiSelectionInquiryData? inquiry = null;
        Action<MultiSelectionInquiryData, bool, bool> shown = (data, _, _) => inquiry = data;
        if (ctx.HasArg("pick")) MBInformationManager.OnShowMultiSelectionInquiry += shown;
        InquiryData? question = null;
        TextInquiryData? box = null;
        var answered = 0;
        var typed = 0;
        Action<InquiryData, bool, bool> asked = (data, _, _) => question = data;
        Action<TextInquiryData, bool, bool> boxed = (data, _, _) => box = data;
        if (ctx.HasArg("confirm")) InformationManager.OnShowInquiry += asked;
        if (ctx.HasArg("text")) InformationManager.OnShowTextInquiry += boxed;
        void Note(string line)
        {
            transcript.AppendLine(line);
            ModderLords.CompatSync.Log.Info("bellum.talk: " + line);
        }
        Note(hero != null
            ? $"talking to {hero.StringId} \"{hero.Name}\" of {hero.Clan?.StringId} ({hero.Clan?.Kingdom?.StringId}); path {string.Join(" > ", path)}"
            : $"talking to {character.StringId} \"{character.Name}\" ({character.Occupation}); path {string.Join(" > ", path)}");
        CampaignMapConversation.OpenConversation(new ConversationCharacterData(CharacterObject.PlayerCharacter, PartyBase.MainParty),
            new ConversationCharacterData(character, hero?.PartyBelongedTo?.Party));
        yield return ctx.Until(() => cm.IsConversationInProgress && cm.OneToOneConversationCharacter == character, "the conversation to open", TimeSpan.FromSeconds(30));
        Note($"open: [{TokenName(cm, cm.ActiveToken)}] {LastSentenceId(cm)}: {Safe(() => cm.CurrentSentenceText)}");

        string? problem = null;
        var lastState = "";
        var sameState = 0;
        var actions = 0;
        var said = "";
        bool Step()
        {
            if (!cm.IsConversationInProgress) return true;
            try
            {
                if (inquiry != null)
                {
                    var data = inquiry;
                    inquiry = null;
                    var pick = ctx.Arg("pick");
                    var element = data.InquiryElements.FirstOrDefault(e => e.IsEnabled && (e.Title ?? "").Contains(pick));
                    if (element == null)
                    {
                        problem = $"the list '{data.TitleText}' has no entry with '{pick}': {string.Join(" | ", data.InquiryElements.Select(e => e.Title + (e.IsEnabled ? "" : " (disabled)")))}";
                        return true;
                    }
                    Note($"P  -> {element.Title} (in '{data.TitleText}'; confirm {data.AffirmativeAction?.Method.DeclaringType?.Name}.{data.AffirmativeAction?.Method.Name ?? "none"})");
                    InformationManager.HideInquiry();
                    data.AffirmativeAction?.Invoke(new List<InquiryElement> { element });
                    return false;
                }
                if (question != null)
                {
                    var data = question;
                    question = null;
                    var yes = ctx.Arg("confirm") == "yes";
                    var action = yes ? data.AffirmativeAction : data.NegativeAction;
                    Note($"P  -> {(yes ? data.AffirmativeText : data.NegativeText)} (to '{data.TitleText}'; {action?.Method.DeclaringType?.Name}.{action?.Method.Name ?? "none"})");
                    answered++;
                    InformationManager.HideInquiry();
                    action?.Invoke();
                    return false;
                }
                if (box != null)
                {
                    var data = box;
                    box = null;
                    var text = ctx.Arg("text");
                    var check = data.TextCondition?.Invoke(text);
                    if (check != null && !check.Item1)
                    {
                        problem = $"'{data.TitleText}' does not take '{text}': {check.Item2}";
                        return true;
                    }
                    Note($"P  -> typed '{text}' (in '{data.TitleText}'; {data.AffirmativeAction?.Method.DeclaringType?.Name}.{data.AffirmativeAction?.Method.Name ?? "none"})");
                    typed++;
                    InformationManager.HideInquiry();
                    data.AffirmativeAction?.Invoke(text);
                    return false;
                }
                var state = $"{cm.ActiveToken}/{LastSentenceId(cm)}";
                sameState = state == lastState ? sameState + 1 : 0;
                lastState = state;
                if (sameState >= 6) { problem = $"the conversation stopped at [{TokenName(cm, cm.ActiveToken)}] after {LastSentenceId(cm)}"; return true; }
                if (++actions > 60) { problem = "the conversation did not finish in 60 steps"; return true; }
                cm.GetPlayerSentenceOptions();
                var options = cm.CurOptions ?? new List<ConversationSentenceOption>();
                if (options.Count > 0)
                    Note($"  [{TokenName(cm, cm.ActiveToken)}] options: {string.Join(" | ", options.Select(o => $"{o.Id}{(o.IsClickable ? "" : " (not clickable)")}: {o.Text}"))}");
                if (options.Count == 0)
                {
                    cm.ContinueConversation();
                }
                else if (path.Count == 0)
                {
                    // Done: leave as a player would.
                    var leave = options.FirstOrDefault(o => o.IsClickable && (o.Id.Contains("leave") || o.Id.Contains("leaving") || o.Id.Contains("goodbye")));
                    if (leave.Id == null) { cm.EndConversation(); return true; }
                    Note($"P  -> {leave.Id} (leaving)");
                    cm.DoOption(leave.Id);
                }
                else
                {
                    // The first line on the path that is offered and can be picked; a greyed-out one gives way to a later one
                    // on the path (alternatives, such as swaying a vote for or against).
                    var at = path.FindIndex(id => options.Any(o => o.Id == id && o.IsClickable));
                    if (at < 0) at = path.FindIndex(id => options.Any(o => o.Id == id));
                    if (at < 0 && TokenName(cm, cm.ActiveToken) != "hero_main_options"
                        && options.FirstOrDefault(o => o.IsClickable && !o.Id.Contains("leav") && !o.Id.Contains("goodbye")) is { Id: not null } on)
                    {
                        // An introduction or a reply on the way to the main options (a first meeting): answer it and go on.
                        Note($"P  -> {on.Id} (on the way)");
                        cm.DoOption(options.IndexOf(on));
                        said = LastSentenceId(cm);
                        Note($"   now [{TokenName(cm, cm.ActiveToken)}] {said}: {Safe(() => cm.CurrentSentenceText)}");
                        return false;
                    }
                    if (at < 0) { problem = $"none of {string.Join(", ", path)} is offered at [{TokenName(cm, cm.ActiveToken)}]"; return true; }
                    var index = options.FindIndex(o => o.Id == path[at]);
                    var option = options[index];
                    if (!option.IsClickable) { problem = $"{option.Id} is offered but not clickable: {option.HintText}"; return true; }
                    Note($"P  -> {option.Id}{(at > 0 ? $" (skipping {string.Join(", ", path.Take(at))})" : "")}");
                    path.RemoveRange(0, at + 1);
                    cm.DoOption(index);
                }
                said = LastSentenceId(cm);
                Note($"   now [{TokenName(cm, cm.ActiveToken)}] {said}: {Safe(() => cm.CurrentSentenceText)}");
            }
            catch (Exception ex)
            {
                problem = $"the conversation threw at [{TokenName(cm, cm.ActiveToken)}] after {LastSentenceId(cm)}: {ex}";
                return true;
            }
            return false;
        }
        while (!(wantsBarter && path.Count == 0 && barter != null) && !Step()) yield return ctx.Delay(TimeSpan.FromMilliseconds(300));
        if (wantsBarter)
        {
            if (problem == null && path.Count == 0)
            {
                yield return ctx.Until(() => barter != null, "the barter to open", TimeSpan.FromSeconds(30));
                var offered = AcceptBarter(barter!, Note);
                ctx.Report("gold", offered);
                yield return ctx.Delay(TimeSpan.FromSeconds(1));
            }
            BarterManager.Instance.BarterBegin -= began;
        }
        if (ctx.HasArg("pick")) MBInformationManager.OnShowMultiSelectionInquiry -= shown;
        if (ctx.HasArg("confirm")) InformationManager.OnShowInquiry -= asked;
        if (ctx.HasArg("text")) InformationManager.OnShowTextInquiry -= boxed;
        if (problem == null && ctx.HasArg("confirm") && answered == 0) problem = "no yes/no question was asked on the way";
        if (problem == null && ctx.HasArg("text") && typed == 0) problem = "no text box was shown on the way";
        ctx.Dump("conversation", transcript.ToString());
        if (cm.IsConversationInProgress)
        {
            try { cm.EndConversation(); } catch (Exception ex) { ctx.Log("closing the conversation threw: " + ex.Message); }
        }
        ctx.Assert(problem == null, problem ?? "");
        ctx.Assert(path.Count == 0, $"the conversation ended before {string.Join(", ", path)}");
        yield return ctx.Until(() => !cm.IsConversationInProgress, "the conversation to close", TimeSpan.FromSeconds(30));
        ctx.Report("value", said);
    }

    // The barter's own terms stay as offered; the player's gold is raised until the other side accepts, then the barter is
    // applied and closed as its Offer button does (BarterManager.ApplyAndFinalizePlayerBarter, then Close).
    private static int AcceptBarter(BarterData data, Action<string> note)
    {
        var manager = BarterManager.Instance;
        var other = data.OtherHero;
        note($"barter with {other?.StringId}: {string.Join(" | ", data.GetBarterables().Select(b => $"{b.GetType().Name} {b.OriginalOwner?.StringId} x{b.CurrentAmount}{(b.IsOffered ? " offered" : "")}"))}");
        var gold = data.GetBarterables().OfType<TaleWorlds.CampaignSystem.BarterSystem.Barterables.GoldBarterable>().FirstOrDefault(g => g.OriginalOwner == Hero.MainHero);
        var amount = 0;
        if (!manager.IsOfferAcceptable(data, other, data.OtherParty))
        {
            if (gold == null) throw new StepFailedException("the barter wants more and offers the player no gold to give");
            gold.SetIsOffered(true);
            var most = Hero.MainHero.Gold;
            for (amount = Math.Min(1000, most); ; amount = Math.Min(most, amount * 2))
            {
                gold.CurrentAmount = amount;
                if (manager.IsOfferAcceptable(data, other, data.OtherParty)) break;
                if (amount >= most) throw new StepFailedException($"the lord would not accept even {most} gold");
            }
        }
        note($"offering {amount} gold; accepted");
        manager.ApplyAndFinalizePlayerBarter(Hero.MainHero, other, data);
        try { manager.Close(); } catch (Exception ex) { note("closing the barter threw: " + ex.Message); }
        return amount;
    }

    private static readonly FieldInfo? SentencesField = typeof(ConversationManager).GetField("_sentences", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo? CurrentSentenceField = typeof(ConversationManager).GetField("_currentSentence", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo? StateMapField = typeof(ConversationManager).GetField("stateMap", BindingFlags.Instance | BindingFlags.NonPublic);

    private static string LastSentenceId(ConversationManager cm)
    {
        if (SentencesField?.GetValue(cm) is not List<ConversationSentence> sentences || CurrentSentenceField?.GetValue(cm) is not int i || i < 0 || i >= sentences.Count) return "-";
        return (sentences[i].IsPlayer ? "P " : "AI ") + sentences[i].Id;
    }

    private static string TokenName(ConversationManager cm, int token) =>
        StateMapField?.GetValue(cm) is Dictionary<string, int> map && map.FirstOrDefault(kv => kv.Value == token).Key is { } name ? name : token.ToString(CultureInfo.InvariantCulture);

    private static string Safe(Func<string> read)
    {
        try { return read(); } catch (Exception ex) { return "(" + ex.GetType().Name + ")"; }
    }

    /// <summary>
    /// Server: the leader of a landless lords' house of realm <c>kingdom</c> with no estate yet, for an estate grant
    /// (Bellum's ExiledClanRecoveryBehavior.IsLandlessForExile). Reports value (the hero's id) and clan.
    /// </summary>
    private static IEnumerable<StepWait?> LandlessLord(StepContext ctx)
    {
        var kingdom = KingdomById(ctx.Arg("kingdom")) ?? throw new StepFailedException($"no kingdom '{ctx.Arg("kingdom")}'");
        var landless = FindType("BellumCivile.Behaviors.ExiledClanRecoveryBehavior")?.GetMethod("IsLandlessForExile", Any)
            ?? throw new StepFailedException("Bellum has no IsLandlessForExile");
        var lordships = Behavior("BellumCivile.Behaviors.LordshipBehavior") ?? throw new StepFailedException("Bellum's lordships are not running");
        var getGrant = lordships.GetType().GetMethods(Any).FirstOrDefault(m => m.Name == "GetGrant" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(Hero));
        var clan = kingdom.Clans.Where(c => c != null && c != kingdom.RulingClan && c.IsNoble && !c.IsMinorFaction && !c.IsEliminated && !c.IsUnderMercenaryService
                && c.Leader != null && c.Leader.IsAlive && !c.Leader.IsChild && !c.Leader.IsPrisoner && !c.Leader.IsDisabled
                && landless.Invoke(null, new object[] { c }) is true && getGrant?.Invoke(lordships, new object[] { c.Leader }) == null)
            .OrderBy(c => c.StringId).FirstOrDefault() ?? throw new StepFailedException($"{kingdom.StringId} has no landless lords' house without an estate");
        ctx.Log($"{clan.StringId}, led by {clan.Leader.StringId}");
        ctx.Report("value", clan.Leader.StringId);
        ctx.Report("clan", clan.StringId);
        yield break;
    }

    /// <summary>
    /// Either side: the village hero <c>heroId</c> holds as Bellum's personal estate ("none" without one), waited for until
    /// it is <c>expect</c> ("any" for any village) when given, then settled. Reports it.
    /// </summary>
    private static IEnumerable<StepWait?> Lordship(StepContext ctx)
    {
        var hero = HeroById(ctx.Arg("heroId"));
        var lordships = Behavior("BellumCivile.Behaviors.LordshipBehavior") ?? throw new StepFailedException("Bellum's lordships are not running");
        var getGrant = lordships.GetType().GetMethods(Any).FirstOrDefault(m => m.Name == "GetGrant" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(Hero))
            ?? throw new StepFailedException("Bellum has no LordshipBehavior.GetGrant(Hero)");
        string Read() => (Member(getGrant.Invoke(lordships, new object[] { hero }), "Village") as Settlement)?.StringId ?? "none";
        bool Matches(string value) => !ctx.HasArg("expect") || (ctx.Arg("expect") == "any" ? value != "none" : value == ctx.Arg("expect"));
        if (ctx.HasArg("expect"))
            yield return ctx.Until(() => Matches(Read()), $"{hero.StringId}'s estate to be {ctx.Arg("expect")}", TimeSpan.FromSeconds(ctx.ArgInt("timeoutSeconds", 30)));
        yield return ctx.Settled(Read, $"{hero.StringId}'s estate", quiet: TimeSpan.FromSeconds(3));
        var value = Read();
        ctx.Assert(Matches(value), $"{hero.StringId}'s estate is {value}, expected {ctx.Arg("expect", "")}");
        ctx.Report("value", value);
    }

    /// <summary>
    /// Server: a realm other than <c>kingdom</c> at peace with it, ruled by a lord (no player), for a client kingdom.
    /// Reports value (its id) and ruler (its ruler's hero id).
    /// </summary>
    private static IEnumerable<StepWait?> PeacefulRealm(StepContext ctx)
    {
        var kingdom = KingdomById(ctx.Arg("kingdom")) ?? throw new StepFailedException($"no kingdom '{ctx.Arg("kingdom")}'");
        var clients = Behavior("BellumCivile.Behaviors.ClientKingdomBehavior");
        var isClient = clients?.GetType().GetMethod("IsClientKingdom", Any);
        var other = Kingdom.All.Where(k => k != null && k != kingdom && !k.IsEliminated && !k.StringId.Contains("_rebels") && k.RulingClan?.Leader != null
                && k.RulingClan.Leader.IsAlive && !k.IsAtWarWith(kingdom) && isClient?.Invoke(clients, new object[] { k }) is not true)
            .OrderBy(k => k.Clans.Count).FirstOrDefault() ?? throw new StepFailedException($"no realm at peace with {kingdom.StringId}");
        ctx.Log($"{other.StringId}, ruled by {other.RulingClan.Leader.StringId}");
        ctx.Report("value", other.StringId);
        ctx.Report("ruler", other.RulingClan.Leader.StringId);
        yield break;
    }

    /// <summary>
    /// Either side: the realm kingdom <c>client</c> is a client of in Bellum ("none" if free), waited for until it is
    /// <c>expect</c> when given, then settled. Reports it.
    /// </summary>
    private static IEnumerable<StepWait?> ClientOf(StepContext ctx)
    {
        var client = KingdomById(ctx.Arg("client")) ?? throw new StepFailedException($"no kingdom '{ctx.Arg("client")}'");
        var clients = Behavior("BellumCivile.Behaviors.ClientKingdomBehavior") ?? throw new StepFailedException("Bellum's client kingdoms are not running");
        var record = clients.GetType().GetMethod("GetClientRecord", Any) ?? throw new StepFailedException("Bellum has no GetClientRecord");
        string Read() => Member(record.Invoke(clients, new object[] { client }), "SuzerainKingdomId") as string ?? "none";
        if (ctx.HasArg("expect"))
            yield return ctx.Until(() => Read() == ctx.Arg("expect"), $"{client.StringId}'s suzerain to be {ctx.Arg("expect")}", TimeSpan.FromSeconds(ctx.ArgInt("timeoutSeconds", 30)));
        yield return ctx.Settled(Read, $"{client.StringId}'s suzerain", quiet: TimeSpan.FromSeconds(3));
        var value = Read();
        if (ctx.HasArg("expect"))
            ctx.Assert(value == ctx.Arg("expect"), $"{client.StringId}'s suzerain is {value}, expected {ctx.Arg("expect")}");
        ctx.Report("value", value);
    }

    /// <summary>
    /// Server: a lord of kingdom <c>kingdom</c> (a house's leader, not its ruler's house nor player <c>heroName</c>'s) who
    /// would bargain over his vote: Bellum asks a lord's honor, mercy and generosity (a lord with any of them refuses
    /// bribes outright) and calculation. His relation with the player counts too, but a random new character can start
    /// below 0 with every such lord, so the suite sets it (bellum.setRelation). Reports value (the lord's id).
    /// </summary>
    private static IEnumerable<StepWait?> BribableLord(StepContext ctx)
    {
        var kingdom = KingdomById(ctx.Arg("kingdom")) ?? throw new StepFailedException($"no kingdom '{ctx.Arg("kingdom")}'");
        var player = HeroNames.Single(ctx.Arg("heroName"));
        var clan = kingdom.Clans.Where(c => c != null && c != kingdom.RulingClan && c != player.Clan && c.IsNoble && !c.IsMinorFaction && !c.IsEliminated
                && !c.IsUnderMercenaryService && c.Leader != null && c.Leader.IsAlive && !c.Leader.IsChild && !c.Leader.IsPrisoner
                && c.Leader.GetTraitLevel(DefaultTraits.Honor) <= 0 && c.Leader.GetTraitLevel(DefaultTraits.Mercy) <= 0
                && c.Leader.GetTraitLevel(DefaultTraits.Generosity) <= 0 && c.Leader.GetTraitLevel(DefaultTraits.Calculating) >= 0)
            .OrderBy(c => c.StringId).FirstOrDefault() ?? throw new StepFailedException($"{kingdom.StringId} has no lord who would bargain over his vote");
        ctx.Log($"{clan.StringId}, led by {clan.Leader.StringId}");
        ctx.Report("value", clan.Leader.StringId);
        yield break;
    }

    /// <summary>
    /// Server: Bellum queues a vote on a policy kingdom <c>kingdom</c> lacks (PolicyDeliberationBehavior.QueuePolicyVote,
    /// moved by its ruling house), as a faction's motion does. Reports value (the policy's id).
    /// </summary>
    private static IEnumerable<StepWait?> QueuePolicyVote(StepContext ctx)
    {
        var kingdom = KingdomById(ctx.Arg("kingdom")) ?? throw new StepFailedException($"no kingdom '{ctx.Arg("kingdom")}'");
        var policies = Behavior("BellumCivile.Behaviors.PolicyDeliberationBehavior") ?? throw new StepFailedException("Bellum's policy deliberation is not running");
        var queue = policies.GetType().GetMethod("QueuePolicyVote", Any) ?? throw new StepFailedException("Bellum has no QueuePolicyVote");
        var pending = policies.GetType().GetMethod("HasPendingVoteForPolicy", Any);
        foreach (var policy in TaleWorlds.ObjectSystem.MBObjectManager.Instance.GetObjectTypeList<PolicyObject>().Where(p => !kingdom.ActivePolicies.Contains(p)).OrderBy(p => p.StringId))
        {
            if (pending?.Invoke(policies, new object[] { kingdom, policy }) is true) { ctx.Report("value", policy.StringId); yield break; }
            var args = new object?[] { kingdom, policy, false, null, kingdom.RulingClan, null };
            if (queue.Invoke(policies, args) is true)
            {
                ctx.Log($"{kingdom.StringId} deliberates on {policy.StringId}");
                ctx.Report("value", policy.StringId);
                yield break;
            }
        }
        throw new StepFailedException($"Bellum would queue no policy vote in {kingdom.StringId}");
    }

    /// <summary>
    /// Either side: the vote Bellum records for lord <c>heroId</c>'s house on policy <c>policy</c> in <c>kingdom</c>
    /// (PolicyDeliberationBehavior.GetBribedVote; "none" without one), waited for until it is set when <c>expect</c> is
    /// "set", or is <c>expect</c>. Reports it.
    /// </summary>
    private static IEnumerable<StepWait?> PolicyBribe(StepContext ctx)
    {
        var kingdom = KingdomById(ctx.Arg("kingdom")) ?? throw new StepFailedException($"no kingdom '{ctx.Arg("kingdom")}'");
        var policy = TaleWorlds.ObjectSystem.MBObjectManager.Instance.GetObject<PolicyObject>(ctx.Arg("policy")) ?? throw new StepFailedException($"no policy '{ctx.Arg("policy")}'");
        var clan = HeroById(ctx.Arg("heroId")).Clan ?? throw new StepFailedException("the lord has no house");
        var policies = Behavior("BellumCivile.Behaviors.PolicyDeliberationBehavior") ?? throw new StepFailedException("Bellum's policy deliberation is not running");
        var get = policies.GetType().GetMethod("GetBribedVote", Any) ?? throw new StepFailedException("Bellum has no GetBribedVote");
        string Read() => get.Invoke(policies, new object[] { kingdom, policy, clan }) is int v ? v.ToString(CultureInfo.InvariantCulture) : "none";
        bool Matches(string value) => ctx.Arg("expect", "") == "set" ? value != "none" : !ctx.HasArg("expect") || value == ctx.Arg("expect");
        if (ctx.HasArg("expect"))
            yield return ctx.Until(() => Matches(Read()), $"{clan.StringId}'s vote on {policy.StringId} to be {ctx.Arg("expect")}", TimeSpan.FromSeconds(ctx.ArgInt("timeoutSeconds", 30)));
        var value = Read();
        ctx.Assert(Matches(value), $"{clan.StringId}'s vote on {policy.StringId} is {value}, expected {ctx.Arg("expect", "")}");
        ctx.Report("value", value);
    }

    /// <summary>
    /// Either side: hero <c>heroName</c> has spent exactly <c>spent</c> gold since having <c>before</c> (waited for, as gold
    /// travels from the server). Paying twice, or not at all, fails. Reports value (the gold now).
    /// </summary>
    private static IEnumerable<StepWait?> GoldSpent(StepContext ctx)
    {
        var hero = HeroNames.Single(ctx.Arg("heroName"));
        var want = ctx.ArgInt("before") - ctx.ArgInt("spent");
        yield return ctx.Until(() => hero.Gold == want, $"{hero.StringId}'s gold to be {want}", TimeSpan.FromSeconds(ctx.ArgInt("timeoutSeconds", 15)));
        ctx.Assert(hero.Gold == want, $"{hero.StringId} has {hero.Gold} gold, expected {want} ({ctx.Arg("before")} less {ctx.Arg("spent")})");
        ctx.Report("value", hero.Gold.ToString(CultureInfo.InvariantCulture));
    }

    private static object? RebelFactionOf(Clan clan)
    {
        var manager = Behavior("BellumCivile.Behaviors.FactionManagerBehavior") ?? throw new StepFailedException("Bellum's factions are not running");
        var get = manager.GetType().GetMethod("GetRebelFaction", Any, null, new[] { typeof(Clan) }, null) ?? throw new StepFailedException("Bellum has no GetRebelFaction");
        return get.Invoke(manager, new object[] { clan });
    }

    /// <summary>
    /// Server: a lord player <c>heroName</c> could buy into his faction (Bellum's AppeasementBehavior): the leader of a
    /// house of the player's realm, not its ruler's, in no faction and not with an army. Reports value (the lord's id).
    /// </summary>
    private static IEnumerable<StepWait?> RecruitTarget(StepContext ctx)
    {
        var player = HeroNames.Single(ctx.Arg("heroName"));
        var realm = player.Clan?.Kingdom ?? throw new StepFailedException("the player is in no realm");
        var clan = realm.Clans.Where(c => c != null && c != realm.RulingClan && c != player.Clan && c.IsNoble && !c.IsMinorFaction && !c.IsEliminated
                && !c.IsUnderMercenaryService && c.Leader != null && c.Leader.IsAlive && !c.Leader.IsChild && !c.Leader.IsPrisoner
                && c.Leader.PartyBelongedTo?.Army == null && RebelFactionOf(c) == null)
            .OrderBy(c => c.Tier).ThenBy(c => c.StringId).FirstOrDefault() ?? throw new StepFailedException($"{realm.StringId} has no house to recruit");
        ctx.Log($"{clan.StringId} (tier {clan.Tier}), led by {clan.Leader.StringId}");
        ctx.Report("value", clan.Leader.StringId);
        yield break;
    }

    /// <summary>
    /// Either side: the rebel faction lord <c>heroId</c>'s house is in (Bellum's FactionManagerBehavior.GetRebelFaction):
    /// its leading house's id, or "none". With <c>expect</c> ("none"), or <c>leaderName</c> (a player whose house leads
    /// it), waited for. Reports value.
    /// </summary>
    private static IEnumerable<StepWait?> RebelFaction(StepContext ctx)
    {
        var clan = HeroById(ctx.Arg("heroId")).Clan ?? throw new StepFailedException("the lord has no house");
        var want = ctx.HasArg("leaderName") ? HeroNames.Single(ctx.Arg("leaderName")).Clan?.StringId ?? "" : ctx.Arg("expect", "");
        string Read() => RebelFactionOf(clan) is object f ? (f.GetType().GetProperty("Leader", Any)?.GetValue(f) as Clan)?.StringId ?? "?" : "none";
        if (want != "")
            yield return ctx.Until(() => Read() == want, $"{clan.StringId}'s faction to be {want}", TimeSpan.FromSeconds(ctx.ArgInt("timeoutSeconds", 30)));
        var value = Read();
        ctx.Assert(want == "" || value == want, $"{clan.StringId}'s faction is {value}, expected {want}");
        ctx.Report("value", value);
    }

    /// <summary>Either side: a ransom broker's character (occupation RansomBroker), whom the player has met. Reports value (its id).</summary>
    private static IEnumerable<StepWait?> RansomBroker(StepContext ctx)
    {
        var broker = TaleWorlds.ObjectSystem.MBObjectManager.Instance.GetObjectTypeList<CharacterObject>()
            .Where(c => c.Occupation == Occupation.RansomBroker && !c.IsHero).OrderBy(c => c.StringId).FirstOrDefault()
            ?? throw new StepFailedException("no ransom broker character");
        // The player has met the brokers, so a conversation opened on the map takes vanilla's "has met" greeting: the
        // first-meeting one reads the settlement the player is in, which a real player always is (a tavern).
        var tavern = FindType("SandBox.CampaignBehaviors.TavernEmployeesCampaignBehavior") is Type t
            ? typeof(Campaign).GetMethod(nameof(Campaign.GetCampaignBehavior))!.MakeGenericMethod(t).Invoke(Campaign.Current, null) : null;
        var met = tavern?.GetType().GetField("_hasMetWithRansomBroker", Any) ?? throw new StepFailedException("vanilla's tavern employees have no ransom broker");
        met.SetValue(tavern, true);
        ctx.Report("value", broker.StringId);
        yield break;
    }

    /// <summary>
    /// Server: a lord joins player <c>heroName</c>'s house (an adult of another house, not its leader, in no party), and
    /// is taken prisoner by the party of a lord of a third house. Reports value (the captive's id) and captor (that
    /// house's id).
    /// </summary>
    private static IEnumerable<StepWait?> CaptiveKin(StepContext ctx)
    {
        var player = HeroNames.Single(ctx.Arg("heroName"));
        var house = player.Clan ?? throw new StepFailedException("the player has no house");
        var kin = Hero.AllAliveHeroes.Where(h => h.IsLord && !h.IsChild && h.Clan != null && h.Clan != house && h.Clan.Leader != h && !h.Clan.IsMinorFaction
                && !h.IsPrisoner && h.PartyBelongedTo == null && h.Spouse == null && h.Age >= 18 && h.Age < 50)
            .OrderBy(h => h.StringId).FirstOrDefault() ?? throw new StepFailedException("no lord to join the player's house");
        var captor = MobileParty.AllLordParties.Where(p => p.IsActive && p.LeaderHero != null && p.LeaderHero.Clan != null && p.LeaderHero.Clan != house
                && p.LeaderHero.Clan != kin.Clan && !p.LeaderHero.Clan.IsMinorFaction && p.MapEvent == null && p.CurrentSettlement == null)
            .OrderBy(p => p.StringId).FirstOrDefault() ?? throw new StepFailedException("no lord's party to hold the captive");
        ctx.Log($"{kin.StringId} of {kin.Clan.StringId} joins {house.StringId}; held by {captor.StringId} of {captor.LeaderHero.Clan.StringId}");
        kin.Clan = house;
        TakePrisonerAction.Apply(captor.Party, kin);
        ctx.Assert(kin.IsPrisoner && kin.PartyBelongedToAsPrisoner == captor.Party, $"{kin.StringId} was not taken prisoner");
        ctx.Report("value", kin.StringId);
        ctx.Report("captor", captor.LeaderHero.Clan.StringId);
        ctx.Report("name", kin.Name.ToString());
        yield break;
    }

    /// <summary>
    /// Server: Bellum's ransom price for captive <c>heroId</c> (HouseholdRansomBehavior.CalculateRansomPrice, from his
    /// captor as Bellum resolves it). Reports value.
    /// </summary>
    private static IEnumerable<StepWait?> RansomPrice(StepContext ctx)
    {
        var prisoner = HeroById(ctx.Arg("heroId"));
        var type = FindType("BellumCivile.Behaviors.HouseholdRansomBehavior") ?? throw new StepFailedException("Bellum has no household ransom");
        var captor = type.GetMethod("ResolveCaptorClan", Any)?.Invoke(null, new object[] { prisoner }) as Clan ?? throw new StepFailedException("Bellum finds no captor");
        var price = type.GetMethod("CalculateRansomPrice", Any)?.Invoke(null, new object[] { prisoner, captor, prisoner.PartyBelongedToAsPrisoner }) is int p ? p
            : throw new StepFailedException("Bellum has no CalculateRansomPrice");
        ctx.Log($"{prisoner.StringId} held by {captor.StringId}: {price} gold");
        ctx.Report("value", price.ToString(CultureInfo.InvariantCulture));
        yield break;
    }

    /// <summary>Either side: whether hero <c>heroId</c> is a prisoner ("yes" or "no"), waited for to be <c>expect</c>.</summary>
    private static IEnumerable<StepWait?> Prisoner(StepContext ctx)
    {
        var hero = HeroById(ctx.Arg("heroId"));
        string Read() => hero.IsPrisoner ? "yes" : "no";
        if (ctx.HasArg("expect"))
            yield return ctx.Until(() => Read() == ctx.Arg("expect"), $"{hero.StringId} to be a prisoner: {ctx.Arg("expect")}", TimeSpan.FromSeconds(ctx.ArgInt("timeoutSeconds", 30)));
        ctx.Assert(!ctx.HasArg("expect") || Read() == ctx.Arg("expect"), $"{hero.StringId} is a prisoner: {Read()}");
        ctx.Report("value", Read());
    }
}
