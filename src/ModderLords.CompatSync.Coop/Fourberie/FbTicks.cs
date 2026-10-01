using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace ModderLords.CompatSync.Coop.Fourberie;

/// <summary>
/// fourb-ticks (docs/FOURBERIE-LAYER-PLAN.md). Coop never ticks a player's game, so every one of Fourberie's timers,
/// incomes and schemes runs on the server, where "the player" is an idle stand-in. Each handler below runs instead once
/// per player, as that player, with that player's book.
///
/// Periodic ticks and the player's own movements/actions run for connected players only: an offline player's empire
/// freezes (maintainer decision). World events (deaths, wars, clans and kingdoms ending) reach every book, so nobody
/// comes back to schemes against a hero who died while they were away.
///
/// Every handler here writes only the player's book, or world state behind a "this is the player's" check, so running
/// it per player is what single player would do for each of them. Reviewed against Fourberie 1.4.8.2.
/// </summary>
internal static class FbTicks
{
    private static readonly Dictionary<MethodBase, FbFields.Reach> Reaches = new Dictionary<MethodBase, FbFields.Reach>();
    /// <summary>The handler this prefix is invoking itself right now; that one call is let through, and only that one.</summary>
    [ThreadStatic] private static MethodBase? _invoking;
    private static long _runs, _placeholderSkips, _errors, _suspendedSkips;
    private static readonly HashSet<string> Warned = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>Where the players and their books come from; set on the server only. Null = the prefix lets everything through.</summary>
    internal static IFbFanOutHost? Host { get; set; }

    internal static void Bind(IEnumerable<(MethodInfo Method, FbFields.Reach Reach)> found)
    {
        foreach (var (m, r) in found) Reaches[m] = r;
    }

    /// <summary>
    /// Prefix on every handler: on the server, run it once per player instead of once for the stand-in. It also fans
    /// out when the event fires inside another player's run (their scheme killed a hero: every book hears of the death).
    /// A book suspended further up the stack is skipped: its live values are parked there, not in its store.
    /// </summary>
    internal static bool Prefix(object? __instance, object?[] __args, MethodBase __originalMethod)
    {
        if (ReferenceEquals(_invoking, __originalMethod))
        {
            _invoking = null;
            return true;
        }
        var host = Host;
        if (host == null || !Reaches.TryGetValue(__originalMethod, out var reach)) return true;
        var players = host.Players(reach);
        _placeholderSkips++;
        foreach (var player in players)
        {
            if (host.IsSuspended(player.Key)) { _suspendedSkips++; continue; }
            using var scope = player.Enter();
            if (scope == null) return true;   // no campaign yet: leave Fourberie alone
            _invoking = __originalMethod;
            try
            {
                __originalMethod.Invoke(__instance, __args);
                _runs++;
            }
            catch (Exception ex)
            {
                _errors++;
                var name = __originalMethod.DeclaringType?.Name + "." + __originalMethod.Name;
                if (Warned.Add(name)) host.Warn($"{name} failed for {player.Name}: {ex.GetBaseException().Message}");
            }
            finally { _invoking = null; }
        }
        return false;
    }

    internal static string Summary() => $"handler runs {_runs} (for {_placeholderSkips} stand-in run(s) skipped), failures {_errors}, suspended books skipped {_suspendedSkips}";
}

/// <summary>One player a handler can run for: their book key, a name for log lines, and how to run as them.</summary>
internal readonly struct FbPlayerRun
{
    public FbPlayerRun(string key, string name, Func<IDisposable?> enter)
    {
        Key = key;
        Name = name;
        Enter = enter;
    }

    public string Key { get; }
    public string Name { get; }
    /// <summary>Installs the player's book and stands them in as the player; null when that cannot be done yet.</summary>
    public Func<IDisposable?> Enter { get; }
}

/// <summary>What FbTicks needs from the server: who the players are and their books (FbBooks in the game; a stand-in in tests).</summary>
internal interface IFbFanOutHost
{
    IReadOnlyList<FbPlayerRun> Players(FbFields.Reach reach);
    bool IsSuspended(string key);
    void Warn(string line);
}
