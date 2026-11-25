using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ModderLords.Testing;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Library;
using static ModderLords.Tests.Bellum.Steps;

namespace ModderLords.Tests.Bellum;

/// <summary>
/// Steps for player agency (docs/BELLUM-COOP-COVERAGE.md, stage 3): the module's channel between a player's PC and the
/// server (Agency), and Bellum's questions put to the player concerned (Prompts). Read by reflection like <see cref="Steps"/>.
/// </summary>
public static class AgencySteps
{
    public static void Register()
    {
        InGameTests.Register("bellum.agency", StepSide.Any, ChannelOpen);
        InGameTests.Register("bellum.agencyEcho", StepSide.Client, Echo);
        InGameTests.Register("bellum.answerSeconds", StepSide.Server, AnswerSeconds);
        InGameTests.Register("bellum.promptShown", StepSide.Client, PromptShown);
        InGameTests.Register("bellum.answer", StepSide.Client, AnswerPrompt);
        InGameTests.Register("bellum.createFaction", StepSide.Server, CreateFaction);
        InGameTests.Register("bellum.stateRequest", StepSide.Client, StateRequest);
        InGameTests.Register("bellum.factions", StepSide.Any, Factions);
        InGameTests.Register("bellum.titles", StepSide.Any, Titles);
        InGameTests.Register("bellum.liftGrace", StepSide.Server, LiftGrace);
        InGameTests.Register("bellum.foundFaction", StepSide.Client, FoundFaction);
        InGameTests.Register("bellum.hasFaction", StepSide.Any, HasFaction);
    }

    /// <summary>
    /// Server: a court faction of a lords' realm's vassals forms, without an ultimatum (CheatCommands.CreateFactionFromArgs,
    /// as Bellum's own civilwars cheats do), in realm <c>kingdom</c> if given. Reports "kingdom:leader:type", and
    /// leaderHero (the leading house's leader).
    /// </summary>
    private static IEnumerable<StepWait?> CreateFaction(StepContext ctx)
    {
        var create = FindType("BellumCivile.CheatCommands")?.GetMethod("CreateFactionFromArgs", Any) ?? throw new StepFailedException("Bellum has no CreateFactionFromArgs");
        // Bellum registers no faction in a realm's first year ("the realm is pacified"), and the suite runs on a new world:
        // lift that grace for the realm tried, on the server only, as bellum.rebellion does.
        var managerType = FindType("BellumCivile.Behaviors.FactionManagerBehavior");
        var manager = managerType == null ? null : typeof(Campaign).GetMethod(nameof(Campaign.GetCampaignBehavior))!.MakeGenericMethod(managerType).Invoke(Campaign.Current, null);
        var grace = managerType?.GetField("_initialCivilWarGrace", Any)?.GetValue(manager) as IDictionary;
        // With kingdom, that realm only (one a player may rule); else the largest realms without players.
        var realms = ctx.HasArg("kingdom")
            ? new[] { KingdomById(ctx.Arg("kingdom")) ?? throw new StepFailedException($"no kingdom '{ctx.Arg("kingdom")}'") }
            : Kingdom.All.Where(k => k != null && !k.IsEliminated && k.RulingClan != null && !HasPlayers(k) && !k.StringId.Contains("_rebels"))
                .OrderByDescending(k => k.Clans.Count).Take(6).ToArray();
        foreach (var kingdom in realms)
        {
            if (grace != null && grace.Contains(kingdom)) grace.Remove(kingdom);
            var args = new object?[] { new List<string> { kingdom.StringId, ctx.Arg("type", "Independence") }, null };
            var faction = create.Invoke(null, args);
            if (faction == null) { ctx.Log($"{kingdom.StringId}: {args[1]}"); continue; }
            var key = Describe(faction);
            var registered = (managerType?.GetField("_activeFactions", Any)?.GetValue(manager) as IList)?.Contains(faction) == true;
            ctx.Log("formed: " + key + (registered ? "" : " (not registered)"));
            if (!registered) continue;
            ctx.Report("value", key);
            ctx.Report("leaderHero", (faction.GetType().GetProperty("Leader", Any)?.GetValue(faction) as Clan)?.Leader?.StringId ?? "");
            yield break;
        }
        throw new StepFailedException("no realm formed a faction");
    }

    private static string Describe(object faction)
    {
        var t = faction.GetType();
        var kingdom = t.GetProperty("ParentKingdom", Any)?.GetValue(faction) as Kingdom;
        var leader = t.GetProperty("Leader", Any)?.GetValue(faction) as Clan;
        return (kingdom?.StringId ?? "?") + ":" + (leader?.StringId ?? "?") + ":" + t.GetProperty("Type", Any)?.GetValue(faction);
    }

