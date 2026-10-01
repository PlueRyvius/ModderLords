using System.Reflection;
using ModderLords.CompatSync.Coop.Fourberie;
using ModderLords.CompatSync.Coop.LivingEconomy;
using Newtonsoft.Json.Linq;

namespace ModderLords.Core.Tests;

// Fourberie's shapes: per-player state in public statics, persisted ones and in-memory scratch.
internal static class FakeMod
{
    public static Dictionary<int, int> CrimeValue = new();
    public static Dictionary<string, FakeTime> Timers = new();
    public static List<string> Territory = new();
    public static FakeHero? GangLeader;
    public static Dictionary<string, FakeHero> HeroDico = new();
    public static List<FakeHero> Followers = new();
    public static bool GetSomeHelp;
    public static float InfluenceCost;          // scratch: swapped, not persisted
    public static readonly List<string> Readonly = new();
}

internal sealed class FakeHero
{
    public FakeHero(string id) => Id = id;
    public string Id { get; }
}

internal readonly struct FakeTime
{
    public FakeTime(long ticks) => Ticks = ticks;
    public long Ticks { get; }
}

internal sealed class FakeRefs : IBookRefs
{
    public readonly Dictionary<string, FakeHero> Heroes = new();

    public FakeHero Hero(string id) => Heroes.TryGetValue(id, out var h) ? h : Heroes[id] = new FakeHero(id);

    public bool TryEncode(object value, out JToken token)
    {
        token = JValue.CreateNull();
        switch (value)
        {
            case FakeHero h: token = h.Id; return true;
            case FakeTime t: token = t.Ticks; return true;
            default: return false;
        }
    }

    public bool TryDecode(Type type, JToken token, out object? value, out string? problem)
    {
        value = null;
        problem = null;
        if (type == typeof(FakeTime)) { value = new FakeTime((long)token); return true; }
        if (type != typeof(FakeHero)) return false;
        var id = (string?)token;
        if (id != null && Heroes.TryGetValue(id, out var h)) value = h;
        else problem = "hero '" + id + "' not found";
        return true;
    }
}

public sealed class StaticBookTests
{
    private static readonly string[] PersistedNames =
        { nameof(FakeMod.CrimeValue), nameof(FakeMod.Timers), nameof(FakeMod.Territory), nameof(FakeMod.GangLeader),
          nameof(FakeMod.HeroDico), nameof(FakeMod.Followers), nameof(FakeMod.GetSomeHelp) };

    private static StaticBookSchema Schema() => new(
        PersistedNames.Append(nameof(FakeMod.InfluenceCost)).Select(n => typeof(FakeMod).GetField(n)!), PersistedNames);

    private static void Reset()
    {
        FakeMod.CrimeValue = new() { [500] = 9 };   // the server's stand-in values, outside any book
        FakeMod.Timers = new();
        FakeMod.Territory = new() { "outside" };
        FakeMod.GangLeader = null;
        FakeMod.HeroDico = new();
        FakeMod.Followers = new();
        FakeMod.GetSomeHelp = true;
        FakeMod.InfluenceCost = 0;
    }

    [Fact]
    public void ReadonlyFieldsAreRefused() =>
        Assert.Throws<ArgumentException>(() => new StaticBookSchema(new[] { typeof(FakeMod).GetField(nameof(FakeMod.Readonly))! }, []));

    [Fact]
    public void AFreshBookIsEmptyAndDoesNotInheritTheStandInValues()
    {
        Reset();
        var schema = Schema();
        var fresh = schema.Fresh();
        Assert.Empty((Dictionary<int, int>)fresh[0]!);
        Assert.Empty((List<string>)fresh[2]!);
        Assert.Null(fresh[3]);
        Assert.Equal(false, fresh[6]);
        Assert.NotSame(FakeMod.CrimeValue, fresh[0]);
    }

