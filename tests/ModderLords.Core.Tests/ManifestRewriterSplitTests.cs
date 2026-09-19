using ModderLords.Core.Overlay;

namespace ModderLords.Core.Tests;

/// <summary>
/// Run must honour a module that already split itself into client and server submodules, rather than flattening the
/// split by stripping every headless-exclusion tag.
///
/// Regression cover for FamilyAppearanceEditor taking the server down with a native access violation on 2026-09-19:
/// it ships a Client submodule (DedicatedServerType=none, full of Gauntlet/ViewModel code) and a Dedicated Server
/// submodule (DedicatedServerType=custom). Run stripped both tags, so the server loaded the client half and died
/// Harmony-patching barber screens headless. CoopMarriage has the same shape and had been quietly doing the same.
/// </summary>
public class ManifestRewriterSplitTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _out;
    public ManifestRewriterSplitTests(Xunit.Abstractions.ITestOutputHelper output) => _out = output;

    private const string SplitManifest = """
<?xml version="1.0" encoding="utf-8"?>
<Module>
  <Name value="Split" />
  <Id value="Split" />
  <Version value="v1.0.0" />
  <SubModules>
    <SubModule>
      <Name value="Split (Client)" />
      <DLLName value="Split.dll" />
      <SubModuleClassType value="Split.ClientSubModule" />
      <Tags>
        <Tag key="DedicatedServerType" value="none" />
        <Tag key="IsNoRenderModeElement" value="false" />
      </Tags>
    </SubModule>
    <SubModule>
      <Name value="Split (Dedicated Server)" />
      <DLLName value="Split.dll" />
      <SubModuleClassType value="Split.DedicatedServerSubModule" />
      <Tags>
        <Tag key="DedicatedServerType" value="custom" />
        <Tag key="IsNoRenderModeElement" value="true" />
      </Tags>
    </SubModule>
  </SubModules>
</Module>
""";

    private const string ClientOnlyManifest = """
<?xml version="1.0" encoding="utf-8"?>
<Module>
  <Name value="Plain" />
  <Id value="Plain" />
  <Version value="v1.0.0" />
  <SubModules>
    <SubModule>
      <Name value="Plain" />
      <DLLName value="Plain.dll" />
      <SubModuleClassType value="Plain.SubModule" />
      <Tags>
        <Tag key="DedicatedServerType" value="none" />
        <Tag key="IsNoRenderModeElement" value="false" />
      </Tags>
    </SubModule>
  </SubModules>
</Module>
""";

    [Fact]
    public void Run_drops_the_client_half_when_the_author_shipped_a_server_half()
    {
        var r = ManifestRewriter.Rewrite(SplitManifest, ServerRole.Run);

        Assert.DoesNotContain("Split.ClientSubModule", r.Xml);
        Assert.Contains("Split.DedicatedServerSubModule", r.Xml);
        Assert.Contains(r.Changes, c => c.Contains("removed client-only submodule") && c.Contains("Split (Client)"));
    }

    [Fact]
    public void Run_still_strips_the_server_halfs_own_tags_so_the_engine_does_not_skip_it()
    {
        // DedicatedServerType=custom is what the engine evaluates; the overlay's job is to make sure the submodule
        // that survives is actually loaded rather than skipped for some other tag.
        var r = ManifestRewriter.Rewrite(SplitManifest, ServerRole.Run);
        Assert.DoesNotContain("IsNoRenderModeElement", r.Xml);
        Assert.DoesNotContain("DedicatedServerType", r.Xml);
    }

    [Fact]
    public void Run_on_a_module_with_no_server_half_behaves_exactly_as_before()
    {
        // The case Run was designed for: a mod that wrongly says "not on a server" and has no alternative to offer.
        // Nothing may change here, or every single-submodule community mod stops loading.
        var r = ManifestRewriter.Rewrite(ClientOnlyManifest, ServerRole.Run);

        Assert.Contains("Plain.SubModule", r.Xml);
        Assert.DoesNotContain("DedicatedServerType", r.Xml);
        Assert.Contains(r.Changes, c => c.Contains("stripped tag DedicatedServerType=none"));
        Assert.DoesNotContain(r.Changes, c => c.Contains("removed client-only submodule"));
    }

    [Fact]
    public void AsShipped_is_untouched_by_any_of_this()
    {
        var r = ManifestRewriter.Rewrite(SplitManifest, ServerRole.AsShipped);
        Assert.Equal(SplitManifest, r.Xml);
        Assert.Empty(r.Changes);
    }

    [Fact]
    public void DependencyOnly_still_removes_both_halves()
    {
        var r = ManifestRewriter.Rewrite(SplitManifest, ServerRole.DependencyOnly);
        Assert.DoesNotContain("Split.ClientSubModule", r.Xml);
        Assert.DoesNotContain("Split.DedicatedServerSubModule", r.Xml);
        // Id and Version must survive: Coop's ModuleValidator compares exactly those across the handshake.
        Assert.Contains("value=\"Split\"", r.Xml);
        Assert.Contains("v1.0.0", r.Xml);
    }

    [Fact]
    public void The_real_split_mods_installed_here_keep_only_their_server_half()
    {
        // Fixtures prove the rule; these prove it against the manifests that actually caused the crash. Skips when
        // the mods are not installed, like the other machine-dependent tests in this suite.
        var mods = InstalledMods.Find();
        if (mods is null) { _out.WriteLine("no game; skipped"); return; }

        var seen = 0;
        foreach (var id in new[] { "FamilyAppearanceEditor", "CoopMarriage" })
        {
            if (!mods.TryGetValue(id, out var mod)) { _out.WriteLine($"{id}: not installed"); continue; }
            var manifest = Path.Combine(mod.FolderPath, "SubModule.xml");
            if (!File.Exists(manifest)) { _out.WriteLine($"{id}: no SubModule.xml"); continue; }

            var r = ManifestRewriter.Rewrite(File.ReadAllText(manifest), ServerRole.Run);
            _out.WriteLine($"{id}:");
            foreach (var c in r.Changes) _out.WriteLine("   " + c);

            // Both of these ship a Client/Dedicated Server pair, so Run must now drop the client half.
            Assert.Contains(r.Changes, c => c.Contains("removed client-only submodule"));
            Assert.DoesNotContain("DedicatedServerType\" value=\"none", r.Xml);
            seen++;
        }
        if (seen == 0) _out.WriteLine("neither mod installed; nothing asserted");
    }
}
