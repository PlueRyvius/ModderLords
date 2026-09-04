using Bannerlord.ModuleManager;
using ModularCoop.Core.Export;
using ModularCoop.Core.Launch;
using ModularCoop.Core.Logs;
using ModularCoop.Core.Modules;
using ModularCoop.Core.Saves;
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
            new DiscoveredModule("Bannerlord.Harmony", "v1.0.0", @"G\Harmony", ModuleSourceKind.GameModules, new ModuleInfoExtended
            {
                Id = "Bannerlord.Harmony", Name = "Harmony", Version = ApplicationVersion.TryParse("v1.0.0", out var hv) ? hv : ApplicationVersion.Empty,
                ModulesToLoadAfterThis = [new DependentModule { Id = "Native" }],   // as the real manifest declares
            }),
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

public class ClientLauncherTests
{
    private static string MakeGameRoot(params string[] exeNames)
    {
        var root = Path.Combine(Path.GetTempPath(), "mc-client-" + Guid.NewGuid().ToString("N"));
        var bin = ClientLauncher.ClientBin(root);
        Directory.CreateDirectory(bin);
        foreach (var name in exeNames) File.WriteAllText(Path.Combine(bin, name), "");
        return root;
    }

    [Fact]
    public void Prefers_the_taleworlds_launcher_so_the_player_still_picks_modules()
    {
        var root = MakeGameRoot(ClientLauncher.LauncherExeName, ClientLauncher.GameExeName);
        try
        {
            Assert.Equal(Path.Combine(ClientLauncher.ClientBin(root), ClientLauncher.LauncherExeName), ClientLauncher.FindExe(root));
            Assert.Equal(Path.Combine(ClientLauncher.ClientBin(root), ClientLauncher.GameExeName), ClientLauncher.FindExe(root, skipLauncher: true));
        }
        finally { Directory.Delete(root, true); }
    }

