using System.Text.RegularExpressions;
using ModderLords.Core.Smoke;

namespace ModderLords.Core.Logs;

/// <summary>
/// The Host tab's time control, read from the server's own lines. Coop logs every time change as
/// <c>Applying server time control: current="Pause" requested=Play_1x effective=Pause ...</c> (it holds time at Pause
/// while nobody is connected, so requested and effective can differ). <see cref="Command"/> builds Coop's server
/// command for a speed.
/// <para>
/// Pause on join: while a player who has not yet reached the campaign map is connected (the dedicated server's
/// <c>@DS@{"ev":"players"}</c> states <c>handshake</c>, <c>loading</c>, <c>creating character</c>, ...), the server is
/// held at Pause, then put back to the speed last asked for. A player already seen on the map is never treated as
/// joining again, so a battle or a menu does not pause anyone.
/// </para>
/// </summary>
public sealed partial class ServerTime
{
    public const string Pause = "Pause", Play1x = "Play_1x", Play2x = "Play_2x";

    /// <summary>Coop's server command that sets campaign time (Pause, Play_1x or Play_2x).</summary>
    public static string Command(string mode) => $"{SmokeSignals.RunTimeCommand} {mode}";

    [GeneratedRegex(@"Applying server time control: current=""?\w+""? requested=(\w+) effective=(\w+)")]
    private static partial Regex AppliedRx();

    private readonly HashSet<int> _seenOnMap = new();

    /// <summary>The speed last asked for, by the host or a player; what a join pause goes back to.</summary>
    public string? Requested { get; private set; }
    /// <summary>The speed the server actually runs at, as Coop last reported it.</summary>
    public string? Effective { get; private set; }
    /// <summary>True while this class is holding the server at Pause for a joining player.</summary>
    public bool Holding { get; private set; }
    /// <summary>Names of the players being waited for, while <see cref="Holding"/>.</summary>
    public IReadOnlyList<string> Joining { get; private set; } = [];

    public bool PauseOnJoin { get; set; }

    /// <summary>The host chose a speed: it is what joins return to, and it ends a join pause.</summary>
    public void Chosen(string mode)
    {
        Requested = mode;
        Holding = false;
    }

    /// <summary>One server line. Returns a command to send, or null.</summary>
    public string? Observe(string line)
    {
        var m = AppliedRx().Match(line);
        if (m.Success)
        {
            // Our own Pause while holding is not what the server should go back to.
            if (!Holding) Requested = m.Groups[1].Value;
            Effective = m.Groups[2].Value;
            return null;
        }
        if (SmokeSignals.ParsePlayers(line) is not { } players) return null;

        _seenOnMap.IntersectWith(players.Select(p => p.Id));
        foreach (var p in players)
            if (p.State == SmokeSignals.OnMapState) _seenOnMap.Add(p.Id);
        var joining = players.Where(p => !_seenOnMap.Contains(p.Id)).ToList();
        Joining = joining.Select(p => string.IsNullOrEmpty(p.Name) ? $"peer {p.Id}" : p.Name).ToList();
        return Update(joining.Count > 0);
    }

    /// <summary>After <see cref="PauseOnJoin"/> changes: a hold that is no longer wanted ends at once.</summary>
    public string? Reconsider() => Update(Joining.Count > 0);

    private string? Update(bool anyoneJoining)
    {
        if (PauseOnJoin && anyoneJoining && !Holding)
        {
            Holding = true;
            return Command(Pause);
        }
        if (Holding && (!anyoneJoining || !PauseOnJoin))
        {
            Holding = false;
            return Requested is { } back && back != Pause ? Command(back) : null;
        }
        return null;
    }
}
