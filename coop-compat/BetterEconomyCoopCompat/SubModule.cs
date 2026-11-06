using System;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace BetterEconomyCoopCompat;

// Makes Living Economy (module id BetterEconomy, by ISKL) work in Bannerlord Coop. Built against Coop 0.1.6 and
// Living Economy for game 1.4.8.
//
// How Coop works: the server runs the campaign; players' PCs ask it through vanilla paths and get the results back.
// Living Economy is 18 campaign behaviours that run on every PC and keep their own records, which Coop never syncs.
//
// Only while a co-op session is running (single player is untouched):
// - The server runs the economy. Players' PCs stop simulating it.
// - On the server a player-owned town's tick runs as its owner, so the AI doesn't run players' towns with their gold,
//   wealth realism leaves players' gold alone, and the AI signs no trade treaties for kingdoms players lead.
// - Players' clicks in Living Economy's menus are carried out by the server, as that player.
// - The mod's records follow the server to every player, so menus, prices and the ledger show the real economy.
// - Lines the mod shows a player on the server reach that player.
//
// The last three need network messages: Living Economy's records are the mod's own save data, which Coop never
// sends, and a player's click can't reach the server any other way. They are this module's own messages, in its own
// namespace, with a protocol version (BetterEconomy.CoopCompat.Coop.dll, loaded only when Coop is). A player's PC
// starts relaying only after the server accepts its hello; if Coop changes, the adapter doesn't load, or the versions
// differ, those parts stay off and that PC plays as if this module weren't there.
public sealed class SubModule : MBSubModuleBase
{
    private const string AdapterFile = "BetterEconomy.CoopCompat.Coop.dll";
    private static bool _adapterTried;
    private static Action? _sessionTick;   // the adapter's Session.Tick: arms its handler for each Coop session
    private static DateTime _nextTick;

    protected override void OnBeforeInitialModuleScreenSetAsRoot()
    {
        base.OnBeforeInitialModuleScreenSetAsRoot();
        Coop.ModulesLoaded();
        TryLoadAdapter();
        Layer.Start();
    }

    protected override void OnApplicationTick(float dt)
    {
        base.OnApplicationTick(dt);
        if (!_adapterTried) TryLoadAdapter();
        if (_sessionTick != null)
        {
            try { _sessionTick(); }
            catch (Exception ex)
            {
                _sessionTick = null;
                Log.Warn("Coop adapter stopped, so players' clicks and records stay local: " + ex.GetBaseException().Message);
            }
        }
        if (!Layer.Active || DateTime.UtcNow < _nextTick) return;
        _nextTick = DateTime.UtcNow.AddSeconds(1);
        try { StateMirror.ServerTick(); }
        catch (Exception ex) { Log.Warn("state mirror tick failed: " + ex.GetBaseException().Message); }
    }

    // The adapter references Coop's assemblies, so it can only load where Coop is. Its handlers are in this module's own
    // namespace, so Coop doesn't build them: the adapter's Session.Tick, called every tick, arms them per session.
    private static void TryLoadAdapter()
    {
        if (_adapterTried) return;
        if (!Coop.Loaded)
        {
            if (Coop.Resolved) _adapterTried = true;   // no Coop in this game: nothing to load
            return;
        }
        _adapterTried = true;
        try
        {
            var path = Path.Combine(Path.GetDirectoryName(typeof(SubModule).Assembly.Location) ?? ".", AdapterFile);
            if (!File.Exists(path)) { Log.Warn(AdapterFile + " is missing next to the module; players' clicks and records stay local"); return; }
            var adapter = Assembly.LoadFrom(path);
            var types = adapter.GetTypes().Length;   // load the types now, inside this try
            var tick = adapter.GetType("BetterEconomyCoopCompat.Handlers.Session", true)!.GetMethod("Tick", BindingFlags.Public | BindingFlags.Static);
            _sessionTick = (Action)Delegate.CreateDelegate(typeof(Action), tick!);
            Log.Info($"Coop adapter loaded ({types} types, protocol {Net.ProtocolVersion}); it arms when a session starts");
        }
        catch (Exception ex) { Log.Warn("Coop adapter did not load, so players' clicks and records stay local: " + ex.GetBaseException().Message); }
    }
}

/// <summary>One line per patch applied and per real problem, in the game's log.</summary>
public static class Log
{
    public static void Info(string text) => Write(text);
    public static void Warn(string text) => Write("WARNING " + text);

    private static void Write(string text)
    {
        try { Debug.Print("[BetterEconomy.CoopCompat] " + text); }
        catch { }
    }
}

/// <summary>
/// The link to the Coop adapter. The adapter fills the senders when its handlers arm and clears them when the session
/// ends; this module calls them, and the adapter calls the receivers. Public so the adapter (another assembly) can.
/// </summary>
public static class Net
{
    /// <summary>Goes up whenever a message below changes shape. Both sides must agree before anything is relayed.</summary>
    public const int ProtocolVersion = 1;

    /// <summary>True once Living Economy was found and this module's components installed.</summary>
    public static bool LayerActive => Layer.Active;

    /// <summary>Client: true once the server accepted this game's hello for the current session.</summary>
    public static bool Ready { get; set; }

    /// <summary>Client: sends (op, args) to the server.</summary>
    public static Action<string, System.Collections.Generic.IList<string>>? SendAction { get; set; }

