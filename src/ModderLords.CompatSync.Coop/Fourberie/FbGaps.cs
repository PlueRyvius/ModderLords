using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using ModderLords.CompatSync;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Party.PartyComponents;
using TaleWorlds.Library;

namespace ModderLords.CompatSync.Coop.Fourberie;

/// <summary>
/// Fourberie actions that cannot work in co-op yet (tier T3, docs/FOURBERIE-LAYER-PLAN.md), and a watch for any party a
/// player's game still makes on its own.
///
/// Not available in co-op, each with an on-screen line instead of a half-working run:
/// - fights against a party Fourberie makes on the spot (the safe-house raids, a village's militia during extortion,
///   the grand caravan heist): the party only exists on that player's game, so Coop will not start the battle;
/// - sending bandit raiders against a castle (sabotage) and spinning a bandit party off your gang: they would be parties
///   only that player sees.
///
/// The watch: in a co-op session, a party made by Fourberie code on a player's game is logged with where it came from,
/// so a creation path nobody handled shows up in the logs instead of as a party nobody else can see.
/// </summary>
internal static class FbGaps
{
    internal const string NotInCoop = "Fourberie: not available in co-op yet.";

    internal static (string Type, string Method, int Params)[] Blocked => FbGapsTable.Blocked;

    private static Assembly? _mod;
    private static long _blocked, _phantoms;
    private static readonly HashSet<string> Seen = new HashSet<string>(StringComparer.Ordinal);
    private static DateTime _lastNotice = DateTime.MinValue;

    internal static void Bind(Assembly mod) => _mod = mod;

    internal static bool BlockPrefix(MethodBase __originalMethod)
    {
        if (!FourberieLayer.IsCoopClient) return true;
        Block(__originalMethod.Name);
        return false;
    }

    /// <summary>LadsOnDoneClicked: training saboteurs goes into the saboteur ledger (fine); anything else makes a bandit party.</summary>
    internal static bool LadsDonePrefix(ref bool __result)
    {
        if (!FourberieLayer.IsCoopClient) return true;
        var bandit = _mod?.GetType("Fourberie.FourbBanditBehavior", false);
        var partyTemp = bandit?.GetField("_partyTemp", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
        var mapAction = bandit?.GetField("_mapAction", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) as string;
        if (partyTemp == null || mapAction == "trainSaboteurs") return true;
        Block("LadsOnDoneClicked/" + mapAction);
        __result = false;
        return false;
    }

    private static void Block(string what)
    {
        _blocked++;
        Log.Info($"{FourberieLayer.Tag}gaps: {what} is not available in co-op; skipped");
        if ((DateTime.UtcNow - _lastNotice).TotalSeconds < 5) return;
        _lastNotice = DateTime.UtcNow;
        InformationManager.DisplayMessage(new InformationMessage(NotInCoop, Colors.Yellow));
    }

    /// <summary>Postfix on the party factories: on a player's game, a party Fourberie made is a phantom; say where it came from.</summary>
    internal static void WatchPostfix(MethodBase __originalMethod)
    {
        if (!FourberieLayer.IsCoopClient || _mod == null) return;
        var frame = new StackTrace(1, false).GetFrames()?.Select(f => f.GetMethod())
            .FirstOrDefault(m => m?.DeclaringType?.Assembly == _mod);
        if (frame == null) return;
        _phantoms++;
        var where = frame.DeclaringType?.Name + "." + frame.Name;
        if (Seen.Add(where)) Log.Warn($"{FourberieLayer.Tag}gaps: {where} made a party with {__originalMethod.Name} on this game; only this player will see it");
    }

    /// <summary>Methods in <paramref name="type"/> and its nested types whose own IL calls <paramref name="callee"/> (lambdas included).</summary>
    internal static List<MethodInfo> Callers(Type type, MethodBase callee)
    {
        var found = new List<MethodInfo>();
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        foreach (var t in new[] { type }.Concat(type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)))
            foreach (var m in t.GetMethods(all))
            {
                try
                {
                    if (m.GetMethodBody() == null) continue;
                    if (PatchProcessor.GetOriginalInstructions(m).Any(i => i.operand is MethodBase target && target == callee)) found.Add(m);
                }
                catch { }
            }
        return found;
    }

    internal static string Summary() => $"not-in-co-op skips {_blocked}" + (_phantoms > 0 ? $", phantom parties {_phantoms}" : "");
}

internal sealed class FbGapsComponent : IFbComponent
{
    private const string Owner = "ModderLords.Fourberie.Gaps";
    private readonly List<MethodInfo> _blocked = new List<MethodInfo>();
    private MethodInfo? _ladsDone;

    public string Id => "gaps";

    public string? SkipReason(FbContext context)
    {
        if (context.IsServer) return "server (only players' games make phantoms)";
        _blocked.Clear();
        foreach (var (type, name, count) in FbGaps.Blocked)
            if (context.Method(type, name, count) is { } m) _blocked.Add(m);
        _ladsDone = context.Method("Fourberie.FourbBanditBehavior", "LadsOnDoneClicked", 9);
        // Sabotage's "send raiders" is a lambda in HelperSubSabotage.Menu; find it by what it calls.
        var createBandit = AccessTools.Method(typeof(BanditPartyComponent), nameof(BanditPartyComponent.CreateBanditParty));
        if (context.Type("Fourberie.HelperSubSabotage") is { } sabotage && createBandit != null)
            _blocked.AddRange(FbGaps.Callers(sabotage, createBandit));
        return null;
    }

    public string Install(FbContext context)
    {
        FbGaps.Bind(context.Mod);
        var h = new Harmony(Owner);
        var block = new HarmonyMethod(typeof(FbGaps), nameof(FbGaps.BlockPrefix)) { priority = Priority.First };
        foreach (var m in _blocked) h.Patch(m, prefix: block);
        if (_ladsDone != null) h.Patch(_ladsDone, prefix: new HarmonyMethod(typeof(FbGaps), nameof(FbGaps.LadsDonePrefix)) { priority = Priority.First });

        var watch = new HarmonyMethod(typeof(FbGaps), nameof(FbGaps.WatchPostfix));
        var factories = new[]
        {
            AccessTools.Method(typeof(MobileParty), nameof(MobileParty.CreateParty)),
            AccessTools.Method(typeof(BanditPartyComponent), nameof(BanditPartyComponent.CreateBanditParty)),
            AccessTools.Method(typeof(BanditPartyComponent), nameof(BanditPartyComponent.CreateLooterParty)),
            AccessTools.Method(typeof(CaravanPartyComponent), nameof(CaravanPartyComponent.CreateCaravanParty)),
            AccessTools.Method(typeof(CustomPartyComponent), nameof(CustomPartyComponent.CreateCustomPartyWithTroopRoster)),
        }.Where(m => m != null).ToList();
        foreach (var m in factories) h.Patch(m, postfix: watch);
        return $"{_blocked.Count + (_ladsDone != null ? 1 : 0)} action(s) not available in co-op (on-screen line instead), {factories.Count} party factories watched";
    }
}
