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
        // A reflection-only adapter that patches no provider method has no method surface to pin. Its broad provider
        // dependency must instead remain a strict required fingerprint.
        var reader = Contract() with { Targets = [], TargetSurfaces = [], Requires = [new("fixture", "fixture.dll", "ABC", Strict: true)] };
        Assert.Equal(ActivationDecision.Activate, CompatibilityPlanner.Build(Request(), [Fingerprint], [], [reader]).Contracts[0].Decision);
    }

    [Fact] public void AProviderAndCapabilityCheckedCoopSurviveUnrelatedFileChangesButMustBePresent()
    {
        var contract = Contract() with
        {
            Requires = [new("fixture", "fixture.dll", "ABC", ExecutionSide.Client, Strict: false), new("coop", "Coop.Core.dll", "DEF", ExecutionSide.Client, Strict: false)],
            TargetSurfaces = [new("Fixture.Behavior::Tick", "HASH", 1)],
        };
        var coop = new InputFingerprint("coop", "Coop.Core.dll", "DEF", ExecutionSide.Client);
        Assert.Equal(ActivationDecision.Activate, CompatibilityPlanner.Build(Request(), [Fingerprint, coop], [], [contract]).Contracts[0].Decision);
        // The provider shipped an unrelated change: still planned, because the methods it patches are what is pinned.
        Assert.Equal(ActivationDecision.Activate, CompatibilityPlanner.Build(Request(), [Fingerprint with { Sha256 = "moved" }, coop], [], [contract]).Contracts[0].Decision);
        // A compatible Coop auto-update remains plannable. Runtime capability probes own its API support boundary.
        Assert.Equal(ActivationDecision.Activate, CompatibilityPlanner.Build(Request(), [Fingerprint, coop with { Sha256 = "moved" }], [], [contract]).Contracts[0].Decision);
        // Absent entirely is still a refusal, whether or not the file hash matters.
        Assert.Equal(ActivationDecision.Diagnostic, CompatibilityPlanner.Build(Request(), [coop], [], [contract]).Contracts[0].Decision);
    }

    [Fact] public void EveryShippedContractThatInstallsAnAdapterPinsTheMethodsItPatches()
    {
        var shipped = CompatibilityPlanner.BundledContracts();
        Assert.NotEmpty(shipped);
        foreach (var contract in shipped.Where(c => c.AdapterId != null))
        {
            Assert.True(contract.SurfacesCoverTargets, contract.Id + " installs an adapter without a surface for every patched target");
            Assert.All(contract.TargetSurfaces, s => Assert.Equal(64, s.BodyHash.Length));
            // Most patched methods must have an IL caller. Public UI Execute methods are invoked by Gauntlet data
            // binding, so zero assembly call sites is expected; their declaring UI type and body are still pinned.
            Assert.All(contract.TargetSurfaces, s => Assert.True(s.Callers > 0
                || (s.Method.StartsWith("BellumCivile.UI.", StringComparison.Ordinal)
                    && s.Method.Substring(s.Method.IndexOf("::", StringComparison.Ordinal) + 2).StartsWith("Execute", StringComparison.Ordinal)),
                s.Method + " records no call sites and is not a data-bound UI command"));
            // A reflection-only adapter has no method boundary, so its provider assembly must be pinned. Patching
            // adapters may also deliberately pin a provider (Bellum is version-pinned by policy); their method
            // surfaces still prove that the compiled target list is complete and make review changes explicit.
            if (contract.Targets.Length == 0) Assert.All(contract.Requires.Where(r => r.Module == contract.Module), r => Assert.True(r.Strict));
            Assert.All(contract.Requires.Where(r => r.Module == "CoopNightly"), r => Assert.False(r.Strict));
        }
    }
    [Fact] public void ExplicitValidationOverrideIsNarrowAndDoesNotChangeNormalPlanning()
    {
        var unvalidated = Contract(false) with { Id = "bellum-civile.state" };
        Assert.Equal(ActivationDecision.ValidationRequired, CompatibilityPlanner.Build(Request(), [Fingerprint], [], [unvalidated]).Contracts[0].Decision);
        Assert.Equal(ActivationDecision.Activate, CompatibilityPlanner.Build(Request(), [Fingerprint], [], [unvalidated],
            new HashSet<string>(StringComparer.Ordinal) { "bellum-civile.state" }).Contracts[0].Decision);
        var narrowed = CompatibilityPlanner.Build(Request(), [Fingerprint, new("unrelated", "other.dll", "DEF")],
            [new("fixture", "expected-validation-noise", "diagnostic")], [unvalidated],
            new HashSet<string>(StringComparer.Ordinal) { "bellum-civile.state" });
        Assert.Equal(ActivationDecision.Activate, narrowed.Contracts[0].Decision);
        Assert.Single(narrowed.Fingerprints);
        Assert.Equal("fixture.dll", narrowed.Fingerprints[0].Name);
        Assert.True(ModderLords.Operations.OperationValidationMode.Allows("bellum-civile.state", ModderLords.Operations.OperationValidationMode.Bellum131Token));
        Assert.True(ModderLords.Operations.OperationValidationMode.Allows("bellum-civile.authority", ModderLords.Operations.OperationValidationMode.Bellum131Token));
        Assert.True(ModderLords.Operations.OperationValidationMode.Allows("bellum-civile.commands", ModderLords.Operations.OperationValidationMode.Bellum131Token));
        Assert.False(ModderLords.Operations.OperationValidationMode.Allows("fixture", ModderLords.Operations.OperationValidationMode.Bellum131Token));
        Assert.False(ModderLords.Operations.OperationValidationMode.Allows("bellum-civile.state", "1"));
    }

    [Fact] public void BellumIsPinnedButCoopIsNotAndAllBellumTiersRemainDisabledPendingIntegration()
    {
        var bellum = CompatibilityPlanner.BundledContracts().Where(c => c.Module == "BellumCivile").ToArray();
        Assert.Equal(3, bellum.Length);
        Assert.All(bellum, c =>
        {
            Assert.False(c.OfflineValidated);
            Assert.False(c.RuntimeValidated);
            Assert.All(c.Requires.Where(r => r.Module == "BellumCivile"), r => Assert.True(r.Strict));
            Assert.All(c.Requires.Where(r => r.Module == "CoopNightly"), r => Assert.False(r.Strict));
        });
        var authority = Assert.Single(bellum, c => c.Id == "bellum-civile.authority");
        Assert.Equal(71, authority.Targets.Length);
        Assert.Equal(authority.Targets.Length, authority.TargetSurfaces.Length);
        Assert.DoesNotContain("BellumCivile.Behaviors.CouncilIncidentBehavior::OnTick", authority.Targets);
        Assert.DoesNotContain("BellumCivile.Behaviors.DynamicMercenaryBandBehavior::OnTick", authority.Targets);
        Assert.DoesNotContain("BellumCivile.Behaviors.ForeignTreatyBehavior::OnDailyTick", authority.Targets);
        Assert.DoesNotContain("BellumCivile.Behaviors.ForeignTreatyBehavior::OnTick", authority.Targets);
        Assert.DoesNotContain("BellumCivile.UI.Map.WarScoreMapWidgetVM::OnTick", authority.Targets);
        var commands = Assert.Single(bellum, c => c.Id == "bellum-civile.commands");
        Assert.Equal(14, commands.Targets.Length);
        Assert.Equal(commands.Targets.Length, commands.TargetSurfaces.Length);
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
