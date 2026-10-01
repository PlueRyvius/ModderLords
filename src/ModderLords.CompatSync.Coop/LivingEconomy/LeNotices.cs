using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using GameInterface;
using GameInterface.Services.Players;
using HarmonyLib;
using ModderLords.CompatSync;
using ModderLords.CompatSync.Coop.Taom;
using TaleWorlds.Library;

namespace ModderLords.CompatSync.Coop.LivingEconomy;

/// <summary>
/// Server: the lines Living Economy shows "the player" reach the right player (docs/LIVING-ECONOMY-LAYER.md, part 3).
///
/// On a dedicated server nobody sees InformationManager.DisplayMessage. Two cases matter:
/// - while the server carries out a player's click (<see cref="LeActionsComponent"/>), every line the mod shows is
///   collected and sent back with the answer, so the player reads exactly what the mod would have told them alone;
/// - while a player-owned settlement's tick runs as that player (<see cref="LeOwnerScopeComponent"/>), the mod's
///   notifications about it (treasury payouts, caravan deliveries, projects finishing) are forwarded to that player.
/// When the shared forwarder (TaomNotices.NoticeComponent, installed by the TAOM or Fourberie layer) is on, it already
/// does the second job, so this one stands aside there.
/// </summary>
internal sealed class LeNoticeComponent : ILeComponent
{
    private const string Owner = "ModderLords.LivingEconomy.Notices";

    [ThreadStatic] private static List<string>? _capture;
    [ThreadStatic] private static bool _forwarding;

    private MethodInfo? _display;

    public string Id => "notices";

    public string? SkipReason(LeContext context)
    {
        if (!context.IsServer) return "client";
        _display = typeof(InformationManager).GetMethod("DisplayMessage", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(InformationMessage) }, null);
        return _display == null ? "InformationManager.DisplayMessage(InformationMessage) not found" : null;
    }

    public string Install(LeContext context)
    {
        new Harmony(Owner).Patch(_display!, prefix: new HarmonyMethod(typeof(LeNoticeComponent), nameof(DisplayPrefix)) { priority = Priority.First });
        return "lines Living Economy shows a player reach that player";
    }

    /// <summary>Starts collecting this thread's lines for one relayed action.</summary>
    internal static void BeginCapture() => _capture = new List<string>();

    /// <summary>Stops collecting and returns what was shown, as "colour|text" lines.</summary>
    internal static List<string> EndCapture()
    {
        var lines = _capture ?? new List<string>();
        _capture = null;
        return lines;
    }

    // Bound by position (__0), not by the engine's parameter name.
    private static void DisplayPrefix(InformationMessage __0)
    {
        var message = __0;
        if (message == null || string.IsNullOrEmpty(message.Information)) return;
        if (_capture != null)
        {
            _capture.Add(LeActionCodec.Line(message.Color.ToUnsignedInteger(), message.Information));
            return;
        }
        if (_forwarding || TaomActions.Running || NoticeComponent.Installed) return;
        if (ServerRelay.PlayerScope.CurrentHero is not { } hero || NoticeComponent.Send == null) return;
        _forwarding = true;
        try
        {
            if (!ContainerProvider.TryResolve<IPlayerManager>(out var players)) return;
            var player = PlayerHeroes.PlayerFor(players, hero);
            if (player != null) NoticeComponent.Send(player.ControllerId, message.Information, message.Color.ToUnsignedInteger(), false);
        }
        catch (Exception ex) { Log.Warn(LivingEconomyLayer.Tag + "could not forward a message to a player: " + ex.GetBaseException().Message); }
        finally { _forwarding = false; }
    }
}
