using System;
using System.Collections.Generic;
using System.Reflection;
using ModderLords.CompatSync;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Library;

namespace ModderLords.CompatSync.Coop.Taom;

/// <summary>The server's answer to one relayed TAOM action: whether it happened, and the line to show the player.</summary>
internal readonly struct TaomActionOutcome
{
    public TaomActionOutcome(bool ok, string message)
    {
        Ok = ok;
        Message = message;
    }

    public bool Ok { get; }
    public string Message { get; }

    public static TaomActionOutcome Fail(string message) => new TaomActionOutcome(false, message);
}

/// <summary>
/// The shared channel for TAOM actions a client cannot perform itself (TAOM declines them on non-host peers because it
/// cannot talk to the server). A component registers a server handler per feature; the client side sends
/// (feature, op, args) and shows the server's answer. Every handler runs on the server's game thread with the
/// sender's hero and party standing in as the main ones (ServerRelay.PlayerScope).
/// </summary>
internal static class TaomActions
{
    /// <summary>Server handler: (player hero, player party, op, args) -> outcome. Runs inside PlayerScope.</summary>
    internal delegate TaomActionOutcome Handler(Hero hero, MobileParty? party, string op, IList<string> args);

    private static readonly Dictionary<string, Handler> Handlers = new Dictionary<string, Handler>(StringComparer.Ordinal);

    /// <summary>Set by the client handler while a session is live. Null outside a co-op client session.</summary>
    internal static Action<string, string, IList<string>>? Send { get; set; }

    /// <summary>True on a client in a live co-op session: TAOM's host-only actions should be sent, not refused.</summary>
    internal static bool IsCoopClient => Send != null;

    internal static void Register(string feature, Handler handler) => Handlers[feature] = handler;

    /// <summary>Server, game thread.</summary>
    internal static TaomActionOutcome Run(Hero hero, MobileParty? party, string feature, string op, IList<string> args)
    {
        if (!Handlers.TryGetValue(feature, out var handler))
            return TaomActionOutcome.Fail($"This server has no TAOM handler for '{feature}'; update ModderLords on the host.");
        using (new ServerRelay.PlayerScope(hero, party))
            return handler(hero, party, op, args);
    }

    /// <summary>Client: shows the server's answer the way TAOM shows its own messages.</summary>
    internal static void ShowToPlayer(bool ok, string message)
    {
        if (string.IsNullOrEmpty(message)) return;
        try { InformationManager.DisplayMessage(new InformationMessage(message, ok ? Colors.Green : Colors.Red)); }
        catch (Exception ex) { Log.Warn("TAOM layer: could not show a server answer: " + ex.GetBaseException().Message); }
    }

    /// <summary>Resolves a TAOM service from TAOM's own container (TAOM.IoC.Resolve&lt;T&gt;).</summary>
    internal static object? Resolve(Assembly taom, string interfaceName)
    {
        var type = taom.GetType(interfaceName, false);
        var resolve = taom.GetType("TAOM.IoC", false)?.GetMethod("Resolve", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
        return type == null || resolve == null ? null : resolve.MakeGenericMethod(type).Invoke(null, null);
    }
}
