using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using TaleWorlds.Library;

namespace BetterEconomyCoopCompat;

/// <summary>
/// Server: the lines Living Economy shows "the player" reach the right player.
///
/// On a dedicated server nobody sees InformationManager.DisplayMessage. Two cases matter:
/// - while the server carries out a player's click (<see cref="Actions"/>), every line the mod shows is
///   collected and sent back with the answer, so the player reads exactly what the mod would have told them alone;
/// - while a player-owned settlement's tick runs as that player (<see cref="OwnerScope"/>), the mod's
///   notifications about it (treasury payouts, caravan deliveries, projects finishing) are forwarded to that player.
/// </summary>
internal sealed class Notices : IComponent
{
    private const string Owner = "modderlords.bettereconomy.coopcompat.Notices";

    [ThreadStatic] private static List<string>? _capture;
    [ThreadStatic] private static bool _forwarding;

    private MethodInfo? _display;

    public string Id => "notices";

    public string? SkipReason(Context context)
    {
        if (!context.IsServer) return "client";
        _display = typeof(InformationManager).GetMethod("DisplayMessage", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(InformationMessage) }, null);
        return _display == null ? "InformationManager.DisplayMessage(InformationMessage) not found" : null;
    }

    public string Install(Context context)
    {
        new Harmony(Owner).Patch(_display!, prefix: new HarmonyMethod(typeof(Notices), nameof(DisplayPrefix)) { priority = Priority.First });
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

    /// <summary>Client: shows a line from the server in its own colour.</summary>
    internal static void Show(string text, uint color)
    {
        try { InformationManager.DisplayMessage(color == 0 ? new InformationMessage(text) : new InformationMessage(text, Color.FromUint(color))); }
        catch (Exception ex) { Log.Warn("could not show a server message: " + ex.GetBaseException().Message); }
    }

    // Bound by position (__0), not by the engine's parameter name.
    private static void DisplayPrefix(InformationMessage __0)
    {
        var message = __0;
        if (message == null || string.IsNullOrEmpty(message.Information)) return;
        if (_capture != null)
        {
            _capture.Add(ActionCodec.Line(message.Color.ToUnsignedInteger(), message.Information));
            return;
        }
        if (_forwarding || Actions.Running) return;
        if (PlayerScope.CurrentHero is not { } hero || Net.SendNotice is not { } send) return;
        _forwarding = true;
        try
        {
            send(hero, message.Information, message.Color.ToUnsignedInteger());
        }
        catch (Exception ex) { Log.Warn("could not forward a message to a player: " + ex.GetBaseException().Message); }
        finally { _forwarding = false; }
    }
}
