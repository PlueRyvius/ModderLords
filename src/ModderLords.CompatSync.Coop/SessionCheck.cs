using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using ModderLords.CompatSync;
using ModderLords.CompatSync.Coop.Taom;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;

namespace ModderLords.CompatSync.Coop;

/// <summary>
/// Both sides, once per joined session: the player's game pings the server over the shared action channel (the one
/// every ModderLords layer sends player actions on) and logs the round trip, and the server logs that it answered.
/// <para>
/// The smoke test (ModderLords.Core.Smoke) reads these lines to tell "joined and on the map" from "joined, and our own
/// channel works both ways". They cost one message per session and say the same thing in any support log.
/// </para>
/// </summary>
internal static class SessionCheck
{
    internal const string Feature = "session";
    // The smoke test matches these prefixes; keep them stable (ModderLords.Core.Smoke.SmokeSignals).
    internal const string ReadyLine = "session check: campaign ready on this player's game";
    internal const string AnsweredLine = "session check: server answered the ping in ";
    internal const string ServerLine = "session check: answered the ping of ";

    private static bool _registered;
    private static bool _due;

    /// <summary>Both sides, every tick: registers the server handler and the client's apply hook once.</summary>
    internal static void EnsureRegistered()
    {
        if (_registered) return;
        _registered = true;
        TaomActions.Register(Feature, Answer);
        TaomActions.RegisterClientApply(Feature, Received);
    }

    /// <summary>Client: the joined campaign is ready; the ping goes on the next tick, once the channel is bound.</summary>
    internal static void CampaignReady()
    {
        _due = true;
        Log.Info(ReadyLine);
    }

    /// <summary>Client, every tick: sends the session's one ping.</summary>
    internal static void ClientTick()
    {
        if (!_due || TaomActions.Send is not { } send) return;
        _due = false;
        send(Feature, "ping", new[] { Stopwatch.GetTimestamp().ToString(CultureInfo.InvariantCulture) });
    }

    private static TaomActionOutcome Answer(Hero hero, MobileParty? party, string op, IList<string> args)
    {
        if (op != "ping") return TaomActionOutcome.Fail("This server does not know the session operation '" + op + "'.");
        Log.Info(ServerLine + hero.Name);
        // An empty message: the client shows nothing, its apply hook gets the data back.
        return new TaomActionOutcome(true, "", new List<string> { args.Count > 0 ? args[0] : "", hero.Name?.ToString() ?? "" });
    }

    private static void Received(IList<string> data)
    {
        if (!long.TryParse(data[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var sent)) return;
        var ms = (Stopwatch.GetTimestamp() - sent) * 1000 / Stopwatch.Frequency;
        Log.Info($"{AnsweredLine}{ms} ms (as {(data.Count > 1 ? data[1] : "?")})");
    }
}
