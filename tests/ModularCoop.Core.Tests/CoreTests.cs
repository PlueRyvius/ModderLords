using Bannerlord.ModuleManager;
using ModularCoop.Core.Launch;
using ModularCoop.Core.Logs;
using ModularCoop.Core.Modules;
using ModularCoop.Core.Overlay;
using Xunit;

namespace ModularCoop.Core.Tests;

public class ManifestRewriterTests
{
    private const string ClientOnlyMod = """
        <?xml version="1.0" encoding="utf-8"?>
        <Module>
          <Id value="HealOnKill"/>
          <Version value="v1.3.0"/>
          <SubModules>
            <SubModule>
              <Name value="Core"/>
              <DLLName value="HealOnKill.dll"/>
              <SubModuleClassType value="HealOnKill.HealOnKillSubModule"/>
              <Tags>
                <Tag key="DedicatedServerType" value="none"/>
                <Tag key="IsNoRenderModeElement" value="false"/>
                <Tag key="RejectedPlatform" value="Orbis"/>
              </Tags>
            </SubModule>
          </SubModules>
        </Module>
        """;

    [Fact]
    public void Run_strips_only_headless_exclusion_tags_and_keeps_identity()
    {
        var r = ManifestRewriter.Rewrite(ClientOnlyMod, ServerRole.Run);
        Assert.Contains("<Id value=\"HealOnKill\"", r.Xml);
        Assert.Contains("<Version value=\"v1.3.0\"", r.Xml);
        Assert.DoesNotContain("DedicatedServerType", r.Xml);
        Assert.DoesNotContain("IsNoRenderModeElement", r.Xml);
        Assert.Contains("RejectedPlatform", r.Xml);
        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"utf-8\"", r.Xml);
        Assert.Equal(2, r.Changes.Count);
    }

    [Fact]
    public void DependencyOnly_removes_submodules_except_allow_list()
    {
        var r = ManifestRewriter.Rewrite(ClientOnlyMod, ServerRole.DependencyOnly, ["Something.Else"]);
        Assert.DoesNotContain("<SubModule>", r.Xml);
        Assert.Contains("<Id value=\"HealOnKill\"", r.Xml);
        var kept = ManifestRewriter.Rewrite(ClientOnlyMod, ServerRole.DependencyOnly, ["HealOnKill.HealOnKillSubModule"]);
        Assert.Contains("HealOnKill.dll", kept.Xml);
        Assert.DoesNotContain("DedicatedServerType", kept.Xml);
    }

    [Fact]
    public void AsShipped_returns_original_text()
    {
        var r = ManifestRewriter.Rewrite(ClientOnlyMod, ServerRole.AsShipped);
        Assert.Equal(ClientOnlyMod, r.Xml);
        Assert.Empty(r.Changes);
    }
}

public class LaunchPlanTests
{
    [Fact]
    public void Builds_engine_token_and_official_arguments()
    {
        var paths = ServerPaths.Create(@"C:\srv", @"C:\data\DedicatedServer", @"C:\data");
        var plan = new LaunchPlan { Paths = paths, ModuleIds = ["Native", "CoopNightly"], SaveName = "my save", Password = "pw", Visibility = ServerVisibility.FriendsOnly };
        Assert.Equal("_MODULES_*Native*CoopNightly*_MODULES_", plan.ModuleToken);
        Assert.Equal(["TaleWorlds.Starter.DotNetCore.dll", "_MODULES_*Native*CoopNightly*_MODULES_", "/dedicatedcustomserver", "7210", "EU", "0", "/coopsave", "my save", "/cooppassword", "pw", "/coopvisibility", "friends_only"], plan.Arguments());
        var env = plan.Environment();
        Assert.Equal(@"C:\data\DedicatedServer", env["BANNERLORD_USER_DIR"]);
        Assert.Equal(@"C:\data", env["COOP_DATA_DIR"]);
        Assert.Equal("0", env["DOTNET_MULTILEVEL_LOOKUP"]);
        Assert.DoesNotContain("pw", plan.Describe());
    }
}

