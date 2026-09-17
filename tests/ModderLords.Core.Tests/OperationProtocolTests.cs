using ModderLords.Operations;

namespace ModderLords.Core.Tests;

public sealed class OperationProtocolTests
{
    private sealed class Counter : IServerOperation
    {
        public string Id => "fixture.counter";
        public int MaxPayloadBytes => 16;
        public int Value;
        public bool Allowed = true;
        public bool Fail;
        public bool Validate(Actor actor, string payload, out string reason) { reason = "Not the owner"; return Allowed && actor.ClanId == "owned" && payload == "increment"; }
        public string Execute(Actor actor, string payload) { if (Fail) throw new InvalidOperationException(); return (++Value).ToString(); }
    }
    private static readonly Actor Player = new("controller", "hero", "owned");
    private static OperationCommand Command(string? id = null, string payload = "increment", string epoch = "epoch") => new("digest", epoch, id ?? Guid.NewGuid().ToString("N"), "fixture.counter", payload);

    [Fact] public void CommandsAreAuthenticatedBoundedAndDeduplicated()
    {
        var op = new Counter(); var host = new CommandDispatcher("digest", "epoch", [op]); var command = Command();
        Assert.Equal(RequestState.Rejected, host.Execute(command, null).State);
        Assert.Equal(RequestState.Rejected, host.Execute(Command(payload: new string('x', 17)), Player).State);
        Assert.Equal(RequestState.Rejected, host.Execute(Command(epoch: "stale"), Player).State);
        var first = host.Execute(command, Player);
        Assert.Equal(RequestState.Completed, first.State);
        Assert.Same(first, host.Execute(command, Player));
        Assert.Equal(1, op.Value);
        Assert.Equal(RequestState.Rejected, host.Execute(Command(command.RequestId, "different"), Player).State);
    }
    [Fact] public void OwnershipIsRevalidatedAndFailureIsNotRetried()
    {
        var op = new Counter(); var host = new CommandDispatcher("digest", "epoch", [op]);
        Assert.Equal(RequestState.Rejected, host.Execute(Command(), new Actor("controller", "hero", "other-clan")).State);
        op.Fail = true; var request = Command(); var result = host.Execute(request, Player); op.Fail = false;
        Assert.Equal(RequestState.Rejected, result.State); Assert.Same(result, host.Execute(request, Player)); Assert.Equal(0, op.Value);
    }
    [Fact] public void ReceiptCapacityNeverEvictsAndReexecutesOldMutations()
    {
        var op = new Counter(); var host = new CommandDispatcher("digest", "epoch", [op], 1); var first = Command();
        host.Execute(first, Player); Assert.Equal(RequestState.Rejected, host.Execute(Command(), Player).State);
        Assert.Equal(RequestState.Completed, host.Execute(first, Player).State); Assert.Equal(1, op.Value);
    }
    [Fact] public void SubmissionIsPendingUntilCorrelatedResultAndDisconnectDoesNotCompleteIt()
    {
        var client = new ClientOperationState(); client.BeginSession("epoch");
        Assert.True(client.BeginRequest("one")); Assert.Equal(RequestState.Pending, client.State("one"));
        Assert.False(client.AcceptResult("other", new OperationResult("one", RequestState.Completed, "")));
        client.Disconnect(); Assert.Equal(RequestState.Disconnected, client.State("one"));
        Assert.False(client.AcceptResult("epoch", new OperationResult("one", RequestState.Completed, "")));
    }
    [Fact] public void SnapshotsDeferDiscardOlderAndDoNotEchoCommands()
    {
        var client = new ClientOperationState(); client.BeginSession("epoch"); var ready = false; var value = ""; var refreshed = 0;
        void Apply(string state) { value = state; Assert.False(client.BeginRequest("echo")); }
        Assert.False(client.OfferSnapshot("epoch", 2, "new", () => ready, Apply, () => refreshed++));
        Assert.False(client.OfferSnapshot("epoch", 1, "old", () => true, Apply, () => refreshed++));
        ready = true; Assert.True(client.ApplyPending(() => ready, Apply, () => refreshed++));
        Assert.Equal("new", value); Assert.Equal(1, refreshed);
        Assert.False(client.OfferSnapshot("epoch", 2, "duplicate", () => ready, Apply, () => refreshed++));
        client.BeginSession("reconnected");
        Assert.False(client.OfferSnapshot("epoch", 999, "stale session", () => true, Apply, () => refreshed++));
        Assert.True(client.OfferSnapshot("reconnected", 0, "bootstrap", () => true, Apply, () => refreshed++));
    }
    [Fact] public void CampaignStartAndDigestFreezePreventLateOrReplacedPlans()
    {
        var session = new SessionActivation(); Assert.True(session.Freeze("first")); session.MarkCampaignStarted();
        Assert.True(session.Freeze("first")); Assert.False(session.Freeze("second")); Assert.False(session.Admit("other"));
        var late = new SessionActivation(); late.MarkCampaignStarted(); Assert.False(late.Freeze("first"));
    }
    [Fact] public void ResourcePolicyIncludesDisconnectedOwnersAndPreservesSinglePlayer()
    {
        var policy = new ResourceAdderPolicy();
        Assert.False(policy.MayRun(true, true)); Assert.True(policy.MayRun(false, false));
        Assert.True(policy.UpdateOwners(["online-clan", "offline-clan"]));
        Assert.False(policy.IsAiClan("online-clan", true, true)); Assert.False(policy.IsAiClan("offline-clan", true, true));
        Assert.True(policy.IsAiClan("ai", true, false)); Assert.False(policy.MayRun(true, false));
        Assert.True(policy.IsAiClan("offline-clan", false, true));
        Assert.False(policy.UpdateOwners(["online-clan", null])); Assert.False(policy.MayRun(true, true));
        Assert.False(policy.UpdateOwners([]));
    }
    [Fact] public void TwoPeerFixtureRoundTripReconcilesReconnectWithoutDuplicateMutation()
    {
        var op = new Counter(); var host = new CommandDispatcher("digest", "epoch", [op]);
        var first = new ClientOperationState(); var second = new ClientOperationState(); first.BeginSession("epoch"); second.BeginSession("epoch");
        var command = Command(); Assert.True(first.BeginRequest(command.RequestId));
        var result = host.Execute(command, Player); Assert.True(first.AcceptResult("epoch", result));
        var a = ""; var b = "";
        Assert.True(first.OfferSnapshot("epoch", result.Revision, result.Payload, () => true, s => a = s, () => { }));
        Assert.True(second.OfferSnapshot("epoch", result.Revision, result.Payload, () => true, s => b = s, () => { }));
        first.Disconnect(); first.BeginSession("epoch");
        Assert.Equal("1", host.Execute(command, Player).Payload); Assert.Equal(1, op.Value); Assert.Equal(a, b);
    }
}
