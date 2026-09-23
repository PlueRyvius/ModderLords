using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using ModderLords.CompatSync;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Localization;

namespace ModderLords.CompatSync.Coop.Taom;

/// <summary>
/// TAOM's Supply Lines in co-op (TAOM-MAP checklist: Supply Lines).
///
/// An order buys goods and recruits at a town (or from a friendly lord), spawns a caravan party that travels to the
/// player, and hands the cargo over on arrival. TAOM has no co-op handling for it. On a client, placing an order takes
/// stock out of the town, spawns a party and charges gold, none of which Coop lets reach the server, and every
/// client's per-frame tick moved and delivered caravans locally; the server's one order book is read as "the player's",
/// so its caravans would head for, and deliver to, whichever player TAOM happened to be serving.
///
/// Client: placing an order is sent to the server; TAOM's order screen closes and the server's answer is shown. The
/// ticks that move caravans, deliver, cancel or respawn them do not run. The order book comes from the server through
/// the state mirror, and the route arrows show only this player's own caravans.
/// Server: places the order for the sender (PlayerScope, so the town sells to them and their gold pays); the per-frame
/// and hourly ticks and the camp-break cancellation run once per player, each seeing only their own orders, so every
/// caravan travels to and delivers to its owner. Caravans whose owner is offline wait (they are ticked unowned, which
/// only lets them be lost). Route arrows are never drawn on the server (SandBox.View).
/// </summary>
internal sealed class SupplyLinesComponent : ITaomComponent
{
    public const string Feature = "supply";
    private const string Owner = "ModderLords.Taom.SupplyLines";

    private static Assembly? _taom;
    private static FieldInfo? _orders;
    private static FieldInfo? _lastFrameHours;
    private static FieldInfo? _activeCache;
    private static FieldInfo? _trackers;
    private static MethodInfo? _tryPlace;
    private static MethodInfo? _frameTick;
    private static MethodInfo? _hourlyTick;
    private static MethodInfo? _cancelCamp;
    private static MethodInfo? _activeOrders;
    private static MethodInfo? _getSources;
    private static Type? _escortType;
    private static Type? _orderType;
    private static readonly List<MethodInfo> ClientSkipped = new List<MethodInfo>();
    private static readonly List<MethodInfo> Visuals = new List<MethodInfo>();

    public string Id => "supply-lines";

    public string? SkipReason(TaomContext context)
    {
        var t = _taom = context.Taom;
        var service = t.GetType("TAOM.Features.SupplyLines.SupplyOrderService", false);
        var caravans = t.GetType("TAOM.Features.SupplyLines.SupplyCaravanService", false);
        var sourceInfo = t.GetType("TAOM.Features.SupplyLines.SupplySourceInfo", false);
        _escortType = t.GetType("TAOM.Features.SupplyLines.Domain.SupplyEscortOption", false);
        _orderType = t.GetType("TAOM.Features.SupplyLines.Domain.SupplyOrder", false);
        var visuals = t.GetType("TAOM.Features.SupplyLines.SupplyRouteVisualService", false);
        const BindingFlags inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        _orders = service?.GetField("_orders", inst);
        _lastFrameHours = service?.GetField("_lastFrameHours", inst);
        _activeCache = service?.GetField("_activeOrdersCache", inst);
        _trackers = caravans?.GetField("_caravans", inst);
        _tryPlace = sourceInfo == null || _escortType == null ? null : service?.GetMethod("TryPlaceOrder", new[]
        {
            sourceInfo, typeof(IReadOnlyDictionary<string, int>), typeof(IReadOnlyDictionary<string, int>), _escortType,
            typeof(string).MakeByRefType(), typeof(bool),
        });
        _frameTick = service?.GetMethod("FrameTick", Type.EmptyTypes);
        _hourlyTick = service?.GetMethod("HourlyTick", Type.EmptyTypes);
        _cancelCamp = service?.GetMethod("CancelCampOrders", Type.EmptyTypes);
        _activeOrders = service?.GetProperty("ActiveOrders")?.GetGetMethod();
        _getSources = t.GetType("TAOM.Features.SupplyLines.ISupplySourceService", false)?.GetMethod("GetSources", Type.EmptyTypes);

        ClientSkipped.Clear();
        foreach (var (name, args) in new (string, Type[])[]
                 {
                     ("FrameTick", Type.EmptyTypes), ("HourlyTick", Type.EmptyTypes), ("CancelCampOrders", Type.EmptyTypes),
                     ("OnCaravanDestroyed", new[] { typeof(string) }), ("OnGameLoaded", Type.EmptyTypes),
                 })
            if (service?.GetMethod(name, args) is { } m) ClientSkipped.Add(m);
        Visuals.Clear();
        foreach (var name in new[] { "Update", "ClearAll" })
            if (visuals?.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null) is { } m)
                Visuals.Add(m);

