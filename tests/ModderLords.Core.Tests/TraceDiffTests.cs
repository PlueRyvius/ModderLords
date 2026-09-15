using ModderLords.Core.Compat.Authority;

namespace ModderLords.Core.Tests;

/// <summary>The expectation matrix behind trace-diff, one case per cell, plus the jsonl parser.</summary>
public sealed class TraceDiffTests
{
    private static RootVerdict V(string method, RootTrigger trigger, AuthorityVerdict verdict, IReadOnlyList<string>? gateInstead = null) =>
        new(new AuthorityRoot(method, trigger, "test"), verdict, "test", [], false, GateInstead: gateInstead);

    private static Dictionary<string, TraceRecordDto> Trace(params (string method, long ran, long skips)[] rows) =>
        rows.ToDictionary(r => r.method, r => new TraceRecordDto(r.method, r.ran, r.skips, 1, 2, 3), StringComparer.Ordinal);

    private static TraceJoin One(RootVerdict v, Dictionary<string, TraceRecordDto> server, Dictionary<string, TraceRecordDto> client) =>
        Assert.Single(TraceDiff.Compare(new AuthorityReport { ModuleId = "Mod", Roots = [v] }, server, client).Joins);

    [Fact]
    public void ServerOnlySimulation_RanOnServerOnly_IsExpected()
    {
        var j = One(V("M::Tick", RootTrigger.Simulation, AuthorityVerdict.ServerOnly), Trace(("M::Tick", 10, 0)), Trace(("M::Tick", 0, 10)));
        Assert.Equal(TraceOutcome.Expected, j.Outcome);
        Assert.Contains("gate skipped 10", j.Reason);
    }

    [Fact]
    public void ServerOnlySimulation_RanOnClient_IsContradicted()
    {
        var j = One(V("M::Tick", RootTrigger.Simulation, AuthorityVerdict.ServerOnly), Trace(("M::Tick", 10, 0)), Trace(("M::Tick", 4, 0)));
        Assert.Equal(TraceOutcome.FalseNegativeCandidate, j.Outcome);
        Assert.Contains("ran 4x on a client", j.Reason);
    }

    [Fact]
    public void ServerOnly_NeverRanOnServer_IsContradicted()
    {
        var j = One(V("M::Tick", RootTrigger.Simulation, AuthorityVerdict.ServerOnly), Trace(), Trace(("M::Tick", 0, 3)));
        Assert.Equal(TraceOutcome.FalseNegativeCandidate, j.Outcome);
        Assert.Contains("never ran there", j.Reason);
    }

    [Fact]
    public void GateInstead_JudgesTheGatedMethods_NotTheHandler()
    {
        var v = V("M::OnGameOpen", RootTrigger.Session, AuthorityVerdict.ServerOnly, ["M::SetupParties"]);
        var j = One(v, Trace(("M::OnGameOpen", 1, 0), ("M::SetupParties", 1, 0)), Trace(("M::OnGameOpen", 1, 0), ("M::SetupParties", 0, 1)));
        Assert.Equal(TraceOutcome.Expected, j.Outcome);   // the handler itself running on the client is fine
    }

    [Fact]
    public void NeedsRelay_RanOnClient_IsExpected_EvenWithoutServerRun()
    {
        var j = One(V("M::Buy", RootTrigger.PlayerInput, AuthorityVerdict.NeedsRelay), Trace(), Trace(("M::Buy", 2, 0)));
        Assert.Equal(TraceOutcome.Expected, j.Outcome);
    }

    [Fact]
    public void PlayerStateUnsynced_RanOnServer_IsContradicted()
    {
        var j = One(V("M::Set", RootTrigger.PlayerInput, AuthorityVerdict.PlayerStateUnsynced), Trace(("M::Set", 1, 0)), Trace(("M::Set", 1, 0)));
        Assert.Equal(TraceOutcome.FalseNegativeCandidate, j.Outcome);
    }

    [Fact]
    public void LeakingPostfix_StillFiringOnClient_IsContradicted()
    {
        var j = One(V("M::Postfix", RootTrigger.Patch, AuthorityVerdict.LeakingPostfix), Trace(("M::Postfix", 5, 0)), Trace(("M::Postfix", 5, 0)));
        Assert.Equal(TraceOutcome.FalseNegativeCandidate, j.Outcome);
        j = One(V("M::Postfix", RootTrigger.Patch, AuthorityVerdict.LeakingPostfix), Trace(("M::Postfix", 5, 0)), Trace());
        Assert.Equal(TraceOutcome.Expected, j.Outcome);
    }

    [Fact]
    public void Both_MustRunOnBothSides()
    {
        Assert.Equal(TraceOutcome.Expected, One(V("M::Wage", RootTrigger.Query, AuthorityVerdict.Both), Trace(("M::Wage", 9, 0)), Trace(("M::Wage", 9, 0))).Outcome);
        Assert.Equal(TraceOutcome.FalseNegativeCandidate, One(V("M::Wage", RootTrigger.Query, AuthorityVerdict.Both), Trace(("M::Wage", 9, 0)), Trace()).Outcome);
    }

