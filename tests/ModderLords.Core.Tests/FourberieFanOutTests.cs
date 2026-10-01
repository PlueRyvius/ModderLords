using System.Reflection;
using HarmonyLib;
using ModderLords.CompatSync.Coop.Fourberie;

namespace ModderLords.Core.Tests;

// A stand-in for Fourberie's behaviours: handlers that act on "the player's" statics, one of which fires a world event
// from inside a player's run (a scheme killing a hero), as KillCharacterAction would raise HeroKilled.
internal static class StandInFourberie
{
    public static Dictionary<int, int> Crime = new();
    public static List<string> Victims = new();
    public static int StandInRuns;

    public static void DailyTick()
    {
        Crime[1] = Crime.TryGetValue(1, out var v) ? v + 1 : 1;   // income counter: one per day, per player
        if (Crime.ContainsKey(666)) HeroKilled("victim7");          // this player's scheme kills a hero
    }

    public static void HeroKilled(string hero) => Victims.Remove(hero);   // every book forgets the dead hero

    public static void Unpatched() => StandInRuns++;
}

/// <summary>Players and books for FbTicks, with no game: the same StaticBookSwitch the server uses.</summary>
internal sealed class StandInHost : IFbFanOutHost
{
    private readonly StaticBookSwitch _switch;
    public readonly Dictionary<string, object?[]> Books = new();
    public readonly List<string> Connected = new();
    public readonly List<string> Offline = new();
    public readonly List<string> Warnings = new();

    public StandInHost(StaticBookSwitch @switch) => _switch = @switch;

    public IReadOnlyList<FbPlayerRun> Players(FbFields.Reach reach) =>
        (reach == FbFields.Reach.Everyone ? Connected.Concat(Offline) : Connected)
            .Select(k => new FbPlayerRun(k, k, () => _switch.Enter(k, () => Books[k], v => Books[k] = v))).ToList();

    public bool IsSuspended(string key) => _switch.IsSuspended(key);

    public void Warn(string line) => Warnings.Add(line);
}

public sealed class FourberieFanOutTests : IDisposable
{
    private readonly Harmony _harmony = new("ModderLords.Tests.FourberieFanOut");
    private readonly StandInHost _host;
    private readonly StaticBookSchema _schema;

    public FourberieFanOutTests()
    {
        StandInFourberie.Crime = new() { [500] = 1 };     // the server's stand-in book
        StandInFourberie.Victims = new() { "victim7" };
        _schema = new StaticBookSchema(
            new[] { typeof(StandInFourberie).GetField(nameof(StandInFourberie.Crime))!, typeof(StandInFourberie).GetField(nameof(StandInFourberie.Victims))! },
            new[] { nameof(StandInFourberie.Crime), nameof(StandInFourberie.Victims) });
        _host = new StandInHost(new StaticBookSwitch(_schema));
        foreach (var key in new[] { "alice", "bob", "carol" })
        {
            var book = _schema.Fresh();
            ((List<string>)book[1]!).Add("victim7");
            _host.Books[key] = book;
        }
        _host.Connected.AddRange(new[] { "alice", "bob" });
        _host.Offline.Add("carol");
        ((Dictionary<int, int>)_host.Books["alice"][0]!)[666] = 1;   // alice has a murder scheme running

        var daily = typeof(StandInFourberie).GetMethod(nameof(StandInFourberie.DailyTick))!;
        var killed = typeof(StandInFourberie).GetMethod(nameof(StandInFourberie.HeroKilled))!;
        FbTicks.Bind(new[] { (daily, FbFields.Reach.Connected), (killed, FbFields.Reach.Everyone) });
        FbTicks.Host = _host;
        var prefix = new HarmonyMethod(typeof(FbTicks).GetMethod(nameof(FbTicks.Prefix), BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Public)) { priority = Priority.First };
        _harmony.Patch(daily, prefix: prefix);
        _harmony.Patch(killed, prefix: prefix);
    }

    public void Dispose()
    {
        _harmony.UnpatchAll(_harmony.Id);
        FbTicks.Host = null;
    }

    private Dictionary<int, int> Crime(string key) => (Dictionary<int, int>)_host.Books[key][0]!;
    private List<string> Victims(string key) => (List<string>)_host.Books[key][1]!;

    [Fact]
    public void ADailyTickRunsOncePerConnectedPlayerAndNeverForTheStandIn()
    {
        StandInFourberie.DailyTick();
        StandInFourberie.DailyTick();

        Assert.Equal(2, Crime("alice")[1]);
        Assert.Equal(2, Crime("bob")[1]);
        Assert.False(Crime("carol").ContainsKey(1));                 // offline: frozen
        Assert.False(StandInFourberie.Crime.ContainsKey(1));          // the stand-in's book is untouched
        Assert.Equal(1, StandInFourberie.Crime[500]);
        Assert.Empty(_host.Warnings);
    }

    [Fact]
    public void AnEventRaisedInsideOnePlayersRunReachesEveryBook()
    {
        StandInFourberie.DailyTick();   // alice's scheme kills victim7 during her run

        Assert.DoesNotContain("victim7", Victims("alice"));
        Assert.DoesNotContain("victim7", Victims("bob"));
        Assert.DoesNotContain("victim7", Victims("carol"));            // offline books hear of deaths too
        Assert.Contains("victim7", StandInFourberie.Victims);          // not the stand-in's
        Assert.Equal(1, Crime("alice")[1]);                            // and alice's own tick still counted once
    }

    [Fact]
    public void WithNoPlayersConnectedTicksDoNothing()
    {
        _host.Connected.Clear();
        StandInFourberie.DailyTick();
        Assert.False(StandInFourberie.Crime.ContainsKey(1));
        Assert.False(Crime("carol").ContainsKey(1));
    }

    [Fact]
    public void WithoutAHostEverythingRunsAsWritten()
    {
        FbTicks.Host = null;     // a player's game, or the layer off
        StandInFourberie.DailyTick();
        Assert.Equal(1, StandInFourberie.Crime[1]);
    }
}
