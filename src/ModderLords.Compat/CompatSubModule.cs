using System;
using HarmonyLib;
using TaleWorlds.MountAndBlade;

namespace ModderLords.Compat;

/// <summary>
/// Layer 0 of root compatibility: generic guards so single-player mods survive on a headless dedicated server.
/// Loaded by the engine as a normal module (id prefixed "DedicatedServer." so Coop's validator exempts it from the
/// client match). Does nothing outside a dedicated server process. Creation mode also routes Coop startup.
/// </summary>
public sealed class CompatSubModule : MBSubModuleBase
{
    public const string HarmonyId = "ModderLords.Compat";
    internal static readonly Harmony Harmony = new Harmony(HarmonyId);

    /// <summary>Null off a dedicated server, so the tick override costs a null check and nothing else.</summary>
    private PerfSampler? _perf;

    /// <summary>Seconds since the terrain spikes last reported. Stays at zero unless one of them is on.</summary>
    private float _sinceSpikeReport;

    /// <summary>
    /// Null unless MODDERLORDS_CREATE_WORLD asked for a world to be generated, which is never the case on a
    /// normal launch. See <see cref="WorldCreator"/>.
    /// </summary>
    private WorldCreator? _worldCreator;

    protected override void OnSubModuleLoad()
    {
        base.OnSubModuleLoad();
        try
        {
            if (!ServerDetect.IsDedicatedServer)
            {
                Log.Info("not a dedicated server process; guards stay off");
                return;
            }
            var installed = Guards.InstallAll(Harmony);
            Log.Info($"server guards installed: {installed}");

            var creator = WorldCreator.TryCreate();
            if (creator.IsEnabled) _worldCreator = creator;
            _perf = new PerfSampler();
            Log.Info($"performance samples every {PerfSampler.ReportPeriodSeconds:0} seconds");
        }
        catch (Exception ex)
        {
            Log.Error("guard installation failed: " + ex);
        }
    }

    /// <summary>
    /// Arm routing after modules load. World creation starts only when the initialized host requests a save load.
    /// </summary>
    protected override void OnBeforeInitialModuleScreenSetAsRoot()
    {
        base.OnBeforeInitialModuleScreenSetAsRoot();
        try { HeadlessMapExperiment.Install(); }
        catch (Exception ex)
        {
            Log.Info("headless-map: unsupported diagnostic map: " + ex);
            Environment.Exit(12);
            return;
        }
        // Both are off unless their own variable is set, and both are diagnostics rather than fixes, so a failure to
        // install one warns and leaves the server alone. Unlike the headless map, nothing downstream depends on them.
        try { TerrainStubExperiment.Install(Harmony); }
        catch (Exception ex) { Log.Warn("terrain stub not installed: " + ex.GetBaseException().Message); }
        try { TerrainReplayExperiment.Install(Harmony); }
        catch (Exception ex) { Log.Warn("terrain replay not installed: " + ex.GetBaseException().Message); }
        if (_worldCreator is null) return;
        try
        {
            _worldCreator.Arm();
            _worldCreator.Tick();
        }
        catch (Exception ex) { Log.Error("world creation failed to start: " + ex); }
    }

    /// <summary>
    /// The engine already calls this every frame for every submodule; dt is the frame delta it hands us. Measuring
    /// from here adds an add and a compare per frame and no allocation at all — see PerfSampler for why that matters.
    /// </summary>
    protected override void OnApplicationTick(float dt)
    {
        base.OnApplicationTick(dt);
        // The host installs its own IDebugManager well after OnSubModuleLoad, discarding our decorator. Last writer
        // wins, so take it back. Costs a type check per frame on every frame but the one where it actually happens.
        try
        {
            if (HeadlessDebugManager.Reassert() is { } retaken)
            {
                Log.Info("re-decorating the IDebugManager the host replaced ours with (" + retaken + ")");
                // The host has just finished its own debug setup, so this is the point at which re-applying the
                // native toggles sticks. Two calls per run, not per frame.
                Log.Info("native assertions: " + HeadlessDebugManager.ReleaseNativeAssertions());
            }
        }
        catch { }
        // Off unless MODDERLORDS_TERRAIN_PROBE is set, and it disarms itself on the first tick when it is not. It
        // rides in this module rather than the settings-sync one because this module is always in the server's load
        // order, and a diagnostic nobody remembered to enable a second module for is a diagnostic that never ran.
        Diagnostics.TerrainProbe.Tick(Log.Info, Log.Warn);

        // Every 30 s while a spike is on, say how many answers it has changed. A spike that patched the wrong thing
        // and served nothing is the failure mode most likely to be mistaken for a disproved hypothesis.
        _sinceSpikeReport += dt;
        if (_sinceSpikeReport >= 30f)
        {
            _sinceSpikeReport = 0f;
            try
            {
                if (TerrainStubExperiment.Summary() is { } stub) Log.Info(stub);
                if (TerrainReplayExperiment.Summary() is { } replay) Log.Info(replay);
            }
            catch { }
        }

        if (_perf is null) return;
        try { _perf.Tick(dt); }
        catch { _perf = null; }   // never let a meter break the server it is measuring

        if (_worldCreator is null) return;
        try { _worldCreator.Tick(); }
        catch (Exception ex) { Log.Error("world creation tick failed: " + ex); _worldCreator = null; }
    }
}

internal static class ServerDetect
{
    /// <summary>The engine sets StartupInfo.DedicatedServerType from the /dedicatedcustomserver command line switch.</summary>
    public static bool IsDedicatedServer
    {
        get
        {
            try
            {
                var m = Module.CurrentModule;
                if (m?.StartupInfo != null && m.StartupInfo.DedicatedServerType != DedicatedServerType.None) return true;
            }
            catch { }
            // Fallback: the server's working directory name (Common.ConfigName) is Win64_Shipping_Server.
            try { return System.IO.Directory.GetCurrentDirectory().EndsWith("Win64_Shipping_Server", StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }
    }
}

internal static class Log
{
    private const string Prefix = "[ModderLords.Compat] ";
    public static void Info(string msg) => Console.WriteLine(Prefix + msg);
    /// <summary>Says "Warning" deliberately: that is what LogClassifier matches to colour the line in the console.</summary>
    public static void Warn(string msg) => Console.WriteLine(Prefix + "Warning: " + msg);
    public static void Error(string msg) => Console.Error.WriteLine(Prefix + msg);
}
