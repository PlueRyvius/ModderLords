using System;
using System.IO;
using System.Linq;
using System.Reflection;
using TaleWorlds.MountAndBlade;

namespace ModderLords.CompatSync;

/// <summary>
/// Client + server half of root compatibility (Layer 2a: settings sync). This assembly is the submodule the engine
/// loads and it references nothing of Coop's. The Coop-touching code lives in ModderLords.CompatSync.Coop.dll next
/// to it, which is loaded by hand once Coop's assemblies are present (the same split ModularSmithing2 uses). Coop then
/// discovers the handlers in that assembly by namespace and constructs them when a session starts.
/// </summary>
public sealed class SyncSubModule : MBSubModuleBase
{
    public const string Version = "0.1.11";
    private const string AdapterFileName = "ModderLords.CompatSync.Coop.dll";

    private float _sinceTick;
    private float _sinceVerify;
    private float _sinceRelay;
    private bool _adapterTried;
    private MethodInfo? _adapterTick;
    private MethodInfo? _adapterRelayTick;
    private MethodInfo? _adapterVerify;
    private MethodInfo? _adapterTraceFlush;

    protected override void OnSubModuleUnloaded()
    {
        base.OnSubModuleUnloaded();
        TraceFlush("final ");
    }

    /// <summary>Writes the ground-truth trace counters, if the recipe traces anything. Logs once per flush that wrote something.</summary>
    private void TraceFlush(string what = "")
    {
        if (_adapterTraceFlush is null) return;
        try { if (_adapterTraceFlush.Invoke(null, null) is int n && n > 0) Log.Info($"trace: {what}flush wrote {n} method(s)"); }
        catch (Exception ex) { Log.Warn("trace flush failed: " + ex.GetBaseException().Message); }
    }

    private MethodInfo? _beforeCampaign;
    protected override void OnGameStart(TaleWorlds.Core.Game game, TaleWorlds.Core.IGameStarter gameStarterObject)
    {
        base.OnGameStart(game, gameStarterObject);
        TryLoadAdapter();
        if (_beforeCampaign == null && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MODDERLORDS_OPERATION_PLAN")))
            throw new InvalidOperationException("Required operation plan cannot activate: Coop adapter unavailable.");
        _beforeCampaign?.Invoke(null, null);
    }

    protected override void OnSubModuleLoad()
    {
        base.OnSubModuleLoad();
        // Nothing thrown from here may escape: the engine has no catch around submodule load and dies with 0xE0434352.
        try { Log.Info($"v{Version} loaded; settings sources: {SettingsSources.Summary()}; waiting for Coop"); }
        catch (Exception ex) { Log.Warn("load-time probe failed: " + ex); }
        try { SettingsHints.Load(); }
        catch (Exception ex) { Log.Warn("settings hints failed: " + ex.GetBaseException().Message); }
        try { LiveSettings.Init(); }
        catch (Exception ex) { Log.Warn("live settings init failed: " + ex.GetBaseException().Message); }
    }

    /// <summary>Every module is loaded by now, so Coop's assemblies are present when Coop is enabled.</summary>
    protected override void OnBeforeInitialModuleScreenSetAsRoot()
    {
        base.OnBeforeInitialModuleScreenSetAsRoot();
        TryLoadAdapter();
    }

    protected override void OnApplicationTick(float dt)
    {
        base.OnApplicationTick(dt);
        _sinceTick += dt;
        _sinceVerify += dt;
        _sinceRelay += dt;
        // Relayed player actions go out soon after the control settles, not on the 3 s settings tick.
        if (_sinceRelay >= 0.25f && _adapterRelayTick is not null)
        {
            _sinceRelay = 0f;
            try { _adapterRelayTick.Invoke(null, null); }
            catch (Exception ex) { Log.Warn("relay tick failed: " + ex.GetBaseException().Message); }
        }
        if (_sinceTick >= 3f)
        {
            _sinceTick = 0f;
            // Discover lazily created settings objects (both sides), then host-side live edits, then the adapter tick
            // broadcasts anything that changed and re-applies snapshots that arrived before their object existed.
            try { CampaignWatch.Tick(); SettingsSources.Refresh(); }
            catch (Exception ex) { Log.Warn("settings discovery failed: " + ex.GetBaseException().Message); }
            // Off unless MODDERLORDS_TERRAIN_PROBE is set, and it disarms itself on the first tick when it is not.
            Diagnostics.TerrainProbe.Tick(Log.Info, Log.Warn);
            LiveSettings.Poll();
            if (_adapterTick is null) { TryLoadAdapter(); }
            else try { _adapterTick.Invoke(null, null); }
                 catch (Exception ex) { Log.Warn("settings broadcast tick failed: " + ex.GetBaseException().Message); }
        }
        // Every 30 s, print the verification counter so a solo join shows server counts vs a client's zeros.
        if (_sinceVerify >= 30f && _adapterVerify is not null)
        {
            _sinceVerify = 0f;
            try { if (_adapterVerify.Invoke(null, null) is string s) Log.Info(s); }
            catch (Exception ex) { Log.Warn("verification summary failed: " + ex.GetBaseException().Message); }
            TraceFlush();
        }
    }

    private void TryLoadAdapter()
    {
        if (_adapterTried) return;
        try
        {
            if (!CoopProbe.Present)
            {
                // Coop may simply not be loaded yet (it is normally last in the order); try again on the next tick.
                if (CoopProbe.Report.Contains("IMessageBroker")) return;
                _adapterTried = true;
                Log.Warn("settings sync disabled: " + CoopProbe.Report);
                return;
            }
            _adapterTried = true;
            var dir = Path.GetDirectoryName(typeof(SyncSubModule).Assembly.Location) ?? ".";
            var path = Path.Combine(dir, AdapterFileName);
            if (!File.Exists(path)) { Log.Warn("settings sync disabled: " + AdapterFileName + " missing next to the submodule"); return; }
            var asm = Assembly.LoadFrom(path);
            var bridge = asm.GetType("ModderLords.CompatSync.Coop.Bridge", throwOnError: false);
            _adapterTick = bridge?.GetMethod("Tick", BindingFlags.Public | BindingFlags.Static);
            _adapterRelayTick = bridge?.GetMethod("RelayTick", BindingFlags.Public | BindingFlags.Static);
            _adapterVerify = bridge?.GetMethod("VerificationSummary", BindingFlags.Public | BindingFlags.Static);
            _adapterTraceFlush = bridge?.GetMethod("TraceFlush", BindingFlags.Public | BindingFlags.Static);
            _beforeCampaign = bridge?.GetMethod("BeforeCampaign", BindingFlags.Public | BindingFlags.Static);
            bridge?.GetMethod("InitializeOperations", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null);
            var typeCount = asm.GetTypes().Length;   // forces the type load now, inside our try, not in some other mod's scan
            Log.Info($"coop adapter loaded ({typeCount} types); handlers will arm when a session starts");
        }
        catch (Exception ex)
        {
            _adapterTried = true;
            Log.Warn("coop adapter failed to load, settings sync disabled: " + ex.GetBaseException().Message);
        }
    }
}

/// <summary>Console (the server launcher captures it) plus a file under Documents\Mount and Blade II Bannerlord\Configs\ModLogs (the only place a client can look).</summary>
public static class Log
{
    private const string Prefix = "[ModderLords.Compat] ";
    private static readonly object Gate = new object();
    private static string? _file;
    private static bool _fileTried;

