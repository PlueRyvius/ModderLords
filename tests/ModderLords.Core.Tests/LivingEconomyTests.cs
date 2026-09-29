using ModderLords.CompatSync.Coop.LivingEconomy;
using ModderLords.Core.Compat;
using ModderLords.Coop.Launch;
using Newtonsoft.Json.Linq;

namespace ModderLords.Core.Tests;

public sealed class LivingEconomyLaunchPolicyTests
{
    [Fact]
    public void LivingEconomyWithoutSettingsSyncIsRefused()
    {
        var problem = LivingEconomyLaunchPolicy.SyncProblem(["Native", "BetterEconomy"], settingsSync: false);
        Assert.NotNull(problem);
        Assert.Contains("Settings sync", problem);
    }

    [Fact]
    public void LivingEconomyWithSettingsSyncIsAllowed() =>
        Assert.Null(LivingEconomyLaunchPolicy.SyncProblem(["Native", "betterEconomy"], settingsSync: true));

    [Fact]
    public void OtherProfilesAreUnchanged() =>
        Assert.Null(LivingEconomyLaunchPolicy.SyncProblem(["Native", "ImprovedGarrisons"], settingsSync: false));

    [Fact]
    public void ServerOnlyLogicOnLivingEconomyIsWarnedAbout()
    {
        Assert.NotNull(LivingEconomyLaunchPolicy.ServerOnlyLogicProblem(["BetterEconomy"]));
        Assert.Null(LivingEconomyLaunchPolicy.ServerOnlyLogicProblem(["ImprovedGarrisons"]));
    }

    [Fact]
    public void TheBundledRecordKeepsPlayersLedgerPreferencesOutOfSettingsSync()
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "compat-db.json");
        if (!File.Exists(bundled)) bundled = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "ModderLords.Core", "compat-db.json"));
        var db = CompatDb.Load(bundled, Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json"));

        var record = db.Find("BetterEconomy");
        Assert.NotNull(record);
        Assert.Contains("BetterEconomy.Config.RuntimeSettings", record!.IgnoreSettingsTypes);
        Assert.NotEqual(true, record.ServerAuthoritative);
    }
}

public sealed class LeMirrorDeltaTests
{
    private static JObject Book(params (string Id, int Value)[] rows)
    {
        var book = new JObject();
        foreach (var (id, value) in rows) book[id] = new JObject { ["Gold"] = value };
        return book;
    }

    [Fact]
    public void NothingChangedMeansNothingToSend()
    {
        var a = new JObject { ["BEE_TownEconomyStates"] = Book(("town_ES1", 5)) };
        Assert.Null(LeMirrorDelta.Diff(a, (JObject)a.DeepClone()));
    }

    [Fact]
    public void OnlyChangedRowsTravelAndApplyingThemRebuildsTheServerCopy()
    {
        var before = new JObject { ["BEE_TownEconomyStates"] = Book(("town_ES1", 5), ("town_ES2", 7), ("town_ES3", 9)) };
        var after = new JObject { ["BEE_TownEconomyStates"] = Book(("town_ES1", 5), ("town_ES2", 8), ("town_ES4", 1)) };

        var delta = LeMirrorDelta.Diff(before, after)!;
        var entry = (JObject)delta["BEE_TownEconomyStates"]!;
        Assert.Equal(new[] { "town_ES2", "town_ES4" }, ((JObject)entry["$set"]!).Properties().Select(p => p.Name).OrderBy(n => n));
        Assert.Equal(new[] { "town_ES3" }, ((JArray)entry["$del"]!).Select(t => t.ToString()));

        var client = (JObject)before.DeepClone();
        LeMirrorDelta.Apply(client, delta);
        Assert.True(JToken.DeepEquals(after, client));
    }

    [Fact]
    public void ValuesThatAreNotBooksAreReplacedWhole()
    {
        var before = new JObject { ["BEE_TradeActionLogs"] = new JArray(1, 2), ["Gone"] = 3 };
        var after = new JObject { ["BEE_TradeActionLogs"] = new JArray(1, 2, 3), ["New"] = new JObject { ["x"] = 1 } };

        var client = (JObject)before.DeepClone();
        LeMirrorDelta.Apply(client, LeMirrorDelta.Diff(before, after)!);
        Assert.True(JToken.DeepEquals(after, client));
    }

