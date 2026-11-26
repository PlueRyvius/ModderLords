using System;
using System.Collections;
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
/// Steps for Bellum's asylum audiences (stage 4d, ExiledClanRecoveryBehavior): an exiled house that asks a player ruler for
/// refuge and waits in the player's keep.
/// </summary>
public static class AsylumSteps
{
    private const string Behaviour = "BellumCivile.Behaviors.ExiledClanRecoveryBehavior";

    public static void Register()
    {
        InGameTests.Register("bellum.asylumRequest", StepSide.Server, AsylumRequest);
        InGameTests.Register("bellum.enterSettlement", StepSide.Server, EnterSettlement);
        InGameTests.Register("bellum.asylumVisit", StepSide.Any, AsylumVisit);
        InGameTests.Register("bellum.clanRealm", StepSide.Any, ClanRealm);
    }

    private static object Recovery() =>
        (FindType(Behaviour) is { } type ? typeof(Campaign).GetMethod(nameof(Campaign.GetCampaignBehavior))!.MakeGenericMethod(type).Invoke(Campaign.Current, null) : null)
        ?? throw new StepFailedException("Bellum's exile recovery is not running");

    /// <summary>Coop's scope that makes the server's player hero, party and house the given player's, for a while.</summary>
    private static IDisposable AsPlayer(Hero player)
    {
        var type = FindType("GameInterface.Services.Heroes.Patches.MainHeroSubstitutionScope") ?? throw new StepFailedException("Coop has no MainHeroSubstitutionScope");
        var ctor = type.GetConstructor(Any, null, new[] { typeof(Hero), typeof(MobileParty) }, null) ?? throw new StepFailedException("Coop's MainHeroSubstitutionScope takes no (Hero, MobileParty)");
        return (IDisposable)ctor.Invoke(new object?[] { player, player.PartyBelongedTo });
    }

