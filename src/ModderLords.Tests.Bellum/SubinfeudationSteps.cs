using System;
using System.Collections.Generic;
using System.Linq;
using ModderLords.Testing;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.Settlements;
using static ModderLords.Tests.Bellum.Steps;

namespace ModderLords.Tests.Bellum;

/// <summary>
/// Steps for Bellum's companion vassal houses (stage 4d, CompanionSubinfeudationBehavior): a player vassal grants a barony
/// to a companion, who founds a new house under them.
/// </summary>
public static class SubinfeudationSteps
{
    public static void Register()
    {
        InGameTests.Register("bellum.liegeTitle", StepSide.Server, LiegeTitle);
        InGameTests.Register("bellum.hireCompanion", StepSide.Server, HireCompanion);
        InGameTests.Register("bellum.vassalHouse", StepSide.Any, VassalHouse);
    }

    private static object Titles() =>
        (FindType("BellumCivile.Behaviors.FeudalTitleBehavior") is { } type
            ? typeof(Campaign).GetMethod(nameof(Campaign.GetCampaignBehavior))!.MakeGenericMethod(type).Invoke(Campaign.Current, null) : null)
        ?? throw new StepFailedException("Bellum's feudal titles are not running");

    /// <summary>
    /// Server: player <c>heroName</c>'s clan is given the barony of its fief <c>fief</c> and the title above it (Bellum's
    /// own TryGrantTitleForTesting, de jure too), so it holds a superior title in that barony's hierarchy, as Bellum asks of
    /// a lord who grants it on. Reports value (the title above's id).
    /// </summary>
    private static IEnumerable<StepWait?> LiegeTitle(StepContext ctx)
    {
        var clan = HeroNames.Single(ctx.Arg("heroName")).Clan ?? throw new StepFailedException("the hero has no clan");
        var fief = Settlement.Find(ctx.Arg("fief")) ?? throw new StepFailedException($"no settlement '{ctx.Arg("fief")}'");
        var titles = Titles();
        var args = new object?[] { fief, null };
        if (!(titles.GetType().GetMethod("TryGetBarony", Any)!.Invoke(titles, args) is true) || args[1] == null)
            throw new StepFailedException($"{fief.StringId} has no barony title");
        var barony = args[1]!;
        var deFacto = titles.GetType().Assembly.GetType("BellumCivile.FeudalHierarchyMode")!;
        var parent = titles.GetType().GetMethods(Any).First(m => m.Name == "GetParentTitle" && m.GetParameters().Length == 2)
            .Invoke(titles, new[] { barony, Enum.Parse(deFacto, "DeFacto") }) ?? throw new StepFailedException($"{fief.StringId}'s barony has no title above it");
        // The barony's own title too, de jure, so the grant passes it on in full (the conversation's "full rights" line).
        foreach (var title in new[] { barony, parent })
        {
            var grant = new object?[] { clan, title, true, null, null };
            if (!(titles.GetType().GetMethod("TryGrantTitleForTesting", Any)!.Invoke(titles, grant) is true))
                throw new StepFailedException("Bellum refused the title: " + grant[4]);
        }
        var id = parent.GetType().GetField("TitleId", Any)?.GetValue(parent) ?? parent.GetType().GetProperty("TitleId", Any)?.GetValue(parent);
        ctx.Log($"{clan.StringId} holds {id} above {fief.StringId}");
        ctx.Report("value", id?.ToString() ?? "");
        yield break;
    }

    /// <summary>
    /// Server: a wanderer joins player <c>heroName</c>'s clan as a companion, in the player's party; with <c>female</c>
    /// ("yes" or "no"), one of that sex. Reports value (the companion's id) and name.
    /// </summary>
    private static IEnumerable<StepWait?> HireCompanion(StepContext ctx)
    {
        var player = HeroNames.Single(ctx.Arg("heroName"));
        var clan = player.Clan ?? throw new StepFailedException("the hero has no clan");
        var party = player.PartyBelongedTo ?? throw new StepFailedException("the player has no party");
        var female = ctx.Arg("female", "");
        var wanderer = Hero.AllAliveHeroes.Where(h => h.IsWanderer && h.Clan == null && h.CompanionOf == null && !h.IsPrisoner && !h.IsChild && h.PartyBelongedTo == null
                && (female == "" || h.IsFemale == (female == "yes")) && h.Spouse == null)
            .OrderBy(h => h.StringId).FirstOrDefault() ?? throw new StepFailedException("no free wanderer to hire");
        AddCompanionAction.Apply(clan, wanderer);
        AddHeroToPartyAction.Apply(wanderer, party);
        yield return ctx.Until(() => wanderer.Clan == clan && wanderer.PartyBelongedTo == party, $"{wanderer.StringId} to join {clan.StringId}", Sync);
        ctx.Log($"{wanderer.StringId} \"{wanderer.Name}\" joins {clan.StringId} in {party.StringId}");
        ctx.Report("value", wanderer.StringId);
        ctx.Report("name", wanderer.Name.ToString());
    }

    /// <summary>
    /// Either side: whether hero <c>heroId</c> leads a house of his own in player <c>heroName</c>'s realm that holds
    /// <c>fief</c> ("yes" or "no"), waited for to be <c>expect</c>. Reports value and clan (the house's id).
    /// </summary>
    private static IEnumerable<StepWait?> VassalHouse(StepContext ctx)
    {
        var player = HeroNames.Single(ctx.Arg("heroName"));
        var hero = Hero.FindFirst(h => h.StringId == ctx.Arg("heroId")) ?? throw new StepFailedException($"no hero '{ctx.Arg("heroId")}'");
        var fief = Settlement.Find(ctx.Arg("fief")) ?? throw new StepFailedException($"no settlement '{ctx.Arg("fief")}'");
        string Read() => hero.Clan is { } c && c != player.Clan && c.Leader == hero && c.Kingdom != null && c.Kingdom == player.Clan?.Kingdom && fief.OwnerClan == c ? "yes" : "no";
        if (ctx.HasArg("expect"))
            yield return ctx.Until(() => Read() == ctx.Arg("expect"), $"{hero.StringId}'s own house holding {fief.StringId}: {ctx.Arg("expect")}", Sync);
        ctx.Assert(!ctx.HasArg("expect") || Read() == ctx.Arg("expect"),
            $"{hero.StringId} of {hero.Clan?.StringId} (leader {hero.Clan?.Leader?.StringId}, realm {hero.Clan?.Kingdom?.StringId}); {fief.StringId} held by {fief.OwnerClan?.StringId}");
        ctx.Report("value", Read());
        ctx.Report("clan", hero.Clan?.StringId ?? "");
    }
}
