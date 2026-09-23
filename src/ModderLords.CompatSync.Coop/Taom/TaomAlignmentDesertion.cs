using System;
using System.Reflection;
using HarmonyLib;
using ModderLords.CompatSync;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;

namespace ModderLords.CompatSync.Coop.Taom;

/// <summary>
/// TAOM's alignment desertion in co-op (TAOM-MAP checklist). Every day, troops of the opposing alignment leave lord
/// parties and garrisons. TAOM runs it on every peer with no co-op gate:
/// - on a client it removed troops from rosters Coop owns on the server; Coop does not send a client's roster change,
///   so the troops vanished on that screen only (the upkeep-desertion bug, #121). Clients now leave it to the server.
/// - on the server a player's party is not "the main party" and a player's clan is not "the player clan", so players'
///   parties and fiefs deserted under TAOM's AI setting and its "apply to the player" setting was ignored. The server
///   now runs the day's check for a player's party or fief with that player as TAOM's player (PlayerScope).
/// </summary>
internal sealed class AlignmentDesertionComponent : ITaomComponent
{
    private static MethodInfo? _party;
    private static MethodInfo? _settlement;

    public string Id => "alignment-desertion";

    public string? SkipReason(TaomContext context)
    {
        var t = context.Taom.GetType("TAOM.Features.AlignmentDesertion.Hooks.AlignmentDesertionBehavior", false);
        const BindingFlags inst = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        _party = t?.GetMethod("OnDailyTickParty", inst, null, new[] { typeof(MobileParty) }, null);
        _settlement = t?.GetMethod("OnDailyTickSettlement", inst, null, new[] { typeof(Settlement) }, null);
        return _party == null || _settlement == null
            ? "TAOM changed; not found: AlignmentDesertionBehavior.OnDailyTickParty/OnDailyTickSettlement"
            : null;
    }

    public string Install(TaomContext context)
    {
        var h = new Harmony("ModderLords.Taom.AlignmentDesertion");
        if (!context.IsServer)
        {
            var skip = new HarmonyMethod(typeof(AlignmentDesertionComponent), nameof(ClientSkip));
            h.Patch(_party!, prefix: skip);
            h.Patch(_settlement!, prefix: skip);
            return "client leaves alignment desertion to the server";
        }
        var close = new HarmonyMethod(typeof(AlignmentDesertionComponent), nameof(Close));
        h.Patch(_party!, prefix: new HarmonyMethod(typeof(AlignmentDesertionComponent), nameof(PartyPrefix)), finalizer: close);
        h.Patch(_settlement!, prefix: new HarmonyMethod(typeof(AlignmentDesertionComponent), nameof(SettlementPrefix)), finalizer: close);
        return "server runs alignment desertion for players' parties and fiefs as those players";
    }

    private static bool ClientSkip() => !TaomActions.IsCoopClient && !Common.ModInformation.IsClient;

    private static IDisposable? ScopeFor(Clan? clan, MobileParty? party)
    {
        if (ServerRelay.PlayerScope.Active) return null;
        foreach (var p in PlayerContextComponent.Players())
            if ((party != null && p.Party == party) || (clan != null && p.Hero.Clan == clan))
                return p.Hero == Hero.MainHero ? null : new ServerRelay.PlayerScope(p.Hero, p.Party);
        return null;
    }

    private static void PartyPrefix(MobileParty party, out IDisposable? __state) =>
        __state = ScopeFor(party?.LeaderHero?.Clan, party);

    private static void SettlementPrefix(Settlement settlement, out IDisposable? __state) =>
        __state = ScopeFor(settlement?.OwnerClan, null);

    private static Exception? Close(IDisposable? __state, Exception? __exception)
    {
        __state?.Dispose();
        return __exception;
    }
}
