using ModderLords.CompatSync.Coop.Operations;

namespace ModderLords.Core.Tests;

public sealed class PromptQueueTests
{
    private static readonly DateTime Start = new(2026, 10, 3, 20, 0, 0, DateTimeKind.Utc);
    private readonly PromptQueue<string> _queue = new(TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(1));

    private string? Take(double secondsIn, bool busy = false) =>
        _queue.TryTake(Start.AddSeconds(secondsIn), busy, out var prompt, out _) ? prompt : null;

    [Fact]
    public void ABurstIsHandedOverOneAtATimeInTheOrderItArrived()
    {
        foreach (var p in new[] { "rebellion", "call to arms", "succession" }) _queue.Add(p, Start);

        Assert.Equal("rebellion", Take(0));
        // The first is open now: the game reports an inquiry, so nothing else follows it.
        Assert.Null(Take(0.25, busy: true));
        Assert.Null(Take(30, busy: true));

        _queue.Answered();
        Assert.Equal("call to arms", Take(31));
        _queue.Answered();
        Assert.Equal("succession", Take(32));
        Assert.Equal(0, _queue.Count);
    }

    [Fact]
    public void NothingOpensWhileThePlayerIsInAMission()
    {
        _queue.Add("rebellion", Start);
        for (var s = 0; s < 120; s++) Assert.Null(Take(s, busy: true));
        Assert.Equal("rebellion", Take(120));
    }

    [Fact]
    public void ThePromptAfterOneThatHasNotAppearedYetWaitsForIt()
    {
        _queue.Add("first", Start);
        _queue.Add("second", Start);
        Assert.Equal("first", Take(0));
        // The screen opens an inquiry a moment after the call, so "nothing is open" straight afterwards proves nothing.
        Assert.Null(Take(0.25));
        Assert.Null(Take(0.75));
    }

    [Fact]
    public void APromptClosedWithoutAnAnswerDoesNotHoldTheRestBackForEver()
    {
        _queue.Add("first", Start);
        _queue.Add("second", Start);
        Assert.Equal("first", Take(0));
        Assert.Null(Take(5, busy: true));
        // Closed by something else: neither button callback ran, and no inquiry is open any more.
        Assert.Equal("second", Take(6));
    }

    [Fact]
    public void APromptTheServerHasStoppedWaitingForIsNotShown()
    {
        _queue.Add("decided during the battle", Start);
        _queue.Add("still open", Start.AddMinutes(5));

        Assert.False(_queue.TryTake(Start.AddMinutes(10), busy: true, out _, out var dropped));
        Assert.Equal(1, dropped);

        Assert.True(_queue.TryTake(Start.AddMinutes(11), busy: false, out var prompt, out dropped));
        Assert.Equal("still open", prompt);
        Assert.Equal(0, dropped);
    }

    [Fact]
    public void ClearForgetsWhatWasWaitingAndWhatWasOpen()
    {
        _queue.Add("first", Start);
        _queue.Add("second", Start);
        Assert.Equal("first", Take(0));
        _queue.Clear();
        Assert.Equal(0, _queue.Count);

        _queue.Add("next session", Start.AddSeconds(0.1));
        Assert.Equal("next session", Take(0.2));
    }
}
