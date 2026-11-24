using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ModderLords.Testing;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.Election;
using static ModderLords.Tests.Bellum.Steps;

namespace ModderLords.Tests.Bellum;

/// <summary>
/// Steps for stage 2 of docs/BELLUM-COOP-COVERAGE.md: Bellum's AI leaves real players' clans and realms alone on the
/// server (the module's PlayersNotAi), and a ruler's death is settled once by Bellum's succession law (CrownAccession).
/// Read by reflection like <see cref="Steps"/>.
/// </summary>
public static class PlayersSteps
{
    public static void Register()
    {
        InGameTests.Register("bellum.joinKingdom", StepSide.Server, JoinKingdom);
        InGameTests.Register("bellum.addVassals", StepSide.Server, AddVassals);
        InGameTests.Register("bellum.clanState", StepSide.Any, ClanState);
        InGameTests.Register("bellum.treason", StepSide.Server, Treason);
        InGameTests.Register("bellum.ultimatum", StepSide.Server, Ultimatum);
        InGameTests.Register("bellum.ask", StepSide.Server, Ask);
        InGameTests.Register("bellum.keptFromPlayers", StepSide.Server, KeptFromPlayers);
        InGameTests.Register("bellum.killRuler", StepSide.Server, KillRuler);
        InGameTests.Register("bellum.ruler", StepSide.Any, Ruler);
        InGameTests.Register("bellum.enemies", StepSide.Any, Enemies);
    }

    private static object? Behavior(string typeName)
    {
        var type = FindType(typeName) ?? throw new StepFailedException($"{typeName} is not loaded");
        return typeof(Campaign).GetMethod(nameof(Campaign.GetCampaignBehavior))!.MakeGenericMethod(type).Invoke(Campaign.Current, null);
    }

    private static Clan PlayerClan(StepContext ctx) => HeroNames.Single(ctx.Arg("heroName")).Clan ?? throw new StepFailedException("the hero has no clan");

    // ---- setting up a player's place in a realm --------------------------------------------------------------------

    /// <summary>
    /// Server: a hero's clan joins kingdom <c>kingdom</c> (default: the largest lords' kingdom) as a vassal, and is given
    /// a fief of that realm so Bellum has something to take. Reports the kingdom (value) and the fief.
    /// </summary>
    private static IEnumerable<StepWait?> JoinKingdom(StepContext ctx)
    {
        var clan = PlayerClan(ctx);
        var kingdom = ctx.HasArg("kingdom") ? KingdomById(ctx.Arg("kingdom")) ?? throw new StepFailedException($"no kingdom '{ctx.Arg("kingdom")}'")
            : Kingdom.All.Where(k => k != null && !k.IsEliminated && k.RulingClan != null && !HasPlayers(k) && !k.StringId.Contains("_rebels"))
                .OrderByDescending(k => k.Clans.Count).First();
        if (clan.Kingdom != kingdom)
            ChangeKingdomAction.ApplyByJoinToKingdom(clan, kingdom, default, false);
        yield return ctx.Until(() => clan.Kingdom == kingdom, $"{clan.StringId} to join {kingdom.StringId}", Sync);
        var fief = clan.Settlements.FirstOrDefault(s => s.IsCastle || s.IsTown)
            ?? kingdom.Settlements.Where(s => s.IsCastle && s.OwnerClan != kingdom.RulingClan).OrderBy(s => s.StringId).FirstOrDefault();
        if (fief != null && fief.OwnerClan != clan)
            ChangeOwnerOfSettlementAction.ApplyByGift(fief, clan.Leader);
        if (fief != null)
            yield return ctx.Until(() => fief.OwnerClan == clan, $"{clan.StringId} to hold {fief.StringId}", Sync);
        ctx.Report("value", kingdom.StringId);
        ctx.Report("fief", fief?.StringId ?? "");
    }

    /// <summary>Server: <c>count</c> lords' clans (default 3) of other realms join kingdom <c>kingdom</c> as its vassals.</summary>
    private static IEnumerable<StepWait?> AddVassals(StepContext ctx)
    {
        var kingdom = KingdomById(ctx.Arg("kingdom")) ?? throw new StepFailedException($"no kingdom '{ctx.Arg("kingdom")}'");
        var clans = Clan.All.Where(c => c != null && !c.IsEliminated && !c.IsMinorFaction && c.Leader != null && c.Leader.IsAlive && c.Kingdom != null
                && c.Kingdom != kingdom && c.Kingdom.RulingClan != c && !HasPlayers(c.Kingdom) && c != Clan.PlayerClan)
            .OrderByDescending(c => c.Tier).Take(ctx.ArgInt("count", 3)).ToList();
        foreach (var c in clans)
            ChangeKingdomAction.ApplyByJoinToKingdom(c, kingdom, default, false);
        yield return ctx.Until(() => clans.All(c => c.Kingdom == kingdom), $"{clans.Count} clans to join {kingdom.StringId}", Sync);
        ctx.Log("joined: " + string.Join(", ", clans.Select(c => c.StringId)));
        ctx.Report("value", clans.Count);
    }

