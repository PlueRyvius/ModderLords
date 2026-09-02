using System.Linq;
using System;
using TaleWorlds.MountAndBlade;

namespace ModularCoop.CompatSync;

/// <summary>
/// Client + server half of root compatibility (Layer 2a: settings sync). The interesting classes are the two
/// IHandler implementations: Coop discovers handlers by namespace prefix and constructs them with its container
/// when a session starts, so this submodule only has to exist, log, and keep Coop-touching code out of its own
/// JIT path (see <see cref="CoopProbe"/>).
/// </summary>
public sealed class SyncSubModule : MBSubModuleBase
{
    public const string Version = "0.1.0";

    private float _sinceBroadcastCheck;

    protected override void OnSubModuleLoad()
    {
        base.OnSubModuleLoad();
        // Nothing thrown from here may escape: the engine has no catch around submodule load and dies with 0xE0434352.
        try
        {
            Log.Info($"v{Version} loaded; Coop {(CoopProbe.Present ? "present (" + CoopProbe.Report + ")" : "not detected, settings sync idle")}; MCM {(McmBridge.Present ? "present" : "absent")}");
        }
        catch (Exception ex) { Log.Warn("load-time probe failed: " + ex); }
    }

    protected override void OnApplicationTick(float dt)
    {
        base.OnApplicationTick(dt);
        _sinceBroadcastCheck += dt;
        if (_sinceBroadcastCheck < 3f) return;
        _sinceBroadcastCheck = 0f;
        if (!CoopProbe.Present) return;
        try { TickServerBroadcast(); }
        catch (Exception ex) { Log.Warn("settings broadcast tick failed: " + ex.GetBaseException().Message); }
    }

    // Separate method so the reference to the handler type is only JIT-compiled once the probe passed.
    private static void TickServerBroadcast()
        => global::Coop.Core.Server.Services.ModularCoopCompat.Handlers.ServerSettingsHandler.Current?.BroadcastChanges();
}

internal static class Log
{
    private const string Prefix = "[ModularCoop.Compat] ";
    public static void Info(string msg) => Console.WriteLine(Prefix + msg);
    public static void Warn(string msg) => Console.WriteLine(Prefix + "WARNING " + msg);
}

/// <summary>
/// By-name probe of the Coop members the handlers use, evaluated before any method that references Coop types is
/// JIT-compiled. A Coop update that moves a member makes the feature report "disabled" instead of throwing
/// MissingMethodException inside the engine.
/// </summary>
internal static class CoopProbe
{
    private static bool? _present;
    private static string _report = "";

    public static bool Present { get { Ensure(); return _present == true; } }
    public static string Report { get { Ensure(); return _report; } }

    private static void Ensure()
    {
        if (_present.HasValue) return;
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
        _present = broker != null && missing.Count == 0;
        _report = missing.Count == 0 ? "all seams present" : "missing: " + string.Join(", ", missing);
    }
}
