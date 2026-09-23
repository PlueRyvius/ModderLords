using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ModderLords.CompatSync;

namespace ModderLords.CompatSync.Coop.Taom;

/// <summary>
/// One piece of ModderLords' TAOM support. Each component decides for itself whether it applies, so a TAOM update
/// that moves what one component depends on switches that component off, with a log line, and leaves the rest running.
/// </summary>
internal interface ITaomComponent
{
    string Id { get; }

    /// <summary>Null when the component should install; otherwise the reason it stays off.</summary>
    string? SkipReason(TaomContext context);

    /// <summary>Returns a one-line status for the log.</summary>
    string Install(TaomContext context);
}

/// <summary>What every component gets: the loaded TAOM assemblies and which side this process is.</summary>
internal sealed class TaomContext
{
    public TaomContext(Assembly taom, Assembly? dependencies, bool isServer)
    {
        Taom = taom;
        Dependencies = dependencies;
        IsServer = isServer;
    }

    public Assembly Taom { get; }
    public Assembly? Dependencies { get; }
    public bool IsServer { get; }
}

/// <summary>
/// ModderLords' TAOM co-op layer (docs/TAOM-LAYER-PLAN.md, backlog in docs/TAOM-MAP.md). Inert unless TAOM is an
/// active module, so nothing here runs in any other session. Called from <see cref="Bridge.Tick"/> until TAOM's
/// assembly is loaded, then installs every component once.
/// </summary>
public static class TaomLayer
{
    private static readonly ITaomComponent[] Components =
    {
        new CoopDetectionCheck(),
        new ServerBinaryCheck(),
        new JoinGrantComponent(),
        new FieldCampComponent(),
        new EmissaryComponent(),
        new SiegeDefenseComponent(),
        new MessengerNoticeComponent(),
        new StateMirrorComponent(),
        new SpecialResourceSyncComponent(),
        new CareerSyncComponent(),
    };

    private static bool _done;

    public static void Tick()
    {
        if (_done) return;
        try
        {
            var active = TaleWorlds.ModuleManager.ModuleHelper.GetActiveModules();
            if (active == null) return;
            if (!active.Any(m => string.Equals(m.Id, "TAOM", StringComparison.OrdinalIgnoreCase))) { _done = true; return; }
            var taom = Loaded("TAOM");
            if (taom == null) return;   // TAOM's DLL loads with its submodule; try again next tick
            _done = true;
            InstallAll(new TaomContext(taom, Loaded("TAOM.Dependencies"), IsServerProcess()));
        }
        catch (Exception ex)
        {
            _done = true;
            Log.Warn("TAOM layer failed to start: " + ex.GetBaseException().Message);
        }
    }

    private static void InstallAll(TaomContext context)
    {
        var version = context.Taom.GetName().Version?.ToString() ?? "?";
        Log.Info($"TAOM layer: TAOM {version} loaded ({(context.IsServer ? "server" : "client")}); {Components.Length} component(s)");
        foreach (var component in Components)
        {
            try
            {
                var skip = component.SkipReason(context);
                Log.Info(skip == null
                    ? $"TAOM layer: {component.Id} on: {component.Install(context)}"
                    : $"TAOM layer: {component.Id} off: {skip}");
            }
            catch (Exception ex)
            {
                Log.Warn($"TAOM layer: {component.Id} failed and is off: {ex.GetBaseException().Message}");
            }
        }
    }

    /// <summary>
    /// Whether this process is the dedicated server. Coop's ModInformation.IsServer is still false when the layer
    /// installs (it is set when hosting starts, and is sticky afterwards), so ask the process instead: the server runs
    /// from a Win64_Shipping_Server folder. Measured 2026-09-22: the server logged "loaded (client)" and installed the
    /// client half before this.
    /// </summary>
    private static bool IsServerProcess()
    {
        try
        {
            return System.IO.Directory.GetCurrentDirectory().EndsWith("Win64_Shipping_Server", StringComparison.OrdinalIgnoreCase)
                || Common.ModInformation.IsServer;
        }
        catch { return Common.ModInformation.IsServer; }
    }

    internal static Assembly? Loaded(string name) =>
        AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => !a.IsDynamic && a.GetName().Name == name);
}

/// <summary>
/// TAOM decides at start-up whether a co-op mod is running (TAOM.Dependencies' CoopPresence), from a list of module
/// ids that does not include Coop's current Workshop id, CoopNightly. The launcher adds that id to
/// TAOM.Dependencies\coop-modules.txt. When the line is missing TAOM behaves as if it were alone on every peer:
/// PatchShield strips foreign patches, SaveShield swallows save faults, host-only features run on every client. By the
/// time this module loads that decision has been made, so this component can only report it, loudly.
/// </summary>
internal sealed class CoopDetectionCheck : ITaomComponent
{
    public string Id => "coop-detection";

    public string? SkipReason(TaomContext context) =>
        context.Dependencies?.GetType("TAOM.Dependencies.Foundation.CoopPresence", false) == null
            ? "TAOM.Dependencies' CoopPresence was not found"
            : null;

    public string Install(TaomContext context)
    {
        var presence = context.Dependencies!.GetType("TAOM.Dependencies.Foundation.CoopPresence", true)!;
        var active = presence.GetProperty("IsActive", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) as bool?;
        var ids = presence.GetProperty("ActiveCoopModuleIds", BindingFlags.Public | BindingFlags.Static)?.GetValue(null)
            as IEnumerable<string>;
        if (active == true) return "TAOM sees co-op as active (" + string.Join(", ", ids ?? Array.Empty<string>()) + ")";
        Log.Warn("TAOM layer: TAOM believes NO co-op mod is running. Every player will run TAOM's host-only logic and " +
                 "its shields are in solo mode. TAOM.Dependencies\\coop-modules.txt needs the line CoopNightly under [modules]; " +
                 "launching through ModderLords adds it, so a manual edit or a TAOM reinstall since is the likely cause.");
        return "TAOM sees co-op as INACTIVE (see warning)";
    }
}

/// <summary>
/// TAOM tells a headless server from a real player by the folder its own DLL was loaded from
/// (IDedicatedServerProvider: a path containing Win64_Shipping_Server). A server that loaded TAOM from any other bin
/// believes it has a player at the keyboard and credits the idle world-generation hero with what real players earn.
/// </summary>
internal sealed class ServerBinaryCheck : ITaomComponent
{
    public string Id => "server-binary";

    public string? SkipReason(TaomContext context) => context.IsServer ? null : "client";

    public string Install(TaomContext context)
    {
        var location = context.Taom.Location ?? "";
        if (location.IndexOf("Win64_Shipping_Server", StringComparison.OrdinalIgnoreCase) >= 0)
            return "TAOM loaded from its server binaries, so it knows this is a dedicated server";
        Log.Warn("TAOM layer: this server loaded TAOM from '" + location + "', not a Win64_Shipping_Server folder. TAOM will " +
                 "treat the server as a player and credit the idle world-generation hero with rewards meant for players.");
        return "TAOM loaded from a non-server folder (see warning)";
    }
}
