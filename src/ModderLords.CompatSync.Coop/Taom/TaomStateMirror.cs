using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ModderLords.CompatSync;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TaleWorlds.CampaignSystem;

namespace ModderLords.CompatSync.Coop.Taom;

/// <summary>
/// An IDataStore that records a behaviour's SyncData values as JSON (saving) or hands them back (loading). Only the
/// value types TAOM's mirrored behaviours use are accepted; anything else throws, which switches that behaviour's
/// mirror off rather than sending something the client cannot rebuild.
/// </summary>
internal sealed class MirrorStore : IDataStore
{
    private static readonly HashSet<Type> Supported = new HashSet<Type>
    {
        typeof(int), typeof(long), typeof(float), typeof(double), typeof(bool), typeof(string),
        typeof(List<string>), typeof(List<int>),
        typeof(Dictionary<string, string>), typeof(Dictionary<string, int>), typeof(Dictionary<string, float>),
    };

    private readonly JObject _values;

    private MirrorStore(bool saving, JObject values)
    {
        IsSaving = saving;
        _values = values;
    }

    public static MirrorStore ForSaving() => new MirrorStore(true, new JObject());
    public static MirrorStore ForLoading(string json) => new MirrorStore(false, JObject.Parse(json));

    public bool IsSaving { get; }
    public bool IsLoading => !IsSaving;

    public string Json => _values.ToString(Formatting.None);

    public bool SyncData<T>(string key, ref T data)
    {
        if (!Supported.Contains(typeof(T)))
            throw new NotSupportedException($"key '{key}' has type {typeof(T).Name}, which the mirror does not carry");
        if (IsSaving)
        {
            _values[key] = data == null ? JValue.CreateNull() : JToken.FromObject(data);
            return true;
        }
        // A key the server did not send is left alone, exactly as the engine leaves it for a key absent from a save.
        if (!_values.TryGetValue(key, out var token)) return false;
        data = token.Type == JTokenType.Null ? default! : token.ToObject<T>()!;
        return true;
    }
}

/// <summary>
/// TAOM state clients never hear about (TAOM-MAP item 2 / P3). TAOM keeps campaign state in its behaviours' SyncData;
/// a client gets it once, from the save it joins with, and never again. This runs each mirrored behaviour's own SyncData
/// on the server in saving mode, sends the values when they change, and runs the same SyncData on clients in loading
/// mode, so TAOM itself restores the state the way it does from a save.
///
/// Only behaviours whose state the server alone changes are mirrored. State a client changes by its own actions
/// (special-resource balances, career picks) would be overwritten by the server's copy, so those are added once their
/// actions reach the server.
/// </summary>
internal static class TaomStateMirror
{
    /// <summary>Behaviour type -> why it is safe to mirror (server-authoritative state).</summary>
    private static readonly (string Type, string Why)[] Mirrored =
    {
        ("TAOM.Features.Diplomacy.WarOfTheRingBehavior", "phase and outcome, changed only by the host's daily check"),
        ("TAOM.Features.WarOfTheRingMomentum.WarOfTheRingMomentumBehavior", "momentum, scored only on the host"),
    };

    private const double CaptureIntervalSeconds = 10;

    private static readonly Dictionary<string, Type> Types = new Dictionary<string, Type>(StringComparer.Ordinal);
    private static readonly Dictionary<string, string> LastSent = new Dictionary<string, string>(StringComparer.Ordinal);
    private static readonly HashSet<string> Disabled = new HashSet<string>(StringComparer.Ordinal);
    private static MethodInfo? _getBehavior;
    private static DateTime _nextCapture = DateTime.MinValue;

    /// <summary>Server: set by the server handler; sends (behaviour, json) to every client.</summary>
    internal static Action<string, string>? Broadcast { get; set; }

