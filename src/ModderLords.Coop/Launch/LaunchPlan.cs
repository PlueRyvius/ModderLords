using System.Diagnostics;
using System.Text;

using ModderLords.Core.Compat;
using ModderLords.Core.Config;
using ModderLords.Core.Export;
using ModderLords.Core.Launch;
using ModderLords.Core.Logs;
using ModderLords.Core.Modules;
using ModderLords.Core.Overlay;
using ModderLords.Core.Perf;
using ModderLords.Core.Profiles;
using ModderLords.Core.Saves;
using ModderLords.Coop.Compat;
using ModderLords.Coop.Config;
using ModderLords.Coop.Launch;
using ModderLords.Coop.Live;
using ModderLords.Coop.Saves;

namespace ModderLords.Coop.Launch;

/// <summary>
/// Everything needed to start the headless engine the way the official host does, with our module list.
/// Verified contract (2026-09-02): cwd = engine\bin\Win64_Shipping_Server;
/// engine\dotnet\dotnet.exe TaleWorlds.Starter.DotNetCore.dll _MODULES_*A*B*_MODULES_ /dedicatedcustomserver PORT REGION 0
/// env DOTNET_ROOT, DOTNET_MULTILEVEL_LOOKUP=0, BANNERLORD_USER_DIR, COOP_DATA_DIR.
/// </summary>
public sealed class LaunchPlan
{
    /// <summary>Fallback only; the real ids come from LoadOrder.Compute (the Coop folder may carry id "CoopNightly").</summary>
    public static readonly string[] StockModuleOrder = ["Native", "SandBoxCore", "SandBox", "CoopNightly", "DedicatedServer.Windows"];

    public required ServerPaths Paths { get; init; }
    public required IReadOnlyList<string> ModuleIds { get; init; }
    public int EnginePort { get; init; } = 7210;
    public string Region { get; init; } = "EU";
    /// <summary>Save to auto-load via /coopsave. Null = whatever server-config.json says.</summary>
    public string? SaveName { get; init; }
    public string? Password { get; init; }
    public ServerVisibility? Visibility { get; init; }
    public IReadOnlyDictionary<string, string> ExtraEnvironment { get; init; } = new Dictionary<string, string>();

    public string ModuleToken => "_MODULES_" + string.Concat(ModuleIds.Select(id => "*" + id)) + "*_MODULES_";

    public IReadOnlyList<string> Arguments()
    {
        var args = new List<string> { "TaleWorlds.Starter.DotNetCore.dll", ModuleToken, "/dedicatedcustomserver", EnginePort.ToString(), Region, "0" };
        if (SaveName is not null) { args.Add("/coopsave"); args.Add(SaveName); }
        if (!string.IsNullOrEmpty(Password)) { args.Add("/cooppassword"); args.Add(Password); }
        if (Visibility is { } v)
        {
            args.Add("/coopvisibility");
            args.Add(v switch { ServerVisibility.Public => "public", ServerVisibility.FriendsOnly => "friends_only", _ => "none" });
        }
        return args;
    }

    public IReadOnlyDictionary<string, string> Environment()
    {
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["DOTNET_ROOT"] = Path.Combine(Paths.EngineRoot, "dotnet"),
            ["DOTNET_MULTILEVEL_LOOKUP"] = "0",
            ["BANNERLORD_USER_DIR"] = Paths.DataDir,
            ["COOP_DATA_DIR"] = Paths.CoopDataDir,
        };
        foreach (var kv in ExtraEnvironment) env[kv.Key] = kv.Value;
        return env;
    }

    public ProcessStartInfo ToStartInfo()
    {
        var psi = new ProcessStartInfo
        {
            FileName = Paths.DotnetExe,
            WorkingDirectory = Paths.ServerBin,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in Arguments()) psi.ArgumentList.Add(a);
        foreach (var kv in Environment()) psi.Environment[kv.Key] = kv.Value;
        return psi;
    }

    /// <summary>Human-readable, password redacted.</summary>
    public string Describe()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"cwd : {Paths.ServerBin}");
        sb.AppendLine($"exe : {Paths.DotnetExe}");
        sb.Append("args: ");
        var args = Arguments();
        for (int i = 0; i < args.Count; i++)
        {
            var a = args[i];
            if (i > 0 && args[i - 1] == "/cooppassword") a = "****";
            sb.Append(a.Contains(' ') ? $"\"{a}\" " : a + " ");
        }
        sb.AppendLine();
        foreach (var kv in Environment()) sb.AppendLine($"env : {kv.Key}={kv.Value}");
        return sb.ToString();
    }
}
