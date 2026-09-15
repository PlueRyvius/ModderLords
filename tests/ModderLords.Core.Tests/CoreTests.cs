using Bannerlord.ModuleManager;
using ModderLords.Core.Compat;
using ModderLords.Coop.Compat;
using ModderLords.Core.Export;
using ModderLords.Core.Launch;
using ModderLords.Core.Profiles;
using ModderLords.Coop.Launch;
using ModderLords.Core.Logs;
using ModderLords.Core.Modules;
using ModderLords.Core.Saves;
using ModderLords.Coop.Saves;
using ModderLords.Core.Overlay;
using Xunit;

namespace ModderLords.Core.Tests;

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

    /// <summary>
    /// The whole point of DependencyOnly for a content mod (TAOM and friends): the C# submodule goes, the module's
    /// data stays. The engine loads &lt;Xmls&gt; from the manifest whether or not a SubModule loads, so a mod that
    /// cannot run headless can still serve its world data.
    /// </summary>
    [Fact]
    public void DependencyOnly_drops_code_but_keeps_Xmls_and_identity()
    {
        const string contentMod = """
            <?xml version="1.0" encoding="utf-8"?>
            <Module>
              <Id value="TAOM"/>
              <Version value="v2.0.27"/>
              <SubModules>
                <SubModule>
                  <Name value="TAOM"/>
                  <DLLName value="TAOM.dll"/>
                  <SubModuleClassType value="TAOM.SubModule"/>
                  <Tags><Tag key="DedicatedServerType" value="none"/></Tags>
                </SubModule>
              </SubModules>
              <Xmls>
                <XmlNode><XmlName id="Kingdoms" path="spkingdoms"/></XmlNode>
                <XmlNode><XmlName id="SPCultures" path="spcultures"/></XmlNode>
              </Xmls>
            </Module>
            """;
        var r = ManifestRewriter.Rewrite(contentMod, ServerRole.DependencyOnly, ["Something.Else"]);
        Assert.DoesNotContain("TAOM.dll", r.Xml);
        Assert.DoesNotContain("TAOM.SubModule", r.Xml);
        // Identity survives for Coop's ModuleValidator handshake, and so does every data entry.
        Assert.Contains("<Id value=\"TAOM\"", r.Xml);
        Assert.Contains("<Version value=\"v2.0.27\"", r.Xml);
        Assert.Contains("spkingdoms", r.Xml);
        Assert.Contains("spcultures", r.Xml);
    }

    [Fact]
    public void AsShipped_returns_original_text()
    {
        var r = ManifestRewriter.Rewrite(ClientOnlyMod, ServerRole.AsShipped);
        Assert.Equal(ClientOnlyMod, r.Xml);
        Assert.Empty(r.Changes);
    }
}

public class HookSetupTests
{
    [Fact]
    public void Environment_carries_the_sidecar_path_only_when_one_is_given()
    {
        var hook = Path.Combine(Path.GetTempPath(), "ModderLords.Hook.dll");
        var withOut = HookSetup.Environment(hook, [Path.GetTempPath()]);
        Assert.False(withOut.ContainsKey(HookSetup.SidecarVariable));

        var sidecar = Path.Combine(Path.GetTempPath(), "logs", "hook.log");
        var with_ = HookSetup.Environment(hook, [Path.GetTempPath()], sidecarPath: sidecar);
        Assert.Equal(sidecar, with_[HookSetup.SidecarVariable]);
        Assert.Equal(Path.GetFullPath(hook), with_["DOTNET_STARTUP_HOOKS"]);
    }

