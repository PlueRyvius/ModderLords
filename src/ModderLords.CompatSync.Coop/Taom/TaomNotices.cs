using System;
using System.Linq;
using System.Reflection;
using GameInterface;
using GameInterface.Services.Players;
using HarmonyLib;
using ModderLords.CompatSync;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.Localization;

namespace ModderLords.CompatSync.Coop.Taom;

/// <summary>
/// What TAOM tells "the player" while the server acts for one (TAOM-MAP checklist). Everything this layer runs for a
/// player runs under PlayerScope on the dedicated server: founding a refuge, a caravan arriving or being lost, a
/// refuge raised, desertion. TAOM reports those with InformationManager / MBInformationManager, which on a server go
/// nowhere. Server: a message shown inside a PlayerScope is also sent to that player's client, which shows it the same
/// way. Messages outside any scope (the server's own idle hero) are not sent.
/// </summary>
internal sealed class NoticeComponent : ITaomComponent
{
    /// <summary>Server: set by the server handler; sends (controller id, text, colour, quick) to that player.</summary>
    internal static Action<string, string, uint, bool>? Send { get; set; }

    private static MethodInfo? _display;
    private static MethodInfo? _quick;

    public string Id => "notices";

    public string? SkipReason(TaomContext context)
    {
        if (!context.IsServer) return "client";
        _display = typeof(InformationManager).GetMethod("DisplayMessage", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(InformationMessage) }, null);
        _quick = typeof(MBInformationManager).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(m => m.Name == "AddQuickInformation" && m.GetParameters().FirstOrDefault()?.ParameterType == typeof(TextObject));
        return _display == null || _quick == null ? "InformationManager.DisplayMessage / MBInformationManager.AddQuickInformation not found" : null;
    }

    public string Install(TaomContext context)
    {
        var h = new Harmony("ModderLords.Taom.Notices");
        h.Patch(_display!, prefix: new HarmonyMethod(typeof(NoticeComponent), nameof(DisplayPrefix)));
        h.Patch(_quick!, prefix: new HarmonyMethod(typeof(NoticeComponent), nameof(QuickPrefix)));
        return "server forwards messages shown for a player to that player";
    }

    [ThreadStatic] private static bool _sending;

    private static void DisplayPrefix(InformationMessage message) =>
        Forward(message?.Information, message == null ? 0u : message.Color.ToUnsignedInteger(), false);

    private static void QuickPrefix(TextObject message) => Forward(message?.ToString(), 0u, true);

    private static void Forward(string? text, uint color, bool quick)
    {
        if (_sending || TaomActions.Running || Send == null || string.IsNullOrEmpty(text) || ServerRelay.PlayerScope.CurrentHero is not { } hero) return;
        _sending = true;
        try
        {
            if (!ContainerProvider.TryResolve<IPlayerManager>(out var players)) return;
            var player = players.Players.FirstOrDefault(p => p.HeroId == hero.StringId);
            if (player != null) Send(player.ControllerId, text!, color, quick);
        }
        catch (Exception ex) { Log.Warn("TAOM layer: could not forward a message to a player: " + ex.GetBaseException().Message); }
        finally { _sending = false; }
    }

    /// <summary>Client, game thread.</summary>
    internal static void Show(string text, uint color, bool quick)
    {
        try
        {
            if (quick) MBInformationManager.AddQuickInformation(new TextObject("{=!}" + text), 0, null, null, "");
            else InformationManager.DisplayMessage(color == 0 ? new InformationMessage(text) : new InformationMessage(text, Color.FromUint(color)));
        }
        catch (Exception ex) { Log.Warn("TAOM layer: could not show a server message: " + ex.GetBaseException().Message); }
    }
}
