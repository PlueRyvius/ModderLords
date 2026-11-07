using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace ModderLords.Testing;

/// <summary>
/// What a test run needs to know from inside Coop, read by reflection so this module still references no Coop and
/// keeps working when a Coop update renames something (each part then says it is unavailable instead of failing).
/// </summary>
internal static class CoopWatch
{
    // ---- join finished ---------------------------------------------------------------------------------------

    /// <summary>When Coop's client published ClientCampaignReady: the join data applied, time caught up, loading screen hidden.</summary>
    internal static DateTime? JoinReadyAt { get; private set; }

    /// <summary>Why the join signal cannot be watched, or null while it can.</summary>
    internal static string? JoinUnavailable { get; private set; } = "not armed yet";

    // Coop's broker holds only a weak reference to a listener's target (a static method has none and is dropped as
    // dead), so the listener is an instance kept alive here for the whole process.
    private static object? _listener;

    /// <summary>Subscribes to Coop's ClientCampaignReady on its message broker. Safe to call repeatedly.</summary>
    internal static void ArmJoin()
    {
        if (_listener != null) return;
        try
        {
            var ready = FindType("Coop.Core", "Coop.Core.Client.Messages.ClientCampaignReady");
            var brokerType = FindType("Common", "Common.Messaging.MessageBroker");
            var payloadOpen = FindType("Common", "Common.Messaging.MessagePayload`1");
            if (ready == null || brokerType == null || payloadOpen == null)
            {
                JoinUnavailable = "Coop's ClientCampaignReady, MessageBroker or MessagePayload is not loaded";
                return;
            }
            var broker = brokerType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null, null);
            var subscribe = brokerType.GetMethods().FirstOrDefault(m => m.Name == "Subscribe" && m.IsGenericMethodDefinition && m.GetParameters().Length == 1);
            if (broker == null || subscribe == null) { JoinUnavailable = "MessageBroker.Instance or Subscribe<T> not found"; return; }

            var payload = payloadOpen.MakeGenericType(ready);
            var listener = new JoinListener();
            var handler = Delegate.CreateDelegate(typeof(Action<>).MakeGenericType(payload), listener,
                typeof(JoinListener).GetMethod(nameof(JoinListener.OnReady))!.MakeGenericMethod(payload));
            subscribe.MakeGenericMethod(ready).Invoke(broker, new object[] { handler });
            _listener = listener;
            JoinUnavailable = null;
        }
        catch (Exception ex)
        {
            JoinUnavailable = "subscribing failed: " + ex.GetBaseException().Message;
        }
    }

    private sealed class JoinListener
    {
        public void OnReady<TPayload>(TPayload _) => JoinReadyAt = DateTime.UtcNow;
    }

    // ---- message trace ---------------------------------------------------------------------------------------

    /// <summary>Coop messages a test run logs as they pass through Coop's broker: the trade and the roster writes it causes.</summary>
    internal static readonly string[] TracedMessages =
    {
        "GameInterface.Services.Inventory.Messages.TradeAttempted",       // player's game, at Done: what it is about to send
        "GameInterface.Services.Inventory.Messages.CompleteTrade",        // server: the trade as it arrived
        "GameInterface.Services.ItemRosters.Messages.ItemRosterUpdated",  // server: each roster change it sends out
        "GameInterface.Services.Inventory.Messages.NetworkTradeCompleted",
    };

    private static readonly List<object> TraceListeners = new List<object>();
    private static bool _traceArmed;
    private static int _traceLines;
    private const int MaxTraceLines = 2000;

    /// <summary>The traced message types that could not be subscribed, or null before arming.</summary>
    internal static string? TraceMissing { get; private set; }

    /// <summary>Subscribes to <see cref="TracedMessages"/> on Coop's broker, writing each to <paramref name="log"/>. Once.</summary>
    internal static void ArmTrace(Action<string> log)
    {
        if (_traceArmed) return;
        _traceArmed = true;
        // Labels now, on the game thread, so tracing a roster update costs a lookup instead of a scan of the world.
        try { LabelRosters(); }
        catch (Exception ex) { log("coop-trace: rosters not labelled: " + ex.GetBaseException().Message); }
        var missing = new List<string>();
        try
        {
            var brokerType = FindType("Common", "Common.Messaging.MessageBroker");
            var payloadOpen = FindType("Common", "Common.Messaging.MessagePayload`1");
            var broker = brokerType?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null, null);
            var subscribe = brokerType?.GetMethods().FirstOrDefault(m => m.Name == "Subscribe" && m.IsGenericMethodDefinition && m.GetParameters().Length == 1);
            if (broker == null || subscribe == null || payloadOpen == null) { TraceMissing = "Coop's MessageBroker not found"; return; }
            foreach (var name in TracedMessages)
            {
                var type = FindType("GameInterface", name);
                if (type == null) { missing.Add(name); continue; }
                var payload = payloadOpen.MakeGenericType(type);
                var listener = new TraceListener(type.Name, log);
                var handler = Delegate.CreateDelegate(typeof(Action<>).MakeGenericType(payload), listener,
                    typeof(TraceListener).GetMethod(nameof(TraceListener.On))!.MakeGenericMethod(payload));
                subscribe.MakeGenericMethod(type).Invoke(broker, new object[] { handler });
                TraceListeners.Add(listener);   // Coop's broker holds listeners weakly
            }
            TraceMissing = missing.Count == 0 ? "" : string.Join(", ", missing);
        }
        catch (Exception ex) { TraceMissing = "arming failed: " + ex.GetBaseException().Message; }
    }

    private sealed class TraceListener
    {
        private readonly string _name;
        private readonly Action<string> _log;
        public TraceListener(string name, Action<string> log) { _name = name; _log = log; }

        public void On<TPayload>(TPayload payload)
        {
            try
            {
                if (System.Threading.Interlocked.Increment(ref _traceLines) > MaxTraceLines) return;
                var what = payload?.GetType().GetProperty("What")?.GetValue(payload, null);
                _log($"coop-trace {_name}: {Describe(what)}");
            }
            catch (Exception ex) { _log($"coop-trace {_name}: could not describe ({ex.GetBaseException().Message})"); }
        }
    }

    /// <summary>A message's public fields; rosters and roster data as item x amount, so a trade reads at a glance.</summary>
    private static string Describe(object? message)
    {
        if (message == null) return "(null)";
        var parts = new List<string>();
        foreach (var field in message.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            var value = field.GetValue(message);
            parts.Add(field.Name + "=" + DescribeValue(value));
        }
        return string.Join("; ", parts);
    }

    private static string DescribeValue(object? value)
    {
        switch (value)
        {
            case null: return "null";
            case string s: return s;
            case TaleWorlds.CampaignSystem.Roster.ItemRoster roster:
                return $"roster[{RosterLabel(roster)}]{{{string.Join(", ", roster.Select(e => $"{e.EquipmentElement.Item?.StringId ?? "(empty)"} x{e.Amount}"))}}}";
            case TaleWorlds.Core.ItemRosterElement[] elements:
                return $"[{elements.Length}]{{{string.Join(", ", elements.Select(e => $"{e.EquipmentElement.Item?.StringId ?? "(empty)"} x{e.Amount}"))}}}";
            case TaleWorlds.ObjectSystem.MBObjectBase obj: return obj.StringId;
            case System.Collections.ICollection c: return $"({c.Count} items)";
            default: return value.ToString();
        }
    }

    /// <summary>Which roster this is, for the trace: a settlement's stash or roster, or a party's.</summary>
    private static string RosterLabel(TaleWorlds.CampaignSystem.Roster.ItemRoster roster) =>
        RosterLabels.TryGetValue(roster, out var label) ? label : "other";

    /// <summary>Every settlement's stash and roster and every party's roster, by reference, taken once when the trace is armed.</summary>
    private static readonly Dictionary<object, string> RosterLabels = new Dictionary<object, string>(new ByReference());

    private static void LabelRosters()
    {
        RosterLabels.Clear();
        foreach (var s in TaleWorlds.CampaignSystem.Settlements.Settlement.All)
        {
            if (s.Stash != null) RosterLabels[s.Stash] = s.StringId + " stash";
            if (s.ItemRoster != null) RosterLabels[s.ItemRoster] = s.StringId;
        }
        foreach (var p in TaleWorlds.CampaignSystem.Party.MobileParty.All)
            if (p.ItemRoster != null) RosterLabels[p.ItemRoster] = p.StringId;
    }

    private sealed class ByReference : IEqualityComparer<object>
    {
        public new bool Equals(object x, object y) => ReferenceEquals(x, y);
        public int GetHashCode(object obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
    }

    // ---- Coop's object ids -----------------------------------------------------------------------------------

    /// <summary>
    /// The id Coop's object manager has for <paramref name="obj"/>, "unregistered" when it has none, or "unavailable: ..."
    /// A trade whose source roster has no id reaches the server as "no source roster", and only the party is written.
    /// </summary>
    internal static string CoopId(object obj)
    {
        try
        {
            var provider = FindType("GameInterface", "GameInterface.ContainerProvider");
            var managerType = FindType("GameInterface", "GameInterface.Services.ObjectManager.IObjectManager");
            if (provider == null || managerType == null) return "unavailable: ContainerProvider or IObjectManager not loaded";
            var resolve = provider.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(m => m.Name == "TryResolve" && m.IsGenericMethodDefinition && m.GetParameters().Length == 1);
            if (resolve == null) return "unavailable: ContainerProvider.TryResolve<T> not found";
            var args = new object?[] { null };
            if (!(bool)resolve.MakeGenericMethod(managerType).Invoke(null, args)! || args[0] == null) return "unavailable: no IObjectManager resolved";
            var tryGetId = managerType.GetMethod("TryGetId", new[] { typeof(object), typeof(string).MakeByRefType() });
            if (tryGetId == null) return "unavailable: IObjectManager.TryGetId(object, out string) not found";
            var idArgs = new object?[] { obj, null };
            return (bool)tryGetId.Invoke(args[0], idArgs)! ? (string)idArgs[1]! : "unregistered";
        }
        catch (Exception ex) { return "unavailable: " + ex.GetBaseException().Message; }
    }

    // ---- Coop's logger ---------------------------------------------------------------------------------------

    /// <summary>Whether Coop's logger existed when this module was constructed (Coop sets up its log file later, at load).</summary>
    internal static string LoggerAtConstruction { get; private set; } = "not checked";

    /// <summary>The stack that first built Coop's logger, when it was built after this module was constructed.</summary>
    internal static string? LoggerFirstBuiltBy { get; private set; }

    /// <summary>The run folder's copy of Coop's log, when it could be attached; null otherwise.</summary>
    internal static string? RescueLog { get; private set; }

    /// <summary>Why the run folder's copy of Coop's log could not be attached, or null.</summary>
    internal static string? RescueFailed { get; private set; }

    private static object? _originalLazy;
    private static string? _rescuePath;

    /// <summary>
    /// Test runs only, from this module's constructor. Coop's logger is a Lazy that captures its configuration when first
    /// used; used before Coop adds its file sink, Coop's log file stays empty. Records whether it already exists, and if
    /// not, swaps in a Lazy that notes the stack of whatever builds it, adds a copy of Coop's own file sink writing to
    /// <paramref name="rescuePath"/>, and then builds the original.
    /// </summary>
    internal static void WatchLogger(string rescuePath)
    {
        _rescuePath = rescuePath;
        try
        {
            var manager = FindType("Common", "Common.Logging.LogManager");
            if (manager == null)
            {
                LoggerAtConstruction = "Coop's Common was not loaded yet";
                AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;
                return;
            }
            Arm(manager);
        }
        catch (Exception ex) { LoggerAtConstruction = "watch failed: " + ex.GetBaseException().Message; }
    }

    private static void OnAssemblyLoad(object sender, AssemblyLoadEventArgs args)
    {
        if (args.LoadedAssembly.GetName().Name != "Common") return;
        var manager = args.LoadedAssembly.GetType("Common.Logging.LogManager", false);
        if (manager == null) return;
        AppDomain.CurrentDomain.AssemblyLoad -= OnAssemblyLoad;
        try { Arm(manager); LoggerAtConstruction += " (armed when Coop's Common loaded)"; }
        catch (Exception ex) { LoggerAtConstruction = "watch failed: " + ex.GetBaseException().Message; }
    }

    private static void Arm(Type manager)
    {
        var field = manager.GetField("_logger", BindingFlags.NonPublic | BindingFlags.Static);
        var lazy = field?.GetValue(null);
        if (field == null || lazy == null) { LoggerAtConstruction = "LogManager._logger not found"; return; }
        if ((bool)lazy.GetType().GetProperty("IsValueCreated")!.GetValue(lazy, null))
        {
            LoggerAtConstruction = "already built when this module was constructed: Coop's own constructor or type initialisers built it";
            return;
        }
        LoggerAtConstruction = "not built yet when this module was constructed";
        var loggerType = lazy.GetType().GetGenericArguments()[0];
        _originalLazy = lazy;
        var build = Delegate.CreateDelegate(typeof(Func<>).MakeGenericType(loggerType),
            typeof(CoopWatch).GetMethod(nameof(Build), BindingFlags.NonPublic | BindingFlags.Static)!.MakeGenericMethod(loggerType));
        field.SetValue(null, Activator.CreateInstance(typeof(Lazy<>).MakeGenericType(loggerType), build));
    }

    private static T Build<T>()
    {
        LoggerFirstBuiltBy = Environment.StackTrace;
        AddRescueSink(typeof(T).Assembly);
        return ((Lazy<T>)_originalLazy!).Value;
    }

    /// <summary>
    /// Adds Coop's own SizeLimitedFileSink, writing to the run folder, to LogManager.Configuration just before the logger
    /// is built from it: Coop's errors then reach a file however early the logger is built. A separate file, so it never
    /// competes with Coop's own Coop_*.log.
    /// </summary>
    private static void AddRescueSink(Assembly serilog)
    {
        if (_rescuePath == null) return;
        try
        {
            var manager = FindType("Common", "Common.Logging.LogManager");
            var sinkType = FindType("Common", "Common.Logging.SizeLimitedFileSink");
            var sinkInterface = serilog.GetType("Serilog.Core.ILogEventSink", false);
            if (manager == null || sinkType == null || sinkInterface == null) { RescueFailed = "LogManager, SizeLimitedFileSink or ILogEventSink not found"; return; }
            var configuration = manager.GetProperty("Configuration", BindingFlags.Public | BindingFlags.Static)?.GetValue(null, null);
            var writeTo = configuration?.GetType().GetProperty("WriteTo")?.GetValue(configuration, null);
            var sinkMethod = writeTo?.GetType().GetMethods().FirstOrDefault(m => m.Name == "Sink" && !m.IsGenericMethodDefinition
                && m.GetParameters().Length >= 1 && m.GetParameters()[0].ParameterType == sinkInterface);
            if (writeTo == null || sinkMethod == null) { RescueFailed = "LogManager.Configuration.WriteTo.Sink not found"; return; }
            const string template = "[({ProcessId}) {Timestamp:HH:mm:ss} {Level:u3} {SourceContext}] {Message:lj}{NewLine}{Exception}";
            var sink = Activator.CreateInstance(sinkType, _rescuePath, template, 30L * 1024 * 1024, 1L * 1024 * 1024, 28L * 1024 * 1024);
            var args = sinkMethod.GetParameters().Select(p =>
                p.Position == 0 ? sink
                : p.ParameterType.IsEnum ? Enum.ToObject(p.ParameterType, p.HasDefaultValue && p.DefaultValue != null ? p.DefaultValue : 0)
                : p.HasDefaultValue ? p.DefaultValue : null).ToArray();
            sinkMethod.Invoke(writeTo, args);
            RescueLog = _rescuePath;
        }
        catch (Exception ex) { RescueFailed = ex.GetBaseException().Message; }
    }

    private static Type? FindType(string assembly, string name)
    {
        foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            if (!a.IsDynamic && a.GetName().Name == assembly)
            {
                var t = a.GetType(name, false);
                if (t != null) return t;
            }
        return null;
    }
}
