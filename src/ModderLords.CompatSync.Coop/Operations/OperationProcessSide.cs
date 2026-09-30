using System;
using System.IO;

namespace ModderLords.CompatSync.Coop.Operations;

/// <summary>
/// Identifies the process side before Coop finishes initializing its sticky ModInformation flags. Operation
/// adapters are installed from Coop handler constructors, which is early enough for IsServer to still be false on
/// the dedicated host.
/// </summary>
internal static class OperationProcessSide
{
    public static bool IsServer
    {
        get
        {
            try
            {
                var module = TaleWorlds.MountAndBlade.Module.CurrentModule;
                if (module?.StartupInfo != null
                    && module.StartupInfo.DedicatedServerType != TaleWorlds.MountAndBlade.DedicatedServerType.None)
                    return true;
            }
            catch { }

            try
            {
                if (Directory.GetCurrentDirectory().EndsWith("Win64_Shipping_Server", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            catch { }

            try { return Common.ModInformation.IsServer; }
            catch { return false; }
        }
    }
}