    [Fact]
    public void EditsAndReassignmentsLandInTheBookAndTheOutsideValuesComeBack()
    {
        Reset();
        var schema = Schema();
        var switcher = new StaticBookSwitch(schema);
        var book = schema.Fresh();
        var outsideCrime = FakeMod.CrimeValue;

        using (switcher.Enter("alice", () => book, v => book = v))
        {
            Assert.Equal("alice", switcher.Current);
            FakeMod.CrimeValue[1] = 1;                     // in place
            FakeMod.Territory = new List<string> { "town_A1" };   // reassigned
            FakeMod.InfluenceCost = 12.5f;
        }

        Assert.Null(switcher.Current);
        Assert.Same(outsideCrime, FakeMod.CrimeValue);
        Assert.Equal(9, FakeMod.CrimeValue[500]);
        Assert.Equal(new[] { "outside" }, FakeMod.Territory);
        Assert.Equal(1, ((Dictionary<int, int>)book[0]!)[1]);
        Assert.Equal(new[] { "town_A1" }, (List<string>)book[2]!);
        Assert.Equal(12.5f, book[7]);
    }

    [Fact]
    public void ReenteringTheInstalledBookKeepsAReassignmentMadeInside()
    {
        Reset();
        var schema = Schema();
        var switcher = new StaticBookSwitch(schema);
        var book = schema.Fresh();
        using (switcher.Enter("alice", () => book, v => book = v))
        {
            FakeMod.Territory = new List<string> { "new" };
            using (switcher.Enter("alice", () => book, v => book = v))
                Assert.Equal(new[] { "new" }, FakeMod.Territory);
            Assert.Equal(new[] { "new" }, FakeMod.Territory);
        }
        Assert.Equal(new[] { "new" }, (List<string>)book[2]!);
    }

    [Fact]
    public void TwoPlayersBooksStaySeparateWhenOneRunsInsideTheOther()
    {
        Reset();
        var schema = Schema();
        var switcher = new StaticBookSwitch(schema);
        var alice = schema.Fresh();
        var bob = schema.Fresh();
        using (switcher.Enter("alice", () => alice, v => alice = v))
        {
            FakeMod.CrimeValue[1] = 1;
            using (switcher.Enter("bob", () => bob, v => bob = v))
            {
                Assert.Empty(FakeMod.CrimeValue);
                FakeMod.CrimeValue[2] = 2;
            }
            Assert.Equal(new[] { 1 }, FakeMod.CrimeValue.Keys);
        }
        Assert.Equal(new[] { 1 }, ((Dictionary<int, int>)alice[0]!).Keys);
        Assert.Equal(new[] { 2 }, ((Dictionary<int, int>)bob[0]!).Keys);
    }

    [Fact]
    public void AnOuterPlayersBookIsSuspendedWhileAnotherRunsInsideIt()
    {
        Reset();
        var schema = Schema();
        var switcher = new StaticBookSwitch(schema);
        var alice = schema.Fresh();
        var bob = schema.Fresh();
        using (switcher.Enter("alice", () => alice, v => alice = v))
        {
            Assert.False(switcher.IsSuspended("alice"));            // installed, not suspended
            using (switcher.Enter("bob", () => bob, v => bob = v))
            {
                Assert.True(switcher.IsSuspended("alice"));         // her live values are parked, her store is stale
                Assert.False(switcher.IsSuspended("bob"));
                Assert.False(switcher.IsSuspended("carol"));
            }
            Assert.False(switcher.IsSuspended("alice"));
        }
    }

    [Fact]
    public void ABookIsReadBackEvenWhenTheCallThrows()
    {
        Reset();
        var schema = Schema();
        var switcher = new StaticBookSwitch(schema);
        var book = schema.Fresh();
        Assert.Throws<InvalidOperationException>((Action)(() =>
        {
            using (switcher.Enter("alice", () => book, v => book = v))
            {
                FakeMod.CrimeValue[7] = 7;
                throw new InvalidOperationException();
            }
        }));
        Assert.Equal(7, ((Dictionary<int, int>)book[0]!)[7]);
        Assert.Equal(9, FakeMod.CrimeValue[500]);
        Assert.Null(switcher.Current);
    }
}

public sealed class FbBookCodecTests
{
    private static readonly (string, Type)[] Shape =
    {
        ("CrimeValue", typeof(Dictionary<int, int>)), ("Timers", typeof(Dictionary<string, FakeTime>)),
        ("Territory", typeof(List<string>)), ("GangLeader", typeof(FakeHero)), ("HeroDico", typeof(Dictionary<string, FakeHero>)),
        ("Followers", typeof(List<FakeHero>)), ("GetSomeHelp", typeof(bool)), ("Cost", typeof(float)),
    };