    /// <summary>Server: lifts Bellum's first-year rebellion grace ("the realm is pacified") for realm <c>kingdom</c>.</summary>
    private static IEnumerable<StepWait?> LiftGrace(StepContext ctx)
    {
        var kingdom = KingdomById(ctx.Arg("kingdom")) ?? throw new StepFailedException($"no kingdom '{ctx.Arg("kingdom")}'");
        var managerType = FindType("BellumCivile.Behaviors.FactionManagerBehavior") ?? throw new StepFailedException("Bellum is not loaded");
        var manager = typeof(Campaign).GetMethod(nameof(Campaign.GetCampaignBehavior))!.MakeGenericMethod(managerType).Invoke(Campaign.Current, null);
        var grace = managerType.GetField("_initialCivilWarGrace", Any)?.GetValue(manager) as IDictionary;
        var had = grace != null && grace.Contains(kingdom);
        if (had) grace!.Remove(kingdom);
        ctx.Report("value", had ? "lifted" : "none");
        yield break;
    }

    /// <summary>
    /// Player: founds a faction from Bellum's Factions screen as the player does: Create, pick the first faction type on
    /// offer, Found (FactionsWindowVM.ExecuteCreate, OnFactionTypeSelected, ExecuteConfirmCreate). In co-op the module
    /// sends that to the server. Reports the faction type.
    /// </summary>
    private static IEnumerable<StepWait?> FoundFaction(StepContext ctx)
    {
        var window = FindType("BellumCivile.UI.FactionsWindowVM") ?? throw new StepFailedException("Bellum has no Factions screen");
        var vm = Activator.CreateInstance(window);
        try
        {
            window.GetMethod("ExecuteCreate", Any)!.Invoke(vm, null);
            var types = (window.GetProperty("AvailableFactionTypes", Any)?.GetValue(vm) as IEnumerable)?.Cast<object>().ToList() ?? new List<object>();
            string Of(object o, string n) => o.GetType().GetProperty(n, Any)?.GetValue(o)?.ToString() ?? "";
            ctx.Log("on offer: " + string.Join(", ", types.Select(t => Of(t, "ActionKind") + "/" + Of(t, "FactionType") + (Of(t, "IsEnabled") == "True" ? "" : " (off)"))));
            var pick = types.FirstOrDefault(t => Of(t, "ActionKind") == "FoundFaction" && Of(t, "IsEnabled") == "True" && (!ctx.HasArg("type") || Of(t, "FactionType") == ctx.Arg("type")))
                ?? throw new StepFailedException("Bellum offers this player no faction to found");
            window.GetMethod("OnFactionTypeSelected", Any)!.Invoke(vm, new[] { pick });
            window.GetMethod("ExecuteConfirmCreate", Any)!.Invoke(vm, null);
            ctx.Report("value", Of(pick, "FactionType"));
        }
        finally
        {
            window.GetMethod("OnFinalize", Any)?.Invoke(vm, null);
        }
        yield break;
    }

    /// <summary>
    /// Whether Bellum on this side has a faction in realm <c>kingdom</c> led by the clan of hero <c>heroName</c>, of type
    /// <c>type</c>; waits for it up to timeoutSeconds (default 30). Clans are matched by name: a player's ids differ between PCs.
    /// </summary>
    private static IEnumerable<StepWait?> HasFaction(StepContext ctx)
    {
        var clan = HeroNames.Single(ctx.Arg("heroName")).Clan ?? throw new StepFailedException("the hero has no clan");
        var type = FindType("BellumCivile.Behaviors.FactionManagerBehavior") ?? throw new StepFailedException("Bellum is not loaded");
        bool Has()
        {
            var manager = typeof(Campaign).GetMethod(nameof(Campaign.GetCampaignBehavior))!.MakeGenericMethod(type).Invoke(Campaign.Current, null);
            var list = type.GetField("_activeFactions", Any)?.GetValue(manager) as IEnumerable;
            return list?.Cast<object>().Any(f => f != null
                && (f.GetType().GetProperty("ParentKingdom", Any)?.GetValue(f) as Kingdom)?.StringId == ctx.Arg("kingdom")
                && (f.GetType().GetProperty("Leader", Any)?.GetValue(f) as Clan)?.Name?.ToString() == clan.Name?.ToString()
                && f.GetType().GetProperty("Type", Any)?.GetValue(f)?.ToString() == ctx.Arg("type")) == true;
        }
        yield return ctx.Until(Has, $"a {ctx.Arg("type")} faction led by {clan.Name} in {ctx.Arg("kingdom")}", TimeSpan.FromSeconds(ctx.ArgInt("timeoutSeconds", 30)));
        ctx.Report("value", "yes");
    }

