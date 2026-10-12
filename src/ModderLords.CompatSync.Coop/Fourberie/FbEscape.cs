using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.GameMenus;
using TaleWorlds.Localization;

namespace ModderLords.CompatSync.Coop.Fourberie;

/// <summary>
/// "Break free" in the captivity menu (FourbEscapeBehavior) is switched off on a player's game in co-op.
///
/// Fourberie frees the player with PlayerCaptivity.EndCaptivity, starts a new encounter with the town and opens the
/// prison-break mission inside it. Coop has one way to free a player's hero: the server takes the party out of the
/// dungeon and puts it on the map at a release position. So the two cannot agree while the attempt runs.
///
/// - Left alone, the player is free on their own game only and the server keeps the hero in the dungeon.
/// - Asking the server for the release as well (tried in 0.1.42) frees the party onto the map on the server while the
///   player's game is inside the town in the mission. Reported 2026-10-11: after Break free the surrender button and
///   crafting and smelting with that character stopped working for the rest of the session.
///
/// Carrying it properly needs the server to hold the release until the mission's outcome is known, which nothing here
/// does yet. Until then the option is shown greyed out with the reason, and its consequence is refused as well in case
/// something else runs it.
/// </summary>
internal static class FbEscape
{
    internal const string EscapeBehavior = "Fourberie.FourbEscapeBehavior";
    internal const string Condition = "breakFree_cond";
    internal const string Reason = "Not available in co-op yet: the server cannot follow a prison break.";

    private static long _greyed;

    /// <summary>After Fourberie's condition: the option stays visible, disabled, with the reason as its tooltip.</summary>
    internal static void ConditionPostfix(MenuCallbackArgs args, bool __result)
    {
        if (!__result || args == null || !FourberieLayer.IsCoopClient) return;
        args.IsEnabled = false;
        args.Tooltip = new TextObject("{=!}" + Reason);
        _greyed++;
    }

    internal static string Summary() => _greyed > 0 ? ", Break free shown as unavailable" : "";
}

internal sealed class FbEscapeComponent : IFbComponent
{
    private const string Owner = "ModderLords.Fourberie.Escape";
    private MethodInfo? _condition;
    private readonly List<MethodInfo> _breakFree = new List<MethodInfo>();

    public string Id => "escape";

    public string? SkipReason(FbContext context)
    {
        if (context.IsServer) return "server (Break free is a menu on a player's game)";
        if (context.Type(FbEscape.EscapeBehavior) is not { } behavior) return FbEscape.EscapeBehavior + " not found";
        _condition = context.Method(FbEscape.EscapeBehavior, FbEscape.Condition, 1);
        _breakFree.Clear();
        if (AccessTools.Method(typeof(PlayerCaptivity), nameof(PlayerCaptivity.EndCaptivity)) is { } endCaptivity)
            _breakFree.AddRange(FbGaps.Callers(behavior, endCaptivity));
        if (_condition == null && _breakFree.Count == 0) return "Break free was not found (Fourberie changed?); it is not switched off";
        return null;
    }

    public string Install(FbContext context)
    {
        var h = new Harmony(Owner);
        if (_condition != null) h.Patch(_condition, postfix: new HarmonyMethod(typeof(FbEscape), nameof(FbEscape.ConditionPostfix)));
        FbGaps.Bind(context.Mod);
        var block = new HarmonyMethod(typeof(FbGaps), nameof(FbGaps.BlockPrefix)) { priority = Priority.First };
        foreach (var m in _breakFree) h.Patch(m, prefix: block);
        return "Break free is not available in co-op: " + (_condition != null ? "shown greyed out with the reason" : "its option could not be greyed out")
            + $", {_breakFree.Count} consequence(s) refused";
    }
}
