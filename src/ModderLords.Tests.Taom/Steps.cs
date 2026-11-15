using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using ModderLords.Testing;
using TAOM;
using TAOM.Features.SupplyLines;
using TAOM.Features.SupplyLines.Domain;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Library;

namespace ModderLords.Tests.Taom;

/// <summary>
/// Steps for TAOM's co-op features, starting with Supply Lines. A step calls what TAOM's own screen calls, so in Coop
/// it takes the path a player's click does: on the player's game ModderLords' TAOM layer sends the order to the server,
/// the server places it as that player, spawns the caravan and moves it towards the player every frame.
/// </summary>
public static class Steps
{
    private static readonly TimeSpan Sync = TimeSpan.FromSeconds(30);

    public static void Register()
    {
        InGameTests.Register("TAOM.health", StepSide.Any, Health);
        InGameTests.Register("taom.supplySources", StepSide.Client, SupplySources);
        InGameTests.Register("taom.supplyOrder", StepSide.Client, SupplyOrder);
        InGameTests.Register("taom.supplyCaravans", StepSide.Server, SupplyCaravans);
        InGameTests.Register("taom.watchCaravans", StepSide.Server, WatchCaravans);
        InGameTests.Register("taom.setTime", StepSide.Server, SetTime);
    }

    // ---- health --------------------------------------------------------------------------------------------

    /// <summary>TAOM's version and whether its Supply Lines services resolve, compared across sides by mods.health.</summary>
    private static IEnumerable<StepWait?> Health(StepContext ctx)
    {
        yield return ctx.Until(() => Campaign.Current != null, "the campaign");
        var version = typeof(IoC).Assembly.GetName().Version?.ToString() ?? "?";
        var orders = IoC.Resolve<ISupplyOrderService>();
        var sources = IoC.Resolve<ISupplySourceService>();
        ctx.Assert(orders != null && sources != null, "TAOM's Supply Lines services did not resolve (IoC.Resolve returned null)");
        ctx.Report("value", "TAOM " + version + ", supply lines ready");
    }

    // ---- the player's side ---------------------------------------------------------------------------------

    /// <summary>The supply sources TAOM's order screen lists for this player, nearest first.</summary>
    private static IEnumerable<StepWait?> SupplySources(StepContext ctx)
    {
        yield return ctx.Until(() => MobileParty.MainParty != null, "this player's party");
        var sources = IoC.Resolve<ISupplySourceService>().GetSources();
        ctx.Dump("sources", Describe(sources));
        ctx.Report("count", sources.Count);
        ctx.Report("orderable", sources.Count(s => s.CanOrder));
    }

