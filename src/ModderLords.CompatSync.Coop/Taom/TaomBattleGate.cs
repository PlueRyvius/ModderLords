using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using GameInterface;
using HarmonyLib;
using ModderLords.CompatSync;
using TaleWorlds.MountAndBlade;

namespace ModderLords.CompatSync.Coop.Taom;

/// <summary>
/// TAOM battle logic on agents another player's machine controls (TAOM-MAP checklist: Battles).
///
/// Coop runs a battle on the clients: each agent is controlled by exactly one of them (the battle host owns the AI) and
/// the others replay what it does. TAOM's creature and combat behaviours (wargs, spiders, elephants, advanced combat,
/// cavalry AI, siege dismount, dread aura, ...) have no co-op gate, so every client runs them on every agent, including
/// agents it does not control, which can double effects or fight the controlling machine.
///
/// Which of them actually misbehave is not knowable from the code alone, so this measures first. Client, during a Coop
/// battle: when TAOM code damages, kills, changes the morale of or moves an agent this machine does NOT control, the
/// TAOM class responsible is logged ("battle gate: WargMissionBehavior -> Agent.RegisterBlow on a remote agent",
/// counted). Classes named in Configs\ModLogs\taom-battle-gate-client.txt (one class name per line, or * for all) are then
/// skipped for remote agents: the controlling machine's copy is the one that counts. The file does not exist by
/// default, so out of the box nothing is skipped.
/// </summary>
internal sealed class BattleGateComponent : ITaomComponent
{
    private static readonly string[] AgentActions =
    {
        "RegisterBlow", "Die", "MakeDead", "ChangeMorale", "SetMorale", "TeleportToPosition", "FadeOut",
        "SetMaximumSpeedLimit", "SetScriptedPosition", "SetScriptedPositionAndDirection", "DisableScriptedMovement",
    };

    private static Assembly? _taom;
    private static MethodInfo? _resolve;
    private static MethodInfo? _isLocal;
    private static MethodInfo? _tryGetInfo;

    public string Id => "battle-gate";

    public string? SkipReason(TaomContext context)
    {
        if (context.IsServer) return "server (battles run on the clients)";
        _taom = context.Taom;
        var registry = AccessTools.TypeByName("Missions.INetworkAgentRegistry");
        if (registry == null) return "Coop's Missions.INetworkAgentRegistry not found";
        _isLocal = registry.GetMethod("IsLocallyControlled", new[] { typeof(Agent) });
        _tryGetInfo = registry.GetMethods().FirstOrDefault(m => m.Name == "TryGetAgentInfo" && m.GetParameters().Length == 2
            && m.GetParameters()[0].ParameterType == typeof(Agent));
        _resolve = typeof(ContainerProvider).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(m => m.Name == "TryResolve" && m.IsGenericMethodDefinition && m.GetParameters().Length == 1)
            ?.MakeGenericMethod(registry);
        return _isLocal == null || _tryGetInfo == null || _resolve == null
            ? "Coop's agent registry API changed (IsLocallyControlled / TryGetAgentInfo / ContainerProvider.TryResolve)"
            : null;
    }

    public string Install(TaomContext context)
    {
        var h = new Harmony("ModderLords.Taom.BattleGate");
        var patched = new List<string>();
        foreach (var name in AgentActions)
            foreach (var m in typeof(Agent).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                         .Where(m => m.Name == name && !m.IsAbstract && !m.ContainsGenericParameters))
            {
                try
                {
                    h.Patch(m, prefix: new HarmonyMethod(typeof(BattleGateComponent), nameof(Prefix)));
                    patched.Add(name);
                }
                catch (Exception ex) { Log.Warn($"TAOM layer: battle gate could not watch Agent.{name}: {ex.GetBaseException().Message}"); }
            }
        var watched = 0;
        foreach (var m in TaomBehaviourOverrides())
        {
            try
            {
                h.Patch(m, prefix: new HarmonyMethod(typeof(BattleGateComponent), nameof(Enter)),
                    finalizer: new HarmonyMethod(typeof(BattleGateComponent), nameof(Leave)));
                watched++;
            }
            catch (Exception ex) { Log.Warn($"TAOM layer: battle gate could not watch {m.DeclaringType?.Name}.{m.Name}: {ex.GetBaseException().Message}"); }
        }
        LoadGate();
        return $"{watched} TAOM battle-behaviour method(s) watched; watching TAOM actions on other players' agents ({string.Join(", ", patched.Distinct())}); gated: " +
               (_gated.Count == 0 ? "none (measure only)" : string.Join(", ", _gated));
    }

