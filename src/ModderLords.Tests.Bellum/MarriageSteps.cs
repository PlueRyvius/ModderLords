using System;
using System.Collections.Generic;
using System.Linq;
using ModderLords.Testing;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using static ModderLords.Tests.Bellum.Steps;

namespace ModderLords.Tests.Bellum;

/// <summary>
/// Steps for Bellum's marriage proposals (stage 4d, SpecificMarriageProposalBehavior): a lord's house with an unmarried son
/// for a bride of the player's house, a meeting in town, and who married whom, in which house.
/// </summary>
public static class MarriageSteps
{
    public static void Register()
    {
        InGameTests.Register("bellum.groomFor", StepSide.Server, GroomFor);
        InGameTests.Register("bellum.meetInTown", StepSide.Server, MeetInTown);
        InGameTests.Register("bellum.inTown", StepSide.Any, InTown);
        InGameTests.Register("bellum.married", StepSide.Any, Married);
    }

    /// <summary>
    /// Server: a lord who may wed hero <c>brideId</c> of player <c>heroName</c>'s house: unmarried, of age, not the head of
    /// his house, free (no army, battle or captivity), of a lords' house at peace with the player's realm whose leader
    /// leads a party; the game's marriage rules allow the couple, and Bellum lets him join the bride's house. Reports value (his id), name, leader (his house's
    /// leader's id) and clan.
    /// </summary>
    private static IEnumerable<StepWait?> GroomFor(StepContext ctx)
    {
        var player = HeroNames.Single(ctx.Arg("heroName"));
        var bride = Hero.FindFirst(h => h.StringId == ctx.Arg("brideId")) ?? throw new StepFailedException($"no hero '{ctx.Arg("brideId")}'");
        var model = Campaign.Current.Models.MarriageModel;
        // Bellum's rules for a companion read "the player's house" (Clan.PlayerClan); Coop concludes the barter as the
        // player, so they are read here as the player too.
        using var asPlayer = AsPlayer(player);
        static bool Free(Hero h) => h.IsAlive && h.IsActive && !h.IsPrisoner && !h.IsTraveling && h.PartyBelongedTo is not { MapEvent: not null } && h.PartyBelongedTo is not { SiegeEvent: not null } && h.PartyBelongedTo?.Army == null;
        var groom = Clan.All.Where(c => c != null && c != player.Clan && !c.IsEliminated && !c.IsMinorFaction && !c.IsBanditFaction && c.Kingdom != null
                    && c.Leader is { } l && l.IsAlive && !l.IsPrisoner && l.PartyBelongedTo is { } p && p.LeaderHero == l && Free(l)
                    && !FactionManager.IsAtWarAgainstFaction(c.MapFaction, player.MapFaction))
            .OrderBy(c => c.StringId)
            .SelectMany(c => c.Heroes.Where(h => h != c.Leader && h.IsLord && !h.IsFemale && h.Spouse == null && !h.IsChild && h.Age >= model.MinimumMarriageAgeMale && Free(h)
                && h.CanMarry() && model.IsCoupleSuitableForMarriage(bride, h) && JoinsBride(bride, h)).OrderBy(h => h.StringId))
            .FirstOrDefault();
        if (groom == null) throw new StepFailedException($"no lord may wed {bride.StringId}: {Why(player, bride)}");
        ctx.Log($"{groom.StringId} \"{groom.Name}\" of {groom.Clan.StringId} (led by {groom.Clan.Leader.StringId}) for {bride.StringId} \"{bride.Name}\"");
        ctx.Report("value", groom.StringId);
        ctx.Report("name", groom.Name.ToString());
        ctx.Report("leader", groom.Clan.Leader.StringId);
        ctx.Report("clan", groom.Clan.StringId);
        yield break;
    }