    /// <summary>
    /// Server: a landless house of another realm (not <c>skip</c>, clan ids "a|b") is exiled and asks the realm player
    /// <c>heroName</c> rules for refuge, as Bellum's own asylum test does for the player (CreateAsylumTest, as that player):
    /// its leader waits in the player's keep. Reports value (the house's id), guest (its leader's id), settlement and
    /// origin (the realm it left).
    /// </summary>
    private static IEnumerable<StepWait?> AsylumRequest(StepContext ctx)
    {
        var player = HeroNames.Single(ctx.Arg("heroName"));
        var realm = player.Clan?.Kingdom is { } k && k.Leader == player ? k : throw new StepFailedException("the player rules no realm");
        var recovery = Recovery();
        var rejection = recovery.GetType().GetMethod("AsylumTestRejection", Any) ?? throw new StepFailedException("Bellum has no AsylumTestRejection");
        var create = recovery.GetType().GetMethod("CreateAsylumTest", Any) ?? throw new StepFailedException("Bellum has no CreateAsylumTest");
        var skip = new HashSet<string>(ctx.Arg("skip", "").Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries));
        Clan? clan;
        Kingdom? origin;
        string answer;
        using (AsPlayer(player))
        {
            clan = Clan.All.Where(c => c != null && !skip.Contains(c.StringId)).OrderBy(c => c.StringId)
                .FirstOrDefault(c => rejection.Invoke(recovery, new object[] { c, realm }) == null)
                ?? throw new StepFailedException($"no house may ask {realm.StringId} for asylum");
            origin = clan.Kingdom;
            answer = create.Invoke(recovery, new object[] { clan.StringId }) as string ?? "";
        }
        ctx.Log(answer);
        if (answer.StartsWith("Error", StringComparison.Ordinal)) throw new StepFailedException(answer);
        var visit = Visit(clan.StringId) ?? throw new StepFailedException($"{clan.StringId} has no audience: {answer}");
        var guest = (Hero)Field(visit, "Guest")!;
        var settlement = (Settlement)Field(visit, "Settlement")!;
        yield return ctx.Until(() => guest.CurrentSettlement == settlement, $"{guest.StringId} in {settlement.StringId}", Sync);
        ctx.Report("value", clan.StringId);
        ctx.Report("guest", guest.StringId);
        ctx.Report("settlement", settlement.StringId);
        ctx.Report("origin", origin?.StringId ?? "");
    }

    /// <summary>Server: player <c>heroName</c>'s party enters settlement <c>settlement</c>, as when the player rides in.</summary>
    private static IEnumerable<StepWait?> EnterSettlement(StepContext ctx)
    {
        var player = HeroNames.Single(ctx.Arg("heroName"));
        var party = player.PartyBelongedTo ?? throw new StepFailedException("the player has no party");
        var settlement = Settlement.Find(ctx.Arg("settlement")) ?? throw new StepFailedException($"no settlement '{ctx.Arg("settlement")}'");
        if (party.CurrentSettlement != settlement)
        {
            if (party.CurrentSettlement != null) LeaveSettlementAction.ApplyForParty(party);
            party.Position = settlement.GatePosition;
            EnterSettlementAction.ApplyForParty(party, settlement);
        }
        yield return ctx.Until(() => party.CurrentSettlement == settlement, $"{party.StringId} in {settlement.StringId}", Sync);
    }

    /// <summary>
    /// Either side: the state of house <c>clanId</c>'s asylum audience ("waiting", "accepted", "refused", "cancelled",
    /// "expired", or "none"), waited for to be <c>expect</c>. Reports value.
    /// </summary>
    private static IEnumerable<StepWait?> AsylumVisit(StepContext ctx)
    {
        var id = ctx.Arg("clanId");
        string Read() => Visit(id) is { } v && Field(v, "State") is int s ? s switch { 0 => "waiting", 1 => "accepted", 2 => "refused", 3 => "cancelled", 4 => "expired", _ => s.ToString() } : "none";
        if (ctx.HasArg("expect"))
            yield return ctx.Until(() => Read() == ctx.Arg("expect"), $"{id}'s audience: {ctx.Arg("expect")}", Sync);
        ctx.Assert(!ctx.HasArg("expect") || Read() == ctx.Arg("expect"), $"{id}'s audience: {Read()}");
        ctx.Report("value", Read());
        ctx.Report("checks", Checks(Visit(id)));
    }

    // What Bellum checks before the audience's lines show (IsAsylumAudiencePresent and ConversationAsylumVisit), as this
    // side reads it.
    private static string Checks(object? visit)
    {
        if (visit == null) return "no audience";
        var type = Recovery().GetType();
        bool Call(string name, params object?[] args) => type.GetMethod(name, Any)?.Invoke(null, args) is true;
        var ruler = Field(visit, "Ruler") as Hero;
        var realm = Field(visit, "Realm") as Kingdom;
        var guest = Field(visit, "Guest") as Hero;
        var settlement = Field(visit, "Settlement") as Settlement;
        var clan = Field(visit, "Clan") as Clan;
        return $"ruler {ruler?.StringId} is me {ruler == Hero.MainHero}; realm {realm?.StringId} led by ruler {realm?.Leader == ruler}; refuge {Call("IsPlayerRuledValidRefuge", realm)}; "
            + $"authority {Call("AsylumAuthorityValid", visit)}; guest valid {Call("AsylumGuestValid", visit)}; host {Call("IsAsylumHost", settlement, realm)}; "
            + $"me in {Hero.MainHero?.CurrentSettlement?.StringId}, guest in {guest?.CurrentSettlement?.StringId}, at {settlement?.StringId}; "
            + $"guest's party {guest?.PartyBelongedTo?.StringId} is the visit's {guest?.PartyBelongedTo == Field(visit, "Party") as MobileParty} ({(Field(visit, "Party") as MobileParty)?.StringId ?? "null"}, same id {(Field(visit, "Party") as MobileParty)?.StringId == guest?.PartyBelongedTo?.StringId}); "
            + $"house realm {clan?.Kingdom?.StringId ?? "none"}; at war {clan?.IsAtWarWith(realm) == true}; deadline future {(Field(visit, "Deadline") is CampaignTime d && d.IsFuture)}; "
            + $"ruling {realm?.RulingClan?.StringId} player clan {Clan.PlayerClan?.StringId}";
    }

    /// <summary>
    /// Either side: house <c>clanId</c>'s realm ("none" without one), waited for to be <c>expect</c>, or not to be
    /// <c>not</c>. Reports value.
    /// </summary>
    private static IEnumerable<StepWait?> ClanRealm(StepContext ctx)
    {
        var clan = Clan.FindFirst(c => c.StringId == ctx.Arg("clanId")) ?? throw new StepFailedException($"no house '{ctx.Arg("clanId")}'");
        string Read() => clan.Kingdom?.StringId ?? "none";
        if (ctx.HasArg("expect"))
            yield return ctx.Until(() => Read() == ctx.Arg("expect"), $"{clan.StringId}'s realm: {ctx.Arg("expect")}", Sync);
        if (ctx.HasArg("not"))
            yield return ctx.Until(() => Read() != ctx.Arg("not"), $"{clan.StringId}'s realm: not {ctx.Arg("not")}", Sync);
        ctx.Assert((!ctx.HasArg("expect") || Read() == ctx.Arg("expect")) && (!ctx.HasArg("not") || Read() != ctx.Arg("not")), $"{clan.StringId}'s realm: {Read()}");
        ctx.Report("value", Read());
    }

    private static object? Visit(string clanId) =>
        Recovery().GetType().GetField("_asylumVisits", Any)?.GetValue(Recovery()) is IDictionary visits && visits.Contains(clanId) ? visits[clanId] : null;

    private static object? Field(object? o, string name) => o?.GetType().GetField(name, Any)?.GetValue(o);
}