public class LoadOrderTests
{
    private static DiscoveredModule Mod(string id, ModuleSourceKind kind, string folder, params (string dep, bool optional)[] deps) =>
        new(id, "v1.0.0", folder, kind, new ModuleInfoExtended
        {
            Id = id, Name = id, Version = ApplicationVersion.TryParse("v1.0.0", out var v) ? v : ApplicationVersion.Empty,
            IsOfficial = kind == ModuleSourceKind.ServerStock,
            DependentModules = deps.Select(d => new DependentModule { Id = d.dep, IsOptional = d.optional }).ToList(),
        });

    [Fact]
    public void Official_host_order_with_community_between_sandbox_and_coop()
    {
        var stock = new[]
        {
            Mod("Native", ModuleSourceKind.ServerStock, @"S\Native"),
            Mod("SandBoxCore", ModuleSourceKind.ServerStock, @"S\SandBoxCore", ("Native", false)),
            Mod("Sandbox", ModuleSourceKind.ServerStock, @"S\SandBox", ("SandBoxCore", false)),
            Mod("CoopNightly", ModuleSourceKind.ServerStock, @"S\Coop", ("Sandbox", false)),
            Mod("DedicatedServer.Windows", ModuleSourceKind.ServerStock, @"S\DedicatedServer.Windows", ("Native", false)),
        };
        var community = new[]
        {
            Mod("ModularSmithing2", ModuleSourceKind.GameModules, @"G\ModularSmithing2", ("Sandbox", false), ("StoryMode", false)),
            Mod("Bannerlord.Harmony", ModuleSourceKind.GameModules, @"G\Harmony"),
            Mod("HealOnKill", ModuleSourceKind.GameModules, @"G\HealOnKill", ("Bannerlord.MBOptionScreen", false)),
            Mod("Bannerlord.MBOptionScreen", ModuleSourceKind.GameModules, @"G\MCM", ("Bannerlord.Harmony", false)),
        };
        var r = LoadOrder.Compute(stock, community);
        var ids0 = r.ModuleIds.ToList();
        Assert.True(ids0.IndexOf("Bannerlord.Harmony") < ids0.IndexOf("Native"));   // frameworks that say "load Native after me" come first
        Assert.True(ids0.IndexOf("Native") < ids0.IndexOf("SandBoxCore") && ids0.IndexOf("SandBoxCore") < ids0.IndexOf("Sandbox"));
        Assert.Equal("CoopNightly", r.ModuleIds[^2]);
        Assert.Equal("DedicatedServer.Windows", r.ModuleIds[^1]);
        var ids = r.ModuleIds.ToList();
        Assert.True(ids.IndexOf("Bannerlord.MBOptionScreen") < ids.IndexOf("HealOnKill"));
        Assert.Contains("ModularSmithing2", r.ModuleIds);           // StoryMode is a soft official dependency, never blocks
        Assert.DoesNotContain(r.Issues, i => i.Contains("StoryMode"));
    }
}

public class LogClassifierTests
{
    [Theory]
    [InlineData("[00:58:26.728] Messagebox [ERROR] message: Cannot load: Coop.Steam.dll", LogCategory.Probe)]
    [InlineData("[DedicatedServer] SERVING — coop server up, waiting for clients", LogCategory.Milestone)]
    [InlineData("[00:54:15.234] Loader Exceptions: Could not load file or assembly 'SandBox.View'", LogCategory.Error)]
    [InlineData("[DedicatedServer] FATAL during A: System.TypeInitializationException", LogCategory.Error)]
    [InlineData("[ModularCoop.Hook] resolved Serilog from X", LogCategory.Tool)]
    [InlineData("[00:40:28.394] LoadWithFullPath  subModulePath = ..\\..\\Modules\\Coop/SubModule.xml", LogCategory.ModuleLoad)]
    [InlineData("[00:40:25.940] Loading xml file: $BASE/Modules/Native/ModuleData/voices.xml.", LogCategory.Engine)]
    public void Classifies_representative_lines(string line, LogCategory expected)
        => Assert.Equal(expected, LogClassifier.Classify(line).Category);
}

public class ExitCodeTests
{
    [Fact]
    public void Documented_codes_have_specific_text()
    {
        Assert.Contains("Clean", ExitCodeExplainer.Explain(0));
        Assert.Contains("verification", ExitCodeExplainer.Explain(4));
        Assert.Contains("0xE0434352", ExitCodeExplainer.Explain(unchecked((int)0xE0434352)));
    }
}
