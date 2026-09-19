using ModderLords.Core.Overlay;

namespace ModderLords.Core.Tests;

/// <summary>
/// The silent failure of 2026-09-19: RBM ships DedicatedServerType="none", was left As-shipped, and its code never
/// ran on the server for six launches - while the generated save header listed the module the whole time.
/// </summary>
public sealed class HeadlessExclusionTests
{
    private static string Manifest(params string[] serverTypes) =>
        "<Module><Id value=\"X\" /><SubModules>" + string.Concat(serverTypes.Select(t =>
            "<SubModule><Name value=\"S\" /><DLLName value=\"S.dll\" />" +
            (t is null ? "" : $"<Tags><Tag key=\"DedicatedServerType\" value=\"{t}\" /></Tags>") +
            "</SubModule>")) + "</SubModules></Module>";

    [Fact]
    public void NoneMeansExcluded() =>
        Assert.True(ManifestRewriter.IsExcludedFromDedicatedServer(Manifest("none")));

    [Fact]
    public void EverySubModuleMustBeExcludedBeforeTheModIs() =>
        Assert.False(ManifestRewriter.IsExcludedFromDedicatedServer(Manifest("none", "custom")));

    [Fact]
    public void AllExcludedCounts() =>
        Assert.True(ManifestRewriter.IsExcludedFromDedicatedServer(Manifest("none", "none")));

    [Fact]
    public void NoTagIsNotAnExclusion() =>
        Assert.False(ManifestRewriter.IsExcludedFromDedicatedServer("<Module><Id value=\"X\" /><SubModules>" +
            "<SubModule><Name value=\"S\" /><DLLName value=\"S.dll\" /></SubModule></SubModules></Module>"));

    [Fact]
    public void NoSubModulesAtAllIsNotAnExclusion() =>
        Assert.False(ManifestRewriter.IsExcludedFromDedicatedServer("<Module><Id value=\"X\" /><SubModules /></Module>"));

    /// <summary>Reporting a parse failure belongs to the module scanner; this check must not guess or throw.</summary>
    [Fact]
    public void AnUnparseableManifestIsNotAnExclusion() =>
        Assert.False(ManifestRewriter.IsExcludedFromDedicatedServer("<Module><SubModules>"));

    /// <summary>Run is the documented escape hatch, and must actually strip the tag.</summary>
    [Fact]
    public void RunStripsTheExclusionSoTheEngineLoadsIt()
    {
        var rewritten = ManifestRewriter.Rewrite(Manifest("none"), ServerRole.Run).Xml;
        Assert.False(ManifestRewriter.IsExcludedFromDedicatedServer(rewritten));
    }

    /// <summary>AsShipped passes the manifest straight through - which is exactly why the warning has to exist.</summary>
    [Fact]
    public void AsShippedLeavesTheExclusionInPlace()
    {
        var rewritten = ManifestRewriter.Rewrite(Manifest("none"), ServerRole.AsShipped).Xml;
        Assert.True(ManifestRewriter.IsExcludedFromDedicatedServer(rewritten));
    }
}