    /// <summary>One sidecar per launch, named like the launcher log next to it so the pair is obvious.</summary>
    [Fact]
    public void Sidecar_path_is_timestamped_next_to_the_launcher_logs()
    {
        var path = HookSetup.SidecarPathFor(new DateTime(2026, 9, 7, 3, 51, 27));
        Assert.Equal("hook-20260907-035127.log", Path.GetFileName(path));
        Assert.Equal("logs", Path.GetFileName(Path.GetDirectoryName(path)));
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
    [InlineData("[ModderLords.Hook] resolved Serilog from X", LogCategory.Tool)]
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
    /// <summary>
    /// The launcher's own DedicatedServer.* module runs on the server and cannot exist on a client. It used to be
    /// listed in the manifest players are told to enable, and reported as "missing on client" on every check.
    /// </summary>
    [Fact]
    public void Server_only_modules_are_not_reported_missing_on_the_client()
    {
        var path = Path.Combine(Path.GetTempPath(), "mc-cm2-" + Guid.NewGuid().ToString("N") + ".xml");
        File.WriteAllText(path,
            "<?xml version=\"1.0\" encoding=\"utf-8\"?><UserData><SingleplayerData><ModDatas>" +
            "<UserModData><Id>CoopNightly</Id><LastKnownVersion>v0.1.4</LastKnownVersion><IsSelected>true</IsSelected></UserModData>" +
            "</ModDatas></SingleplayerData></UserData>");
        try
        {
            var checks = ClientManifest.CompareWithLauncherData(
                [new ClientManifest.Entry("DedicatedServer.ModderLordsCompat", "v0.1.0", null)], path);
            Assert.DoesNotContain(checks, c => c.Id.StartsWith("DedicatedServer."));
        }
        finally { File.Delete(path); }
    }

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
/// A machine that hosts and plays has ModderLords.Compat installed in the game's Modules too; if a profile ticks that copy
/// the server must still load the launcher's own, which is where recipes.json is written.
/// </summary>
public class CompatSelectionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mc-compatsel-" + Guid.NewGuid().ToString("N"));

    public CompatSelectionTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private DiscoveredModule Module(string where, string id)
    {
        var dir = Path.Combine(_dir, where, id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "SubModule.xml"),
            $"<Module><Name value=\"{id}\" /><Id value=\"{id}\" /><Version value=\"v0.1.1\" /><SubModules /></Module>");
        return ModuleCatalog.TryParse(dir, ModuleSourceKind.Custom, out _)!;
    }

    [Fact]
    public void A_ticked_installed_copy_is_replaced_by_the_launchers_own()
    {
        var installed = Module("game", LaunchSession.SyncModuleId);
        var bundled = Module("compat", LaunchSession.SyncModuleId);
        var other = Module("game", "ImprovedGarrisons");
        var messages = new List<string>();
        var profile = new Profile { SettingsSync = true, CompatGuards = false };

        var list = LaunchSession.WithCompat(profile,
            [new ModSelection(other, ServerRole.AsShipped), new ModSelection(installed, ServerRole.AsShipped)], messages,
            id => id == LaunchSession.SyncModuleId ? bundled : null);

        var sync = Assert.Single(list, s => s.Module.Id == LaunchSession.SyncModuleId);
        Assert.Equal(bundled.FolderPath, sync.Module.FolderPath);
        Assert.Contains(list, s => s.Module.Id == "ImprovedGarrisons");
        Assert.Contains(messages, m => m.Contains("launcher's own copy", StringComparison.Ordinal));
    }

    [Fact]
    public void Settings_sync_adds_the_launchers_copy_once_and_quietly()
    {
        var bundled = Module("compat", LaunchSession.SyncModuleId);
        var messages = new List<string>();
        var list = LaunchSession.WithCompat(new Profile { SettingsSync = true, CompatGuards = false }, [], messages,
            id => id == LaunchSession.SyncModuleId ? bundled : null);
        Assert.Equal(bundled.FolderPath, Assert.Single(list).Module.FolderPath);
        Assert.Empty(messages);
    }
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
            $"<Module><Name value=\"ModderLords Compat\" /><Id value=\"{LaunchSession.SyncModuleId}\" /><Version value=\"{version}\" /><SubModules /></Module>");
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