    /// <summary>Whether Bellum lets the couple marry in the bride's house (PlayerMarriageAgreement.CanChoose, negotiated).</summary>
    private static bool JoinsBride(Hero bride, Hero groom)
    {
        var agreement = FindType("BellumCivile.PlayerMarriageAgreement") ?? throw new StepFailedException("Bellum has no PlayerMarriageAgreement");
        var policy = FindType("BellumCivile.MarriageHouseholdPolicy") ?? throw new StepFailedException("Bellum has no MarriageHouseholdPolicy");
        var canChoose = agreement.GetMethod("CanChoose", Any) ?? throw new StepFailedException("Bellum has no PlayerMarriageAgreement.CanChoose");
        return canChoose.Invoke(null, new object?[] { bride, groom, bride.Clan, true, Activator.CreateInstance(policy, true), null }) is true;
    }

    /// <summary>Coop's scope that makes the server's player hero, party and house the given player's, for a while.</summary>
    private static IDisposable AsPlayer(Hero player)
    {
        var type = FindType("GameInterface.Services.Heroes.Patches.MainHeroSubstitutionScope") ?? throw new StepFailedException("Coop has no MainHeroSubstitutionScope");
        var ctor = type.GetConstructor(Any, null, new[] { typeof(Hero), typeof(MobileParty) }, null) ?? throw new StepFailedException("Coop's MainHeroSubstitutionScope takes no (Hero, MobileParty)");
        return (IDisposable)ctor.Invoke(new object?[] { player, player.PartyBelongedTo });
    }

    // Why no lord was found: how many pass each test in turn.
    private static string Why(Hero player, Hero bride)
    {
        var model = Campaign.Current.Models.MarriageModel;
        var houses = Clan.All.Where(c => c != null && c != player.Clan && !c.IsEliminated && !c.IsMinorFaction && !c.IsBanditFaction && c.Kingdom != null
            && !FactionManager.IsAtWarAgainstFaction(c.MapFaction, player.MapFaction)).ToList();
        var led = houses.Where(c => c.Leader is { } l && l.IsAlive && !l.IsPrisoner && l.PartyBelongedTo is { } p && p.LeaderHero == l).ToList();
        var men = led.SelectMany(c => c.Heroes.Where(h => h != c.Leader && h.IsLord && !h.IsFemale && h.IsAlive)).ToList();
        var single = men.Where(h => h.Spouse == null && !h.IsChild && h.Age >= model.MinimumMarriageAgeMale).ToList();
        var free = single.Where(h => h.IsActive && !h.IsPrisoner && !h.IsTraveling && h.PartyBelongedTo?.MapEvent == null && h.PartyBelongedTo?.Army == null).ToList();
        var can = free.Where(h => h.CanMarry()).ToList();
        return $"houses at peace {houses.Count}, led from a party {led.Count}, other men {men.Count}, single adults {single.Count}, free {free.Count}, CanMarry {can.Count}, "
            + $"couple allowed {can.Count(h => model.IsCoupleSuitableForMarriage(bride, h))}, in the bride's house {can.Count(h => model.IsCoupleSuitableForMarriage(bride, h) && JoinsBride(bride, h))}; the bride suitable {model.IsSuitableForMarriage(bride)}, "
            + $"companion of {bride.CompanionOf?.StringId}, clan {bride.Clan?.StringId}, active {bride.IsActive}, age {bride.Age:0}";
    }