    [Fact]
    public void Local_HasNoClaim_UnlessSimulationRunsOnClientsOnly()
    {
        Assert.Equal(TraceOutcome.Expected, One(V("M::Show", RootTrigger.Presentation, AuthorityVerdict.Local), Trace(), Trace(("M::Show", 3, 0))).Outcome);
        var j = One(V("M::Daily", RootTrigger.Simulation, AuthorityVerdict.Local), Trace(), Trace(("M::Daily", 3, 0)));
        Assert.Equal(TraceOutcome.FalseNegativeCandidate, j.Outcome);
        Assert.Contains("no verdict claim", j.Reason);
        Assert.Equal(TraceOutcome.Expected, One(V("M::Daily", RootTrigger.Simulation, AuthorityVerdict.Local), Trace(("M::Daily", 3, 0)), Trace(("M::Daily", 3, 0))).Outcome);
    }

    [Fact]
    public void NeverFired_IsUntestableForActionVerdicts_NeverExercisedOtherwise()
    {
        Assert.Equal(TraceOutcome.Untestable, One(V("M::Tick", RootTrigger.Simulation, AuthorityVerdict.ServerOnly), Trace(), Trace()).Outcome);
        Assert.Equal(TraceOutcome.NeverExercised, One(V("M::Tick", RootTrigger.Simulation, AuthorityVerdict.Review), Trace(), Trace()).Outcome);
    }

    [Fact]
    public void Totals_PerVerdict_AndUnmatchedTracedMethods()
    {
        var report = new AuthorityReport
        {
            ModuleId = "Mod",
            Roots =
            [
                V("M::A", RootTrigger.Simulation, AuthorityVerdict.ServerOnly),
                V("M::B", RootTrigger.Simulation, AuthorityVerdict.ServerOnly),
                V("M::C", RootTrigger.Simulation, AuthorityVerdict.ServerOnly),
                V("M::D", RootTrigger.Presentation, AuthorityVerdict.Local),
            ],
        };
        var diff = TraceDiff.Compare(report, Trace(("M::A", 1, 0), ("M::B", 1, 0), ("M::Stale", 1, 0)), Trace(("M::B", 2, 0)));
        var t = diff.Totals["ServerOnly"];
        Assert.Equal((1, 1, 1), (t.Expected, t.FalseNegativeCandidates, t.Untestable));
        Assert.Equal(0.5, t.Soundness);
        Assert.Equal(1, diff.Totals["Local"].NeverExercised);
        Assert.Equal(["M::Stale"], diff.Unmatched);
        Assert.Contains("soundness 50%", diff.Summary);
        Assert.Equal(TraceOutcome.Expected, diff.Joins[0].Outcome);   // sorted: expected, contradicted, untestable, never
        Assert.Contains("\"Outcome\": \"FalseNegativeCandidate\"", diff.ToJson());
    }

    [Fact]
    public void ParseTrace_LastLinePerMethodWins_AndSkipsJunk()
    {
        var parsed = TraceDiff.ParseTrace(
        [
            "{\"t\":30.0,\"method\":\"M::A\",\"ran\":3,\"gatedSkips\":0,\"firstSeen\":1.0,\"lastSeen\":20.0}",
            "not json",
            "",
            "{\"t\":60.0,\"method\":\"M::A\",\"ran\":7,\"gatedSkips\":1,\"firstSeen\":1.0,\"lastSeen\":55.5}",
        ]);
        var a = Assert.Single(parsed).Value;
        Assert.Equal((7L, 1L, 55.5, 60.0), (a.Ran, a.GatedSkips, a.LastSeen, a.T));
    }

    [Fact]
    public void ErrorBursts_CountCoopErrorsInTheWindowTheRootLastFiredIn()
    {
        var report = TraceDiff.Compare(new AuthorityReport { ModuleId = "Mod", Roots = [V("M::A", RootTrigger.Simulation, AuthorityVerdict.Both)] },
            Trace(("M::A", 1, 0)), Trace(("M::A", 1, 0)));
        var client = new Dictionary<string, TraceRecordDto> { ["M::A"] = new("M::A", 1, 0, 40, 70, 90) };   // last seen at 70 s → window 2 (60–90 s)
        var started = new TimeSpan(19, 0, 0);
        var annotated = TraceDiff.AnnotateErrorBursts(report,
        [
            "19:01:05.100 [ERR] ObjectManager: no such id",      // 65 s → window 2
            "19:01:20 [ERR] MobileParty: missing",              // 80 s → window 2
            "19:00:10 [ERR] early",                              // 10 s → window 0
            "19:01:10 [INF] fine",
        ], started, client);
        Assert.Equal(2, Assert.Single(annotated.Joins).ErrorsNearby);
    }
}