public class ModListFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mc-mlf-" + Guid.NewGuid().ToString("N"));

    public ModListFileTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static ModListFile Sample() => new()
    {
        Name = "profile1",
        Coop = new ModListFile.CoopRef("CoopNightly", "v0.1.4"),
        Mods =
        [
            new("Bannerlord.Harmony", "v2.4.2.248", null, ServerRole.DependencyOnly, false),
            new("ModularSmithing2", "v0.9.28", "https://steamcommunity.com/sharedfiles/filedetails/?id=3790263286", ServerRole.Run, true),
            new("ImprovedGarrisons", "v4.2.0.7", null, ServerRole.Run, false),
        ],
    };

    [Fact]
    public void Round_trips_through_a_file_keeping_order_and_roles()
    {
        var path = Path.Combine(_dir, "list.json");
        ModListFile.Write(path, Sample());
        var read = ModListFile.Read(path);

        Assert.Equal(["Bannerlord.Harmony", "ModularSmithing2", "ImprovedGarrisons"], read.Mods.Select(m => m.Id));
        Assert.Equal(ServerRole.DependencyOnly, read.Mods[0].Role);
        Assert.True(read.Mods[1].ServerAuthoritative);
        Assert.Equal("CoopNightly", read.Coop!.Id);
        Assert.Contains("3790263286", read.Mods[1].Source);
    }

    [Fact]
    public void The_order_it_carries_is_the_order_the_sync_will_use()
    {
        var file = Sample();
        Assert.Equal(file.Mods.Select(m => m.Id), file.ToOrder().ModuleIds);
        Assert.Equal(file.Mods.Select(m => m.Version), file.ToClientEntries().Select(e => e.Version));
    }

    [Fact]
    public void Becomes_a_runnable_profile_with_the_same_mods_and_roles()
    {
        var p = ModListFile.Read(Round(Sample())).ToProfile("shared");
        Assert.Equal("shared", p.Name);
        Assert.Equal(["Bannerlord.Harmony", "ModularSmithing2", "ImprovedGarrisons", "CoopNightly"], p.Mods.Select(m => m.Id));
        Assert.All(p.Mods, m => Assert.True(m.Enabled));
        Assert.Equal(ServerRole.DependencyOnly, p.Mods[0].Role);
        Assert.True(p.Mods[1].ServerAuthoritative);
        Assert.Null(p.Mods[0].SourcePath);   // resolved by id on the importer's PC, not the exporter's layout
    }

    private string Round(ModListFile f)
    {
        var path = Path.Combine(_dir, "round.json");
        ModListFile.Write(path, f);
        return path;
    }

    [Fact]
    public void A_newer_format_is_refused_with_a_readable_message()
    {
        var path = Path.Combine(_dir, "future.json");
        File.WriteAllText(path, "{\"FormatVersion\":99,\"Mods\":[{\"Id\":\"X\",\"Version\":\"v1\",\"Role\":\"Run\",\"ServerAuthoritative\":false}]}");
        var ex = Assert.Throws<InvalidOperationException>(() => ModListFile.Read(path));
        Assert.Contains("newer launcher", ex.Message);
    }

    [Fact]
    public void Junk_and_empty_lists_are_refused_without_a_raw_json_error()
    {
        var junk = Path.Combine(_dir, "junk.json");
        File.WriteAllText(junk, "not json at all");
        Assert.Contains("not a mod list", Assert.Throws<InvalidOperationException>(() => ModListFile.Read(junk)).Message);

        var empty = Path.Combine(_dir, "empty.json");
        File.WriteAllText(empty, "{\"FormatVersion\":1,\"Mods\":[]}");
        Assert.Contains("lists no mods", Assert.Throws<InvalidOperationException>(() => ModListFile.Read(empty)).Message);
    }
}

/// <summary>
/// The release ships a single-file exe with its odds and ends in folders, so what a player unzips is readable.
/// A dev build leaves the same files next to the exe. Both layouts have to work, or the launcher silently loses
/// its assembly-resolution hook (mods stop loading) or its compatibility database (every badge goes blank).
/// </summary>
public class ReleaseLayoutTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mc-layout-" + Guid.NewGuid().ToString("N"));

    public ReleaseLayoutTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string Write(params string[] parts)
    {
        var path = Path.Combine(new[] { _dir }.Concat(parts).ToArray());
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
        return path;
    }

    [Fact]
    public void The_hook_is_found_in_the_release_bin_folder()
        => Assert.Equal(Write(HookSetup.BinFolder, HookSetup.HookFileName), HookSetup.LocateHookIn(_dir));

    [Fact]
    public void The_hook_is_still_found_next_to_the_exe_in_a_dev_build()
        => Assert.Equal(Write(HookSetup.HookFileName), HookSetup.LocateHookIn(_dir));

    [Fact]
    public void The_release_copy_of_the_hook_wins_when_both_exist()
    {
        Write(HookSetup.HookFileName);
        var inBin = Write(HookSetup.BinFolder, HookSetup.HookFileName);
        Assert.Equal(inBin, HookSetup.LocateHookIn(_dir));
    }

    [Fact]
    public void No_hook_anywhere_is_null_rather_than_a_bogus_path()
        => Assert.Null(HookSetup.LocateHookIn(_dir));

    [Fact]
    public void The_compat_database_is_found_in_the_release_data_folder()
        => Assert.Equal(Write(CompatDb.DataFolder, CompatDb.BundledFileName), CompatDb.BundledPathIn(_dir));

    [Fact]
    public void The_compat_database_falls_back_to_next_to_the_exe()
    {
        var beside = Write(CompatDb.BundledFileName);
        Assert.Equal(beside, CompatDb.BundledPathIn(_dir));
    }
}

