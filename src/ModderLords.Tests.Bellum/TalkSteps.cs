using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using ModderLords.Testing;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Conversation;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
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
    /// line on the path, a later one on the path is picked if offered, else the step fails with what was offered. After the
    /// last line the conversation is left (any "leave" line), or closed. Every line and option is in the
    /// <c>conversation</c> dump. Reports value (the last line said).
    /// </summary>
    private static IEnumerable<StepWait?> Talk(StepContext ctx)
    {
        yield return ctx.Until(() => MobileParty.MainParty != null && Campaign.Current?.ConversationManager != null, "this player's party");
        var hero = HeroById(ctx.Arg("heroId"));
        var path = ctx.Arg("path").Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries).ToList();
        var cm = Campaign.Current.ConversationManager;
        var transcript = new StringBuilder();
        void Note(string line)
        {
            transcript.AppendLine(line);
            ModderLords.CompatSync.Log.Info("bellum.talk: " + line);
        }
        Note($"talking to {hero.StringId} \"{hero.Name}\" of {hero.Clan?.StringId} ({hero.Clan?.Kingdom?.StringId}); path {string.Join(" > ", path)}");
        CampaignMapConversation.OpenConversation(new ConversationCharacterData(CharacterObject.PlayerCharacter, PartyBase.MainParty),
            new ConversationCharacterData(hero.CharacterObject, hero.PartyBelongedTo?.Party));
        yield return ctx.Until(() => cm.IsConversationInProgress && cm.OneToOneConversationHero == hero, "the conversation to open", TimeSpan.FromSeconds(30));
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
                    var at = path.FindIndex(id => options.Any(o => o.Id == id));
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
        while (!Step()) yield return ctx.Delay(TimeSpan.FromMilliseconds(300));
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
}
