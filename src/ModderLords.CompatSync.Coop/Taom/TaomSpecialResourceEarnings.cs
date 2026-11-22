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
///
/// TAOM 2.0.41 does this itself: SpecialResourceEarningService credits every participant Coop's player registry marks
/// as a player (TAOM's own CoopPlayerControlQuery), on the session's authority only, and runs each player's daily
/// income and upkeep there too. CanEarn is gone, and running a handler once per player would now credit every player
/// in the battle once per player. On that TAOM the server leaves crediting to TAOM and only pushes each player's
/// changed balances to their client, so a client's next balance report cannot undo a server-side change.
///
/// What is pushed is driven by the writes themselves: every balance change goes through
/// SpecialResourceStorageService.Set (Add calls it), so a postfix there notes which connected player's balance changed.
/// That covers earnings, daily income and upkeep, recruit charges (OnUnitRecruited's ChargeRecruit) and Coop upgrade
/// charges (ChargeCoopUpgrades), inside a PlayerScope or not, and any path TAOM adds later. Known multi-write paths
/// (the four earning handlers, the daily tick, recruiting, Coop upgrades) are batches: their writes are pushed once
/// when the outermost one returns. A write outside a batch is pushed straight away. Writes that come from the player's
/// own client report (special-resource sync) are not pushed back, and the emissary's answer carries its balances.
/// </summary>
internal sealed class SpecialResourceEarningsComponent : ITaomComponent
{
    private static MethodInfo? _canEarn, _mapEvent, _raid, _hideout, _tournament, _dailyTick;
    private static MethodInfo? _recruited, _coopUpgrades, _storageSet, _storageGet;
    private static bool _native;

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
        _dailyTick = t?.GetMethod("OnDailyTickHero", inst, null, new[] { typeof(Hero) }, null);
        var earning = context.Taom.GetType("TAOM.Features.SpecialResources.SpecialResourceEarningService", false);
        _native = _canEarn == null && earning?.GetMethod("OnVictory", BindingFlags.Instance | BindingFlags.Public) != null;
        _recruited = t?.GetMethod("OnUnitRecruited", inst, null, new[] { typeof(CharacterObject), typeof(int) }, null);
        _coopUpgrades = earning?.GetMethod("ChargeCoopUpgrades", BindingFlags.Instance | BindingFlags.Public);
        var storage = context.Taom.GetType("TAOM.Features.SpecialResources.SpecialResourceStorageService", false);
        _storageSet = storage?.GetMethod("Set", BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(string), typeof(string), typeof(float) }, null);
        _storageGet = storage?.GetMethod("Get", BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(string), typeof(string) }, null);

        if (_native)
        {
            // The push hangs on the storage write; the handlers and ticks batch it so a change goes out once, not once
            // per write. All are required, the daily tick (income and upkeep, every day for every player) included.
            var missing = new List<string>();
            if (_storageSet == null || _storageGet?.ReturnType != typeof(float))
                missing.Add("SpecialResourceStorageService.Set(string, string, float)/Get(string, string)");
            if (_mapEvent == null || _raid == null || _hideout == null || _tournament == null)
                missing.Add("SpecialResourcesBehavior.OnMapEventEnded/OnRaidCompleted/OnHideoutCompleted/OnTournamentFinished");
            if (_dailyTick == null) missing.Add("SpecialResourcesBehavior.OnDailyTickHero(Hero)");
            if (_recruited == null) missing.Add("SpecialResourcesBehavior.OnUnitRecruited(CharacterObject, int)");
            if (_coopUpgrades == null) missing.Add("SpecialResourceEarningService.ChargeCoopUpgrades");
            return missing.Count == 0 ? null : "TAOM changed; not found: " + string.Join(", ", missing);
        }
        if (_mapEvent == null || _raid == null || _hideout == null || _tournament == null)
            return "TAOM changed; not found: SpecialResourcesBehavior.OnMapEventEnded/OnRaidCompleted/OnHideoutCompleted/OnTournamentFinished";
        return _canEarn?.ReturnType == typeof(bool)
            ? null
            : "TAOM changed; not found: SpecialResourcesBehavior.CanEarn() (up to 2.0.28) or SpecialResourceEarningService (2.0.41)";
    }

    public string Install(TaomContext context)
    {
        var h = new Harmony("ModderLords.Taom.SpecResEarnings");
        if (_native)
        {
            h.Patch(_storageSet!, prefix: new HarmonyMethod(typeof(SpecialResourceEarningsComponent), nameof(StorageSetPrefix)),
                postfix: new HarmonyMethod(typeof(SpecialResourceEarningsComponent), nameof(StorageSetPostfix)));
            foreach (var batch in new[] { _mapEvent!, _raid!, _hideout!, _tournament!, _dailyTick!, _recruited!, _coopUpgrades! })
                h.Patch(batch, prefix: new HarmonyMethod(typeof(SpecialResourceEarningsComponent), nameof(BatchEnterPrefix)),
                    finalizer: new HarmonyMethod(typeof(SpecialResourceEarningsComponent), nameof(BatchLeaveFinalizer)));
            return "TAOM credits and charges players itself; server pushes each player's changed special-resource balances to them";
        }
        h.Patch(_canEarn!, postfix: new HarmonyMethod(typeof(SpecialResourceEarningsComponent), nameof(CanEarnPostfix)));
        h.Patch(_mapEvent!, prefix: new HarmonyMethod(typeof(SpecialResourceEarningsComponent), nameof(MapEventPrefix)));
        h.Patch(_raid!, prefix: new HarmonyMethod(typeof(SpecialResourceEarningsComponent), nameof(RaidPrefix)));
        h.Patch(_hideout!, prefix: new HarmonyMethod(typeof(SpecialResourceEarningsComponent), nameof(HideoutPrefix)));
        h.Patch(_tournament!, prefix: new HarmonyMethod(typeof(SpecialResourceEarningsComponent), nameof(TournamentPrefix)));
        return "server pays players' special resources for battles, raids, hideouts and tournaments";
    }

    // ---- TAOM 2.0.41: TAOM credits and charges, the server pushes ---------------------------------------

    // Game thread only, like every campaign write.
    private static int _batchDepth;
    private static readonly HashSet<string> Changed = new HashSet<string>(StringComparer.Ordinal);
    private static string? _unpushedHero;

    /// <summary>
    /// While the returned scope is open, writes to this hero's balances are not pushed: they came from the player's own
    /// report, or the action's answer already carries them.
    /// </summary>
    internal static IDisposable NotPushedFor(string heroId) => new NotPushed(heroId);

    private sealed class NotPushed : IDisposable
    {
        private readonly string? _previous = _unpushedHero;
        public NotPushed(string heroId) => _unpushedHero = heroId;
        public void Dispose() => _unpushedHero = _previous;
    }

    /// <summary>Set(heroId, resourceId, amount): remembers the balance before, for a connected player's hero only.</summary>
    private static void StorageSetPrefix(object __instance, string __0, string __1, out float? __state)
    {
        __state = null;
        try
        {
            if (TaomActions.Push == null || __0 == null || __0 == _unpushedHero || PlayerHero(__0) == null) return;
            __state = (float)_storageGet!.Invoke(__instance, new object[] { __0, __1 })!;
        }
        catch { }
    }

    private static void StorageSetPostfix(object __instance, string __0, string __1, float? __state)
    {
        if (__state is not { } before) return;
        try
        {
            if ((float)_storageGet!.Invoke(__instance, new object[] { __0, __1 })! == before) return;
            Changed.Add(__0);
            if (_batchDepth == 0) Flush(null);
        }
        catch { }
    }

    private static void BatchEnterPrefix() => _batchDepth++;

    private static void BatchLeaveFinalizer(MethodBase __originalMethod)
    {
        if (--_batchDepth > 0) return;
        _batchDepth = 0;
        Flush(__originalMethod.Name);
    }

    /// <summary>Pushes each changed player's balances to their client, once.</summary>
    private static void Flush(string? source)
    {
        if (Changed.Count == 0) return;
        var heroIds = Changed.ToList();
        Changed.Clear();
        if (TaomActions.Push == null) return;
        foreach (var heroId in heroIds)
        {
            if (PlayerHero(heroId) is not { } hero) continue;
            try
            {
                TaomActions.Push(hero, SpecialResourceSyncComponent.Feature, SpecialResourceSyncComponent.BalancesFor(heroId));
                if (source != null && source != "OnDailyTickHero") Log.Info($"TAOM layer: {hero.Name}'s special resources changed ({source})");
            }
            catch (Exception ex) { Log.Warn($"TAOM layer: could not push {hero.Name}'s special resources: {ex.GetBaseException().Message}"); }
        }
    }

    /// <summary>A connected player's hero by id; never the server's own idle hero.</summary>
    private static Hero? PlayerHero(string heroId)
    {
        foreach (var (hero, _) in PlayerContextComponent.Players())
            if (hero.StringId == heroId && hero != Hero.MainHero) return hero;
        return null;
    }

    // ---- up to TAOM 2.0.28: the server runs each handler per player -----------------------------------

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