/// <summary>
/// Your order is a preference, and it used to be honoured all or nothing: one dependency conflict discarded every
/// other choice you had made, so dragging an unrelated mod appeared to do nothing at all.
/// </summary>
public class PreferredOrderTests
{
    private static DiscoveredModule Mod(string id, params string[] needs) =>
        new(id, "v1.0.0", @"G\" + id, ModuleSourceKind.GameModules, new ModuleInfoExtended
        {
            Id = id, Name = id, Version = ApplicationVersion.Empty,
            DependentModules = needs.Select(n => new DependentModule { Id = n }).ToList(),
        });

    private static DiscoveredModule Stock(string id) =>
        new(id, "v1.0.0", @"S\" + id, ModuleSourceKind.ServerStock, new ModuleInfoExtended
        { Id = id, Name = id, Version = ApplicationVersion.Empty, IsOfficial = true });

    private static readonly DiscoveredModule[] StockSet = [Stock("Native"), Stock("SandBoxCore"), Stock("Sandbox")];

    private static List<string> Community(LoadOrder.Result r, params string[] ids) =>
        r.ModuleIds.Where(ids.Contains).ToList();

    [Fact]
    public void An_unrelated_mod_keeps_the_place_you_moved_it_to()
    {
        // Framework needs Dependent. Alpha and Zulu depend on nothing, so your order for them must be respected
        // even though the Framework/Dependent pair is the wrong way round in the preference.
        var community = new[] { Mod("Framework"), Mod("Dependent", "Framework"), Mod("Alpha"), Mod("Zulu") };
        var preferred = new[] { "Dependent", "Framework", "Zulu", "Alpha" };

        var r = LoadOrder.Compute(StockSet, community, preferred);

        Assert.Equal(["Framework", "Dependent"], Community(r, "Framework", "Dependent"));   // repaired
        Assert.Equal(["Zulu", "Alpha"], Community(r, "Zulu", "Alpha"));                      // your order kept
    }

    [Fact]
    public void Only_the_conflicting_pair_is_reported()
    {
        var community = new[] { Mod("Framework"), Mod("Dependent", "Framework"), Mod("Alpha") };
        var r = LoadOrder.Compute(StockSet, community, ["Dependent", "Framework", "Alpha"]);

        var moved = r.Issues.Where(i => i.StartsWith("moved to satisfy")).ToList();
        Assert.Single(moved);
        Assert.Contains("Dependent", moved[0]);
        Assert.Contains("Framework", moved[0]);
        Assert.DoesNotContain(r.Issues, i => i.Contains("Alpha"));
    }

    [Fact]
    public void A_valid_preference_is_followed_exactly_and_reported_as_nothing_moved()
    {
        var community = new[] { Mod("Framework"), Mod("Dependent", "Framework"), Mod("Alpha") };
        var r = LoadOrder.Compute(StockSet, community, ["Alpha", "Framework", "Dependent"]);

        Assert.Equal(["Alpha", "Framework", "Dependent"], Community(r, "Alpha", "Framework", "Dependent"));
        Assert.DoesNotContain(r.Issues, i => i.StartsWith("moved to satisfy"));
    }

    [Fact]
    public void Real_frameworks_are_repaired_but_the_rest_of_the_list_survives()
    {
        // Andy's actual case: ButterLib before Harmony and MCM before UIExtenderEx, both backwards.
        var community = new[]
        {
            Mod("Bannerlord.Harmony"), Mod("Bannerlord.ButterLib", "Bannerlord.Harmony"),
            Mod("Bannerlord.UIExtenderEx"), Mod("Bannerlord.MBOptionScreen", "Bannerlord.UIExtenderEx"),
            Mod("MyLittleWarband"), Mod("ModularSmithing2"),
        };
        var r = LoadOrder.Compute(StockSet, community,
            ["Bannerlord.ButterLib", "Bannerlord.Harmony", "Bannerlord.MBOptionScreen", "Bannerlord.UIExtenderEx", "MyLittleWarband", "ModularSmithing2"]);

        var ids = r.ModuleIds.ToList();
        Assert.True(ids.IndexOf("Bannerlord.Harmony") < ids.IndexOf("Bannerlord.ButterLib"));
        Assert.True(ids.IndexOf("Bannerlord.UIExtenderEx") < ids.IndexOf("Bannerlord.MBOptionScreen"));
        Assert.True(ids.IndexOf("MyLittleWarband") < ids.IndexOf("ModularSmithing2"));   // untouched by the repair
    }
}