    /// <summary>Player: asks the server for its Bellum state and waits until every mirrored behaviour has been applied here.</summary>
    private static IEnumerable<StepWait?> StateRequest(StepContext ctx)
    {
        var mirror = FindType("BellumCivileCoopCompat.StateMirror") ?? throw new StepFailedException("the compat module mirrors no state (older than 1.4.0)");
        var applied = mirror.GetField("Applied", Any)?.GetValue(null) as IDictionary ?? throw new StepFailedException("StateMirror has no Applied");
        var names = mirror.GetField("Behaviours", Any)?.GetValue(null) as string[] ?? new string[0];
        int Rev(string n) { lock (applied) return applied.Contains(n) ? (int)applied[n]! : 0; }
        var before = names.ToDictionary(n => n, Rev);
        ctx.Assert(mirror.GetMethod("Request", Any)!.Invoke(null, null) is true, "the request could not be sent (no channel)");
        yield return ctx.Until(() => names.All(n => Rev(n) > before[n]), "the server's Bellum state to arrive", TimeSpan.FromSeconds(ctx.ArgInt("timeoutSeconds", 60)));
        ctx.Report("value", string.Join(",", names.Select(n => n.Substring(n.LastIndexOf('.') + 1) + "=" + Rev(n))));
    }

    /// <summary>Bellum's active factions on this side, sorted ("kingdom:leader:type", comma separated), for a compare.</summary>
    private static IEnumerable<StepWait?> Factions(StepContext ctx)
    {
        var type = FindType("BellumCivile.Behaviors.FactionManagerBehavior") ?? throw new StepFailedException("Bellum is not loaded");
        var manager = typeof(Campaign).GetMethod(nameof(Campaign.GetCampaignBehavior))!.MakeGenericMethod(type).Invoke(Campaign.Current, null);
        var list = type.GetField("_activeFactions", Any)?.GetValue(manager) as IEnumerable ?? throw new StepFailedException("Bellum keeps no _activeFactions");
        var value = string.Join(",", list.Cast<object>().Where(f => f != null).Select(Describe).OrderBy(x => x, StringComparer.Ordinal));
        if (ctx.HasArg("contains"))
            ctx.Assert(value.Split(',').Contains(ctx.Arg("contains")), $"this side has no faction {ctx.Arg("contains")}; it has: {(value.Length == 0 ? "none" : value)}");
        ctx.Report("value", value.Length == 0 ? "none" : value);
        yield break;
    }

    /// <summary>A digest of Bellum's titles on this side (title id = de facto holder, sorted), for a compare.</summary>
    private static IEnumerable<StepWait?> Titles(StepContext ctx)
    {
        var type = FindType("BellumCivile.Behaviors.FeudalTitleBehavior") ?? throw new StepFailedException("Bellum is not loaded");
        var titles = typeof(Campaign).GetMethod(nameof(Campaign.GetCampaignBehavior))!.MakeGenericMethod(type).Invoke(Campaign.Current, null);
        var byId = type.GetField("_titlesById", Any)?.GetValue(titles) as IDictionary ?? throw new StepFailedException("Bellum keeps no _titlesById");
        var rows = new List<string>();
        foreach (DictionaryEntry e in byId)
            rows.Add(e.Key + "=" + e.Value?.GetType().GetProperty("DeFactoHolderClanId", Any)?.GetValue(e.Value));
        rows.Sort(StringComparer.Ordinal);
        ctx.Dump("bellum-titles-" + ctx.Side.Replace(':', '-'), string.Join(Environment.NewLine, rows));
        var digest = 17;
        foreach (var row in rows) foreach (var ch in row) digest = unchecked(digest * 31 + ch);
        ctx.Report("value", rows.Count + ":" + digest.ToString("x8"));
        yield break;
    }

    private static Type Agency() => FindType("BellumCivileCoopCompat.Agency") ?? throw new StepFailedException("the compat module has no player agency (older than 1.4.0)");

    private static Type Prompts() => FindType("BellumCivileCoopCompat.Prompts") ?? throw new StepFailedException("the compat module has no prompts (older than 1.4.0)");

    private static List<string> Recent() => Agency().GetProperty("Recent", Any)?.GetValue(null) as List<string> ?? new List<string>();