    public static void Info(string msg) => Write(Prefix + msg);
    public static void Warn(string msg) => Write(Prefix + "WARNING " + msg);

    /// <summary>A sidecar file next to this side's log: ModLogs\{prefix}{client|server}{suffix}. Null when the folder cannot be made.</summary>
    public static string? SideFilePath(string prefix, string suffix)
    {
        try
        {
            var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var dir = Path.Combine(docs, "Mount and Blade II Bannerlord", "Configs", "ModLogs");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, prefix + Side + suffix);
        }
        catch { return null; }
    }

    private static string Side => Directory.GetCurrentDirectory().EndsWith("Win64_Shipping_Server", StringComparison.OrdinalIgnoreCase) ? "server" : "client";

    private static void Write(string line)
    {
        Console.WriteLine(line);
        try
        {
            lock (Gate)
            {
                if (!_fileTried)
                {
                    _fileTried = true;
                    var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                    var dir = Path.Combine(docs, "Mount and Blade II Bannerlord", "Configs", "ModLogs");
                    Directory.CreateDirectory(dir);
                    _file = Path.Combine(dir, "ModderLords.Compat-" + Side + ".log");
                    File.WriteAllText(_file, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} log started (pid {System.Diagnostics.Process.GetCurrentProcess().Id}){Environment.NewLine}");
                }
                if (_file != null) File.AppendAllText(_file, $"{DateTime.Now:HH:mm:ss.fff} {line}{Environment.NewLine}");
            }
        }
        catch { }
    }
}

/// <summary>
/// By-name probe of the Coop members the adapter uses, evaluated before the adapter assembly is loaded. A Coop update
/// that moves a member makes the feature report "disabled" instead of throwing inside the engine.
/// </summary>
public static class CoopProbe
{
    private static bool? _present;
    private static string _report = "";

    public static bool Present { get { Ensure(); return _present == true; } }
    public static string Report { get { Ensure(); return _report; } }

    private static void Ensure()
    {
        var missing = new System.Collections.Generic.List<string>();
        Type? Find(string asm, string name)
        {
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                if (!a.IsDynamic && a.GetName().Name == asm) return a.GetType(name, false);
            return null;
        }
        var broker = Find("Common", "Common.Messaging.IMessageBroker");
        var network = Find("Common", "Common.Network.INetwork");
        var message = Find("Common", "Common.Messaging.IMessage");
        var handler = Find("Common", "Common.Messaging.IHandler");
        var payload = Find("Common", "Common.Messaging.MessagePayload`1");
        var peer = Find("LiteNetLib", "LiteNetLib.NetPeer");
        var modInfo = Find("Common", "Common.ModInformation");
        var campaignReady = Find("GameInterface", "GameInterface.Services.GameState.Messages.CampaignReady");
        if (broker == null) missing.Add("IMessageBroker");
        if (network == null) missing.Add("INetwork");
        else
        {
            // GetMethod(name) throws AmbiguousMatchException on overloads; enumerate instead.
            var methods = network.GetMethods();
            if (!methods.Any(m => m.Name == "SendAll")) missing.Add("INetwork.SendAll");
            if (!methods.Any(m => m.Name == "Send")) missing.Add("INetwork.Send");
        }
        if (message == null) missing.Add("IMessage");
        if (handler == null) missing.Add("IHandler");
        if (payload == null) missing.Add("MessagePayload<T>");
        if (peer == null) missing.Add("NetPeer");
        if (modInfo?.GetProperty("IsServer") == null) missing.Add("ModInformation.IsServer");
        if (campaignReady == null) missing.Add("CampaignReady");
        // Not cached while Coop is absent: it may simply not be loaded yet.
        _present = missing.Count == 0;
        _report = missing.Count == 0 ? "all seams present" : "missing: " + string.Join(", ", missing);
    }
}
