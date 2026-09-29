using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ModderLords.CompatSync;
using ModderLords.CompatSync.Coop.Taom;
using TaleWorlds.CampaignSystem;

namespace ModderLords.CompatSync.Coop.LivingEconomy;

/// <summary>
/// One piece of ModderLords' Living Economy support. Each component checks the members of the mod it relies on and
/// switches itself off, with a log line, when a mod update moved one; the rest keep running.
/// </summary>
internal interface ILeComponent
{
    string Id { get; }

    /// <summary>Null when the component should install; otherwise the reason it stays off.</summary>
    string? SkipReason(LeContext context);

    /// <summary>Returns a one-line status for the log.</summary>
    string Install(LeContext context);
}

/// <summary>What every component gets: Living Economy's assembly and which side this process is.</summary>
internal sealed class LeContext
{
    public LeContext(Assembly mod, bool isServer)
    {
        Mod = mod;
        IsServer = isServer;
    }

    public Assembly Mod { get; }
    public bool IsServer { get; }

    public Type? Type(string fullName) => Mod.GetType(fullName, false);

    /// <summary>A declared method by name (any visibility, static or instance), optionally by parameter count.</summary>
    public MethodInfo? Method(string typeName, string name, int parameters = -1)
    {
        var t = Type(typeName);
        if (t == null) return null;
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        return t.GetMethods(all).FirstOrDefault(m => m.Name == name && (parameters < 0 || m.GetParameters().Length == parameters));
    }
}

/// <summary>
/// ModderLords' co-op layer for Bannerlord Living Economy (module id BetterEconomy, by ISKL), written with the
/// author's permission. See docs/LIVING-ECONOMY-LAYER.md. Inert unless BetterEconomy is an active module, so nothing
/// here runs in any other session; inside a Living Economy session every client-side patch also checks that a co-op
/// session is live, so single-player is untouched.
///
/// The shape: the server runs the economy (players' games stop simulating it), the server treats every connected
/// player as a player rather than an AI lord, players' clicks are carried out by the server as that player, and the
/// mod's saved records follow the server to every player.
/// </summary>
public static class LivingEconomyLayer
{
    public const string ModuleId = "BetterEconomy";
    public const string AssemblyName = "BetterEconomy";
    internal const string Tag = "Living Economy layer: ";

    private static readonly ILeComponent[] Components =
    {
        new LeSettingsGuardComponent(),
        new LeServerOnlyComponent(),
        new LeOwnerScopeComponent(),
        new LePlayerChecksComponent(),
        new LeNoticeComponent(),
        new LeActionsComponent(),
        new LeStateMirrorComponent(),
    };

    private static bool _done;

    /// <summary>True once the layer found Living Economy loaded and installed its components.</summary>
    internal static bool Active { get; private set; }

    internal static bool IsServer { get; private set; }

    /// <summary>
    /// True on a player's game while a co-op session is live (the client handler exists). Every client-side patch asks
    /// this at call time, so the same game played alone runs Living Economy exactly as the author wrote it.
    /// </summary>
    internal static bool IsCoopClient => !IsServer && TaomActions.IsCoopClient;

    public static void Tick()
    {
        if (_done) return;
        try
        {
            var active = TaleWorlds.ModuleManager.ModuleHelper.GetActiveModules();
            if (active == null) return;
            if (!active.Any(m => string.Equals(m.Id, ModuleId, StringComparison.OrdinalIgnoreCase))) { _done = true; return; }
            var mod = TaomLayer.Loaded(AssemblyName);
            if (mod == null) return;   // the DLL loads with its submodule; try again next tick
            _done = true;
            IsServer = IsServerProcess();
            InstallAll(new LeContext(mod, IsServer));
        }
        catch (Exception ex)
        {
            _done = true;
            Log.Warn(Tag + "failed to start: " + ex.GetBaseException().Message);
        }
    }

    private static void InstallAll(LeContext context)
    {
        var version = context.Mod.GetName().Version?.ToString() ?? "?";
        Log.Info($"{Tag}Living Economy {version} loaded ({(context.IsServer ? "server" : "client")}); {Components.Length} component(s)");
        Active = true;
        foreach (var component in Components)
        {
            try
            {
                var skip = component.SkipReason(context);
                Log.Info(skip == null
                    ? $"{Tag}{component.Id} on: {component.Install(context)}"
                    : $"{Tag}{component.Id} off: {skip}");
            }
            catch (Exception ex)
            {
                Log.Warn($"{Tag}{component.Id} failed and is off: {ex.GetBaseException().Message}");
            }
        }
    }

    /// <summary>Same test as the TAOM layer: Coop's IsServer is not set yet when the layer installs, the folder is.</summary>
    private static bool IsServerProcess()
    {
        try
        {
            return System.IO.Directory.GetCurrentDirectory().EndsWith("Win64_Shipping_Server", StringComparison.OrdinalIgnoreCase)
                || Common.ModInformation.IsServer;
        }
        catch { return false; }
    }

    private static MethodInfo? _getBehavior;

    /// <summary>The campaign's instance of one of Living Economy's behaviours, or null outside a campaign.</summary>
    internal static CampaignBehaviorBase? Behavior(Type type)
    {
        if (Campaign.Current == null) return null;
        _getBehavior ??= typeof(Campaign).GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == "GetCampaignBehavior" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0);
        return _getBehavior?.MakeGenericMethod(type).Invoke(Campaign.Current, null) as CampaignBehaviorBase;
    }

    /// <summary>"a, b and N more" for log lines.</summary>
    internal static string Some(IEnumerable<string> names, int max = 4)
    {
        var list = names.ToList();
        return list.Count <= max ? string.Join(", ", list) : string.Join(", ", list.Take(max)) + $" and {list.Count - max} more";
    }

    /// <summary>"; Living Economy: ..." for the 30 s verification line, or empty when the layer is not running.</summary>
    public static string Summary()
    {
        if (!Active) return "";
        var side = IsServer
            ? $"owner-scoped ticks {LeOwnerScopeComponent.ScopedCount}, AI treaty values refused for players {LePlayerChecksComponent.TreatiesRefused}"
            : $"simulation handlers skipped {LeServerOnlyComponent.Skipped}";
        return $"; Living Economy: {side}, {LeActionsComponent.Summary()}, {LeStateMirror.Summary()}";
    }

    internal static string Short(string typeName) => typeName.Substring(typeName.LastIndexOf('.') + 1);
}
