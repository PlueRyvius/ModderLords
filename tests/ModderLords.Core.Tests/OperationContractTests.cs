using System.Collections.Immutable;
using System.Text.Json;
using ModderLords.Analysis;
using ModderLords.Core.Profiles;
using ModderLords.Coop.Compat;

namespace ModderLords.Core.Tests;

public sealed class OperationContractTests
{
    private static readonly InputFingerprint Fingerprint = new("fixture", "fixture.dll", "ABC", ExecutionSide.Client);
    private static OperationContract Contract(bool verified = true, string provider = "ModderLords", bool suppressed = false) => new("fixture", "1", "fixture", "operation", provider,
        AuthorityDomain.Campaign, "authenticated actor", ["campaign-mutation"], "fixture", "before campaign", [new("fixture", "fixture.dll", "ABC")], ["Fixture.Behavior::Tick"], suppressed, verified, verified, "fixture");
    private static AnalysisRequest Request(bool auto = true, bool legacy = false) => new([new("fixture", "1", "unused", ["fixture.dll"], true, legacy ? ["Fixture.Behavior"] : [])], [], [], "v1.4.8", auto);
    [Fact] public void MatchingValidatedContractActivatesButUnknownOrUnvalidatedNeverDoes()
    {
        Assert.Equal(ActivationDecision.Activate, CompatibilityPlanner.Build(Request(), [Fingerprint], [], [Contract()]).Contracts[0].Decision);
        Assert.Equal(ActivationDecision.Diagnostic, CompatibilityPlanner.Build(Request(), [Fingerprint with { Sha256 = "changed" }], [], [Contract()]).Contracts[0].Decision);
        Assert.Equal(ActivationDecision.ValidationRequired, CompatibilityPlanner.Build(Request(), [Fingerprint], [], [Contract(false)]).Contracts[0].Decision);
        Assert.Equal(ActivationDecision.Diagnostic, CompatibilityPlanner.Build(Request(), [Fingerprint], [new("fixture", "unknown", "unresolved")], [Contract()]).Contracts[0].Decision);
        Assert.Equal(ActivationDecision.Disabled, CompatibilityPlanner.Build(Request(false), [Fingerprint], [], [Contract()]).Contracts[0].Decision);
    }
    [Fact] public void ExternalSuppressionAndLegacyOverlapAreDistinct()
    {
        Assert.Equal(ActivationDecision.Suppressed, CompatibilityPlanner.Build(Request(), [Fingerprint], [], [Contract(provider: "provider", suppressed: true)]).Contracts[0].Decision);
        Assert.Equal(ActivationDecision.RecognizedExternal, CompatibilityPlanner.Build(Request(), [Fingerprint], [], [Contract(provider: "provider")]).Contracts[0].Decision);
        Assert.Equal(ActivationDecision.Conflict, CompatibilityPlanner.Build(Request(legacy: true), [Fingerprint], [], [Contract()]).Contracts[0].Decision);
        var overlap = CompatibilityPlanner.Build(Request(), [Fingerprint], [], [Contract(), Contract(provider: "provider") with { Id = "external" }]);
        Assert.Equal(ActivationDecision.Conflict, overlap.Contracts[0].Decision);
        var managedOverlap = CompatibilityPlanner.Build(Request(), [Fingerprint], [], [Contract(), Contract() with { Id = "second" }]);
        Assert.All(managedOverlap.Contracts, c => Assert.Equal(ActivationDecision.Conflict, c.Decision));
    }
    [Fact] public void ChangedConfigurationAndOrderChangeDigest()
    {
        var original = CompatibilityPlanner.Build(Request(), [Fingerprint], [], [Contract()]);
        var changed = CompatibilityPlanner.Build(Request(), [Fingerprint, new("fixture", "settings.xml", "changed")], [], [Contract()]);
        Assert.NotEqual(original.Digest, changed.Digest);
        var request = Request() with { Modules = Request().Modules.Add(new("second", "1", "unused", [])) };
        Assert.NotEqual(CompatibilityPlanner.Build(request, [Fingerprint], []).Digest,
            CompatibilityPlanner.Build(request with { Modules = request.Modules.Reverse().ToImmutableArray() }, [Fingerprint], []).Digest);
    }
    [Fact] public void ProfileMigrationPreservesLegacyAndDefaultsAutomaticOn()
    {
        var p = JsonSerializer.Deserialize<Profile>("{\"Mods\":[{\"Id\":\"fixture\",\"ServerAuthoritative\":true,\"ClientSideBehaviors\":[\"Ui\"]}]}")!;
        Assert.True(p.AutomaticCompatibility); var copy = ProfileStore.Snapshot(p);
        Assert.True(copy.Mods[0].ServerAuthoritative); Assert.Equal("Ui", copy.Mods[0].ClientSideBehaviors[0]);
        p.AutomaticCompatibility = false; Assert.False(ProfileStore.Snapshot(p).AutomaticCompatibility);
    }
    [Fact] public void StagingAnotherPlanCannotModifyExistingSessionFile()
    {
        var root = Path.Combine(Path.GetTempPath(), "operation-plan-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var plan = CompatibilityPlanner.Build(Request(), [Fingerprint], [], [Contract()]);
            var one = OperationPreparation.Stage(plan, root); var before = File.ReadAllText(one);
            var two = OperationPreparation.Stage(plan with { Digest = "different" }, root);
            Assert.NotEqual(one, two); Assert.Equal(before, File.ReadAllText(one));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
