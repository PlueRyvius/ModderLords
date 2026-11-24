using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ModderLords.Testing;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.Conversation;
using TaleWorlds.CampaignSystem.Election;
using TaleWorlds.Library;
using TaleWorlds.Localization;
using static ModderLords.Tests.Bellum.Steps;

namespace ModderLords.Tests.Bellum;

/// <summary>
/// Steps for what the compat module does about Bellum's politics on players' PCs (docs/BELLUM-COOP-COVERAGE.md): the
/// server decides, a player's PC only shows. Also treaties put to a player's realm, the alliance peace block and claim
/// feuds. Read by reflection like <see cref="Steps"/>.
/// </summary>
public static class CoopSteps
{
    public static void Register()
    {
        InGameTests.Register("bellum.passDays", StepSide.Server, PassDays);
        InGameTests.Register("bellum.skipped", StepSide.Client, Skipped);
        InGameTests.Register("bellum.modDecision", StepSide.Client, ModDecision);
        InGameTests.Register("bellum.policyVote", StepSide.Any, PolicyVote);
        InGameTests.Register("bellum.foundKingdom", StepSide.Client, FoundKingdom);
        InGameTests.Register("bellum.kingdoms", StepSide.Any, Kingdoms);
        InGameTests.Register("bellum.dialogLines", StepSide.Client, DialogLines);
        InGameTests.Register("bellum.changeRelation", StepSide.Server, ChangeRelation);
        InGameTests.Register("bellum.relation", StepSide.Any, Relation);
        InGameTests.Register("bellum.pickKingdoms", StepSide.Server, PickKingdoms);
        InGameTests.Register("bellum.makeClient", StepSide.Server, MakeClient);
        InGameTests.Register("bellum.queueTreaty", StepSide.Server, QueueTreaty);
        InGameTests.Register("bellum.treaties", StepSide.Server, Treaties);
        InGameTests.Register("bellum.awaitPeaceVote", StepSide.Server, AwaitPeaceVote);
        InGameTests.Register("bellum.callToArms", StepSide.Server, CallToArms);
        InGameTests.Register("bellum.startFeud", StepSide.Server, StartFeud);
    }

    private static object? Behavior(string typeName)
    {
        var type = FindType(typeName) ?? throw new StepFailedException($"{typeName} is not loaded");
        return typeof(Campaign).GetMethod(nameof(Campaign.GetCampaignBehavior))!.MakeGenericMethod(type).Invoke(Campaign.Current, null);
    }

    // ---- time ------------------------------------------------------------------------------------------------------

    /// <summary>Server: lets campaign time run <c>days</c> game days (default 3) at Coop's fastest speed, then stops it.</summary>
    private static IEnumerable<StepWait?> PassDays(StepContext ctx)
    {
        var end = CampaignTime.Now + CampaignTime.Days(ctx.ArgInt("days", 3));
        SetTime(ctx, play: true, fast: true);
        try
        {
            yield return ctx.Until(() => CampaignTime.Now >= end, $"{ctx.ArgInt("days", 3)} game days to pass", TimeSpan.FromSeconds(ctx.ArgInt("timeoutSeconds", 400)));
        }
        finally
        {
            SetTime(ctx, play: false);
        }
        ctx.Report("value", CampaignTime.Now.ToString());
    }

    // ---- the player's PC leaves Bellum's politics to the server -------------------------------------------------------

