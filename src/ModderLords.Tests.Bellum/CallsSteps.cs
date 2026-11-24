using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using ModderLords.Testing;
using TaleWorlds.CampaignSystem;
using static ModderLords.Tests.Bellum.Steps;

namespace ModderLords.Tests.Bellum;

/// <summary>
/// Steps for Bellum's calls to the player in a civil war (docs/BELLUM-COOP-COVERAGE.md, stage 3, A1): a call to a
/// civil-war side and an accused lord's appeal, each put to the player they concern (the module's Calls).
/// </summary>
public static class CallsSteps
{
    public static void Register()
    {
        InGameTests.Register("bellum.factionIn", StepSide.Server, FactionIn);
        InGameTests.Register("bellum.refuseUltimatum", StepSide.Server, RefuseUltimatum);
        InGameTests.Register("bellum.setRelation", StepSide.Server, SetRelation);
        InGameTests.Register("bellum.treasonAppeal", StepSide.Server, TreasonAppeal);
        InGameTests.Register("bellum.clanKingdom", StepSide.Any, ClanKingdom);
    }

    private static object Manager()
    {
        var type = FindType("BellumCivile.Behaviors.FactionManagerBehavior") ?? throw new StepFailedException("Bellum is not loaded");
        return typeof(Campaign).GetMethod(nameof(Campaign.GetCampaignBehavior))!.MakeGenericMethod(type).Invoke(Campaign.Current, null)
               ?? throw new StepFailedException("Bellum's faction manager is not running");
    }

    private static IList Active(object manager) => manager.GetType().GetField("_activeFactions", Any)?.GetValue(manager) as IList
                                                   ?? throw new StepFailedException("Bellum's faction list was not found");

    private static object Get(object o, string member) => o.GetType().GetProperty(member, Any)?.GetValue(o) ?? o.GetType().GetField(member, Any)?.GetValue(o)!;

    /// <summary>Server: the faction in realm <c>kingdom</c> led by clan <c>leader</c>.</summary>
    private static object FactionLedBy(string kingdom, string leader) =>
        Active(Manager()).Cast<object>().FirstOrDefault(f => (Get(f, "ParentKingdom") as Kingdom)?.StringId == kingdom && (Get(f, "Leader") as Clan)?.StringId == leader)
        ?? throw new StepFailedException($"no faction in {kingdom} led by {leader}");

    /// <summary>
    /// Server: a court faction of AI vassals forms in realm <c>kingdom</c> (Bellum's CreateFactionFromArgs, after lifting the
    /// realm's first-year grace), with the player's clan (<c>memberHero</c>) added as a member when given. Reports value
    /// (kingdom:leader:type), leader (clan id) and leaderHero (hero id).
    /// </summary>
    private static IEnumerable<StepWait?> FactionIn(StepContext ctx)
    {
        var kingdom = KingdomById(ctx.Arg("kingdom")) ?? throw new StepFailedException($"no kingdom '{ctx.Arg("kingdom")}'");
        var manager = Manager();
        if (manager.GetType().GetField("_initialCivilWarGrace", Any)?.GetValue(manager) is IDictionary grace && grace.Contains(kingdom))
            grace.Remove(kingdom);
        var create = FindType("BellumCivile.CheatCommands")?.GetMethod("CreateFactionFromArgs", Any) ?? throw new StepFailedException("Bellum has no CreateFactionFromArgs");
        var args = new object?[] { new List<string> { kingdom.StringId, ctx.Arg("type", "Independence") }, null };
        var faction = create.Invoke(null, args) ?? throw new StepFailedException($"{kingdom.StringId}: {args[1]}");
        if (ctx.HasArg("memberHero"))
        {
            var clan = HeroNames.Single(ctx.Arg("memberHero")).Clan ?? throw new StepFailedException("the hero has no clan");
            faction.GetType().GetMethod("AddMember", Any, null, new[] { typeof(Clan) }, null)!.Invoke(faction, new object[] { clan });
            ctx.Assert((Get(faction, "Members") as IEnumerable)?.Cast<object>().Contains(clan) == true, $"{clan.StringId} did not join the faction");
        }
        var leader = (Clan)Get(faction, "Leader");
        ctx.Assert(Active(manager).Contains(faction), "the faction was not registered");
        var key = $"{kingdom.StringId}:{leader.StringId}:{Get(faction, "Type")}";
        ctx.Log("formed: " + key);
        ctx.Report("value", key);
        ctx.Report("leader", leader.StringId);
        ctx.Report("leaderHero", leader.Leader?.StringId ?? "");
        yield break;
    }

