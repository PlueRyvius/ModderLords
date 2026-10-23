using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TaleWorlds.CampaignSystem;

namespace BetterEconomyCoopCompat;

/// <summary>
/// One piece of the module. Each checks the members of Living Economy it relies on and switches itself off, with a
/// log line, when a mod update moved one; the rest keep running.
/// </summary>
internal interface IComponent
{
    string Id { get; }

    /// <summary>Null when the component should install; otherwise the reason it stays off.</summary>
    string? SkipReason(Context context);

    /// <summary>Returns a one-line status for the log.</summary>
    string Install(Context context);
}

/// <summary>What every component gets: Living Economy's assembly and which side this process is.</summary>
internal sealed class Context
{
    public Context(Assembly mod, bool isServer)
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

internal static class Layer
{
    public const string AssemblyName = "BetterEconomy";

    private static readonly IComponent[] Components =
    {
        new SettingsGuard(),
        new ServerOnly(),
        new OwnerScope(),
        new PlayerChecks(),
        new Notices(),
        new Actions(),
        new StateMirrorComponent(),
    };

    private static bool _started;

    /// <summary>True once Living Economy was found and the components installed.</summary>
    internal static bool Active { get; private set; }

    /// <summary>
    /// True on a player's PC in a live co-op session whose server accepted this PC's hello. Every client-side patch asks
    /// this at call time, so the same game played alone, or with a server that doesn't speak this protocol, runs Living
    /// Economy exactly as the author wrote it.
    /// </summary>
    internal static bool IsCoopClient => Net.Ready && Coop.OnPlayerPc;

    /// <summary>Server side of a co-op session.</summary>
    internal static bool IsCoopServer => Coop.OnServer;

    internal static void Start()
    {
        if (_started) return;
        _started = true;
        var mod = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a =>
        {
            try { return a.GetName().Name == AssemblyName; }
            catch { return false; }
        });
        if (mod == null) { Log.Info("Living Economy is not loaded; nothing to do"); return; }
        if (!Coop.Loaded) { Log.Info("Coop is not loaded; Living Economy runs as it does alone"); return; }

        // The patches ask at call time which side they are on; this only picks which ones to install.
        var context = new Context(mod, Coop.IsServer);
        Log.Info($"Living Economy {mod.GetName().Version} found ({(context.IsServer ? "server" : "player's PC")}); {Components.Length} component(s)");
        Active = true;
        foreach (var component in Components)
        {
            try
            {
                var skip = component.SkipReason(context);
                Log.Info(skip == null ? $"{component.Id} on: {component.Install(context)}" : $"{component.Id} off: {skip}");
            }
            catch (Exception ex) { Log.Warn($"{component.Id} failed and is off: {ex.GetBaseException().Message}"); }
        }
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

    internal static string Short(string typeName) => typeName.Substring(typeName.LastIndexOf('.') + 1);

    /// <summary>One line of what the module did, for a launcher's log (Hooks.Summary).</summary>
    internal static string Summary()
    {
        if (!Active) return "";
        var side = Coop.IsServer
            ? $"owner-scoped ticks {OwnerScope.ScopedCount}, AI treaty values refused for players {PlayerChecks.TreatiesRefused}"
            : $"simulation handlers skipped {ServerOnly.Skipped}";
        return $"{side}, {Actions.Summary()}, {StateMirror.Summary()}";
    }
}
