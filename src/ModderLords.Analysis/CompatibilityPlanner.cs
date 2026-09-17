using System.Collections.Immutable;
using System.Text.Json;

namespace ModderLords.Analysis;

public static class CompatibilityPlanner
{
    public static ImmutableArray<OperationContract> BundledContracts()
    {
        using var stream = typeof(CompatibilityPlanner).Assembly.GetManifestResourceStream("ModderLords.Analysis.provider-contracts.json")!;
        return JsonSerializer.Deserialize<ImmutableArray<OperationContract>>(stream, AnalysisJson.Options);
    }
    public static CompatibilityPlan Build(AnalysisRequest request, ImmutableArray<InputFingerprint> fingerprints,
        ImmutableArray<CoverageGap> gaps, IEnumerable<OperationContract>? contracts = null)
    {
        var decisions = new List<ContractDecision>();
        foreach (var contract in contracts ?? BundledContracts())
        {
            var mod = request.Modules.FirstOrDefault(m => m.Id == contract.Module);
            if (mod == null) continue;
            var required = contract.Requires;
            var matches = required.Length > 0 && required.All(r =>
            {
                var observed = fingerprints.Where(f => f.Module == r.Module && f.Name == r.Name && (r.Side == null || f.Side == r.Side)).ToArray();
                return observed.Length > 0 && observed.All(f => f.Sha256.Equals(r.Sha256, StringComparison.OrdinalIgnoreCase));
            });
            var legacy = !mod.LegacyGatedTypes.IsDefaultOrEmpty && contract.Targets.Any(t => mod.LegacyGatedTypes.Any(l => t.StartsWith(l + "::", StringComparison.Ordinal)));
            if (legacy) decisions.Add(new(contract, ActivationDecision.Conflict, "Explicit legacy behavior gate overlaps this operation; legacy configuration retained."));
            else if (!matches) decisions.Add(new(contract, ActivationDecision.Diagnostic, "Required binary fingerprints do not all match; coverage is unverified."));
            else if (contract.Provider != "ModderLords") decisions.Add(new(contract, contract.Suppressed ? ActivationDecision.Suppressed : ActivationDecision.RecognizedExternal,
                contract.Suppressed ? "The matching external provider suppresses this feature." : "External implementation recognized; runtime installation is not verified."));
            else if (!request.AutomaticCompatibility) decisions.Add(new(contract, ActivationDecision.Disabled, "Automatic compatibility is disabled for this profile."));
            else if (!contract.OfflineValidated || !contract.RuntimeValidated) decisions.Add(new(contract, ActivationDecision.ValidationRequired, "Contract is implemented but has not completed its required offline and integration validation."));
            else if (gaps.Length > 0) decisions.Add(new(contract, ActivationDecision.Diagnostic, "The selected execution environment has unresolved coverage gaps."));
            else decisions.Add(new(contract, ActivationDecision.Activate, "Compiled adapter and exact inputs have a validated contract."));
        }
        // No two managed contracts may own the same target. A provider's claim wins over a new adapter.
        var initialDecisions = decisions.ToArray();
        for (var i = 0; i < decisions.Count; i++)
            if (initialDecisions[i].Decision == ActivationDecision.Activate && initialDecisions.Where((_, j) => j != i).Any(d =>
                d.Decision is ActivationDecision.Activate or ActivationDecision.RecognizedExternal or ActivationDecision.Suppressed &&
                d.Contract.Targets.Intersect(decisions[i].Contract.Targets).Any()))
                decisions[i] = decisions[i] with { Decision = ActivationDecision.Conflict, Reason = "Another matching provider or contract owns the target." };
        var immutable = decisions.ToImmutableArray();
        var contextDigest = AnalysisJson.Hash(JsonSerializer.Serialize(new { Rules = OperationAnalyzer.RulesVersion,
            Order = request.Modules.Select(m => new { m.Id, m.Version, m.RunsOnServer }),
            request.AutomaticCompatibility, Fingerprints = fingerprints, Decisions = immutable }, AnalysisJson.Options));
        var plan = new CompatibilityPlan("", immutable, fingerprints, contextDigest, request.Modules.Select(m => m.Id).ToImmutableArray());
        var json = ModderLords.Operations.PlanIntegrity.Parse(JsonSerializer.Serialize(plan, AnalysisJson.Options));
        return plan with { Digest = ModderLords.Operations.PlanIntegrity.Compute(json) };
    }
}
