using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ModderLords.Analysis;

public enum ExecutionSide { Client, Server }
public enum AuthorityDomain { Unknown, LocalPresentation, Campaign, Mission, SharedCalculation, Mixed }
public enum CapabilityState { Unknown, Observed, RecognizedExternal, Suppressed, Incomplete, Conflicting, Unsupported, Verified, Pending }
public enum ActivationDecision { Diagnostic, RecognizedExternal, Suppressed, Activate, Conflict, ValidationRequired, Disabled }
public sealed record AnalysisModule(string Id, string Version, string Folder, ImmutableArray<string> EntryDlls,
    bool RunsOnServer = true, ImmutableArray<string> LegacyGatedTypes = default, string? ServerFolder = null);
public sealed record AnalysisRequest(ImmutableArray<AnalysisModule> Modules, ImmutableArray<string> ClientSearchPaths,
    ImmutableArray<string> ServerSearchPaths, string GameVersion, bool AutomaticCompatibility = true);
public sealed record InputFingerprint(string Module, string Name, string Sha256, ExecutionSide? Side = null);
public sealed record LocalInputFile(InputFingerprint Fingerprint, string Path);
public sealed record CoverageGap(string Module, string Code, string Detail);
public sealed record EffectWitness(string Effect, ImmutableArray<string> CallChain, string MethodIdentity, int IlOffset, string Detail);
public sealed record CapabilityStatus(CapabilityState Loading, CapabilityState Authority, CapabilityState Interaction,
    CapabilityState Replication, CapabilityState OfflineVerification, CapabilityState RuntimeVerification);
public sealed record OperationFinding(string Module, ExecutionSide Side, string Id, string EntryPoint, AuthorityDomain Authority,
    ImmutableArray<string> Effects, ImmutableArray<EffectWitness> Evidence, CapabilityStatus Status);
public sealed record RequiredFingerprint(string Module, string Name, string Sha256, ExecutionSide? Side = null);
public sealed record OperationContract(string Id, string Version, string Module, string Operation, string Provider,
    AuthorityDomain Authority, string ActorBinding, ImmutableArray<string> Effects, string Transport, string Lifecycle,
    ImmutableArray<RequiredFingerprint> Requires, ImmutableArray<string> Targets, bool Suppressed = false,
    bool OfflineValidated = false, bool RuntimeValidated = false, string? AdapterId = null);
public sealed record ContractDecision(OperationContract Contract, ActivationDecision Decision, string Reason);
public sealed record CompatibilityPlan(string Digest, ImmutableArray<ContractDecision> Contracts, ImmutableArray<InputFingerprint> Fingerprints, string ContextDigest = "",
    ImmutableArray<string> ModuleOrder = default)
{
    public bool RequiresRuntime => Contracts.Any(c => c.Decision == ActivationDecision.Activate);
}
public sealed record AnalysisReport(string RulesVersion, string InputDigest, ImmutableArray<InputFingerprint> Fingerprints,
    ImmutableArray<CoverageGap> Gaps, ImmutableArray<OperationFinding> Operations, CompatibilityPlan Plan)
{
    [JsonIgnore] public ImmutableArray<LocalInputFile> LocalFiles { get; init; } = [];
    public string Summary => $"{Operations.Length} operations; {Gaps.Length} coverage gaps; " +
        $"{Plan.Contracts.Count(c => c.Decision == ActivationDecision.RecognizedExternal)} externally provided; " +
        $"{Plan.Contracts.Count(c => c.Decision == ActivationDecision.Activate)} automatic";
}
public static class AnalysisJson
{
    public static readonly JsonSerializerOptions Options = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    public static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    public static string FileHash(string path) { using var s = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(s)); }
}
