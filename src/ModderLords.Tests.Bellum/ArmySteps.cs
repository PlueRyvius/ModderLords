using System;
using System.Collections.Generic;
using System.Linq;
using ModderLords.Testing;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using static ModderLords.Tests.Bellum.Steps;

namespace ModderLords.Tests.Bellum;

/// <summary>
/// Steps for a player's army and Bellum's summons to it (stage 4d, ArmySummonsConversationBehavior): the player leads
/// an army, and a lord of their realm is asked in conversation to join it.
/// </summary>
public static class ArmySteps
{
    public static void Register()
    {
        InGameTests.Register("bellum.playerArmy", StepSide.Server, PlayerArmy);
        InGameTests.Register("bellum.summonableLord", StepSide.Server, SummonableLord);
        InGameTests.Register("bellum.inArmy", StepSide.Any, InArmy);
    }

    /// <summary>
    /// Server: player <c>heroName</c>'s realm raises an army led by the player's party (the player must be in a realm).
    /// Reports value (the army's name).
    /// </summary>
    private static IEnumerable<StepWait?> PlayerArmy(StepContext ctx)
    {
        var player = HeroNames.Single(ctx.Arg("heroName"));
        var party = player.PartyBelongedTo ?? throw new StepFailedException("the player has no party");
        var kingdom = player.Clan?.Kingdom ?? throw new StepFailedException("the player is in no realm");
        if (party.Army == null)
        {
            var target = kingdom.Settlements.Where(s => s.IsFortification).OrderBy(s => s.StringId).FirstOrDefault()
                         ?? throw new StepFailedException($"{kingdom.StringId} holds no town or castle to gather at");
            kingdom.CreateArmy(player, target, Army.ArmyTypes.Patrolling);
        }
        yield return ctx.Until(() => party.Army?.LeaderParty == party, $"{player.Name}'s army to be raised", Sync);
        ctx.Log($"{party.StringId} leads {party.Army!.Name} ({party.Army.Parties.Count} parties)");
        ctx.Report("value", party.Army.Name.ToString());
    }

    /// <summary>
    /// Server: a lord of player <c>heroName</c>'s realm who can be asked to join the player's army (vanilla's conditions:
    /// he leads a lord's party in no army, battle or siege, its size over 40% of its limit; not the realm's ruler), and
    /// whose house is not the player's. With <c>skip</c>, a lord other than that id. Reports value (his id).
    /// </summary>
    private static IEnumerable<StepWait?> SummonableLord(StepContext ctx)
    {
        var player = HeroNames.Single(ctx.Arg("heroName"));
        var kingdom = player.Clan?.Kingdom ?? throw new StepFailedException("the player is in no realm");
        var skip = ctx.HasArg("skip") ? ctx.Arg("skip") : null;
        var model = Campaign.Current.Models.ArmyManagementCalculationModel;
        var lord = Hero.AllAliveHeroes.Where(h => h.Clan != null && h.Clan.Kingdom == kingdom && h.Clan != player.Clan && h != kingdom.Leader
                && h.StringId != skip && h.PartyBelongedTo is { } p && p.IsLordParty && p.LeaderHero == h && p.Army == null
                && p.MapEvent == null && p.SiegeEvent == null && p.CurrentSettlement == null && model.GetPartySizeScore(p) > 0.4f)
            .OrderBy(h => h.StringId).FirstOrDefault() ?? throw new StepFailedException($"no lord of {kingdom.StringId} free to join an army");
        ctx.Log($"{lord.StringId} of {lord.Clan.StringId} leads {lord.PartyBelongedTo.StringId}; relation to the player {lord.GetRelation(player)}");
        ctx.Report("value", lord.StringId);
        yield break;
    }

    /// <summary>
    /// Either side: whether hero <c>heroId</c>'s party is in the army player <c>heroName</c> leads ("yes" or "no"),
    /// waited for to be <c>expect</c>.
    /// </summary>
    private static IEnumerable<StepWait?> InArmy(StepContext ctx)
    {
        var player = HeroNames.Single(ctx.Arg("heroName"));
        var lord = Hero.FindFirst(h => h.StringId == ctx.Arg("heroId")) ?? throw new StepFailedException($"no hero '{ctx.Arg("heroId")}'");
        string Read()
        {
            var army = player.PartyBelongedTo?.Army;
            return army != null && lord.PartyBelongedTo is { } p && p.Army == army && army.Parties.Contains(p) ? "yes" : "no";
        }
        if (ctx.HasArg("expect"))
            yield return ctx.Until(() => Read() == ctx.Arg("expect"), $"{lord.StringId}'s party in {player.Name}'s army: {ctx.Arg("expect")}",
                TimeSpan.FromSeconds(ctx.ArgInt("timeoutSeconds", 30)));
        ctx.Assert(!ctx.HasArg("expect") || Read() == ctx.Arg("expect"), $"{lord.StringId}'s party in {player.Name}'s army: {Read()}");
        ctx.Report("value", Read());
    }
}