    private static JObject Encode(FakeRefs refs, object?[] values, List<string> problems) =>
        FbBookCodec.Encode(Shape.Select((s, i) => (s.Item1, s.Item2, values[i])), refs, problems);

    [Fact]
    public void ABookRoundTripsThroughJson()
    {
        var refs = new FakeRefs();
        var leader = refs.Hero("gang_leader_1");
        var values = new object?[]
        {
            new Dictionary<int, int> { [500] = 1, [-3] = 4 },
            new Dictionary<string, FakeTime> { ["town_A1"] = new FakeTime(123456789012) },
            new List<string> { "town_A1", "Ünïcödé ☠" },
            leader,
            new Dictionary<string, FakeHero> { ["victim7"] = leader },
            new List<FakeHero> { leader },
            true,
            2.5f,
        };
        var problems = new List<string>();
        var json = Encode(refs, values, problems);
        Assert.Empty(problems);

        var back = FbBookCodec.Decode(JObject.Parse(json.ToString()), Shape, refs, problems, _ => null);
        Assert.Empty(problems);
        Assert.Equal(new Dictionary<int, int> { [500] = 1, [-3] = 4 }, back[0]);
        Assert.Equal(123456789012, ((Dictionary<string, FakeTime>)back[1]!)["town_A1"].Ticks);
        Assert.Equal(new[] { "town_A1", "Ünïcödé ☠" }, (List<string>)back[2]!);
        Assert.Same(leader, back[3]);
        Assert.Same(leader, ((Dictionary<string, FakeHero>)back[4]!)["victim7"]);
        Assert.Same(leader, ((List<FakeHero>)back[5]!)[0]);
        Assert.Equal(true, back[6]);
        Assert.Equal(2.5f, back[7]);
    }

    [Fact]
    public void DictionariesAreRowsSoOnlyChangedRowsTravel()
    {
        var refs = new FakeRefs();
        var problems = new List<string>();
        var before = Encode(refs, new object?[] { new Dictionary<int, int> { [1] = 1, [2] = 2 }, null, null, null, null, null, false, 0f }, problems);
        var after = Encode(refs, new object?[] { new Dictionary<int, int> { [1] = 1, [2] = 3 }, null, null, null, null, null, false, 0f }, problems);
        var diff = LeMirrorDelta.Diff(before, after)!;
        Assert.Equal(3, (int)diff["CrimeValue"]!["$set"]!["2"]!);
        Assert.Null(diff["CrimeValue"]!["$set"]!["1"]);
    }

    [Fact]
    public void AnObjectThatNoLongerExistsIsDroppedWithAProblem()
    {
        var refs = new FakeRefs();
        var json = JObject.Parse("""{"HeroDico":{"victim7":"dead_hero","victim8":null},"Followers":["dead_hero"],"GangLeader":"dead_hero"}""");
        var problems = new List<string>();
        var back = FbBookCodec.Decode(json, Shape, refs, problems, _ => "fallback");
        var dico = (Dictionary<string, FakeHero>)back[4]!;
        Assert.False(dico.ContainsKey("victim7"));
        Assert.True(dico.ContainsKey("victim8"));          // the mod wrote a null there; keep it
        Assert.Empty((List<FakeHero>)back[5]!);
        Assert.Null(back[3]);
        Assert.Equal("fallback", back[0]);                  // missing field
        Assert.Equal(3, problems.Count(p => p.Contains("dead_hero")));
    }

    [Theory]
    [InlineData("""{"CrimeValue":{"abc":1}}""")]
    [InlineData("""{"CrimeValue":{"1":"x"}}""")]
    [InlineData("""{"CrimeValue":[1,2]}""")]
    [InlineData("""{"GetSomeHelp":"yes"}""")]
    [InlineData("""{"Territory":{"a":1}}""")]
    [InlineData("""{"CrimeValue":{"1":99999999999}}""")]
    public void MalformedValuesAreReportedNotThrown(string text)
    {
        var problems = new List<string>();
        FbBookCodec.Decode(JObject.Parse(text), Shape, new FakeRefs(), problems, _ => null);
        Assert.NotEmpty(problems);
    }