    // ---- the gate file --------------------------------------------------------------------------------

    private static HashSet<string> _gated = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private static DateTime _nextGateCheck = DateTime.MinValue;
    private static DateTime _gateStamp = DateTime.MinValue;

    private static string? GatePath() => Log.SideFilePath("taom-battle-gate-", ".txt");

    private static void LoadGate()
    {
        _nextGateCheck = DateTime.UtcNow.AddSeconds(10);
        try
        {
            var path = GatePath();
            if (path == null || !File.Exists(path)) { _gated = new HashSet<string>(StringComparer.OrdinalIgnoreCase); return; }
            var stamp = File.GetLastWriteTimeUtc(path);
            if (stamp == _gateStamp) return;
            _gateStamp = stamp;
            _gated = new HashSet<string>(File.ReadAllLines(path).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("#")),
                StringComparer.OrdinalIgnoreCase);
            Log.Info("TAOM layer: battle gate now skips remote-agent actions from: " + (_gated.Count == 0 ? "nothing" : string.Join(", ", _gated)));
        }
        catch (Exception ex) { Log.Warn("TAOM layer: battle gate file unreadable: " + ex.GetBaseException().Message); }
    }

    // ---- the check --------------------------------------------------------------------------------------

    private static Mission? _mission;
    private static object? _registry;
    private static readonly Dictionary<string, int> Counts = new Dictionary<string, int>(StringComparer.Ordinal);

    /// <summary>Coop's agent registry for the current battle, or null outside a Coop battle.</summary>
    private static object? Registry()
    {
        var mission = Mission.Current;
        if (mission != _mission)
        {
            _mission = mission;
            _registry = null;
            Counts.Clear();
            if (mission != null)
            {
                var args = new object?[] { null };
                if (_resolve!.Invoke(null, args) is true) _registry = args[0];
            }
        }
        return _registry;
    }

    /// <summary>True when Coop knows this agent and another machine controls it.</summary>
    private static bool IsRemote(object registry, Agent agent)
    {
        var args = new object?[] { agent, null };
        if (_tryGetInfo!.Invoke(registry, args) is not true) return false;   // not a Coop-tracked agent
        return _isLocal!.Invoke(registry, new object[] { agent }) is not true;
    }

    /// <summary>The TAOM battle behaviour whose override is running on this thread, if any.</summary>
    [ThreadStatic] private static Type? _running;

    private static void Enter(object __instance, out Type? __state)
    {
        __state = _running;
        _running = __instance.GetType();
    }

    private static Exception? Leave(Type? __state, Exception? __exception)
    {
        _running = __state;
        return __exception;
    }

    /// <summary>Every override TAOM's battle behaviours declare (OnMissionTick, OnAgentHit, OnAgentRemoved, ...).</summary>
    private static IEnumerable<MethodInfo> TaomBehaviourOverrides()
    {
        Type[] types;
        try { types = _taom!.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray()!; }
        foreach (var t in types.Where(t => t.IsClass && !t.IsAbstract && typeof(MissionBehavior).IsAssignableFrom(t)))
            foreach (var m in t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                if (m.IsVirtual && !m.IsAbstract && m.GetBaseDefinition().DeclaringType != t && !m.ContainsGenericParameters)
                    yield return m;
    }

    private static bool Prefix(Agent __instance, MethodBase __originalMethod)
    {
        try
        {
            var caller = _running;
            if (__instance == null || caller == null) return true;   // not inside a TAOM battle behaviour: cheap exit
            if (DateTime.UtcNow >= _nextGateCheck) LoadGate();
            if (Registry() is not { } registry || !IsRemote(registry, __instance)) return true;

            var key = caller.Name + " -> Agent." + __originalMethod.Name;
            Counts.TryGetValue(key, out var n);
            Counts[key] = ++n;
            var skip = _gated.Contains("*") || _gated.Contains(caller.Name) || _gated.Contains(caller.FullName ?? "");
            if (n == 1 || n % 100 == 0)
                Log.Info($"TAOM layer: battle gate: {key} on an agent another machine controls (x{n}){(skip ? ", skipped" : "")}");
            return !skip;
        }
        catch { return true; }
    }
}
