using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using ModderLords.CompatSync;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;

namespace ModderLords.CompatSync.Coop.Taom;

/// <summary>
/// Special-resource balances in co-op: each player's own client owns their balance (TAOM-MAP P3/P4).
///
/// TAOM earns and spends a player's special resource on that player's machine: battle, raid, prisoner, tournament and
/// hideout earnings, the daily town income and troop upkeep, upgrade and recruit costs. A dedicated server credits
/// nobody (TAOM's own rule: its main hero is the idle world-generation hero), so the server never knew any player's
/// balance, did not save it, and could not charge it for anything done server-side (the emissary).
///
/// Chosen model (co-op among friends, so the client is trusted with its own numbers): the client keeps doing what TAOM
/// does and reports its own hero's balances to the server whenever they change; the server stores them, so they are in
/// the server's save and survive a reconnect. When the server itself changes a balance (an emissary purchase), its
/// answer carries the player's new balances and the client adopts them before its next report, so a stale report can
/// never undo a server-side charge.
/// </summary>
internal sealed class SpecialResourceSyncComponent : ITaomComponent
{
    public const string Feature = "specres";

    private static MethodInfo? _getAll;
    private static MethodInfo? _set;
    private static Assembly? _taom;
    private static string _lastReported = "";
    private static DateTime _nextReport = DateTime.MinValue;

    public string Id => "specres-sync";

    public string? SkipReason(TaomContext context)
    {
        _taom = context.Taom;
        var storage = context.Taom.GetType("TAOM.Features.SpecialResources.ISpecialResourceStorageService", false);
        _getAll = storage?.GetMethod("GetAllData", Type.EmptyTypes);
        _set = storage?.GetMethod("Set", new[] { typeof(string), typeof(string), typeof(float) });
        return _getAll?.ReturnType == typeof(Dictionary<string, float>) && _set != null
            ? null
            : "TAOM changed; not found: ISpecialResourceStorageService.GetAllData / Set(string, string, float)";
    }

    public string Install(TaomContext context)
    {
        if (context.IsServer)
        {
            TaomActions.Register(Feature, ServerReport);
            return "server stores each player's special-resource balances";
        }
        TaomActions.RegisterClientApply(Feature, ApplyBalances);
        TaomActions.RegisterClientApply(EmissaryComponent.Feature, ApplyBalances);
        return "client reports its special-resource balances to the server";
    }

    private static object? Storage() => _taom == null ? null : TaomActions.Resolve(_taom, "TAOM.Features.SpecialResources.ISpecialResourceStorageService");

    /// <summary>"heroId:resourceId" = amount, for one hero, as flat [key, value, key, value, ...].</summary>
    internal static List<string> BalancesFor(string heroId)
    {
        var result = new List<string>();
        if (Storage() is not { } storage || _getAll!.Invoke(storage, null) is not Dictionary<string, float> all) return result;
        var prefix = heroId + ":";
        foreach (var pair in all.Where(p => p.Key.StartsWith(prefix, StringComparison.Ordinal)).OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            result.Add(pair.Key);
            result.Add(pair.Value.ToString("R", CultureInfo.InvariantCulture));
        }
        return result;
    }

    /// <summary>Client, from Bridge.Tick every 3 s: reports this player's balances when they changed.</summary>
    internal static void ClientTick()
    {
        if (!TaomActions.IsCoopClient || _getAll == null || DateTime.UtcNow < _nextReport) return;
        _nextReport = DateTime.UtcNow.AddSeconds(3);
        var hero = Hero.MainHero;
        if (hero == null || Campaign.Current == null) return;
        var balances = BalancesFor(hero.StringId);
        var canonical = string.Join("|", balances);
        if (balances.Count == 0 || canonical == _lastReported) return;
        _lastReported = canonical;
        TaomActions.Send!(Feature, "report", balances);
    }

    /// <summary>Client, game thread: adopts balances the server set (after a server-side charge).</summary>
    private static void ApplyBalances(IList<string> data)
    {
        if (Storage() is not { } storage) return;
        for (var i = 0; i + 1 < data.Count; i += 2)
        {
            var split = data[i].IndexOf(':');
            if (split <= 0 || !float.TryParse(data[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out var amount)) continue;
            _set!.Invoke(storage, new object[] { data[i].Substring(0, split), data[i].Substring(split + 1), amount });
        }
        // What the server just set is what it already knows: not reported back.
        if (Hero.MainHero is { } hero) _lastReported = string.Join("|", BalancesFor(hero.StringId));
        Log.Info($"TAOM layer: special-resource balances updated from the server ({data.Count / 2})");
    }

    /// <summary>Server, inside PlayerScope: stores a player's reported balances. Only that player's own keys are accepted.</summary>
    private static TaomActionOutcome ServerReport(Hero hero, MobileParty? party, string op, IList<string> args)
    {
        if (op != "report" || Storage() is not { } storage) return TaomActionOutcome.Fail("");
        var prefix = hero.StringId + ":";
        var stored = 0;
        for (var i = 0; i + 1 < args.Count; i += 2)
        {
            var key = args[i];
            if (!key.StartsWith(prefix, StringComparison.Ordinal) || key.Length == prefix.Length) continue;
            if (!float.TryParse(args[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out var amount) || float.IsNaN(amount) || float.IsInfinity(amount)) continue;
            _set!.Invoke(storage, new object[] { hero.StringId, key.Substring(prefix.Length), amount });
            stored++;
        }
        return new TaomActionOutcome(true, "");
    }
}