    /// <summary>
    /// Server: realm <c>kingdom</c>'s ruler refuses the ultimatum of the faction led by <c>leader</c>, and the faction rises
    /// (Bellum's own FactionObject.RefuseDemandsAndRebel). Reports value (Bellum's resolution) and rebels (the rebel realm).
    /// </summary>
    private static IEnumerable<StepWait?> RefuseUltimatum(StepContext ctx)
    {
        var faction = FactionLedBy(ctx.Arg("kingdom"), ctx.Arg("leader"));
        var refuse = faction.GetType().GetMethod("RefuseDemandsAndRebel", Any) ?? throw new StepFailedException("Bellum has no RefuseDemandsAndRebel");
        var result = refuse.Invoke(faction, new object?[] { null, false, null })?.ToString() ?? "";
        ctx.Log($"refused: {result}");
        ctx.Assert(result == "RebellionStarted", $"the faction did not rise ({result})");
        var rebels = faction.GetType().GetMethod("GetRebelKingdom", Any)?.Invoke(faction, null) as Kingdom;
        yield return ctx.Delay(TimeSpan.FromSeconds(2));
        ctx.Report("value", result);
        ctx.Report("rebels", rebels?.StringId ?? "");
    }

    /// <summary>Server: sets the relation between the player (<c>heroName</c>) and hero <c>b</c> (an id) to <c>value</c>.</summary>
    private static IEnumerable<StepWait?> SetRelation(StepContext ctx)
    {
        var a = HeroNames.Single(ctx.Arg("heroName"));
        var b = Hero.AllAliveHeroes.FirstOrDefault(h => h.StringId == ctx.Arg("b")) ?? throw new StepFailedException($"no hero '{ctx.Arg("b")}'");
        CharacterRelationManager.SetHeroRelation(a, b, ctx.ArgInt("value", 70));
        ctx.Report("value", a.GetRelation(b));
        yield break;
    }

    /// <summary>
    /// Server: the leader of the risen faction led by <c>leader</c> in realm <c>kingdom</c>, accused of treason, appeals to
    /// the realm's lords to stand with them (Bellum's NotificationHelper.ShowTreasonSolidarityChoiceToPlayer, as its treason
    /// trial does when the accused rebels).
    /// </summary>
    private static IEnumerable<StepWait?> TreasonAppeal(StepContext ctx)
    {
        var faction = FactionLedBy(ctx.Arg("kingdom"), ctx.Arg("leader"));
        var kingdom = KingdomById(ctx.Arg("kingdom"))!;
        var accused = (Clan)Get(faction, "Leader");
        var show = FindType("BellumCivile.NotificationHelper")?.GetMethod("ShowTreasonSolidarityChoiceToPlayer", Any) ?? throw new StepFailedException("Bellum has no ShowTreasonSolidarityChoiceToPlayer");
        show.Invoke(null, new object[] { faction, kingdom, accused });
        yield return ctx.Delay(TimeSpan.FromSeconds(1));
        ctx.Report("value", accused.StringId);
    }

    /// <summary>
    /// Either side: the realm of the player's clan (<c>heroName</c>), waited for until it is <c>expect</c>, or until it is not
    /// <c>not</c>, then settled. Reports it.
    /// </summary>
    private static IEnumerable<StepWait?> ClanKingdom(StepContext ctx)
    {
        var hero = HeroNames.Single(ctx.Arg("heroName"));
        string Read() => hero.Clan?.Kingdom?.StringId ?? "none";
        var timeout = TimeSpan.FromSeconds(ctx.ArgInt("timeoutSeconds", 60));
        if (ctx.HasArg("expect"))
            yield return ctx.Until(() => Read() == ctx.Arg("expect"), $"{hero.StringId}'s clan to be in {ctx.Arg("expect")}", timeout);
        if (ctx.HasArg("not"))
            yield return ctx.Until(() => Read() != ctx.Arg("not"), $"{hero.StringId}'s clan to leave {ctx.Arg("not")}", timeout);
        yield return ctx.Settled(Read, $"{hero.StringId}'s realm", quiet: TimeSpan.FromSeconds(3));
        var value = Read();
        if (ctx.HasArg("expect"))
            ctx.Assert(value == ctx.Arg("expect"), $"{hero.StringId}'s clan is in {value}, expected {ctx.Arg("expect")}");
        if (ctx.HasArg("not"))
            ctx.Assert(value != ctx.Arg("not"), $"{hero.StringId}'s clan is still in {value}");
        ctx.Report("value", value);
    }
}