    /// <summary>
    /// Server: player <c>heroName</c>'s party and hero <c>heroId</c>'s party are put in the same town (the town nearest the
    /// hero's party), as when the player finds a lord in town. Reports value (the town's id).
    /// </summary>
    private static IEnumerable<StepWait?> MeetInTown(StepContext ctx)
    {
        var player = HeroNames.Single(ctx.Arg("heroName"));
        var hero = Hero.FindFirst(h => h.StringId == ctx.Arg("heroId")) ?? throw new StepFailedException($"no hero '{ctx.Arg("heroId")}'");
        var mine = player.PartyBelongedTo ?? throw new StepFailedException("the player has no party");
        var theirs = hero.PartyBelongedTo ?? throw new StepFailedException($"{hero.StringId} has no party");
        var town = theirs.CurrentSettlement is { IsTown: true } here ? here
            : Settlement.All.Where(s => s.IsTown && !FactionManager.IsAtWarAgainstFaction(s.MapFaction, player.MapFaction) && !FactionManager.IsAtWarAgainstFaction(s.MapFaction, hero.MapFaction))
                .OrderBy(s => s.Position.DistanceSquared(theirs.Position)).FirstOrDefault() ?? throw new StepFailedException("no town both may enter");
        foreach (var party in new[] { theirs, mine })
        {
            if (party.CurrentSettlement == town) continue;
            if (party.CurrentSettlement != null) LeaveSettlementAction.ApplyForParty(party);
            party.Position = town.GatePosition;
            EnterSettlementAction.ApplyForParty(party, town);
        }
        yield return ctx.Until(() => mine.CurrentSettlement == town && theirs.CurrentSettlement == town, $"both parties in {town.StringId}", Sync);
        ctx.Log($"{mine.StringId} and {theirs.StringId} in {town.StringId} \"{town.Name}\"");
        ctx.Report("value", town.StringId);
    }

    /// <summary>Either side: hero <c>heroId</c> (or player <c>heroName</c>) is in settlement <c>town</c>, waited for.</summary>
    private static IEnumerable<StepWait?> InTown(StepContext ctx)
    {
        var hero = ctx.HasArg("heroId")
            ? Hero.FindFirst(h => h.StringId == ctx.Arg("heroId")) ?? throw new StepFailedException($"no hero '{ctx.Arg("heroId")}'")
            : HeroNames.Single(ctx.Arg("heroName"));
        var town = ctx.Arg("town");
        yield return ctx.Until(() => hero.CurrentSettlement?.StringId == town, $"{hero.StringId} in {town}", Sync);
        ctx.Assert(hero.CurrentSettlement?.StringId == town, $"{hero.StringId} is in {hero.CurrentSettlement?.StringId ?? "no settlement"}");
    }

    /// <summary>
    /// Either side: whether hero <c>heroId</c> is married to hero <c>spouseId</c> ("yes" or "no"), both in player
    /// <c>house</c>'s house when given, waited for to be <c>expect</c>. Reports value and clan (each one's house).
    /// </summary>
    private static IEnumerable<StepWait?> Married(StepContext ctx)
    {
        var hero = Hero.FindFirst(h => h.StringId == ctx.Arg("heroId")) ?? throw new StepFailedException($"no hero '{ctx.Arg("heroId")}'");
        var spouse = Hero.FindFirst(h => h.StringId == ctx.Arg("spouseId")) ?? throw new StepFailedException($"no hero '{ctx.Arg("spouseId")}'");
        var clan = ctx.HasArg("house") ? HeroNames.Single(ctx.Arg("house")).Clan?.StringId ?? throw new StepFailedException("the player has no house") : "";
        string Read() => hero.Spouse == spouse && spouse.Spouse == hero && (clan == "" || (hero.Clan?.StringId == clan && spouse.Clan?.StringId == clan)) ? "yes" : "no";
        if (ctx.HasArg("expect"))
            yield return ctx.Until(() => Read() == ctx.Arg("expect"), $"{hero.StringId} and {spouse.StringId} married{(clan == "" ? "" : " in " + clan)}: {ctx.Arg("expect")}", Sync);
        ctx.Assert(!ctx.HasArg("expect") || Read() == ctx.Arg("expect"),
            $"{hero.StringId} of {hero.Clan?.StringId} married to {hero.Spouse?.StringId ?? "nobody"}; {spouse.StringId} of {spouse.Clan?.StringId} married to {spouse.Spouse?.StringId ?? "nobody"}");
        ctx.Report("value", Read());
        ctx.Report("clan", $"{hero.Clan?.StringId}|{spouse.Clan?.StringId}");
    }
}
