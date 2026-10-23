using System;
using System.Collections.Generic;
using System.Linq;
using TaleWorlds.CampaignSystem.Election;

namespace ModderLords.CompatSync.Coop;

/// <summary>
/// Where a mod's own Coop compat plugin (Bellum Civile, Living Economy, Fourberie: each its own assembly, shipped in
/// ModderLords.Compat's bin folder and as a standalone module) hooks into the shared layer. A plugin's
/// <c>CoopCompatPlugin.Register()</c> runs once, right after this assembly loads and before any session starts.
/// </summary>
internal static class CompatPlugins
{
    /// <summary>Periodic steps, run after the shared ones on every <see cref="Bridge.Tick"/>.</summary>
    internal static readonly List<(string Name, Action Step)> Ticks = new List<(string, Action)>();

    /// <summary>Each plugin's part of the verification line; empty when it has nothing to say.</summary>
    internal static readonly List<Func<string>> Summaries = new List<Func<string>>();

    /// <summary>State that travels in the TAOM layer's state message, told apart by its behaviour-name prefix.</summary>
    internal static readonly List<StateFeed> StateFeeds = new List<StateFeed>();

    /// <summary>Client: the joined campaign is ready / the session ended.</summary>
    internal static readonly List<Action> ClientCampaignReady = new List<Action>();
    internal static readonly List<Action> ClientSessionEnded = new List<Action>();

    /// <summary>
    /// Server, a kingdom vote's deadline with clans still undecided: true when a plugin settled the vote itself (Bellum's
    /// council deciding a treaty the ruler did not answer). Called with the decision and whether the ruler voted.
    /// </summary>
    internal static readonly List<Func<KingdomDecision, bool, bool>> VoteDeadline = new List<Func<KingdomDecision, bool, bool>>();

    internal static readonly List<string> Loaded = new List<string>();

    internal static StateFeed? FeedFor(string behaviour) =>
        StateFeeds.FirstOrDefault(f => behaviour.StartsWith(f.Prefix, StringComparison.Ordinal));

    internal static void RunAll(List<Action> hooks, string what)
    {
        foreach (var hook in hooks)
        {
            try { hook(); }
            catch (Exception ex) { Log.Warn(what + " hook failed: " + ex.GetBaseException().Message); }
        }
    }

    internal static string Summary() => string.Concat(Summaries.Select(s =>
    {
        try { return s(); }
        catch (Exception ex) { return "; summary failed: " + ex.GetBaseException().Message; }
    }));
}

/// <summary>One plugin's mirrored state, carried by the TAOM layer's NetworkTaomState under its own name prefix.</summary>
internal sealed class StateFeed
{
    public StateFeed(string name, string prefix) { Name = name; Prefix = prefix; }

    public string Name { get; }
    public string Prefix { get; }

    /// <summary>Client, game thread: one message for this feed.</summary>
    public Action<string, string> ClientApply { get; set; } = (_, _) => { };

    /// <summary>Server: a session starts.</summary>
    public Action Reset { get; set; } = () => { };

    /// <summary>Server, game thread: everything a joining client needs, as (behaviour, payload) messages.</summary>
    public Func<List<(string Behaviour, string Payload)>> CaptureForJoin { get; set; } = () => new List<(string, string)>();

    /// <summary>Server: how the feed sends a message to every client (null when no session).</summary>
    public Action<Action<string, string>?> SetBroadcast { get; set; } = _ => { };

    /// <summary>Client: how the feed asks the server for everything again (null when no session).</summary>
    public Action<Action?> SetRequestFull { get; set; } = _ => { };
}
