using System;
using HarmonyLib;
using TaleWorlds.MountAndBlade;

namespace ModularCoop.Compat;

/// <summary>
/// Layer 0 of root compatibility: generic guards so single-player mods survive on a headless dedicated server.
/// Loaded by the engine as a normal module (id prefixed "DedicatedServer." so Coop's validator exempts it from the
/// client match). Does nothing at all outside a dedicated server process. Touches no Coop code.
/// </summary>
public sealed class CompatSubModule : MBSubModuleBase
{
    public const string HarmonyId = "ModularCoop.Compat";
    internal static readonly Harmony Harmony = new Harmony(HarmonyId);

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
        }
        catch (Exception ex)
        {
            Log.Error("guard installation failed: " + ex);
        }
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
    private const string Prefix = "[ModularCoop.Compat] ";
    public static void Info(string msg) => Console.WriteLine(Prefix + msg);
    public static void Error(string msg) => Console.Error.WriteLine(Prefix + msg);
}