    /// <summary>
    /// Player: how often the compat module kept each of Bellum's ticks and calls from running on this PC
    /// (ServerDecides.Refused). Fails unless every <c>expect</c> entry ("Type.Method", separated by |) was kept off at
    /// least once. Reports the total.
    /// </summary>
    private static IEnumerable<StepWait?> Skipped(StepContext ctx)
    {
        var refused = FindType("BellumCivileCoopCompat.ServerDecides")?.GetProperty("Refused", Any)?.GetValue(null) as IDictionary
            ?? throw new StepFailedException("the compat module keeps Bellum's ticks running on players' PCs (older than 1.2.0)");
        var counts = new Dictionary<string, int>();
        foreach (var key in refused.Keys) counts[(string)key] = (int)refused[key]!;
        ctx.Dump("bellum-skipped", string.Join(Environment.NewLine, counts.OrderBy(kv => kv.Key).Select(kv => kv.Key + " " + kv.Value)));
        foreach (var want in ctx.Arg("expect", "").Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries))
            ctx.Assert(counts.TryGetValue(want, out var n) && n > 0, $"{want} ran on this player's PC (never kept off)");
        ctx.Report("value", counts.Values.Sum());
        yield break;
    }

    /// <summary>
    /// Player: files a policy vote in a lords' kingdom the way Bellum's court AI does (IdeologyBehavior.AddDecisionAsModAction,
    /// influence waived). Coop sends a decision added on a player's PC to the server, so without the module every player's
    /// Bellum put its own copies there. Reports the kingdom (value) and policy.
    /// </summary>
    private static IEnumerable<StepWait?> ModDecision(StepContext ctx)
    {
        var ideology = FindType("BellumCivile.Behaviors.IdeologyBehavior") ?? throw new StepFailedException("Bellum is not loaded");
        var add = ideology.GetMethod("AddDecisionAsModAction", Any) ?? throw new StepFailedException("Bellum has no AddDecisionAsModAction");
        var kingdom = Kingdom.All.Where(k => k != null && !k.IsEliminated && k.RulingClan != null && !HasPlayers(k) && !k.StringId.Contains("_rebels"))
            .OrderByDescending(k => k.Clans.Count).FirstOrDefault() ?? throw new StepFailedException("no lords' kingdom");
        var policy = PolicyObject.All.FirstOrDefault(p => !kingdom.ActivePolicies.Contains(p) && !Voted(kingdom, p.StringId))
            ?? throw new StepFailedException($"{kingdom.StringId} has every policy");
        add.Invoke(null, new object[] { kingdom, new KingdomPolicyDecision(kingdom.RulingClan, policy, false), true });
        ctx.Log($"Bellum's court filed '{policy.StringId}' in {kingdom.StringId} on this PC; here it is {(Voted(kingdom, policy.StringId) ? "open" : "not open")}");
        ctx.Report("value", kingdom.StringId);
        ctx.Report("policy", policy.StringId);
        yield break;
    }

    private static bool Voted(Kingdom kingdom, string policy) =>
        kingdom.UnresolvedDecisions.OfType<KingdomPolicyDecision>().Any(d => d.Policy?.StringId == policy);

    /// <summary>
    /// Whether kingdom <c>kingdom</c> has an open vote on policy <c>policy</c>: "open" or "none". With <c>holdSeconds</c>,
    /// reads after that long (a vote sent from a player's PC needs a moment to reach the server); with <c>expect</c>, fails
    /// on anything else.
    /// </summary>
    private static IEnumerable<StepWait?> PolicyVote(StepContext ctx)
    {
        var kingdom = KingdomById(ctx.Arg("kingdom")) ?? throw new StepFailedException($"no kingdom '{ctx.Arg("kingdom")}'");
        if (ctx.HasArg("holdSeconds"))
            yield return ctx.Delay(TimeSpan.FromSeconds(ctx.ArgInt("holdSeconds")));
        var value = Voted(kingdom, ctx.Arg("policy")) ? "open" : "none";
        if (ctx.HasArg("expect"))
            ctx.Assert(value == ctx.Arg("expect"), $"the vote on {ctx.Arg("policy")} in {kingdom.StringId} is {value}, expected {ctx.Arg("expect")}");
        ctx.Report("value", value);
    }

    /// <summary>
    /// Player: Bellum founds a kingdom on this PC as a rebellion or feud does (KingdomCreationSafetyHelper.CreateKingdom).
    /// Coop never gives such a kingdom an ID anyone else knows. Reports "refused" or "made".
    /// </summary>
    private static IEnumerable<StepWait?> FoundKingdom(StepContext ctx)
    {
        var helper = FindType("BellumCivile.KingdomCreationSafetyHelper") ?? throw new StepFailedException("Bellum is not loaded");
        var create = helper.GetMethod("CreateKingdom", Any) ?? throw new StepFailedException("Bellum has no KingdomCreationSafetyHelper.CreateKingdom");
        var founder = Clan.All.FirstOrDefault(c => c != null && !c.IsEliminated && c.Kingdom != null && c.Kingdom.RulingClan != c && !c.IsMinorFaction && c != Clan.PlayerClan)
            ?? throw new StepFailedException("no vassal clan to found a kingdom");
        var made = create.Invoke(null, new object[] { "harness_pc_realm", founder }) as Kingdom;
        ctx.Log($"Bellum on this PC tried to found a kingdom for {founder.StringId}: {(made == null ? "refused" : "made " + made.StringId)}");
        ctx.Report("value", made == null ? "refused" : "made");
        ctx.Assert(made == null, $"Bellum founded {made?.StringId} on this player's PC; it exists only here");
        yield break;
    }

    /// <summary>The standing kingdoms' IDs, sorted (value), for a compare: a kingdom only one side has shows here.</summary>
    private static IEnumerable<StepWait?> Kingdoms(StepContext ctx)
    {
        string Read() => string.Join(",", Kingdom.All.Where(k => k != null && !k.IsEliminated).Select(k => k.StringId ?? "(no id)").OrderBy(s => s, StringComparer.Ordinal));
        yield return ctx.Settled(Read, "the kingdom list");
        ctx.Report("value", Read());
    }

    /// <summary>
    /// Player: Bellum's own dialogue lines, which the module wraps so they don't show on a player's PC in co-op. Counts the
    /// lines whose condition is the module's wrapper and how many of them would show now. Fails unless there are at least
    /// <c>min</c> (default 50) and none would show.
    /// </summary>
    private static IEnumerable<StepWait?> DialogLines(StepContext ctx)
    {
        var manager = Campaign.Current.ConversationManager;
        var sentences = typeof(ConversationManager).GetField("_sentences", Any)?.GetValue(manager) as IEnumerable
            ?? throw new StepFailedException("the conversation manager has no _sentences list");
        var module = FindType("BellumCivileCoopCompat.SubModule")?.Assembly ?? throw new StepFailedException("the compat module is not loaded");
        int wrapped = 0, showing = 0;
        foreach (var sentence in sentences)
        {
            var condition = sentence.GetType().GetProperty("OnCondition", Any)?.GetValue(sentence) as Delegate
                ?? sentence.GetType().GetField("OnCondition", Any)?.GetValue(sentence) as Delegate;
            if (condition?.Method.DeclaringType?.Assembly != module) continue;
            wrapped++;
            if (condition.DynamicInvoke() is true) showing++;
        }
        ctx.Log($"{wrapped} of Bellum's lines are the module's; {showing} would show");
        ctx.Assert(wrapped >= ctx.ArgInt("min", 50), $"only {wrapped} of Bellum's dialogue lines are wrapped");
        ctx.Assert(showing == 0, $"{showing} of Bellum's dialogue lines would show on this player's PC");
        ctx.Report("value", wrapped);
        yield break;
    }

    // ---- relations -------------------------------------------------------------------------------------------------

    /// <summary>
    /// Server: changes the relation between two lords of one lords' kingdom by <c>delta</c> (default 20). Reports the two
    /// heroes' IDs (value "a|b"; also a and b), which are the same on every side.
    /// </summary>
    private static IEnumerable<StepWait?> ChangeRelation(StepContext ctx)
    {
        var kingdom = Kingdom.All.Where(k => k != null && !k.IsEliminated && !HasPlayers(k) && k.Clans.Count(c => c.Leader != null && !c.IsMinorFaction) >= 2)
            .OrderByDescending(k => k.Clans.Count).FirstOrDefault() ?? throw new StepFailedException("no kingdom with two lords");
        var lords = kingdom.Clans.Where(c => c.Leader != null && c.Leader.IsAlive && !c.IsMinorFaction).Select(c => c.Leader).Take(2).ToList();
        var before = lords[0].GetRelation(lords[1]);
        ChangeRelationAction.ApplyRelationChangeBetweenHeroes(lords[0], lords[1], ctx.ArgInt("delta", 20), false);
        ctx.Log($"{lords[0].StringId} and {lords[1].StringId}: {before} -> {lords[0].GetRelation(lords[1])}");
        ctx.Report("value", lords[0].StringId + "|" + lords[1].StringId);
        ctx.Report("a", lords[0].StringId);
        ctx.Report("b", lords[1].StringId);
        yield break;
    }

    /// <summary>The relation between heroes <c>a</c> and <c>b</c> (IDs) as this side's game reads it, once settled.</summary>
    private static IEnumerable<StepWait?> Relation(StepContext ctx)
    {
        Hero Find(string id) => Hero.AllAliveHeroes.FirstOrDefault(h => h.StringId == id) ?? throw new StepFailedException($"no hero '{id}'");
        var a = Find(ctx.Arg("a"));
        var b = Find(ctx.Arg("b"));
        yield return ctx.Settled(() => a.GetRelation(b), $"the relation between {a.StringId} and {b.StringId}");
        ctx.Report("value", a.GetRelation(b));
    }

    // ---- client kingdoms -------------------------------------------------------------------------------------------

    /// <summary>
    /// Server: the three largest lords' kingdoms without players, the first two at peace with each other and the third at
    /// peace with the second. Reports value (first), second and third.
    /// </summary>
    private static IEnumerable<StepWait?> PickKingdoms(StepContext ctx)
    {
        var lords = Kingdom.All.Where(k => k != null && !k.IsEliminated && k.RulingClan != null && !HasPlayers(k) && !k.StringId.Contains("_rebels"))
            .OrderByDescending(k => k.Clans.Count).ToList();
        foreach (var first in lords)
            foreach (var second in lords.Where(k => k != first && !k.IsAtWarWith(first)))
                foreach (var third in lords.Where(k => k != first && k != second && !k.IsAtWarWith(second) && !k.IsAtWarWith(first)))
                {
                    ctx.Report("value", first.StringId);
                    ctx.Report("second", second.StringId);
                    ctx.Report("third", third.StringId);
                    ctx.Log($"{first.StringId}, {second.StringId}, {third.StringId}");
                    yield break;
                }
        throw new StepFailedException("no three lords' kingdoms at peace with each other");
    }

    /// <summary>
    /// Server: makes kingdom <c>client</c> a client of <c>suzerain</c> (Bellum's ClientKingdomBehavior.TryEstablishClientKingdom).
    /// Reports the client's id.
    /// </summary>
    private static IEnumerable<StepWait?> MakeClient(StepContext ctx)
    {
        var client = KingdomById(ctx.Arg("client")) ?? throw new StepFailedException($"no kingdom '{ctx.Arg("client")}'");
        var suzerain = KingdomById(ctx.Arg("suzerain")) ?? throw new StepFailedException($"no kingdom '{ctx.Arg("suzerain")}'");
        var type = FindType("BellumCivile.Behaviors.ClientKingdomBehavior") ?? throw new StepFailedException("Bellum has no client kingdoms");
        var instance = type.GetProperty("Instance", Any)?.GetValue(null) ?? throw new StepFailedException("Bellum's client kingdoms are not running");
        var make = type.GetMethod("TryEstablishClientKingdom", Any) ?? throw new StepFailedException("Bellum has no TryEstablishClientKingdom");
        var args = new object?[] { client, suzerain, true, null };
        var ok = make.Invoke(instance, args) is true;
        ctx.Log($"{client.StringId} as a client of {suzerain.StringId}: {args[3]}");
        ctx.Assert(ok, $"Bellum would not make {client.StringId} a client of {suzerain.StringId}: {args[3]}");
        ctx.Report("value", client.StringId);
        yield break;
    }

    // ---- treaties and alliances ------------------------------------------------------------------------------------

    /// <summary>
    /// Server: Bellum drafts a treaty for the war between <c>a</c> and <c>b</c>, as its daily tick does when a war has run
    /// long enough (ForeignTreatyBehavior.TryQueueAiParley, drafted by <c>drafter</c>, default b). On the server no realm is
    /// "the player's", so this is the path a player's realm takes. Reports Bellum's answer.
    /// </summary>
    private static IEnumerable<StepWait?> QueueTreaty(StepContext ctx)
    {
        var a = KingdomById(ctx.Arg("a")) ?? throw new StepFailedException($"no kingdom '{ctx.Arg("a")}'");
        var b = KingdomById(ctx.Arg("b")) ?? throw new StepFailedException($"no kingdom '{ctx.Arg("b")}'");
        var drafter = KingdomById(ctx.Arg("drafter", b.StringId)) ?? b;
        var scores = Behavior("BellumCivile.Behaviors.WarScoreBehavior") ?? throw new StepFailedException("Bellum's war scores are not running");
        object? war = null;
        yield return ctx.Until(() => (war = scores.GetType().GetMethod("GetActiveWar", Any, null, new[] { typeof(Kingdom), typeof(Kingdom) }, null)?.Invoke(scores, new object[] { a, b })) != null,
            $"Bellum to keep a war score for {a.StringId} and {b.StringId}", TimeSpan.FromSeconds(60));
        // A new war has no score, and Bellum's councils take no treaty in a war a day old: give the war a battle score (as
        // won battles do) and an age, so Bellum drafts terms both councils can accept.
        var battle = ctx.ArgInt("battleScore", 100);
        var age = ctx.ArgInt("ageDays", 120);
        if (age > 0)
        {
            // A war's councils hold out early ("for the sake of honor") and while it is inconclusive: date it back as a
            // long war would be.
            var started = war!.GetType().GetField("_startedDay", Any) ?? throw new StepFailedException("Bellum's war score has no _startedDay");
            started.SetValue(war, (float)started.GetValue(war)! - age);
        }
        if (battle != 0)
        {
            var add = war!.GetType().GetMethod("AddBattleScore", Any) ?? throw new StepFailedException("Bellum's war score has no AddBattleScore");
            add.Invoke(war, new object[] { (float)battle, 100f });
            ctx.Log($"war score after {battle} battle points: {war.GetType().GetProperty("Score", Any)?.GetValue(war)} (attacker {war.GetType().GetProperty("AttackerKingdomId", Any)?.GetValue(war)})");
        }
        var treaties = Behavior("BellumCivile.Behaviors.ForeignTreatyBehavior") ?? throw new StepFailedException("Bellum's treaties are not running");
        var queue = treaties.GetType().GetMethods(Any).FirstOrDefault(m => m.Name == "TryQueueAiParley" && m.GetParameters().Length == 6)
            ?? throw new StepFailedException("Bellum has no TryQueueAiParley(war, forced, preferWhitePeace, reason, drafter, out report)");
        object?[] args = Array.Empty<object?>();
        var queued = false;
        foreach (var by in new[] { drafter, drafter == a ? b : a })
        {
            args = new object?[] { war, false, ctx.Arg("whitePeace", "false") == "true", "harness", by, null };
            queued = queue.Invoke(treaties, args) is true;
            ctx.Log($"treaty for {a.StringId} and {b.StringId}, drafted by {by.StringId}: {(queued ? "queued" : "refused")}: {args[5]}");
            if (queued) break;
        }
        ctx.Assert(queued, $"Bellum would not draft a treaty: {args[5]}");
        ctx.Report("value", args[5]?.ToString() ?? "");
    }

    /// <summary>
    /// Server: lets campaign time run (Coop's fastest speed) until kingdom <c>realm</c> has an open peace vote with
    /// <c>enemy</c>, then stops it, so the vote is still open for the player. Fails after <c>days</c> game days (default 3).
    /// </summary>
    private static IEnumerable<StepWait?> AwaitPeaceVote(StepContext ctx)
    {
        var realm = KingdomById(ctx.Arg("realm")) ?? throw new StepFailedException($"no kingdom '{ctx.Arg("realm")}'");
        var enemy = KingdomById(ctx.Arg("enemy")) ?? throw new StepFailedException($"no kingdom '{ctx.Arg("enemy")}'");
        bool Open() => realm.UnresolvedDecisions.OfType<MakePeaceKingdomDecision>().Any(d => d.FactionToMakePeaceWith == enemy);
        var end = CampaignTime.Now + CampaignTime.Days(ctx.ArgInt("days", 3));
        if (!Open())
        {
            SetTime(ctx, play: true, fast: true);
            try
            {
                yield return ctx.Until(() => Open() || CampaignTime.Now >= end, $"a peace vote in {realm.StringId}", TimeSpan.FromSeconds(ctx.ArgInt("timeoutSeconds", 600)));
            }
            finally
            {
                SetTime(ctx, play: false);
            }
        }
        ctx.Assert(Open(), $"no peace vote with {enemy.StringId} opened in {realm.StringId} within {ctx.ArgInt("days", 3)} game days");
        ctx.Report("value", CampaignTime.Now.ToDays.ToString("0.00"));
    }

    /// <summary>Server: Bellum's own report of its treaties (ForeignTreatyBehavior.BuildDebugReport), as evidence.</summary>
    private static IEnumerable<StepWait?> Treaties(StepContext ctx)
    {
        var treaties = Behavior("BellumCivile.Behaviors.ForeignTreatyBehavior") ?? throw new StepFailedException("Bellum's treaties are not running");
        var report = treaties.GetType().GetMethod("BuildDebugReport", Any, null, Type.EmptyTypes, null)?.Invoke(treaties, null) as string ?? "";
        ctx.Dump("bellum-treaties", report);
        foreach (var line in report.Split('\n').Take(30)) ctx.Log(line.TrimEnd());
        ctx.Report("value", CampaignTime.Now.ToDays.ToString("0.00"));
        yield break;
    }

    /// <summary>
    /// Server: kingdom <c>caller</c> allies with <c>called</c> and calls it to its war against <c>enemy</c> (vanilla's
    /// StartAlliance and StartCallToWarAgreement, as an accepted call to arms does), declaring that war first if needed.
    /// Waits until <c>called</c> is at war with <c>enemy</c>.
    /// </summary>
    private static IEnumerable<StepWait?> CallToArms(StepContext ctx)
    {
        var caller = KingdomById(ctx.Arg("caller")) ?? throw new StepFailedException($"no kingdom '{ctx.Arg("caller")}'");
        var called = KingdomById(ctx.Arg("called")) ?? throw new StepFailedException($"no kingdom '{ctx.Arg("called")}'");
        var enemy = KingdomById(ctx.Arg("enemy")) ?? throw new StepFailedException($"no kingdom '{ctx.Arg("enemy")}'");
        var iface = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("TaleWorlds.CampaignSystem.CampaignBehaviors.IAllianceCampaignBehavior", false)).FirstOrDefault(t => t != null)
            ?? throw new StepFailedException("this game has no alliances (before 1.3?)");
        var alliances = typeof(Campaign).GetMethod(nameof(Campaign.GetCampaignBehavior))!.MakeGenericMethod(iface).Invoke(Campaign.Current, null)
            ?? throw new StepFailedException("the game's alliance behaviour is not running");
        if (!caller.IsAtWarWith(enemy))
            DeclareWarAction.ApplyByDefault(caller, enemy);
        iface.GetMethod("StartAlliance")!.Invoke(alliances, new object[] { caller, called });
        iface.GetMethod("StartCallToWarAgreement")!.Invoke(alliances, new object[] { caller, called, enemy, 0, false });
        yield return ctx.Until(() => called.IsAtWarWith(enemy), $"{called.StringId} to be called to war against {enemy.StringId}", Sync);
        var check = iface.GetMethod("IsAtWarByCallToWarAgreement")!;
        var args = new object?[] { called, enemy, null };
        ctx.Assert(check.Invoke(alliances, args) is true, $"{called.StringId} is at war with {enemy.StringId}, but not by a call to arms");
        ctx.Log($"{caller.StringId} called {called.StringId} to its war against {enemy.StringId}");
        ctx.Report("value", called.StringId);
    }

    // ---- claim feuds -----------------------------------------------------------------------------------------------

    private static bool AtForeignWar(Kingdom kingdom) =>
        Kingdom.All.Any(k => k != null && k != kingdom && !k.IsEliminated && kingdom.IsAtWarWith(k));

    /// <summary>
    /// Server: starts a claim feud for a lords' clan and pushes it to war (ClaimFeudBehavior.TryForceStartFeud, then
    /// TryAdvanceFeudPressure in steps, as Bellum's own cheats do), trying clans in turn. Reports the feud's two realms at
    /// war: value (the claimant's side) and other.
    /// </summary>
    private static IEnumerable<StepWait?> StartFeud(StepContext ctx)
    {
        var feuds = Behavior("BellumCivile.Behaviors.ClaimFeudBehavior") ?? throw new StepFailedException("Bellum's claim feuds are not running");
        var start = feuds.GetType().GetMethod("TryForceStartFeud", Any) ?? throw new StepFailedException("Bellum has no TryForceStartFeud");
        var push = feuds.GetType().GetMethod("TryAdvanceFeudPressure", Any) ?? throw new StepFailedException("Bellum has no TryAdvanceFeudPressure");
        var clans = Clan.All.Where(c => c != null && !c.IsEliminated && !c.IsMinorFaction && c.Leader != null && c.Kingdom != null && c.Kingdom.RulingClan != c
                && !HasPlayers(c.Kingdom) && c != Clan.PlayerClan)
            // Bellum raises no private banners while the realm fights a foreign war: realms at peace first.
            .OrderBy(c => AtForeignWar(c.Kingdom!) ? 1 : 0).ThenByDescending(c => c.Tier).Take(ctx.ArgInt("tries", 12)).ToList();
        ctx.Log("realms at peace: " + string.Join(", ", Kingdom.All.Where(k => k != null && !k.IsEliminated && !AtForeignWar(k)).Select(k => k.StringId)));
        var titles = Behavior("BellumCivile.Behaviors.FeudalTitleBehavior") ?? throw new StepFailedException("Bellum's titles are not running");
        var fabrication = Behavior("BellumCivile.Behaviors.FeudalClaimFabricationBehavior") ?? throw new StepFailedException("Bellum's claim fabrication is not running");
        var resolve = FindType("BellumCivile.CheatCommands")?.GetMethod("ResolveFeudalTitleForDebug", Any) ?? throw new StepFailedException("Bellum has no ResolveFeudalTitleForDebug");
        var fabricate = fabrication.GetType().GetMethod("TryStartFabrication", Any) ?? throw new StepFailedException("Bellum has no TryStartFabrication");
        var advance = fabrication.GetType().GetMethod("DebugAdvanceFabrication", Any) ?? throw new StepFailedException("Bellum has no DebugAdvanceFabrication");
        foreach (var clan in clans)
        {
            // A feud needs a claim on a fief another clan of the realm holds: fabricate one and complete it at once, as
            // Bellum's own fabricate_claim and advance_fabrication cheats do.
            var fief = clan.Kingdom!.Settlements.Where(st => (st.IsTown || st.IsCastle) && st.OwnerClan != null && st.OwnerClan != clan && st.OwnerClan != clan.Kingdom.RulingClan)
                .OrderBy(st => st.IsTown ? 1 : 0).FirstOrDefault();
            if (fief != null && resolve.Invoke(null, new object[] { titles, fief.StringId }) is { } title)
            {
                // Bellum raises no private banners while the realm fights a foreign war, and a new world has every realm at
                // war: end the realm's wars the way Bellum's own treaties do (its peace mark lets them past its guard).
                if (AtForeignWar(clan.Kingdom!))
                {
                    var context = FindType("BellumCivile.BellumPeaceResolutionContext")?.GetMethod("RunBellumPeaceResolution", Any)
                        ?? throw new StepFailedException("Bellum has no BellumPeaceResolutionContext.RunBellumPeaceResolution");
                    var realm = clan.Kingdom!;
                    foreach (var enemy in Kingdom.All.Where(k => k != null && k != realm && !k.IsEliminated && realm.IsAtWarWith(k)).ToList())
                        context.Invoke(null, new object[] { (Action)(() => MakePeaceAction.Apply(realm, enemy)) });
                    ctx.Log($"{realm.StringId}: made peace with its enemies; at war now: {AtForeignWar(realm)}");
                }
                var fab = new object?[] { clan, title, false, null, null };
                var started = fabricate.Invoke(fabrication, fab) is true;
                var adv = new object?[] { clan, 1f, null };
                if (started) advance.Invoke(fabrication, adv);
                ctx.Log($"{clan.StringId}: claim on {fief.StringId}: {(started ? "fabricated; " + adv[2] : "not started (" + fab[4] + ")")}");

                // A claim feud's private war, started at once as Bellum does when a claimant takes the fief by force
                // (TryStartImmediateRevocationWar), so the ruler's judgment of a petition doesn't decide the test.
                if (started && fief.OwnerClan is { } holder && feuds.GetType().GetMethod("TryStartImmediateRevocationWar", Any) is { } war)
                {
                    var strength = Enum.Parse(war.GetParameters()[4].ParameterType, "Weak");
                    var standing = new HashSet<Kingdom>(Kingdom.All);
                    var warArgs = new object?[] { titles, clan, holder, title, strength, null, null };
                    var begun = war.Invoke(feuds, warArgs) is true;
                    ctx.Log($"{clan.StringId}: private war over {fief.StringId} against {holder.StringId}: {(begun ? "begun" : "refused")}: {warArgs[6]}");
                    if (begun)
                    {
                        Kingdom? Side() => Kingdom.All.FirstOrDefault(k => k != null && !standing.Contains(k) && !k.IsEliminated && Kingdom.All.Any(o => o != k && !o.IsEliminated && k.IsAtWarWith(o)));
                        // The feud is now "defied, war pending": Bellum's own tick founds its realms and starts the war.
                        var until = DateTime.UtcNow + TimeSpan.FromSeconds(ctx.ArgInt("warSeconds", 300));
                        SetTime(ctx, play: true, fast: true);
                        try
                        {
                            while (Side() == null && DateTime.UtcNow < until) yield return null;
                        }
                        finally
                        {
                            SetTime(ctx, play: false);
                        }
                        if (Side() is { } side)
                        {
                            var other = Kingdom.All.First(o => o != side && !o.IsEliminated && side.IsAtWarWith(o));
                            ctx.Log($"feud war: {side.StringId} against {other.StringId}");
                            ctx.Report("value", side.StringId);
                            ctx.Report("other", other.StringId);
                            yield break;
                        }
                        var record = (feuds.GetType().GetMethod("GetActiveFeuds", Any)?.Invoke(feuds, null) as IEnumerable)?.Cast<object>()
                            .FirstOrDefault(r => r.GetType().GetProperty("ClaimantClanId")?.GetValue(r) as string == clan.StringId);
                        ctx.Log($"{clan.StringId}: no new realm went to war; feud {record?.GetType().GetProperty("State")?.GetValue(record)}: {record?.GetType().GetProperty("DebugReason")?.GetValue(record)}");
                    }
                }
            }
            var args = new object?[] { clan, null };
            if (start.Invoke(feuds, args) is not true)
            {
                ctx.Log($"{clan.StringId}: no feud ({args[1]})");
                continue;
            }
            ctx.Log($"{clan.StringId}: {args[1]}");
            var before = new HashSet<Kingdom>(Kingdom.All);
            for (var i = 0; i < 8; i++)
            {
                var pushArgs = new object?[] { clan, 100f, null };
                push.Invoke(feuds, pushArgs);
                ctx.Log($"{clan.StringId}: pressure +100: {pushArgs[2]}");
                yield return null;
                var war = Kingdom.All.FirstOrDefault(k => k != null && !before.Contains(k) && !k.IsEliminated && k.StringId.Contains("feud"))
                    ?? (clan.Kingdom != null && !before.Contains(clan.Kingdom) ? clan.Kingdom : null);
                if (war != null)
                {
                    var other = Kingdom.All.FirstOrDefault(k => k != null && k != war && !k.IsEliminated && war.IsAtWarWith(k) && before.Contains(k));
                    if (other != null)
                    {
                        ctx.Log($"feud war: {war.StringId} against {other.StringId}");
                        ctx.Report("value", war.StringId);
                        ctx.Report("other", other.StringId);
                        yield break;
                    }
                }
            }
            ctx.Log($"{clan.StringId}: the feud never came to war");
        }
        throw new StepFailedException($"no feud of {clans.Count} clans came to war");
    }
}