        var missing = new List<string>();
        if (_orders == null || !TaomRecords.IsRecordBook(_orders.FieldType)) missing.Add("SupplyOrderService._orders");
        if (_lastFrameHours?.FieldType != typeof(double) || _activeCache == null) missing.Add("SupplyOrderService._lastFrameHours/_activeOrdersCache");
        if (_trackers == null || !typeof(IDictionary).IsAssignableFrom(_trackers.FieldType)) missing.Add("SupplyCaravanService._caravans");
        if (_tryPlace == null) missing.Add("SupplyOrderService.TryPlaceOrder(...)");
        if (_frameTick == null || _hourlyTick == null || _cancelCamp == null || _activeOrders == null) missing.Add("SupplyOrderService ticks/ActiveOrders");
        if (ClientSkipped.Count != 5) missing.Add("SupplyOrderService tick handlers");
        if (_getSources == null) missing.Add("ISupplySourceService.GetSources()");
        if (Visuals.Count != 2) missing.Add("SupplyRouteVisualService.Update/ClearAll");
        return missing.Count == 0 ? null : "TAOM changed; not found: " + string.Join(", ", missing);
    }

    public string Install(TaomContext context)
    {
        var h = new Harmony(Owner);
        if (context.IsServer)
        {
            foreach (var visual in Visuals)
                h.Patch(visual, transpiler: new HarmonyMethod(typeof(TaomFieldCamp), nameof(TaomFieldCamp.EmptyBodyTranspiler)));
            foreach (var tick in new[] { _frameTick!, _hourlyTick!, _cancelCamp! })
                h.Patch(tick, prefix: new HarmonyMethod(typeof(SupplyLinesComponent), nameof(PerOwnerPrefix)));
            TaomActions.Register(Feature, ServerAction);
            return "server places players' supply orders and runs each player's caravans for that player; no route visuals";
        }
        h.Patch(_tryPlace!, prefix: new HarmonyMethod(typeof(SupplyLinesComponent), nameof(TryPlacePrefix)));
        foreach (var m in ClientSkipped)
            h.Patch(m, prefix: new HarmonyMethod(typeof(SupplyLinesComponent), nameof(ClientSkipPrefix)));
        h.Patch(_activeOrders!, postfix: new HarmonyMethod(typeof(SupplyLinesComponent), nameof(OwnActiveOrdersPostfix)));
        return "client sends supply orders to the server; caravans move and deliver on the server";
    }

    private static object? Resolve(string name) => _taom == null ? null : TaomActions.Resolve(_taom, name);

    private static MobileParty? CaravanOf(object order) =>
        TaomOwners.FindParty(order.GetType().GetField("CaravanPartyId")?.GetValue(order) as string);

    // ---- server ----------------------------------------------------------------------------------------

    [ThreadStatic] private static bool _perOwner;

    /// <summary>
    /// Server: TAOM's supply ticks act on "the player" (the caravan heads for the main party and delivers to it; a camp
    /// break cancels the main party's camp orders). Outside a player's scope the call is repeated once per owner with
    /// the book narrowed to that owner's orders, then once for orders with no connected owner.
    /// </summary>
    private static bool PerOwnerPrefix(object __instance, MethodBase __originalMethod)
    {
        if (_perOwner) return true;
        var caravanService = Resolve("TAOM.Features.SupplyLines.ISupplyCaravanService");
        if (_orders!.GetValue(__instance) is not IDictionary book || book.Count == 0) return true;

        // In a player's scope (a camp break relayed for them): only that player's orders.
        if (ServerRelay.PlayerScope.Active)
        {
            RunNarrowed(__instance, caravanService, __originalMethod, o => TaomOwners.IsCurrentPlayers(CaravanOf(o)));
            return false;
        }
        if (__originalMethod == _cancelCamp) return false;   // no player broke a camp: nothing of anyone's to cancel

        var owners = new Dictionary<Clan, (Hero Hero, MobileParty? Party)>();
        foreach (var p in PlayerContextComponent.Players())
            if (p.Hero.Clan != null && !owners.ContainsKey(p.Hero.Clan)) owners[p.Hero.Clan] = p;
        foreach (var owner in owners)
        {
            if (owner.Value.Party == null) continue;
            using (new ServerRelay.PlayerScope(owner.Value.Hero, owner.Value.Party))
                RunNarrowed(__instance, caravanService, __originalMethod, o => CaravanOf(o)?.ActualClan == owner.Key);
        }
        RunNarrowed(__instance, caravanService, __originalMethod, o =>
        {
            var clan = CaravanOf(o)?.ActualClan;
            return clan == null || !owners.ContainsKey(clan) || owners[clan].Party == null;
        });
        return false;
    }

    private static void RunNarrowed(object service, object? caravanService, MethodBase method, Func<object, bool> keep)
    {
        _perOwner = true;
        try
        {
            // The frame tick skips a second call in the same campaign hour; each owner's run is a first call.
            _lastFrameHours!.SetValue(service, double.NaN);
            _activeCache!.SetValue(service, null);
            void Run() => TaomOwners.WithNarrowedBook(service, _orders!, keep, () => method.Invoke(service, null));
            if (caravanService != null && _trackers!.DeclaringType!.IsInstanceOfType(caravanService))
                TaomOwners.WithNarrowedBook(caravanService, _trackers, tracker =>
                {
                    var order = tracker.GetType().GetField("Order")?.GetValue(tracker);
                    return order != null && keep(order);
                }, Run);
            else Run();
        }
        catch (Exception ex) { Log.Warn("TAOM layer: supply tick failed for one owner: " + ex.GetBaseException().Message); }
        finally
        {
            _activeCache!.SetValue(service, null);
            _perOwner = false;
        }
    }

    /// <summary>Server, inside PlayerScope: args = [settlementId, heroId, escort, fromCamp, goods, troops] (id=qty csv).</summary>
    private static TaomActionOutcome ServerAction(Hero hero, MobileParty? party, string op, IList<string> args)
    {
        if (op != "place" || args.Count != 6) return TaomActionOutcome.Fail("");
        if (party == null) return TaomActionOutcome.Fail("Your party was not found on the server.");
        var service = Resolve("TAOM.Features.SupplyLines.ISupplyOrderService");
        var sources = Resolve("TAOM.Features.SupplyLines.ISupplySourceService");
        if (service == null || sources == null) return TaomActionOutcome.Fail("TAOM's supply services are not available on the server.");

        object? source = null;
        if (_getSources!.Invoke(sources, null) is IEnumerable all)
            foreach (var s in all)
            {
                var t = s.GetType();
                var settlement = t.GetField("SettlementId")?.GetValue(s) as string ?? "";
                var lord = t.GetField("HeroId")?.GetValue(s) as string ?? "";
                if (settlement == args[0] && lord == args[1]) { source = s; break; }
            }
        if (source == null) return TaomActionOutcome.Fail(new TextObject("{=taom_sl_fail_source}No supply source selected.").ToString());
        if (source.GetType().GetField("CanOrder")?.GetValue(source) is false)
            return TaomActionOutcome.Fail(source.GetType().GetField("DisabledReason")?.GetValue(source) as string ?? "");

        var escort = Enum.ToObject(_escortType!, int.TryParse(args[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var e) ? e : 0);
        var call = new object?[] { source, Parse(args[4]), Parse(args[5]), escort, null, args[3] == "1" };
        var order = _tryPlace!.Invoke(service, call);
        if (order == null)
            return TaomActionOutcome.Fail(call[4] as string is { Length: > 0 } why ? why : new TextObject("{=taom_sl_order_failed}The order could not be placed.").ToString());
        var fromLord = args[1].Length > 0;
        Log.Info($"TAOM layer: supply order {order.GetType().GetField("OrderId")?.GetValue(order)} placed for {hero.Name} ({(fromLord ? "lord " + args[1] : args[0])})");
        var text = fromLord
            ? new TextObject("{=taom_sl_lord_dispatched}{LORD} sends reinforcements, they are on the way.")
                .SetTextVariable("LORD", source.GetType().GetField("DisplayName")?.GetValue(source) as string ?? "")
            : new TextObject("{=taom_sl_dispatched}A supply caravan has set out for your party.");
        return new TaomActionOutcome(true, text.ToString());
    }

    internal static Dictionary<string, int> Parse(string csv)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var pair in csv.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.LastIndexOf('=');
            if (eq <= 0 || !int.TryParse(pair.Substring(eq + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) || n <= 0) continue;
            result[pair.Substring(0, eq)] = n;
        }
        return result;
    }

    internal static string Format(IEnumerable<KeyValuePair<string, int>>? pairs) =>
        pairs == null ? "" : string.Join(",", pairs.Where(p => p.Value > 0).Select(p => p.Key + "=" + p.Value.ToString(CultureInfo.InvariantCulture)));

    // ---- client ----------------------------------------------------------------------------------------

    private static bool ClientSkipPrefix() => !TaomActions.IsCoopClient;

    /// <summary>
    /// Client: the order goes to the server. A placeholder order is returned so TAOM's screen closes as it does on
    /// success; the server's answer (dispatched, or why not) follows as a message.
    /// </summary>
    private static bool TryPlacePrefix(object source, IReadOnlyDictionary<string, int> goods, IReadOnlyDictionary<string, int> troops,
        object escort, ref string failReason, bool placedFromCamp, ref object __result)
    {
        if (!TaomActions.IsCoopClient) return true;
        var t = source.GetType();
        TaomActions.Send!(Feature, "place", new[]
        {
            t.GetField("SettlementId")?.GetValue(source) as string ?? "",
            t.GetField("HeroId")?.GetValue(source) as string ?? "",
            Convert.ToInt32(escort, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
            placedFromCamp ? "1" : "0",
            Format(goods),
            Format(troops),
        });
        Log.Info("TAOM layer: supply order sent to the server");
        failReason = null!;
        __result = Activator.CreateInstance(_orderType!)!;
        return false;
    }

    /// <summary>Client: the mirrored book holds every player's orders; the route arrows (and TAOM's lists) show only yours.</summary>
    private static void OwnActiveOrdersPostfix(ref IReadOnlyCollection<object> __result)
    {
        if (!TaomActions.IsCoopClient || __result == null) return;
        if (__result.All(o => TaomOwners.IsCurrentPlayers(CaravanOf(o)))) return;
        var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(_orderType!))!;
        foreach (var o in __result)
            if (TaomOwners.IsCurrentPlayers(CaravanOf(o))) list.Add(o);
        __result = (IReadOnlyCollection<object>)list;
    }
}
