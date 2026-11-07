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

    internal static void Init()
    {
        var isServer = Directory.GetCurrentDirectory().EndsWith("Win64_Shipping_Server", StringComparison.OrdinalIgnoreCase);
        var runSwitch = TestRunSwitch.From(Environment.GetCommandLineArgs(), Environment.GetEnvironmentVariable(TestRunSwitch.Variable), isServer);
        if (runSwitch == null) return;
        if ((runSwitch.Side == "server") != isServer)
        {
            Log.Warn($"TEST MODE refused: the switch says {runSwitch.Side}, but this game is the {(isServer ? "server" : "player's game")}");
            return;
        }
        BuiltInSteps.Register();
        _agent = new TestAgent(runSwitch, Log.Info, TakeScreenshot, Versions);
        Log.Warn($"TEST MODE: this game was started for a ModderLords test run ({runSwitch.Side}); test steps may change the world");
    }

    internal static void Tick()
    {
        if (_agent == null || _agent.Ended) return;
        _agent.Tick(DateTime.UtcNow);
    }

    private static bool TakeScreenshot(string path)
    {
        TaleWorlds.Engine.Utilities.TakeScreenshot(path);
        return true;
    }

    private static IDictionary<string, string> Versions()
    {
        var versions = new Dictionary<string, string> { ["modderlords"] = SyncSubModule.Version };
        try { versions["game"] = TaleWorlds.Library.ApplicationVersion.FromParametersFile(null).ToString(); } catch { }
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            var name = assembly.GetName();
            if (name.Name == "Coop.Core") versions["coop"] = name.Version?.ToString() ?? "";
        }
        return versions;
    }
}
