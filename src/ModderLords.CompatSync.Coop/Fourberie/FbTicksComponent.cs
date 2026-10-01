using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using ModderLords.CompatSync;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;

namespace ModderLords.CompatSync.Coop.Fourberie;

/// <summary>The server's players and books, as FbTicks sees them.</summary>
internal sealed class FbBooksFanOutHost : IFbFanOutHost
{
    public IReadOnlyList<FbPlayerRun> Players(FbFields.Reach reach) =>
        (reach == FbFields.Reach.Everyone ? FbBooks.Everyone() : FbBooks.Connected())
            .Select(p => new FbPlayerRun(p.Hero.StringId, p.Hero.Name?.ToString() ?? p.Hero.StringId, () => Enter(p.Hero, p.Party)))
            .ToList();

    private static System.IDisposable? Enter(Hero hero, MobileParty? party) => FbBooks.Enter(hero, party);

    public bool IsSuspended(string key) => FbBooks.IsSuspended(key);

    public void Warn(string line) => Log.Warn(FourberieLayer.Tag + line);
}

internal sealed class FbTicksComponent : IFbComponent
{
    private const string Owner = "ModderLords.Fourberie.Ticks";
    private readonly List<(MethodInfo, FbFields.Reach)> _found = new List<(MethodInfo, FbFields.Reach)>();

    public string Id => "ticks";

    public string? SkipReason(FbContext context)
    {
        if (!context.IsServer) return "client (Coop never ticks a player's game)";
        if (context.Fields == null) return "no books: " + context.FieldsProblem;
        _found.Clear();
        var missing = new List<string>();
        foreach (var (type, method, count, reach) in FbFields.Handlers)
        {
            var m = context.Method(type, method, count);
            if (m == null) missing.Add(type.Substring(type.LastIndexOf('.') + 1) + "." + method);
            else _found.Add((m, reach));
        }
        if (_found.Count == 0) return "Fourberie changed; none of its handlers were found";
        if (missing.Count > 0) Log.Warn(FourberieLayer.Tag + "ticks: not found (Fourberie changed?), these run once for nobody: " + FourberieLayer.Some(missing));
        return null;
    }

    public string Install(FbContext context)
    {
        FbTicks.Bind(_found);
        FbTicks.Host = new FbBooksFanOutHost();
        var h = new Harmony(Owner);
        var prefix = new HarmonyMethod(typeof(FbTicks), nameof(FbTicks.Prefix)) { priority = Priority.First };
        foreach (var (m, _) in _found) h.Patch(m, prefix: prefix);
        return $"{_found.Count} handler(s) run once per player ({_found.Count(f => f.Item2 == FbFields.Reach.Everyone)} reach offline players' books too)";
    }
}
