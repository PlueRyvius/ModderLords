using System;
using System.IO;
using System.Linq;
using System.Reflection;
using TaleWorlds.MountAndBlade;

namespace ModderLords.OperationFixture;

/// <summary>Test-only bootstrap: no eager references to Coop or operation assemblies.</summary>
public sealed class FixtureSubModule : MBSubModuleBase
{
    private static void Checkpoint(string message)
    {
        var line = "[operation-fixture] " + message;
        Console.WriteLine(line);
        var path = Environment.GetEnvironmentVariable("MODDERLORDS_FIXTURE_LOG");
        if (!string.IsNullOrEmpty(path)) File.AppendAllText(path, DateTime.UtcNow.ToString("O") + " " + line + Environment.NewLine);
    }
    protected override void OnSubModuleLoad()
    {
        base.OnSubModuleLoad();
        if (Environment.GetEnvironmentVariable("MODDERLORDS_OPERATION_FIXTURE") == "1") { Checkpoint("bootstrap loaded"); FixtureCheckpoint.Emit("bootstrap.loaded"); }
    }
    private object? driver;
    private MethodInfo? tick;
    protected override void OnApplicationTick(float dt)
    {
        if (Environment.GetEnvironmentVariable("MODDERLORDS_OPERATION_FIXTURE") != "1") return;
        if (driver == null)
        {
            var names = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetName().Name).ToArray();
            if (!names.Contains("Coop.Core") || !names.Contains("ModderLords.CompatSync.Coop")) return;
            var assembly = Assembly.LoadFrom(Path.Combine(Path.GetDirectoryName(typeof(FixtureSubModule).Assembly.Location)!, "ModderLords.OperationFixture.dll"));
            var type = assembly.GetType("ModderLords.OperationFixture.FixtureDriver", true)!;
            driver = Activator.CreateInstance(type); tick = type.GetMethod("Tick")!;
            Checkpoint("driver loaded"); FixtureCheckpoint.Emit("driver.loaded");
        }
        tick!.Invoke(driver, null);
    }
}