    internal static int Bind(Assembly taom)
    {
        Types.Clear();
        foreach (var (name, _) in Mirrored)
            if (taom.GetType(name, false) is { } t && typeof(CampaignBehaviorBase).IsAssignableFrom(t))
                Types[name] = t;
        _getBehavior = typeof(Campaign).GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == "GetCampaignBehavior" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0);
        return Types.Count;
    }

    internal static IEnumerable<string> Names => Types.Keys;

    /// <summary>Server, game thread: every behaviour's current values, keyed by behaviour type.</summary>
    internal static List<(string Behaviour, string Json)> CaptureAll()
    {
        var result = new List<(string, string)>();
        foreach (var name in Types.Keys)
            if (Capture(name) is { } json) result.Add((name, json));
        return result;
    }

    /// <summary>Server, from Bridge.Tick: broadcasts behaviours whose values changed since they were last sent.</summary>
    internal static void ServerTick()
    {
        if (Broadcast == null || Campaign.Current == null || DateTime.UtcNow < _nextCapture) return;
        _nextCapture = DateTime.UtcNow.AddSeconds(CaptureIntervalSeconds);
        foreach (var (name, json) in CaptureAll())
        {
            if (LastSent.TryGetValue(name, out var last) && last == json) continue;
            LastSent[name] = json;
            Broadcast(name, json);
        }
    }

    private static string? Capture(string name)
    {
        if (Disabled.Contains(name) || Behavior(name) is not { } behavior) return null;
        try
        {
            var store = MirrorStore.ForSaving();
            behavior.SyncData(store);
            return store.Json;
        }
        catch (Exception ex)
        {
            Disabled.Add(name);
            Log.Warn($"TAOM layer: state mirror for {Short(name)} is off: {ex.GetBaseException().Message}");
            return null;
        }
    }

    /// <summary>Client, game thread: loads the server's values through the behaviour's own SyncData.</summary>
    internal static void ClientApply(string name, string json)
    {
        if (!Types.ContainsKey(name))
        {
            Log.Warn($"TAOM layer: state mirror got {Short(name)}, which this client does not mirror; update ModderLords");
            return;
        }
        if (Behavior(name) is not { } behavior) return;
        try
        {
            behavior.SyncData(MirrorStore.ForLoading(json));
            Log.Info($"TAOM layer: state mirror applied {Short(name)} from the server ({json.Length} chars)");
            ReportPlayerEvents(behavior);
        }
        catch (Exception ex) { Log.Warn($"TAOM layer: state mirror could not apply {Short(name)}: {ex.GetBaseException().Message}"); }
    }

    private static int _lastPlayerEvents = -1;

    /// <summary>
    /// Momentum only: says when the server recorded a new PLAYER event (a battle, siege or raid the players took part
    /// in, which is what counts toward TAOM's player participation), so "did I contribute?" is answered by the log.
    /// </summary>
    private static void ReportPlayerEvents(CampaignBehaviorBase behavior)
    {
        try
        {
            var store = behavior.GetType().GetField("_stateStore", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(behavior);
            if (store?.GetType().GetProperty("PlayerEvents")?.GetValue(store) is not System.Collections.IList events) return;
            if (events.Count == _lastPlayerEvents) return;
            var added = _lastPlayerEvents < 0 ? "" : " (new: " + string.Join(", ", events.Cast<object>().Skip(Math.Max(0, _lastPlayerEvents)).Select(e => e.ToString())) + ")";
            _lastPlayerEvents = events.Count;
            Log.Info($"TAOM layer: War of the Ring player events recorded by the server: {events.Count}{added}");
        }
        catch { }
    }

    /// <summary>A new session starts from nothing sent: the first capture goes out in full.</summary>
    internal static void Reset()
    {
        LastSent.Clear();
        _nextCapture = DateTime.MinValue;
    }

    private static CampaignBehaviorBase? Behavior(string name) =>
        Campaign.Current == null || _getBehavior == null || !Types.TryGetValue(name, out var t)
            ? null
            : _getBehavior.MakeGenericMethod(t).Invoke(Campaign.Current, null) as CampaignBehaviorBase;

    private static string Short(string name) => name.Substring(name.LastIndexOf('.') + 1);
}

internal sealed class StateMirrorComponent : ITaomComponent
{
    public string Id => "state-mirror";

    public string? SkipReason(TaomContext context) =>
        TaomStateMirror.Bind(context.Taom) == 0 ? "none of the mirrored TAOM behaviours were found" : null;

    public string Install(TaomContext context) =>
        (context.IsServer ? "server sends " : "client receives ") + string.Join(", ", TaomStateMirror.Names.Select(n => n.Substring(n.LastIndexOf('.') + 1)));
}
