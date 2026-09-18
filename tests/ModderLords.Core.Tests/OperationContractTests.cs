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
        AuthorityDomain.Campaign, "authenticated actor", ["campaign-mutation"], "fixture", "before campaign", [new("fixture", "fixture.dll", "ABC")], ["Fixture.Behavior::Tick"], suppressed, verified, verified, "fixture",
        [new("Fixture.Behavior::Tick", "HASH", 1)]);
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
    [Fact] public void AnAdapterContractWithoutASurfaceForEveryTargetCannotActivate()
    {
        Assert.Equal(ActivationDecision.Activate, CompatibilityPlanner.Build(Request(), [Fingerprint], [], [Contract()]).Contracts[0].Decision);
        // An adapter is compiled code patching a named method. With no surface captured for it there is nothing for
        // the runtime to re-check, so the file hash would be the only guard — which is what this replaces.
        var unpinned = Contract() with { TargetSurfaces = [] };
        Assert.Equal(ActivationDecision.ValidationRequired, CompatibilityPlanner.Build(Request(), [Fingerprint], [], [unpinned]).Contracts[0].Decision);
        var partial = Contract() with { Targets = ["Fixture.Behavior::Tick", "Fixture.Behavior::Other"], TargetSurfaces = [new("Fixture.Behavior::Tick", "HASH", 1)] };
        Assert.Equal(ActivationDecision.ValidationRequired, CompatibilityPlanner.Build(Request(), [Fingerprint], [], [partial]).Contracts[0].Decision);
        // A contract that installs nothing describes someone else's implementation, so it pins no surfaces.
        Assert.Equal(ActivationDecision.RecognizedExternal, CompatibilityPlanner.Build(Request(), [Fingerprint], [], [Contract(provider: "provider") with { AdapterId = null }]).Contracts[0].Decision);
    }

    [Fact] public void AProviderPinnedByItsSurfacesSurvivesAnUnrelatedUpdateButCoopDoesNot()
    {
        var contract = Contract() with
        {
            Requires = [new("fixture", "fixture.dll", "ABC", ExecutionSide.Client, Strict: false), new("coop", "Coop.Core.dll", "DEF", ExecutionSide.Client)],
            TargetSurfaces = [new("Fixture.Behavior::Tick", "HASH", 1)],
        };
        var coop = new InputFingerprint("coop", "Coop.Core.dll", "DEF", ExecutionSide.Client);
        Assert.Equal(ActivationDecision.Activate, CompatibilityPlanner.Build(Request(), [Fingerprint, coop], [], [contract]).Contracts[0].Decision);
        // The provider shipped an unrelated change: still planned, because the methods it patches are what is pinned.
        Assert.Equal(ActivationDecision.Activate, CompatibilityPlanner.Build(Request(), [Fingerprint with { Sha256 = "moved" }, coop], [], [contract]).Contracts[0].Decision);
        // Coop moved: refused, because an adapter reaches across that assembly too widely for methods to stand in.
        Assert.Equal(ActivationDecision.Diagnostic, CompatibilityPlanner.Build(Request(), [Fingerprint, coop with { Sha256 = "moved" }], [], [contract]).Contracts[0].Decision);
        // Absent entirely is still a refusal, whether or not the file hash matters.
        Assert.Equal(ActivationDecision.Diagnostic, CompatibilityPlanner.Build(Request(), [coop], [], [contract]).Contracts[0].Decision);
    }

    [Fact] public void EveryShippedContractThatInstallsAnAdapterPinsTheMethodsItPatches()
    {
        var shipped = CompatibilityPlanner.BundledContracts();
        Assert.NotEmpty(shipped);
        foreach (var contract in shipped.Where(c => c.AdapterId != null))
        {
            Assert.True(contract.SurfacesCoverTargets, contract.Id + " installs an adapter without a surface for every target");
            Assert.All(contract.TargetSurfaces, s => Assert.Equal(64, s.BodyHash.Length));
            // A recorded caller count is what makes a NEW caller appearing a refusal rather than a surprise.
            Assert.All(contract.TargetSurfaces, s => Assert.True(s.Callers > 0, s.Method + " records no call sites"));
            // The provider's own assembly is pinned by those surfaces; Coop's is still pinned by file.
            Assert.All(contract.Requires.Where(r => r.Module == contract.Module), r => Assert.False(r.Strict));
            Assert.All(contract.Requires.Where(r => r.Module != contract.Module), r => Assert.True(r.Strict));
        }
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