    [Fact]
    public void PackedMessagesRoundTrip()
    {
        var values = new JObject { ["BEE_Population"] = Book(("village_ES1_1", 42)) };
        var payload = LeMirrorDelta.Pack(LeMirrorDelta.KindDelta, 3, 4, values);

        Assert.True(LeMirrorDelta.TryUnpack(payload, out var kind, out var baseSequence, out var sequence, out var back));
        Assert.Equal(LeMirrorDelta.KindDelta, kind);
        Assert.Equal(3, baseSequence);
        Assert.Equal(4, sequence);
        Assert.True(JToken.DeepEquals(values, back));
    }

    [Fact]
    public void GarbageIsRejectedNotThrown() =>
        Assert.False(LeMirrorDelta.TryUnpack("not a payload", out _, out _, out _, out _));

    [Fact]
    public void ALargeBookCompressesWell()
    {
        var book = new JObject();
        for (var i = 0; i < 700; i++)
            book["settlement_" + i] = new JObject { ["Peasants"] = 1234.5f + i, ["Artisans"] = 210.25f, ["Merchants"] = 88.5f, ["MobilizedPool"] = 24.7f };
        var json = new JObject { ["BEE_Population"] = book }.ToString(Newtonsoft.Json.Formatting.None);
        var packed = LeMirrorDelta.Compress(json);
        Assert.True(packed.Length < json.Length / 2, $"packed {packed.Length} of {json.Length}");
        Assert.Equal(json, LeMirrorDelta.Decompress(packed));
    }
}

public sealed class LeActionCodecTests
{
    private enum Policy { Balanced = 0, Mercantile = 1, Militarist = 2 }

    [Fact]
    public void EnumsTravelAsTheirNumber()
    {
        Assert.True(LeActionCodec.TryEncodeValue(Policy.Militarist, out var text));
        Assert.Equal("2", text);
        Assert.True(LeActionCodec.TryDecodeValue(text, typeof(Policy), out var back));
        Assert.Equal(Policy.Militarist, back);
    }

    [Fact]
    public void AnUndefinedEnumValueIsRefused() =>
        Assert.False(LeActionCodec.TryDecodeValue("9", typeof(Policy), out _));

    [Fact]
    public void NumbersAndFlagsRoundTripInvariantly()
    {
        Assert.True(LeActionCodec.TryEncodeValue(50000, out var amount));
        Assert.True(LeActionCodec.TryDecodeValue(amount, typeof(int), out var n));
        Assert.Equal(50000, n);
        Assert.True(LeActionCodec.TryEncodeValue(true, out var flag));
        Assert.True(LeActionCodec.TryDecodeValue(flag, typeof(bool), out var b));
        Assert.Equal(true, b);
        Assert.False(LeActionCodec.TryDecodeValue("yes", typeof(bool), out _));
    }

    [Fact]
    public void GameObjectsAreNotTheCodecsJob() =>
        Assert.False(LeActionCodec.TryEncodeValue(new object(), out _));

    [Theory]
    [InlineData(0, false)]
    [InlineData(-5000, false)]
    [InlineData(10000, true)]
    [InlineData(100000, true)]
    [InlineData(int.MaxValue, false)]
    public void OnlyPlausibleGoldAmountsAreAccepted(int amount, bool ok) =>
        Assert.Equal(ok, LeActionCodec.IsPlausibleAmount(amount));

    [Fact]
    public void MessageLinesKeepTheirColourAndText()
    {
        var line = LeActionCodec.Line(0xFF00FF00u, "[LivingEconomy] Invested 10000g in Danustica: +3.2 prosperity.");
        Assert.True(LeActionCodec.TrySplitLine(line, out var color, out var text));
        Assert.Equal(0xFF00FF00u, color);
        Assert.Equal("[LivingEconomy] Invested 10000g in Danustica: +3.2 prosperity.", text);
    }

    [Fact]
    public void AnswersAreCapped() =>
        Assert.Equal(6, LeActionCodec.Cap(Enumerable.Range(0, 20).Select(i => i.ToString()).ToList()).Count);
}