    /// <summary>Client: asks the server for every mirrored behaviour in full.</summary>
    public static Action? RequestFull { get; set; }

    /// <summary>Server: sends (behaviour, payload) to every accepted player.</summary>
    public static Action<string, string>? Broadcast { get; set; }

    /// <summary>Server: shows (text, colour) to the player who controls this hero.</summary>
    public static Action<Hero, string, uint>? SendNotice { get; set; }

    /// <summary>The session ended: nothing is sent or held any more.</summary>
    public static void SessionEnded()
    {
        Ready = false;
        SendAction = null;
        RequestFull = null;
        Broadcast = null;
        SendNotice = null;
        StateMirror.Reset();
    }

    // ---- what the adapter calls, already on the game thread ----

    /// <summary>Server: runs a player's relayed click. Returns ("ok", lines) for the reply.</summary>
    public static (bool Ok, System.Collections.Generic.List<string> Lines) RunAction(Hero hero, TaleWorlds.CampaignSystem.Party.MobileParty? party,
        string op, System.Collections.Generic.IList<string> args) => Actions.ServerRun(hero, party, op, args);

    /// <summary>Client: the lines the server collected for one of this player's clicks.</summary>
    public static void ShowLines(System.Collections.Generic.IList<string> lines) => Actions.ShowLines(lines);

    /// <summary>Client: one mirror message.</summary>
    public static void ApplyState(string behaviour, string payload) => StateMirror.ClientApply(behaviour, payload);

    /// <summary>Server: everything a joining player needs, at the sequence the others hold.</summary>
    public static System.Collections.Generic.List<(string Behaviour, string Payload)> StateForJoin() => StateMirror.CaptureForJoin();

    /// <summary>Client: a line the server forwarded.</summary>
    public static void ShowNotice(string text, uint color) => Notices.Show(text, color);
}

/// <summary>
/// Reads Coop's own state by reflection, so this module never compiles against one Coop version:
/// - GameInterface.ContainerProvider._lifetimeScope is set while a co-op session runs.
/// - Common.ModInformation.IsServer tells the server from a player (the server's folder is a backup, since IsServer
///   can be set late).
/// - GameInterface.Services.Heroes.Extensions.HeroExtensions.IsPlayerHero(Hero) answers "is this a player?".
/// If a future Coop renames the session state, this assumes a session on a player's PC, the safe side.
/// </summary>
public static class Coop
{
    private static bool _modulesLoaded;
    private static FieldInfo? _scope;
    private static PropertyInfo? _isServer;
    private static MethodInfo? _isPlayerHero;

    public static bool Resolved { get; private set; }
    public static bool Loaded { get; private set; }

    public static bool InSession
    {
        get
        {
            Resolve();
            if (!Loaded) return false;
            if (_scope == null) return true;
            try { return _scope.GetValue(null) != null; }
            catch { return true; }
        }
    }

    public static bool IsServer
    {
        get
        {
            Resolve();
            try
            {
                if (Directory.GetCurrentDirectory().EndsWith("Win64_Shipping_Server", StringComparison.OrdinalIgnoreCase)) return true;
                return _isServer != null && (bool)_isServer.GetValue(null, null);
            }
            catch { return false; }
        }
    }

    public static bool OnPlayerPc => InSession && !IsServer;
    public static bool OnServer => InSession && IsServer;

    public static void ModulesLoaded()
    {
        _modulesLoaded = true;
        Resolve();
    }

    public static bool IsPlayerHero(Hero? hero)
    {
        Resolve();
        if (hero == null || _isPlayerHero == null) return false;
        try { return (bool)_isPlayerHero.Invoke(null, new object[] { hero }); }
        catch { return false; }
    }

    public static bool IsPlayerClan(Clan? clan)
    {
        if (clan == null) return false;
        try { return clan.Heroes.Any(IsPlayerHero); }
        catch { return false; }
    }

    private static void Resolve()
    {
        if (Resolved) return;
        Type? container = null, info = null, heroes = null;
        var coop = false;
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            string name;
            try { name = assembly.GetName().Name ?? ""; }
            catch { continue; }
            if (name == "GameInterface")
            {
                coop = true;
                container ??= assembly.GetType("GameInterface.ContainerProvider", false);
                heroes ??= assembly.GetType("GameInterface.Services.Heroes.Extensions.HeroExtensions", false);
            }
            else if (name == "Coop.Core" || (name == "Coop" && assembly.GetType("Coop.CoopMod", false) != null))
            {
                coop = true;
            }
            info ??= assembly.GetType("Common.ModInformation", false);
        }

        if (!coop)
        {
            // Before the main menu Coop may not be loaded yet; look again later.
            if (_modulesLoaded) Resolved = true;
            return;
        }

        Loaded = true;
        _scope = container == null ? null : AccessTools.Field(container, "_lifetimeScope");
        _isServer = info == null ? null : AccessTools.Property(info, "IsServer");
        _isPlayerHero = heroes == null ? null : AccessTools.Method(heroes, "IsPlayerHero", new[] { typeof(Hero) });
        if (_scope == null || _isServer == null) Log.Warn("could not read Coop's session state; assuming a player's PC in a session");
        if (_isPlayerHero == null) Log.Warn("could not read Coop's player list; the server treats every town as a lord's");
        Resolved = true;
    }
}
