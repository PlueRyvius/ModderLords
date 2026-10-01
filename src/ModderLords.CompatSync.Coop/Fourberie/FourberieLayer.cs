using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ModderLords.CompatSync;
using ModderLords.CompatSync.Coop.Taom;

namespace ModderLords.CompatSync.Coop.Fourberie;

/// <summary>One piece of ModderLords' Fourberie support; checks the members it needs and switches itself off with a log line if one moved.</summary>
internal interface IFbComponent
{
    string Id { get; }

    /// <summary>Null when the component should install; otherwise the reason it stays off.</summary>
    string? SkipReason(FbContext context);

    /// <summary>Returns a one-line status for the log.</summary>
    string Install(FbContext context);
}

/// <summary>What every component gets: Fourberie's assembly, which side this process is, and the book's fields.</summary>
internal sealed class FbContext
{
    public FbContext(Assembly mod, bool isServer)
    {
        Mod = mod;
        IsServer = isServer;
        (Fields, FieldsProblem, MissingScratch) = FbFields.Resolve(mod);
    }

    public Assembly Mod { get; }
    public bool IsServer { get; }

    /// <summary>The book's fields, or null (with <see cref="FieldsProblem"/>) when Fourberie's saved fields moved.</summary>
    public List<FieldInfo>? Fields { get; }
    public string? FieldsProblem { get; }
    public List<string> MissingScratch { get; }

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
/// ModderLords' co-op layer for Fourberie. See docs/FOURBERIE-LAYER-PLAN.md. Inert unless Fourberie is an active
/// module; inside a Fourberie session the client side also checks that a co-op session is live, so single-player is
/// untouched.
///
/// The shape: Fourberie keeps "the player's" state in statics. The server keeps one copy of them per player (a book)
/// and runs Fourberie's own ticks once per connected player with that player standing in as the player and that book
/// swapped in. Each player's game keeps its statics as its own book, the rows each side changes travel to the other,
/// and the server saves every player's book with the campaign.
/// </summary>
public static class FourberieLayer
{
    public const string ModuleId = "Fourberie";
    public const string AssemblyName = "Fourberie";
    /// <summary>Feature name on the shared action channel.</summary>
    internal const string Feature = "fourberie";
    internal const string Tag = "Fourberie layer: ";

    private static readonly IFbComponent[] Components =
    {
        new FbBooksComponent(),
        new FbTicksComponent(),
        new FbMirrorComponent(),
        new FbPromptsComponent(),
        new FbLedgersComponent(),
        new FbRelayComponent(),
        new FbGapsComponent(),
        new FbModelsComponent(),
        new FbEffectsComponent(),
    };

    private static bool _done;

    internal static bool Active { get; private set; }

    internal static bool IsServer { get; private set; }

    /// <summary>True on a player's game while a co-op session is live.</summary>
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
            InstallAll(new FbContext(mod, IsServer));
        }
        catch (Exception ex)
        {
            _done = true;
            Log.Warn(Tag + "failed to start: " + ex.GetBaseException().Message);
        }
    }

    private static void InstallAll(FbContext context)
    {
        var version = context.Mod.GetName().Version?.ToString() ?? "?";
        Log.Info($"{Tag}Fourberie {version} loaded ({(context.IsServer ? "server" : "client")}); {Components.Length} component(s)");
        if (context.MissingScratch.Count > 0)
            Log.Warn(Tag + "scratch field(s) not found, left out of the book (Fourberie changed?): " + Some(context.MissingScratch));
        Active = true;
        FbSelfTest.Bind(context.Mod);
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

    /// <summary>Same test as the other layers: Coop's IsServer is not set yet when the layer installs, the folder is.</summary>
    private static bool IsServerProcess()
    {
        try
        {
            return System.IO.Directory.GetCurrentDirectory().EndsWith("Win64_Shipping_Server", StringComparison.OrdinalIgnoreCase)
                || Common.ModInformation.IsServer;
        }
        catch { return false; }
    }

    /// <summary>"a, b and N more" for log lines.</summary>
    internal static string Some(IEnumerable<string> names, int max = 4)
    {
        var list = names.ToList();
        return list.Count <= max ? string.Join(", ", list) : string.Join(", ", list.Take(max)) + $" and {list.Count - max} more";
    }

    /// <summary>"; Fourberie: ..." for the 30 s verification line, or empty when the layer is not running.</summary>
    public static string Summary() => Active ? "; Fourberie: " + (IsServer ? FbBooks.Summary() + ", " + FbTicks.Summary() + ", " + FbPrompts.Summary() + ", " + FbRelay.Summary() + ", " + FbModels.Summary() + ", " + FbEffects.Summary() : FbMirrorClient.Summary() + ", " + FbRelay.Summary() + ", " + FbGaps.Summary() + ", " + FbEffects.Summary()) : "";
}