    /// <summary>
    /// A hero's clan as this side sees it, for a compare: "alive|kingdom|fiefs|members" (leader alive, the realm, the
    /// fiefs it holds, its living members). With <c>expect</c> (a part, e.g. "alive" or a kingdom id), fails without it.
    /// </summary>
    private static IEnumerable<StepWait?> ClanState(StepContext ctx)
    {
        var hero = HeroNames.Single(ctx.Arg("heroName"));
        var clan = hero.Clan;
        string Read() => string.Join("|",
            hero.IsAlive ? "alive" : "dead",
            clan?.Kingdom?.StringId ?? "no realm",
            string.Join(",", clan?.Settlements.Where(s => s.IsTown || s.IsCastle).Select(s => s.StringId).OrderBy(s => s, StringComparer.Ordinal) ?? Enumerable.Empty<string>()),
            clan?.Heroes.Count(h => h.IsAlive).ToString() ?? "0");
        yield return ctx.Settled(Read, $"{hero.StringId}'s clan");
        var value = Read();
        foreach (var want in ctx.Arg("expect", "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            ctx.Assert(value.Split('|').Any(part => part == want || part.Split(',').Contains(want)), $"{hero.StringId}'s clan is {value}, expected {want}");
        ctx.Report("value", value);
        ctx.Report("clan", clan?.StringId ?? "");
    }

    // ---- Bellum's AI aimed at a player ------------------------------------------------------------------------------

    /// <summary>
    /// Server: the ruler of the hero's realm charges the hero's clan with treason, as Bellum's crown decree (or vote, with
    /// <c>vote</c>) does (IdeologyBehavior.ForceTreasonJudgmentForTesting, Bellum's own test entry). Reports Bellum's
    /// answer (empty when it went ahead).
    /// </summary>
    private static IEnumerable<StepWait?> Treason(StepContext ctx)
    {
        var clan = PlayerClan(ctx);
        var kingdom = clan.Kingdom ?? throw new StepFailedException("the player's clan is in no realm");
        var ideology = Behavior("BellumCivile.Behaviors.IdeologyBehavior") ?? throw new StepFailedException("Bellum's ideology is not running");
        var force = ideology.GetType().GetMethod("ForceTreasonJudgmentForTesting", Any) ?? throw new StepFailedException("Bellum has no ForceTreasonJudgmentForTesting");
        var answer = force.Invoke(ideology, new object[] { kingdom, clan, ctx.Arg("mode", "decree") != "vote" }) as string;
        ctx.Log($"treason {ctx.Arg("mode", "decree")} against {clan.StringId} in {kingdom.StringId}: {answer ?? "went ahead"}");
        yield return ctx.Delay(TimeSpan.FromSeconds(3));
        ctx.Report("value", answer ?? "");
    }

    /// <summary>
    /// Server: a court faction of the realm's AI vassals issues its ultimatum to realm <c>kingdom</c>, as Bellum's own
    /// civilwars.start cheat does (CheatCommands.StartRebellion). Reports Bellum's answer.
    /// </summary>
    private static IEnumerable<StepWait?> Ultimatum(StepContext ctx)
    {
        var kingdom = KingdomById(ctx.Arg("kingdom")) ?? throw new StepFailedException($"no kingdom '{ctx.Arg("kingdom")}'");
        var cheats = FindType("BellumCivile.CheatCommands") ?? throw new StepFailedException("Bellum is not loaded");
        var start = cheats.GetMethod("StartRebellion", Any) ?? throw new StepFailedException("Bellum has no StartRebellion");
        var answer = start.Invoke(null, new object[] { new List<string> { kingdom.StringId, ctx.Arg("type", "Abdication") } }) as string;
        ctx.Log($"ultimatum to {kingdom.StringId}: {answer}");
        yield return ctx.Delay(TimeSpan.FromSeconds(5));
        ctx.Report("value", answer ?? "");
    }

    /// <summary>
    /// Server: asks one of Bellum's own AI tests (<c>type</c>, <c>method</c>) about the player, as Bellum's AI does each day,
    /// with arguments from <c>args</c> (| separated: playerClan, playerHero, playerRealm, realm:ID, clan:ID, null, true,
    /// false, a number). Fails unless the answer is <c>expect</c> (e.g. False).
    /// </summary>
    private static IEnumerable<StepWait?> Ask(StepContext ctx)
    {
        var type = FindType(ctx.Arg("type")) ?? throw new StepFailedException($"{ctx.Arg("type")} is not loaded");
        var hero = HeroNames.Single(ctx.Arg("heroName"));
        var tokens = ctx.Arg("args", "").Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries);
        var method = type.GetMethods(Any).FirstOrDefault(m => m.Name == ctx.Arg("method") && m.GetParameters().Length == tokens.Length)
            ?? throw new StepFailedException($"{type.Name} has no {ctx.Arg("method")} with {tokens.Length} parameters");
        var parameters = method.GetParameters();
        var args = tokens.Select((t, i) => Token(t.Trim(), hero, parameters[i].ParameterType)).ToArray();
        var target = method.IsStatic ? null
            : typeof(Campaign).GetMethod(nameof(Campaign.GetCampaignBehavior))!.MakeGenericMethod(type).Invoke(Campaign.Current, null);
        var result = method.Invoke(target, args);
        ctx.Log($"{type.Name}.{method.Name}({string.Join(", ", tokens)}) = {result}");
        if (ctx.HasArg("expect"))
            ctx.Assert(string.Equals(result?.ToString(), ctx.Arg("expect"), StringComparison.OrdinalIgnoreCase),
                $"{type.Name}.{method.Name} answered {result} for the player, expected {ctx.Arg("expect")}");
        ctx.Report("value", result?.ToString() ?? "");
        yield break;
    }

    private static object? Token(string token, Hero hero, Type type)
    {
        switch (token)
        {
            case "playerClan": return hero.Clan;
            case "playerHero": return hero;
            case "playerRealm": return hero.Clan?.Kingdom;
            case "null": return type.IsValueType ? Activator.CreateInstance(type) : null;
            case "true": return true;
            case "false": return false;
        }
        if (token.StartsWith("realm:")) return KingdomById(token.Substring(6));
        if (token.StartsWith("clan:")) return Clan.All.FirstOrDefault(c => c.StringId == token.Substring(5));
        if (type == typeof(string)) return token == "playerRealmId" ? hero.Clan?.Kingdom?.StringId : token;
        return Convert.ChangeType(token, type, System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Server: what the compat module kept Bellum's AI from doing to players (PlayersNotAi.KeptFromPlayers). Fails unless
    /// each <c>expect</c> entry ("Type.Method", | separated) fired at least once. Reports the total.
    /// </summary>
    private static IEnumerable<StepWait?> KeptFromPlayers(StepContext ctx)
    {
        var kept = FindType("BellumCivileCoopCompat.PlayersNotAi")?.GetProperty("KeptFromPlayers", Any)?.GetValue(null) as IDictionary
            ?? throw new StepFailedException("the compat module lets Bellum's AI decide for players (older than 1.3.0)");
        var counts = new Dictionary<string, int>();
        foreach (var key in kept.Keys) counts[(string)key] = (int)kept[key]!;
        ctx.Dump("bellum-kept-from-players", string.Join(Environment.NewLine, counts.OrderBy(kv => kv.Key).Select(kv => kv.Key + " " + kv.Value)));
        foreach (var want in ctx.Arg("expect", "").Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries))
            ctx.Assert(counts.TryGetValue(want, out var n) && n > 0, $"the module never kept {want} from a player");
        ctx.Report("value", counts.Values.Sum());
        yield break;
    }

    /// <summary>
    /// The realms kingdom <c>kingdom</c> is at war with, sorted (value; "none" for none). With <c>expect</c>, fails on any
    /// other list: run it before something and again after with <c>expect</c> set to the first run's value.
    /// </summary>
    private static IEnumerable<StepWait?> Enemies(StepContext ctx)
    {
        var realm = KingdomById(ctx.Arg("kingdom")) ?? throw new StepFailedException($"no kingdom '{ctx.Arg("kingdom")}'");
        string Read()
        {
            var enemies = Kingdom.All.Where(k => k != null && k != realm && !k.IsEliminated && realm.IsAtWarWith(k)).Select(k => k.StringId).OrderBy(x => x, StringComparer.Ordinal).ToList();
            return enemies.Count == 0 ? "none" : string.Join(",", enemies);
        }
        if (ctx.HasArg("was"))
            yield return ctx.Until(() => Read() != ctx.Arg("was"), $"{realm.StringId}'s wars to change", TimeSpan.FromSeconds(ctx.ArgInt("timeoutSeconds", 60)));
        yield return ctx.Settled(Read, $"{realm.StringId}'s wars", quiet: TimeSpan.FromSeconds(3));
        var value = Read();
        if (ctx.HasArg("expect"))
            ctx.Assert(value == ctx.Arg("expect"), $"{realm.StringId} is at war with {value}, was {ctx.Arg("expect")}");
        if (ctx.HasArg("was"))
            ctx.Assert(value != ctx.Arg("was"), $"{realm.StringId}'s wars did not change ({value})");
        ctx.Report("value", value);
    }

    // ---- crown accession -------------------------------------------------------------------------------------------

    /// <summary>
    /// Server: the ruler of a hereditary lords' realm (Bellum's own test, CrownAccessionBehavior.IsHereditaryRealm) dies,
    /// away from any battle. Reports the realm (value), the dead ruler, and the heir Bellum's law names
    /// (HereditaryRealmSuccession.GetLine), so later steps can check the crown went there, once.
    /// </summary>
    private static IEnumerable<StepWait?> KillRuler(StepContext ctx)
    {
        var accession = FindType("BellumCivile.Behaviors.CrownAccessionBehavior") ?? throw new StepFailedException("Bellum has no crown accession");
        var hereditary = accession.GetMethod("IsHereditaryRealm", Any);
        var line = FindType("BellumCivile.HereditaryRealmSuccession")?.GetMethods(Any).FirstOrDefault(m => m.Name == "GetLine");
        var laws = FindType("BellumCivile.SuccessionLawHelper")?.GetMethod("GetLawsForKingdom", Any);
        var instance = accession.GetProperty("Instance", Any)?.GetValue(null);
        var realm = Kingdom.All.Where(k => k != null && !k.IsEliminated && !HasPlayers(k) && k.Leader != null && k.Leader.IsAlive && k.Clans.Count >= 2
                && k.Leader.PartyBelongedTo?.MapEvent == null && k.Leader.PartyBelongedTo?.SiegeEvent == null
                && !k.UnresolvedDecisions.OfType<KingSelectionKingdomDecision>().Any()
                && (hereditary == null || hereditary.Invoke(hereditary.IsStatic ? null : instance, new object[] { k }) is true))
            .OrderByDescending(k => k.Clans.Count).FirstOrDefault() ?? throw new StepFailedException("no hereditary lords' realm with a ruler free to die");
        var ruler = realm.Leader;
        Hero? heir = null;
        if (line != null && laws != null)
        {
            var lineArgs = line.GetParameters().Select(p => p.ParameterType == typeof(Kingdom) ? realm : p.ParameterType == typeof(Hero) ? ruler
                : p.ParameterType == laws.ReturnType ? laws.Invoke(null, new object[] { realm }) : p.HasDefaultValue ? p.DefaultValue : null).ToArray();
            heir = (line.Invoke(null, lineArgs) as IEnumerable)?.Cast<object>().OfType<Hero>().FirstOrDefault();
        }
        ctx.Log($"{realm.StringId}: {ruler.StringId} of {ruler.Clan?.StringId} dies; Bellum's heir {heir?.StringId ?? "unknown"} of {heir?.Clan?.StringId ?? "-"}");
        KillCharacterAction.ApplyByMurder(ruler, null, false);
        yield return ctx.Until(() => !ruler.IsAlive, $"{ruler.StringId} to die", TimeSpan.FromSeconds(10));
        ctx.Report("value", realm.StringId);
        ctx.Report("dead", ruler.StringId);
        ctx.Report("heir", heir?.StringId ?? "");
        ctx.Report("heirClan", heir?.Clan?.StringId ?? "");
    }

    /// <summary>
    /// Realm <c>kingdom</c>'s ruling clan as this side sees it, and whether a king vote is open: "clan|vote" or
    /// "clan|no vote". With <c>expectClan</c> and <c>expectVote</c>, fails on anything else.
    /// </summary>
    private static IEnumerable<StepWait?> Ruler(StepContext ctx)
    {
        var realm = KingdomById(ctx.Arg("kingdom")) ?? throw new StepFailedException($"no kingdom '{ctx.Arg("kingdom")}'");
        string Read() => (realm.RulingClan?.StringId ?? "none") + "|" + (realm.UnresolvedDecisions.OfType<KingSelectionKingdomDecision>().Any() ? "vote" : "no vote");
        yield return ctx.Settled(Read, $"{realm.StringId}'s crown", quiet: TimeSpan.FromSeconds(3));
        var value = Read();
        if (ctx.HasArg("expectClan") && ctx.Arg("expectClan").Length > 0)
            ctx.Assert(value.StartsWith(ctx.Arg("expectClan") + "|"), $"{realm.StringId} is ruled by {value.Split('|')[0]}, Bellum's law names {ctx.Arg("expectClan")}");
        if (ctx.HasArg("notClan"))
        {
            yield return ctx.Until(() => !Read().StartsWith(ctx.Arg("notClan") + "|"), $"{realm.StringId} to pass from {ctx.Arg("notClan")}", TimeSpan.FromSeconds(ctx.ArgInt("timeoutSeconds", 60)));
            value = Read();
        }
        if (ctx.HasArg("expectVote"))
            ctx.Assert(value.EndsWith("|" + ctx.Arg("expectVote")), $"{realm.StringId}: {value}, expected {ctx.Arg("expectVote")}");
        ctx.Report("value", value);
    }
}
