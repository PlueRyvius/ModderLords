using System.IO.Compression;
using System.Text;
using ModderLords.Operations;
using Newtonsoft.Json.Linq;

namespace ModderLords.Core.Tests;

public sealed class SnapshotDeltaTests
{
    private static JObject Memory(int i, int value = -5) => new()
    {
        ["MemoryKey"] = $"{91000 + i:000000000.000}-{i:X12}", ["Scope"] = 0, ["FirstId"] = "lord_" + i, ["SecondId"] = "player",
        ["SourceId"] = "recent_grievance", ["ContextText"] = "", ["Value"] = value, ["StartDay"] = 91000.375 + i, ["ExpiryDay"] = 91840.375 + i,
    };

    private static JObject Snapshot(long revision, IEnumerable<JObject> memories) => new()
    {
        ["SchemaVersion"] = 1, ["Revision"] = revision, ["CapturedDay"] = 91077.5,
        ["Titles"] = new JArray(Enumerable.Range(0, 50).Select(i => new JObject { ["TitleId"] = "t" + i, ["Holder"] = "clan_" + i })),
        ["RelationMemories"] = new JArray(memories),
        ["PendingSettlementRefreshClanIds"] = new JArray("clan_1", "clan_2"),
    };

    private static void AssertRebuilds(JObject from, JObject to)
    {
        var before = SnapshotDelta.Canonical(from);
        var delta = SnapshotDelta.Parse(SnapshotDelta.Canonical(SnapshotDelta.Create(from, to)));   // as it travels
        var rebuilt = SnapshotDelta.Apply(from, delta);
        Assert.Equal(SnapshotDelta.Canonical(to), SnapshotDelta.Canonical(rebuilt));
        Assert.Equal(SnapshotDelta.Fingerprint(to), SnapshotDelta.Fingerprint(rebuilt));
        Assert.Equal(before, SnapshotDelta.Canonical(from));   // the old copy is never modified
    }

    [Fact]
    public void MemoriesRemovedAddedAndChangedRebuildExactlyAndTheDiffIsSmall()
    {
        var old = Enumerable.Range(0, 2000).Select(i => Memory(i)).ToList();
        var next = old.Where((_, i) => i != 0 && i != 700 && i != 1999).Select(m => (JObject)m.DeepClone()).ToList();
        next[100]["Value"] = -6;                       // one memory changed
        next.Add(Memory(5000)); next.Add(Memory(5001));  // two new ones at the end
        var from = Snapshot(1, old);
        var to = Snapshot(2, next);
        to.Remove("PendingSettlementRefreshClanIds");   // a property gone
        to["ClaimFeuds"] = new JArray(new JObject { ["RecordId"] = "feud-1" });   // a section new

        AssertRebuilds(from, to);
        var diff = SnapshotDelta.Pack(SnapshotDelta.Canonical(SnapshotDelta.Create(from, to)));
        var full = SnapshotDelta.Pack(SnapshotDelta.Canonical(to));
        Assert.True(diff.Length * 20 < full.Length, $"diff {diff.Length} bytes against full {full.Length}");
    }

    [Fact]
    public void ReorderedAndRepeatedRecordsKeepTheirOrder()
    {
        var a = Memory(1); var b = Memory(2); var c = Memory(3);
        AssertRebuilds(Snapshot(1, new[] { a, b, c, a }), Snapshot(2, new[] { c, a, a, b, (JObject)a.DeepClone() }));
        AssertRebuilds(Snapshot(1, Array.Empty<JObject>()), Snapshot(2, new[] { a, b }));
        AssertRebuilds(Snapshot(1, new[] { a, b }), Snapshot(2, Array.Empty<JObject>()));
    }

    [Fact]
    public void ADiffAppliedToTheWrongCopyIsCaughtByTheFingerprint()
    {
        var from = Snapshot(1, Enumerable.Range(0, 10).Select(i => Memory(i)));
        var to = Snapshot(2, Enumerable.Range(1, 10).Select(i => Memory(i)));
        var delta = SnapshotDelta.Create(from, to);
        var other = Snapshot(1, Enumerable.Range(0, 10).Select(i => Memory(i, value: -9)));
        Assert.NotEqual(SnapshotDelta.Fingerprint(to), SnapshotDelta.Fingerprint(SnapshotDelta.Apply(other, delta)));
        Assert.Throws<InvalidDataException>(() => SnapshotDelta.Apply(Snapshot(1, Array.Empty<JObject>()), delta));   // copies past the old array
    }

    [Fact]
    public void PackedPayloadsRoundTripAndAZipBombIsRefused()
    {
        var json = SnapshotDelta.Canonical(Snapshot(3, Enumerable.Range(0, 500).Select(i => Memory(i))));
        Assert.Equal(json, SnapshotDelta.Unpack(SnapshotDelta.Pack(json)));

        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            var zeros = new byte[1024 * 1024];
            for (var i = 0; i < 70; i++) gzip.Write(zeros, 0, zeros.Length);
        }
        Assert.Throws<InvalidDataException>(() => SnapshotDelta.Unpack(Convert.ToBase64String(output.ToArray())));
    }

    // The server (.NET Core) and players' games (.NET Framework) write doubles differently; numbers must come back out
    // exactly as they were read, whatever the runtime.
    [Theory]
    [InlineData("{\"a\":91077.375,\"b\":0.30000000000000004,\"c\":91840.37500000001,\"d\":1.5E-05,\"e\":-0.0,\"f\":12,\"g\":0.1}")]
    public void NumbersKeepTheDigitsTheyWereReadFrom(string json)
    {
        var once = SnapshotDelta.Canonical(SnapshotDelta.Parse(json));
        Assert.Equal(once, SnapshotDelta.Canonical(SnapshotDelta.Parse(once)));
        Assert.Contains("0.30000000000000004", once);
        Assert.Contains("91840.37500000001", once);
        Assert.All(SnapshotDelta.Parse(json).Properties(), p => Assert.IsNotType<double>(((JValue)p.Value).Value));
    }

    [Fact]
    public void AnUnusableRevisionCanBeReceivedAgainAfterRewinding()
    {
        var reassembler = new SnapshotReassembler();
        reassembler.BeginSession("epoch");
        Assert.True(reassembler.Offer("epoch", 4, 0, 1, "diff", out _));
        Assert.False(reassembler.Offer("epoch", 4, 0, 1, "full", out _));   // already completed
        reassembler.RewindTo(3);
        Assert.True(reassembler.Offer("epoch", 4, 0, 1, "full", out var complete));
        Assert.Equal("full", complete);
    }
}