    /// <summary>Waits until this side's agency channel is open (Coop's session built the module's network part).</summary>
    private static IEnumerable<StepWait?> ChannelOpen(StepContext ctx)
    {
        var agency = Agency();
        bool Open() => agency.GetProperty("Channel", Any)?.GetValue(null) != null;
        yield return ctx.Until(Open, "the module's agency channel to open", TimeSpan.FromSeconds(ctx.ArgInt("timeoutSeconds", 30)));
        ctx.Report("value", "open");
    }

    /// <summary>
    /// Player: sends an echo request to the server, which answers with the name of the hero it says sent it. Fails unless
    /// the answer names this player's hero (the server tells who sent a request; a message cannot claim to be anyone).
    /// </summary>
    private static IEnumerable<StepWait?> Echo(StepContext ctx)
    {
        var token = "t" + DateTime.UtcNow.Ticks;
        var send = Agency().GetMethod("ToServer", Any) ?? throw new StepFailedException("Agency has no ToServer");
        ctx.Assert(send.Invoke(null, new object[] { "echo", new[] { token } }) is true, "the request could not be sent (no channel)");
        string? reply = null;
        yield return ctx.Until(() => (reply = Recent().FirstOrDefault(r => r.StartsWith("echo ") && r.EndsWith("|" + token))) != null,
            "the server's echo", TimeSpan.FromSeconds(20));
        var name = reply!.Substring(5).Split('|')[0];
        var mine = Hero.MainHero?.Name?.ToString() ?? "";
        ctx.Log($"the server says the request came from {name}; this player is {mine}");
        // Bellum puts a feudal title in front of a hero's name ("Captain ..."), so the server's name may carry one.
        ctx.Assert(name == mine || name.EndsWith(" " + mine), $"the server took the request as {name}'s, not {mine}'s");
        ctx.Report("value", name);
    }

    /// <summary>Server: how long a player has to answer one of Bellum's questions (<c>seconds</c>).</summary>
    private static IEnumerable<StepWait?> AnswerSeconds(StepContext ctx)
    {
        Prompts().GetField("AnswerSeconds", Any)!.SetValue(null, (double)ctx.ArgInt("seconds"));
        ctx.Report("value", ctx.ArgInt("seconds"));
        yield break;
    }

    /// <summary>
    /// Player: waits for one of Bellum's questions from the server and closes it on screen (the next step answers it the
    /// way its buttons do). Reports its id (value) and its title.
    /// </summary>
    private static IEnumerable<StepWait?> PromptShown(StepContext ctx)
    {
        // The first question this run has not used yet (it may have arrived before this step began).
        var used = ctx.State.TryGetValue("bellum.prompts", out var u) && u is HashSet<string> set ? set : new HashSet<string>();
        ctx.State["bellum.prompts"] = used;
        string? prompt = null;
        yield return ctx.Until(() => (prompt = Recent().Where(r => r.StartsWith("prompt ") || r.StartsWith("choose ")).FirstOrDefault(r => !used.Contains(r.Substring(7).Split('|')[0]))) != null,
            "a question from the server", TimeSpan.FromSeconds(ctx.ArgInt("timeoutSeconds", 60)));
        var parts = prompt!.Substring(7).Split('|');
        used.Add(parts[0]);
        ctx.Log($"asked: {parts.ElementAtOrDefault(1)}: {parts.ElementAtOrDefault(2)} [{parts.ElementAtOrDefault(3)} / {parts.ElementAtOrDefault(4)}]");
        yield return null;
        InformationManager.HideInquiry();
        ctx.Report("value", parts[0]);
        ctx.Report("title", parts.ElementAtOrDefault(1) ?? "");
    }

    /// <summary>Player: answers question <c>id</c> yes or no (<c>answer</c>), or a list question "pick:N", as its buttons do.</summary>
    private static IEnumerable<StepWait?> AnswerPrompt(StepContext ctx)
    {
        var said = ctx.Arg("answer", "no");
        if (said.StartsWith("pick:"))
        {
            // A list question: the choices by place, as its buttons send them.
            var reply = Prompts().GetMethod("Reply", Any) ?? throw new StepFailedException("Prompts has no Reply (older than 1.5.0)");
            ctx.Assert(reply.Invoke(null, new object[] { ctx.Arg("id"), said }) is true, "the answer could not be sent");
            ctx.Report("value", said);
            yield break;
        }
        var answer = Prompts().GetMethod("Answer", Any) ?? throw new StepFailedException("Prompts has no Answer");
        var yes = said == "yes";
        ctx.Assert(answer.Invoke(null, new object[] { ctx.Arg("id"), yes }) is true, "the answer could not be sent");
        ctx.Report("value", yes ? "yes" : "no");
        yield break;
    }
}