    /// <summary>
    /// Places a supply order as the order screen's Confirm does (SupplyOrderScreenVM.ExecuteConfirm: the quantities
    /// picked, then ISupplyOrderService.TryPlaceOrder). Args: <c>source</c> = <c>here</c> (the settlement the party is
    /// in), <c>village</c> (the nearest orderable village other than that), or a settlement id; <c>goods</c> = how many
    /// of the first good on offer (default 1). Waits for the server's answer on screen.
    /// </summary>
    private static IEnumerable<StepWait?> SupplyOrder(StepContext ctx)
    {
        yield return ctx.Until(() => MobileParty.MainParty != null, "this player's party");
        var here = MobileParty.MainParty.CurrentSettlement?.StringId;
        var sourceService = IoC.Resolve<ISupplySourceService>();
        var sources = sourceService.GetSources();
        ctx.Dump("sources", Describe(sources));

        var wanted = ctx.Arg("source", "here");
        SupplySourceInfo? source = wanted switch
        {
            "here" => sources.FirstOrDefault(s => s.SettlementId == here && string.IsNullOrEmpty(s.HeroId)),
            "village" => sources
                .Where(s => s.CanOrder && string.IsNullOrEmpty(s.HeroId) && s.SettlementId != here && Settlement.Find(s.SettlementId)?.IsVillage == true)
                .OrderBy(s => s.Distance).FirstOrDefault(),
            _ => sources.FirstOrDefault(s => s.SettlementId == wanted && string.IsNullOrEmpty(s.HeroId)),
        };
        ctx.Assert(source != null, $"no supply source '{wanted}' among {sources.Count} listed (the party is in {here ?? "no settlement"})");
        ctx.Assert(source!.CanOrder, $"{source.SettlementId} cannot take an order: {source.DisabledReason}");

        var goods = new Dictionary<string, int>(StringComparer.Ordinal);
        var good = sourceService.GetGoods(source).FirstOrDefault(g => g.Available > 0);
        ctx.Assert(good != null, $"{source.SettlementId} offers no goods");
        goods[good!.Id] = Math.Min(good.Available, ctx.ArgInt("goods", 1));
        ctx.Log($"ordering {goods[good.Id]} {good.Id} from {source.SettlementId} ({source.Distance:0.0} away), party in {here ?? "no settlement"}");

        var answers = new List<string>();
        void OnMessage(InformationMessage m)
        {
            var text = m.Information ?? "";
            if (text.IndexOf("supply", StringComparison.OrdinalIgnoreCase) >= 0 || text.IndexOf("caravan", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("order", StringComparison.OrdinalIgnoreCase) >= 0)
                answers.Add(text);
        }
        InformationManager.DisplayMessageInternal += OnMessage;
        try
        {
            var placed = IoC.Resolve<ISupplyOrderService>().TryPlaceOrder(source, goods, new Dictionary<string, int>(), SupplyEscortOption.None, out var failReason, false);
            ctx.Assert(placed != null, $"TAOM refused the order on this game: {failReason}");
            yield return ctx.Until(() => answers.Count > 0, "the server's answer to the supply order", Sync);
        }
        finally { InformationManager.DisplayMessageInternal -= OnMessage; }
        ctx.Log("answer: " + string.Join(" | ", answers));
        ctx.Assert(answers.Any(a => a.IndexOf("set out", StringComparison.OrdinalIgnoreCase) >= 0 || a.IndexOf("on the way", StringComparison.OrdinalIgnoreCase) >= 0),
            $"the server did not dispatch the caravan: {string.Join(" | ", answers)}");
        ctx.Report("source", source.SettlementId);
        ctx.Report("value", "dispatched");
    }

    /// <summary>
    /// Sets campaign time on the server through Coop's own console command, as the smoke test does: <c>mode</c> =
    /// <c>play</c>, <c>fast</c> or <c>stop</c>. (Setting the mode on the player's game does not reach the server: run 2
    /// of this suite left time stopped.)
    /// </summary>
    private static IEnumerable<StepWait?> SetTime(StepContext ctx)
    {
        yield return ctx.Until(() => Campaign.Current != null, "the campaign");
        var (coopMode, expected) = ctx.Arg("mode") switch
        {
            "play" => ("Play_1x", CampaignTimeControlMode.StoppablePlay),
            "fast" => ("Play_2x", CampaignTimeControlMode.StoppableFastForward),
            "stop" => ("Pause", CampaignTimeControlMode.Stop),
            var other => throw new StepFailedException($"mode '{other}' is not play, fast or stop"),
        };
        const string command = "coop.debug.set_time_mode";
        ctx.Assert(CommandLineFunctionality.HasFunctionForCommand(command), $"the console command {command} is not registered in this game");
        var output = CommandLineFunctionality.CallFunction(command, new List<string> { coopMode }, out var found);
        ctx.Log($"{command} {coopMode} -> {output}");
        ctx.Assert(found, $"the console command {command} was not found when called");
        bool Applied() => expected == CampaignTimeControlMode.Stop
            ? Campaign.Current.TimeControlMode == CampaignTimeControlMode.Stop
            : Campaign.Current.TimeControlMode != CampaignTimeControlMode.Stop;
        yield return ctx.Until(Applied, $"campaign time mode {expected}", TimeSpan.FromSeconds(10));
        ctx.Report("value", Campaign.Current.TimeControlMode.ToString());
    }

    // ---- the server's side ---------------------------------------------------------------------------------

    /// <summary>
    /// The server's whole order book (every player's), waiting until at least <c>atLeast</c> caravans are on the road.
    /// Reports each one's order, source, owner clan and position.
    /// </summary>
    private static IEnumerable<StepWait?> SupplyCaravans(StepContext ctx)
    {
        var atLeast = ctx.ArgInt("atLeast", 1);
        yield return ctx.Until(() => InTransit().Count >= atLeast, $"{atLeast} supply caravan(s) on the road on the server", Sync);
        var caravans = InTransit();
        ctx.Dump("caravans", string.Join("\n", caravans.Select(c => DescribeCaravan(c.Order, c.Party))));
        ctx.Report("value", caravans.Count);
    }

    /// <summary>
    /// Lets campaign time run for <c>seconds</c> (real time, default 60) while sampling every supply caravan's position
    /// each second, as TAOM's per-frame tick moves them. Fails if campaign time did not advance (the caravans were never
    /// asked to move), and reports how far each one travelled. A server that dies here is the crash this module looks
    /// for; the run reports the server's exit.
    /// </summary>
    private static IEnumerable<StepWait?> WatchCaravans(StepContext ctx)
    {
        var seconds = ctx.ArgInt("seconds", 60);
        var start = CampaignTime.Now;
        var startedAt = DateTime.UtcNow;
        var lastSample = DateTime.MinValue;
        var first = new Dictionary<string, Vec2>();
        var last = new Dictionary<string, Vec2>();
        var log = new StringBuilder();
        bool Sample()
        {
            var now = DateTime.UtcNow;
            if (now - lastSample >= TimeSpan.FromSeconds(1))
            {
                lastSample = now;
                foreach (var (order, party) in InTransit())
                {
                    if (party == null) continue;
                    var p = party.GetPosition2D;
                    if (!first.ContainsKey(order.OrderId)) first[order.OrderId] = p;
                    last[order.OrderId] = p;
                    log.AppendLine($"{(now - startedAt).TotalSeconds:0}s {CampaignTime.Now} {order.OrderId} {party.StringId} at {p.x:0.00},{p.y:0.00} {order.StatusEnum}");
                }
            }
            return now - startedAt >= TimeSpan.FromSeconds(seconds);
        }
        yield return ctx.Until(Sample, $"{seconds} s of campaign time with the caravans moving", TimeSpan.FromSeconds(seconds + 30));
        ctx.Dump("caravan-positions", log.ToString());
        var hours = (CampaignTime.Now - start).ToHours;
        ctx.Report("hours", hours.ToString("0.00", CultureInfo.InvariantCulture));
        foreach (var id in first.Keys)
            ctx.Log($"{id}: moved {first[id].Distance(last[id]):0.00} from {first[id].x:0.00},{first[id].y:0.00}");
        ctx.Assert(hours > 0, $"campaign time did not advance in {seconds} s (time mode {Campaign.Current.TimeControlMode}), so the caravans were not moved");
        ctx.Report("value", first.Count);
    }

    // ---- helpers -------------------------------------------------------------------------------------------

    private static List<(SupplyOrder Order, MobileParty? Party)> InTransit()
    {
        IoC.Resolve<ISupplyOrderService>().SaveInto(out var orders, out _);
        return orders.Values
            .Where(o => o.StatusEnum == SupplyOrderStatus.InTransit)
            .Select(o => (o, (MobileParty?)MobileParty.All.FirstOrDefault(p => p.StringId == o.CaravanPartyId)))
            .ToList();
    }

    private static string DescribeCaravan(SupplyOrder order, MobileParty? party)
    {
        var where = party == null ? "no party" : $"{party.StringId} clan {party.ActualClan?.StringId ?? "none"} at {party.GetPosition2D.x:0.00},{party.GetPosition2D.y:0.00}";
        return $"{order.OrderId} from {(string.IsNullOrEmpty(order.SourceHeroId) ? order.SourceSettlementId : "lord " + order.SourceHeroId)}: {where}, planned {order.PlannedHours:0.0} h";
    }

    private static string Describe(IReadOnlyList<SupplySourceInfo> sources) =>
        string.Join("\n", sources.OrderBy(s => s.Distance).Select(s =>
            $"{s.SettlementId}{(string.IsNullOrEmpty(s.HeroId) ? "" : " lord " + s.HeroId)} \"{s.DisplayName}\" {s.Distance:0.0} {(s.CanOrder ? "can order" : "cannot: " + s.DisabledReason)}"));
}
