using System;
using System.Collections.Generic;
using System.IO;
using ModderLords.CompatSync;

namespace ModderLords.Testing;

/// <summary>
/// Wires the test agent into the game: created at submodule load when the launcher's test switch is present
/// (<see cref="TestRunSwitch"/>), ticked every frame. Without the switch nothing here does anything.
/// </summary>
internal static class TestHost
{
    private static TestAgent? _agent;

    private static bool _modulesLoaded;
    private static bool _isServer;

    /// <summary>From OnBeforeInitialModuleScreenSetAsRoot: every module's OnSubModuleLoad, Coop's included, has run.</summary>
    internal static void ModulesLoaded() => _modulesLoaded = true;

    internal static void Init()
    {
        var isServer = _isServer = Directory.GetCurrentDirectory().EndsWith("Win64_Shipping_Server", StringComparison.OrdinalIgnoreCase);
        var runSwitch = TestRunSwitch.From(Environment.GetCommandLineArgs(), Environment.GetEnvironmentVariable(TestRunSwitch.Variable), isServer);
        if (runSwitch == null) return;
        if ((runSwitch.Side == "server") != isServer)
        {
            Log.Warn($"TEST MODE refused: the switch says {runSwitch.Side}, but this game is the {(isServer ? "server" : "player's game")}");
            return;
        }
        BuiltInSteps.Register();
        _agent = new TestAgent(runSwitch, Log.Info, TakeScreenshot, Versions);
        // Info, not a warning: the smoke test counts warnings while joining as failures.
        Log.Info($"TEST MODE: this game was started for a ModderLords test run ({runSwitch.Side}); test steps may change the world");
    }

    /// <summary>
    /// From the submodule's constructor, before any module loads: in a test run, start watching Coop's logger, which
    /// must be seen before Coop's own load sets it up. Otherwise only reads the command line and environment.
    /// </summary>
    internal static void Construct()
    {
        var isServer = Directory.GetCurrentDirectory().EndsWith("Win64_Shipping_Server", StringComparison.OrdinalIgnoreCase);
        if (TestRunSwitch.From(Environment.GetCommandLineArgs(), Environment.GetEnvironmentVariable(TestRunSwitch.Variable), isServer) == null) return;
        CoopWatch.WatchLogger();
    }

    internal static void Tick()
    {
        if (_agent == null || _agent.Ended) return;
        // The join signal exists on a player's game only. Arm it once every module has loaded: Coop's broker builds Coop's
        // logger when first touched, and touching it before Coop's own load sets up its log empties Coop's log (run 5,
        // where this ran on the server before Coop loaded).
        if (_modulesLoaded && !_isServer && CoopWatch.JoinUnavailable != null && CoopWatch.JoinReadyAt == null) CoopWatch.ArmJoin();
        _agent.Tick(DateTime.UtcNow);
    }

    private static bool TakeScreenshot(string path)
    {
        TaleWorlds.Engine.Utilities.TakeScreenshot(path);
        return true;
    }

    /// <summary>The Version in the SubModule.xml at <paramref name="relative"/> from the assembly's folder.</summary>
    private static string? ModuleVersion(System.Reflection.Assembly assembly, string relative)
    {
        try
        {
            var xml = Path.Combine(Path.GetDirectoryName(assembly.Location)!, relative, "SubModule.xml");
            var match = System.Text.RegularExpressions.Regex.Match(File.ReadAllText(xml), "<Version value=\"([^\"]+)\"");
            return match.Success ? match.Groups[1].Value : null;
        }
        catch { return null; }
    }

    private static IDictionary<string, string> Versions()
    {
        // Read from the modules' SubModule.xml: the assemblies' own versions say 1.0.0.0, and the server has no
        // parameters file to read the game's version from.
        var versions = new Dictionary<string, string> { ["modderlords"] = SyncSubModule.Version };
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            var name = assembly.GetName().Name;
            if (name == "TaleWorlds.CampaignSystem") versions["game"] = ModuleVersion(assembly, Path.Combine("..", "..", "Modules", "Native")) ?? "";
            if (name == "Coop.Core") versions["coop"] = ModuleVersion(assembly, Path.Combine("..", "..")) ?? "";
        }
        return versions;
    }
}