    [Fact]
    public void UnsupportedTypesAreLeftOutAndReported()
    {
        var problems = new List<string>();
        var json = FbBookCodec.Encode(new[] { ("Odd", typeof(object), (object?)new System.Text.StringBuilder()) }, new FakeRefs(), problems);
        Assert.Null(json["Odd"]);
        Assert.Single(problems);
    }

    [Fact]
    public void NonFiniteNumbersAreRefused()
    {
        var problems = new List<string>();
        FbBookCodec.Encode(new[] { ("Cost", typeof(float), (object?)float.NaN) }, new FakeRefs(), problems);
        Assert.Single(problems);
    }
}

public sealed class FbBookSyncTests
{
    private static JObject Book(params (string Row, int Value)[] rows)
    {
        var crime = new JObject();
        foreach (var (row, value) in rows) crime[row] = value;
        return new JObject { ["_crimeValue"] = crime, ["_getSomeHelp"] = false };
    }

    private static JObject Unpacked(string payload)
    {
        Assert.True(LeMirrorDelta.TryUnpack(payload, out _, out _, out _, out var values));
        return values;
    }

    [Fact]
    public void NothingIsReportedBeforeTheServersBookArrives() =>
        Assert.Null(new FbClientLedger().Report(Book(("1", 1))));

    [Fact]
    public void RowsTravelBothWaysWithoutEchoAndWithoutLosingTheOtherSidesRows()
    {
        var server = new FbServerLedger();
        var client = new FbClientLedger();
        var serverBook = Book(("500", 1));

        // Join: the whole book.
        var installed = client.Receive(server.Full(serverBook), Book(), out var needFull)!;
        Assert.False(needFull);
        Assert.Equal(1, (int)installed["_crimeValue"]!["500"]!);

        // The player's menu sets a row; it is reported and merged.
        var local = (JObject)installed.DeepClone();
        local["_crimeValue"]!["550"] = 0;
        var report = client.Report(local)!;
        Assert.Null(client.Report(local));                        // reported once
        serverBook = server.Merge(serverBook, Unpacked(report));
        Assert.Equal(0, (int)serverBook["_crimeValue"]!["550"]!);
        Assert.Null(server.DeltaIfChanged(serverBook));           // not echoed back

        // A server tick changes another row while the player changed one more and has not reported it yet.
        serverBook["_crimeValue"]!["500"] = 2;
        local["_crimeValue"]!["551"] = 7;
        var delta = server.DeltaIfChanged(serverBook)!;
        var after = client.Receive(delta, local, out needFull)!;
        Assert.False(needFull);
        Assert.Equal(2, (int)after["_crimeValue"]!["500"]!);      // server's row
        Assert.Equal(0, (int)after["_crimeValue"]!["550"]!);      // agreed row kept
        Assert.Equal(7, (int)after["_crimeValue"]!["551"]!);      // local unreported row kept

        // ...and that local row is still reported afterwards.
        var second = Unpacked(client.Report(after)!);
        Assert.Equal(7, (int)second["_crimeValue"]!["$set"]!["551"]!);
        Assert.Null(second["_crimeValue"]!["$set"]!["500"]);
    }

    [Fact]
    public void AMissedServerMessageAsksForTheWholeBook()
    {
        var server = new FbServerLedger();
        var client = new FbClientLedger();
        var book = Book(("1", 1));
        client.Receive(server.Full(book), Book(), out _);
        book["_crimeValue"]!["1"] = 2;
        server.DeltaIfChanged(book);                               // lost on the way
        book["_crimeValue"]!["1"] = 3;
        Assert.Null(client.Receive(server.DeltaIfChanged(book)!, Book(), out var needFull));
        Assert.True(needFull);
    }

    [Fact]
    public void AFieldMissingLocallyIsNeverReportedAsDeleted()
    {
        var server = new FbServerLedger();
        var client = new FbClientLedger();
        client.Receive(server.Full(Book(("1", 1))), Book(), out _);
        var local = new JObject { ["_crimeValue"] = new JObject { ["1"] = 1 } };   // _getSomeHelp could not be written
        Assert.Null(client.Report(local));
    }

    [Fact]
    public void GarbageIsIgnored() =>
        Assert.Null(new FbClientLedger().Receive("not a payload", Book(), out _));
}
