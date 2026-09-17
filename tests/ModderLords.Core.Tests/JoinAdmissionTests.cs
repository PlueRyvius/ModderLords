using ModderLords.Operations;

namespace ModderLords.Core.Tests;

public sealed class JoinAdmissionTests
{
    [Fact]
    public void ValidationWaitsForAcknowledgementAndResumesOnlyOnce()
    {
        var ledger = new JoinAdmission<object, string>(); var peer = new object();
        Assert.True(ledger.Queue(peer, "actor", "original", 0));
        Assert.False(ledger.TryTake(peer, 1, out _));
        Assert.True(ledger.Queue(peer, "actor", "duplicate", 1));
        Assert.True(ledger.Acknowledge(peer, 2));
        Assert.True(ledger.TryTake(peer, 3, out var validation)); Assert.Equal("original", validation);
        Assert.True(ledger.Acknowledge(peer, 4));
        Assert.False(ledger.TryTake(peer, 4, out _));
        Assert.False(ledger.Queue(peer, "actor", "replay", 4));
    }
    [Fact]
    public void EarlyAcknowledgementStillRequiresValidation()
    {
        var ledger = new JoinAdmission<object, string>(); var peer = new object();
        Assert.False(ledger.Acknowledge(peer, 0));
        Assert.True(ledger.Begin(peer, 0)); Assert.True(ledger.Acknowledge(peer, 1));
        Assert.False(ledger.TryTake(peer, 2, out _));
        Assert.True(ledger.Queue(peer, "actor", "validation", 3));
        Assert.True(ledger.TryTake(peer, 4, out _));
    }
    [Fact]
    public void RepeatedTrafficCannotExtendDeadlineOrChangeActor()
    {
        var ledger = new JoinAdmission<object, string>(1, 10); var peer = new object();
        Assert.True(ledger.Queue(peer, "actor", "validation", 0));
        Assert.False(ledger.Queue(peer, "other", "replacement", 1));
        Assert.True(ledger.Begin(peer, 9));
        Assert.False(ledger.Acknowledge(peer, 10)); Assert.True(ledger.IsExpired(peer, 10));
        Assert.False(ledger.TryTake(peer, 10, out _));
    }
    [Fact]
    public void DisconnectAndSessionEndDiscardPendingValidation()
    {
        var ledger = new JoinAdmission<object, string>(1); var peer = new object(); var next = new object();
        Assert.True(ledger.Queue(peer, "actor", "old", 0)); Assert.False(ledger.Begin(next, 0));
        ledger.Disconnect(peer); Assert.False(ledger.Acknowledge(peer, 1));
        Assert.True(ledger.Begin(next, 1)); ledger.Clear(); Assert.Empty(ledger.Peers);
        Assert.False(ledger.TryTake(peer, 2, out _));
    }
}
