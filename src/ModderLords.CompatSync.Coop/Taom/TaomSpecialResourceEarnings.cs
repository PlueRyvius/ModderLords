using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using ModderLords.CompatSync;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.MapEvents;
using TaleWorlds.CampaignSystem.Party;

namespace ModderLords.CompatSync.Coop.Taom;

/// <summary>
/// Special-resource earnings from fighting, in co-op (TAOM-MAP checklist: SpecialResources).
///
/// TAOM pays a player's special resource for a won battle, a completed raid, a cleared hideout and a tournament win,
/// from the campaign events for those. Under Coop none of them paid anyone: a client never raises them (the server
/// finalizes battles; a client's FinalizeEventAux is refused), and the server raises them but TAOM there credits
/// nobody on purpose (its main hero is the idle world-generation hero, and "is this the player's battle?" answers
/// for that hero).
///
/// Server: each of those handlers runs once per connected player who took part (their party fought in the battle,
/// raid or hideout, or they won the tournament), with that player as TAOM's player (PlayerScope), and TAOM's own
/// "credit nobody on a dedicated server" rule is lifted for that run only. The player's client owns their balance
/// (special-resource sync), so their new balances are pushed to it straight away.
/// Not covered: TAOM's prisoner-taken earning, whose event carries no party to tell the players apart.
/// </summary>
internal sealed class SpecialResourceEarningsComponent : ITaomComponent
{
    private static MethodInfo? _canEarn, _mapEvent, _raid, _hideout, _tournament;

    public string Id => "specres-earnings";

    public string? SkipReason(TaomContext context)
    {
        if (!context.IsServer) return "client";
        var t = context.Taom.GetType("TAOM.Features.SpecialResources.SpecialResourcesBehavior", false);
        const BindingFlags inst = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        _canEarn = t?.GetMethod("CanEarn", inst, null, Type.EmptyTypes, null);
        _mapEvent = t?.GetMethod("OnMapEventEnded", inst);
        _raid = t?.GetMethod("OnRaidCompleted", inst);
        _hideout = t?.GetMethod("OnHideoutCompleted", inst);
        _tournament = t?.GetMethod("OnTournamentFinished", inst);
        return _canEarn?.ReturnType != typeof(bool) || _mapEvent == null || _raid == null || _hideout == null || _tournament == null
            ? "TAOM changed; not found: SpecialResourcesBehavior.CanEarn/OnMapEventEnded/OnRaidCompleted/OnHideoutCompleted/OnTournamentFinished"
            : null;
    }

    public string Install(TaomContext context)
    {
        var h = new Harmony("ModderLords.Taom.SpecResEarnings");
        h.Patch(_canEarn!, postfix: new HarmonyMethod(typeof(SpecialResourceEarningsComponent), nameof(CanEarnPostfix)));
        h.Patch(_mapEvent!, prefix: new HarmonyMethod(typeof(SpecialResourceEarningsComponent), nameof(MapEventPrefix)));
        h.Patch(_raid!, prefix: new HarmonyMethod(typeof(SpecialResourceEarningsComponent), nameof(RaidPrefix)));
        h.Patch(_hideout!, prefix: new HarmonyMethod(typeof(SpecialResourceEarningsComponent), nameof(HideoutPrefix)));
        h.Patch(_tournament!, prefix: new HarmonyMethod(typeof(SpecialResourceEarningsComponent), nameof(TournamentPrefix)));
        return "server pays players' special resources for battles, raids, hideouts and tournaments";
    }

    [ThreadStatic] private static bool _earning;

    /// <summary>TAOM credits nobody on a dedicated server; inside a per-player earning run it credits that player.</summary>
    private static void CanEarnPostfix(ref bool __result)
    {
        if (_earning && ServerRelay.PlayerScope.CurrentHero != null) __result = true;
    }

    /// <summary>Runs the TAOM handler once for each given player, as that player, then pushes their balances.</summary>
    private static void RunFor(IEnumerable<(Hero Hero, MobileParty? Party)> players, object instance, MethodBase method, object?[] args)
    {
        foreach (var (hero, party) in players)
        {
            if (hero == Hero.MainHero) continue;
            try
            {
                var before = SpecialResourceSyncComponent.BalancesFor(hero.StringId);
                _earning = true;
                using (new ServerRelay.PlayerScope(hero, party))
                    method.Invoke(instance, args);
                var after = SpecialResourceSyncComponent.BalancesFor(hero.StringId);
                if (!after.SequenceEqual(before) && TaomActions.Push != null)
                {
                    TaomActions.Push(hero, SpecialResourceSyncComponent.Feature, after);
                    Log.Info($"TAOM layer: {hero.Name} earned special resources ({method.Name})");
                }
            }
            catch (Exception ex) { Log.Warn($"TAOM layer: special-resource earning for {hero.Name} failed: {ex.GetBaseException().Message}"); }
            finally { _earning = false; }
        }
    }

    private static List<(Hero Hero, MobileParty? Party)> PlayersIn(MapEvent? mapEvent)
    {
        var result = new List<(Hero, MobileParty?)>();
        if (mapEvent == null) return result;
        List<MobileParty> involved;
        try { involved = mapEvent.InvolvedParties.Select(p => p?.MobileParty).Where(p => p != null).ToList()!; }
        catch { return result; }
        foreach (var p in PlayerContextComponent.Players())
            if (p.Party != null && involved.Contains(p.Party)) result.Add(p);
        return result;
    }

    private static bool MapEventPrefix(object __instance, MapEvent mapEvent, MethodBase __originalMethod)
    {
        if (ServerRelay.PlayerScope.Active) return true;
        RunFor(PlayersIn(mapEvent), __instance, __originalMethod, new object[] { mapEvent });
        return false;   // unscoped, TAOM would only consider the idle server hero
    }

    private static bool RaidPrefix(object __instance, object[] __args, MethodBase __originalMethod)
    {
        if (ServerRelay.PlayerScope.Active) return true;
        var component = __args.Length > 1 ? __args[1] : null;
        var mapEvent = component?.GetType().GetProperty("MapEvent")?.GetValue(component) as MapEvent;
        RunFor(PlayersIn(mapEvent), __instance, __originalMethod, (object[])__args.Clone());
        return false;
    }

    private static bool HideoutPrefix(object __instance, object[] __args, MethodBase __originalMethod) => RaidPrefix(__instance, __args, __originalMethod);

    private static bool TournamentPrefix(object __instance, object[] __args, MethodBase __originalMethod)
    {
        if (ServerRelay.PlayerScope.Active) return true;
        var winner = __args.Length > 0 ? __args[0] as CharacterObject : null;
        var players = PlayerContextComponent.Players().Where(p => winner != null && p.Hero.CharacterObject == winner).ToList();
        RunFor(players, __instance, __originalMethod, (object[])__args.Clone());
        return false;
    }
}