    /// <summary>
    /// Bannerlord.exe is the STARTER, not the launcher: run bare it loads the default modules, because the game
    /// takes its list from the _MODULES_ argument the launcher builds and never reads LauncherData.xml. Starting it
    /// dropped every mod including Coop, so the launcher UI must stay the preferred exe.
    /// </summary>
    [Fact]
    public void The_preferred_exe_is_the_launcher_ui_not_the_game_starter()
    {
        Assert.Equal("TaleWorlds.MountAndBlade.Launcher.exe", ClientLauncher.LauncherExeName);
        Assert.Equal("Bannerlord.exe", ClientLauncher.GameExeName);
        var root = MakeGameRoot(ClientLauncher.LauncherExeName, ClientLauncher.GameExeName);
        try { Assert.EndsWith(ClientLauncher.LauncherExeName, ClientLauncher.FindExe(root)); }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Falls_back_to_whichever_exe_exists()
    {
        var root = MakeGameRoot(ClientLauncher.GameExeName);
        try { Assert.Equal(Path.Combine(ClientLauncher.ClientBin(root), ClientLauncher.GameExeName), ClientLauncher.FindExe(root)); }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Missing_install_or_exe_resolves_to_null()
    {
        Assert.Null(ClientLauncher.FindExe(null));
        Assert.Null(ClientLauncher.FindExe("   "));
        var root = MakeGameRoot();
        try { Assert.Null(ClientLauncher.FindExe(root)); }
        finally { Directory.Delete(root, true); }
    }
}

public class ClientManifestTests
{
    /// <summary>
    /// The Coop module id is matched exactly, not by prefix: CoopModPatch is an ordinary community mod, and a
    /// prefix test used to exempt it silently, so a client running it against a server that does not would be
    /// reported as fine and then rejected at the join screen.
    /// </summary>
    [Fact]
    public void CoopModPatch_on_the_client_only_is_reported_as_an_extra()
    {
        var path = Path.Combine(Path.GetTempPath(), "mc-cm-" + Guid.NewGuid().ToString("N") + ".xml");
        File.WriteAllText(path,
            "<?xml version=\"1.0\" encoding=\"utf-8\"?><UserData><SingleplayerData><ModDatas>" +
            "<UserModData><Id>CoopNightly</Id><LastKnownVersion>v0.1.4</LastKnownVersion><IsSelected>true</IsSelected></UserModData>" +
            "<UserModData><Id>CoopModPatch</Id><LastKnownVersion>v1.0</LastKnownVersion><IsSelected>true</IsSelected></UserModData>" +
            "<UserModData><Id>StoryMode</Id><LastKnownVersion>v1.4.8</LastKnownVersion><IsSelected>true</IsSelected></UserModData>" +
            "</ModDatas></SingleplayerData></UserData>");
        try
        {
            var checks = ClientManifest.CompareWithLauncherData([], path);
            Assert.Contains(checks, c => c.Id == "CoopModPatch" && c.Verdict == "enabled on client but not on server");
            Assert.DoesNotContain(checks, c => c.Id is "CoopNightly" or "StoryMode");
        }
        finally { File.Delete(path); }
    }
}

public class VersionOrderTests
{
    [Theory]
    [InlineData("v0.1.0", "v0.1.0.0", 0)]      // three components vs four: the same version
    [InlineData("v0.2.0", "v0.1.9", 1)]
    [InlineData("v0.1.0", "v0.1.1", -1)]
    [InlineData("e1.4.6", "v1.4.6", 0)]        // workshop mods use an e prefix
    [InlineData("v1.10.0", "v1.9.0", 1)]       // numeric, not lexicographic
    public void Versions_order_numerically(string a, string b, int expected)
        => Assert.Equal(expected, Math.Sign(SaveHeaderReader.CompareVersions(a, b)));
}

/// <summary>
/// The installer writes into the player's game folder, so these cover exactly when it does and does not, on temp
/// directories shaped like a module (SubModule.xml + a bin folder).
/// </summary>
public class ClientModuleInstallerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mc-cmi-" + Guid.NewGuid().ToString("N"));

    public ClientModuleInstallerTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string MakeModule(string where, string version, string payload)
    {
        var dir = Path.Combine(_dir, where);
        Directory.CreateDirectory(Path.Combine(dir, "bin", "Win64_Shipping_Client"));
        File.WriteAllText(Path.Combine(dir, "SubModule.xml"),
            $"<Module><Name value=\"ModularCoop Compat\" /><Id value=\"{LaunchSession.SyncModuleId}\" /><Version value=\"{version}\" /><SubModules /></Module>");
        File.WriteAllText(Path.Combine(dir, "bin", "Win64_Shipping_Client", "marker.txt"), payload);
        return dir;
    }

    private DiscoveredModule Bundled(string version, string payload = "new") =>
        ModuleCatalog.TryParse(MakeModule("bundled", version, payload), ModuleSourceKind.Custom, out _)!;

    private string GameRoot()
    {
        var root = Path.Combine(_dir, "game");
        Directory.CreateDirectory(Path.Combine(root, "Modules"));
        return root;
    }

    private void Install(string gameRoot, string version, string payload) =>
        Directory.Move(MakeModule("staged", version, payload), ClientModuleInstaller.TargetDir(gameRoot));

    [Fact]
    public void Installs_when_the_client_has_no_copy()
    {
        var game = GameRoot();
        var r = ClientModuleInstaller.Ensure(Bundled("v0.2.0"), game, Path.Combine(_dir, "backups"));
        Assert.Equal(ClientModuleInstaller.InstallOutcome.Installed, r.Outcome);
        Assert.True(File.Exists(Path.Combine(ClientModuleInstaller.TargetDir(game), "SubModule.xml")));
        Assert.Null(r.BackupPath);
    }

    [Fact]
    public void Updates_an_older_copy_and_keeps_the_one_it_replaced()
    {
        var game = GameRoot();
        Install(game, "v0.1.0", "old");
        var r = ClientModuleInstaller.Ensure(Bundled("v0.2.0"), game, Path.Combine(_dir, "backups"));

        Assert.Equal(ClientModuleInstaller.InstallOutcome.Updated, r.Outcome);
        Assert.Equal("new", File.ReadAllText(Path.Combine(ClientModuleInstaller.TargetDir(game), "bin", "Win64_Shipping_Client", "marker.txt")));
        Assert.Equal("old", File.ReadAllText(Path.Combine(r.BackupPath!, "bin", "Win64_Shipping_Client", "marker.txt")));
    }

    [Fact]
    public void Leaves_a_current_copy_alone()
    {
        var game = GameRoot();
        Install(game, "v0.2.0", "theirs");
        var r = ClientModuleInstaller.Ensure(Bundled("v0.2.0"), game, Path.Combine(_dir, "backups"));
        Assert.Equal(ClientModuleInstaller.InstallOutcome.UpToDate, r.Outcome);
        Assert.Equal("theirs", File.ReadAllText(Path.Combine(ClientModuleInstaller.TargetDir(game), "bin", "Win64_Shipping_Client", "marker.txt")));
    }

    [Fact]
    public void Never_downgrades_a_newer_copy()
    {
        var game = GameRoot();
        Install(game, "v0.3.0", "theirs");
        var r = ClientModuleInstaller.Ensure(Bundled("v0.2.0"), game, Path.Combine(_dir, "backups"));
        Assert.Equal(ClientModuleInstaller.InstallOutcome.UpToDate, r.Outcome);
        Assert.Equal("theirs", File.ReadAllText(Path.Combine(ClientModuleInstaller.TargetDir(game), "bin", "Win64_Shipping_Client", "marker.txt")));
    }

    [Fact]
    public void Says_so_when_there_is_no_game_install()
    {
        var r = ClientModuleInstaller.Ensure(Bundled("v0.2.0"), Path.Combine(_dir, "nope"), Path.Combine(_dir, "backups"));
        Assert.Equal(ClientModuleInstaller.InstallOutcome.Unavailable, r.Outcome);
    }
}
