using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Common.Messaging;
using GameInterface.Services.MobileParties.Messages.Behavior;
using HarmonyLib;
using TaleWorlds.CampaignSystem.Encounters;
using TaleWorlds.CampaignSystem.Party;

namespace ModderLords.CompatSync.Coop.Fourberie;

/// <summary>
/// Fourberie's own Leave options, on a player's game in co-op.
///
/// Leaving a settlement is the server's to do in co-op. Coop's Leave buttons in vanilla's menus (town, castle,
/// hideout, ...) skip the game's code and ask the server with EndSettlementEncounterAttempted; on a player's game the
/// game's own LeaveSettlementAction is refused. Fourberie's menus have their own Leave options (29 of them: the
/// hideout's "Leave the hideout", the safe house, robberies, extortion, sabotage, ...) that call
/// PlayerEncounter.LeaveSettlement and Finish themselves. On a player's game that cleared the encounter locally while
/// the server kept the party inside, so the hideout's menu came straight back and Leave did nothing (reported
/// 2026-10-19: entering a hideout and pressing Leave, over and over).
///
/// So while one of those Fourberie methods runs on a player's game, LeaveSettlement asks the server the way Coop's own
/// Leave does, and the Finish that follows it is skipped: the server's answer ends the encounter.
/// </summary>
internal static class FbLeave
{
    [ThreadStatic] private static int _depth;
    [ThreadStatic] private static bool _asked;
    private static long _sent;

    /// <summary>Prefix/finalizer on every Fourberie method that calls PlayerEncounter.LeaveSettlement.</summary>
    internal static void CallerPrefix() => _depth++;

    internal static void CallerFinalizer()
    {
        if (_depth > 0) _depth--;
        if (_depth == 0) _asked = false;
    }

    /// <summary>Prefix on PlayerEncounter.LeaveSettlement: inside a Fourberie Leave on a player's game, ask the server instead.</summary>
    internal static bool LeaveSettlementPrefix()
    {
        if (_depth == 0 || !FourberieLayer.IsCoopClient || MobileParty.MainParty is not { } party) return true;
        MessageBroker.Instance.Publish(party, new EndSettlementEncounterAttempted(party));
        _asked = true;
        _sent++;
        return false;
    }

    /// <summary>Prefix on PlayerEncounter.Finish: the one after a leave the server was asked for is the server's to make.</summary>
    internal static bool FinishPrefix() => !(_asked && _depth > 0);

    internal static string Summary() => _sent > 0 ? $", Fourberie leaves sent to the server {_sent}" : "";
}

internal sealed class FbLeaveComponent : IFbComponent
{
    private const string Owner = "ModderLords.Fourberie.Leave";
    private readonly List<MethodInfo> _callers = new List<MethodInfo>();
    private MethodInfo? _leave, _finish;

    public string Id => "leave";

    public string? SkipReason(FbContext context)
    {
        if (context.IsServer) return "server (Leave is a menu option on a player's game)";
        _leave = AccessTools.Method(typeof(PlayerEncounter), nameof(PlayerEncounter.LeaveSettlement));
        _finish = AccessTools.Method(typeof(PlayerEncounter), nameof(PlayerEncounter.Finish));
        if (_leave == null || _finish == null) return "PlayerEncounter.LeaveSettlement or Finish not found";
        _callers.Clear();
        foreach (var type in SafeTypes(context.Mod).Where(t => !t.IsNested))
            _callers.AddRange(FbGaps.Callers(type, _leave));
        return _callers.Count == 0 ? "no Fourberie method leaves a settlement itself (Fourberie changed?)" : null;
    }

    public string Install(FbContext context)
    {
        var h = new Harmony(Owner);
        foreach (var m in _callers)
            h.Patch(m, prefix: new HarmonyMethod(typeof(FbLeave), nameof(FbLeave.CallerPrefix)),
                finalizer: new HarmonyMethod(typeof(FbLeave), nameof(FbLeave.CallerFinalizer)));
        h.Patch(_leave, prefix: new HarmonyMethod(typeof(FbLeave), nameof(FbLeave.LeaveSettlementPrefix)) { priority = Priority.First });
        h.Patch(_finish, prefix: new HarmonyMethod(typeof(FbLeave), nameof(FbLeave.FinishPrefix)) { priority = Priority.First });
        return $"{_callers.Count} Fourberie Leave option(s) ask the server to leave, as Coop's own Leave does";
    }

    private static IEnumerable<Type> SafeTypes(Assembly mod)
    {
        try { return mod.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t != null)!; }
    }
}
