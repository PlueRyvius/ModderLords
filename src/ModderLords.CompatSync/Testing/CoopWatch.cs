using System;
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

    // ---- Coop's logger ---------------------------------------------------------------------------------------

    /// <summary>Whether Coop's logger existed when this module was constructed (Coop sets up its log file later, at load).</summary>
    internal static string LoggerAtConstruction { get; private set; } = "not checked";

    /// <summary>The stack that first built Coop's logger, when it was built after this module was constructed.</summary>
    internal static string? LoggerFirstBuiltBy { get; private set; }

    private static object? _originalLazy;

    /// <summary>
    /// Test runs only, from this module's constructor. Coop's logger is a Lazy that captures its configuration when first
    /// used; used before Coop adds its file sink, Coop's log file stays empty. Records whether it already exists, and if
    /// not, swaps in a Lazy that notes the stack of whatever builds it and then builds the original, unchanged.
    /// </summary>
    internal static void WatchLogger()
    {
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
        return ((Lazy<T>)_originalLazy!).Value;
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
