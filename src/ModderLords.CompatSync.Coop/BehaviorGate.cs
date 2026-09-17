using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using ModderLords.CompatSync;
using Newtonsoft.Json.Linq;

namespace ModderLords.CompatSync.Coop;

/// <summary>
/// Layer 1 on the client: stops the listed behaviours from running locally so the server's copy is the only one.
/// Campaign behaviours: a prefix on their RegisterEvents skips registration (SyncData still runs, so the state the
/// host saved is loaded). Mission behaviours: a prefix on Mission.AddMissionBehavior drops instances of the listed
/// types. Everything is resolved by type name from the recipe; unknown names are logged and ignored.
/// </summary>
public static class BehaviorGate
{
    private static readonly Harmony Harmony = new Harmony("ModderLords.Compat.BehaviorGate");
    private static readonly HashSet<string> GatedMissionTypes = new HashSet<string>(StringComparer.Ordinal);
    private static readonly HashSet<string> PatchedCampaign = new HashSet<string>(StringComparer.Ordinal);
    private static bool _missionPatched;

    /// <summary>Path of recipes.json inside this module folder (both sides ship the module; only the server's copy is authoritative).</summary>
    public static string RecipesPath()
    {
        var bin = Path.GetDirectoryName(typeof(BehaviorGate).Assembly.Location) ?? ".";
        return Path.GetFullPath(Path.Combine(bin, "..", "..", "recipes.json"));
    }

    public static string? ReadLocalRecipes()
    {
        try { var p = RecipesPath(); return File.Exists(p) ? File.ReadAllText(p) : null; }
        catch { return null; }
    }

    /// <summary>Applies a recipe set on this client. Returns a one-line report.</summary>
    private static readonly Lazy<RecipeGates> Gates = new Lazy<RecipeGates>(() =>
        new RecipeGates("ModderLords.Compat.RecipeGates", IsClient, msg => Log.Warn(msg), msg => Log.Info(msg)));

    /// <summary>From the adapter tick: detach leaking postfixes whose mod attached them after the recipe arrived.</summary>
    public static void RetryPendingPostfixes()
    {
        if (!Gates.IsValueCreated || Gates.Value.PendingCount == 0) return;
        try { Gates.Value.RetryPending(); }
        catch (Exception ex) { Log.Warn("recipe: postfix retry failed: " + ex.GetBaseException().Message); }
    }

    public static string Apply(string json)
    {
        if (!LegacyRecipePolicy.Accept(json, out var refusal)) return refusal;
        var campaign = new List<string>();
        var mission = new List<string>();
        var handlers = new List<string>();
        var unpatch = new List<(string target, string patch)>();
        var relays = new List<string>();
        var stateAdded = 0;
        try
        {
            var root = JObject.Parse(json);
            if ((root["Mods"] as JArray ?? new JArray()).Any(mod =>
                new[] { "Handlers", "Unpatch", "PlayerComparisons", "Relays" }.Any(field => (mod[field] as JArray)?.Count > 0)))
                return "Recipe refused: generated transformations require a validated operation contract; regenerate legacy recipes with this launcher.";
            foreach (var mod in root["Mods"] as JArray ?? new JArray())
            {
                campaign.AddRange((mod["CampaignBehaviors"] as JArray ?? new JArray()).Select(t => t.ToString()));
                mission.AddRange((mod["MissionBehaviors"] as JArray ?? new JArray()).Select(t => t.ToString()));
                // Schema v2: generated from the code analysis.
                handlers.AddRange((mod["Handlers"] as JArray ?? new JArray()).Select(t => t.ToString()));
                // Schema v3: player actions sent to the server.
                relays.AddRange((mod["Relays"] as JArray ?? new JArray()).Select(t => t.ToString()));
                // Schema v3: static mod state the server broadcasts (a client learns the list from the server's recipe).
                foreach (var f in mod["SyncState"] as JArray ?? new JArray()) if (SettingsSources.State.Add(f.ToString())) stateAdded++;
                foreach (var u in mod["Unpatch"] as JArray ?? new JArray())
                    unpatch.Add((u["Target"]?.ToString() ?? "", u["Patch"]?.ToString() ?? ""));
            }
        }
        catch (Exception ex) { return "recipe parse failed: " + ex.Message; }

        var generated = "";
        if (stateAdded > 0) { SettingsSources.State.Refresh(); generated += $"; {stateAdded} static field(s) of mod state follow the server ({SettingsSources.State.Summary()})"; }
        if (handlers.Count + unpatch.Count > 0)
        {
            var (applied, handlersMissing) = Gates.Value.SkipHandlers(handlers);
            var (removed, notAttached) = Gates.Value.RemovePostfixes(unpatch);
            generated = $"; {applied} handler(s) gated ({handlersMissing} not found), {removed} leaking postfix(es) removed ({notAttached} not attached)";
        }
        if (relays.Count > 0 && IsClient())
        {
            var (armed, relaysMissing) = RelayGates.Apply(Harmony, relays, msg => Log.Warn(msg));
            generated += $"; {armed} relayed action(s) armed ({relaysMissing} not found)";
        }
        var trace = InstallTrace(json);
        if (trace is not null) generated += "; " + trace;

        int patched = 0, missing = 0, already = 0;
        foreach (var typeName in campaign)
        {
            if (PatchedCampaign.Contains(typeName)) { already++; continue; }
            var type = AccessTools.TypeByName(typeName);
            var method = type == null ? null : AccessTools.Method(type, "RegisterEvents");
            if (method == null) { missing++; Log.Warn("recipe: campaign behaviour not found: " + typeName); continue; }
            try
            {
                Harmony.Patch(method, prefix: new HarmonyMethod(typeof(BehaviorGate), nameof(SkipOnClientPrefix)));
                PatchedCampaign.Add(typeName);
                patched++;
                InstallCounters(type!);
            }
            catch (Exception ex) { missing++; Log.Warn("recipe: could not gate " + typeName + ": " + ex.GetBaseException().Message); }
        }
        foreach (var t in mission) GatedMissionTypes.Add(t);
        if (mission.Count > 0 && !_missionPatched)
        {
            var add = AccessTools.Method(typeof(TaleWorlds.MountAndBlade.Mission), "AddMissionBehavior");
            if (add != null)
            {
                Harmony.Patch(add, prefix: new HarmonyMethod(typeof(BehaviorGate), nameof(AddMissionBehaviorPrefix)));
                _missionPatched = true;
            }
            else Log.Warn("recipe: Mission.AddMissionBehavior not found; mission behaviours cannot be gated");
        }
        return $"{patched} campaign behaviour(s) gated ({already} already), {GatedMissionTypes.Count} mission behaviour type(s) gated, {missing} not found" + generated;
    }

    private static bool IsClient()
    {
        try { return Common.ModInformation.IsClient; } catch { return false; }
    }

    // ---- ground-truth trace ---------------------------------------------------------------------------------

    private static readonly Lazy<RootTracer> Tracer = new Lazy<RootTracer>(() =>
        new RootTracer("ModderLords.Compat.RootTracer", IsClient, msg => Log.Warn(msg)));
    private static bool _traceInstalled;

    /// <summary>
    /// Both sides: traces every entry point listed in <c>Mods[].TraceRoots</c> (present only when the launcher was
    /// asked to trace a mod). Null when the recipe traces nothing; otherwise a one-line report. Idempotent per id.
    /// </summary>
    public static string? InstallTrace(string json)
    {
        var roots = new List<string>();
        var handlers = new List<string>();
        try
        {
            foreach (var mod in JObject.Parse(json)["Mods"] as JArray ?? new JArray())
            {
                roots.AddRange((mod["TraceRoots"] as JArray ?? new JArray()).Select(t => t.ToString()));
                handlers.AddRange((mod["Handlers"] as JArray ?? new JArray()).Select(t => t.ToString()));
            }
        }
        catch { return null; }
        if (roots.Count == 0) return null;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var (applied, missing, failed) = Tracer.Value.Install(roots, handlers);
        _traceInstalled = true;
        return $"trace: {applied} entry point(s) traced ({missing} not found, {failed} not patchable) in {sw.ElapsedMilliseconds} ms";
    }

    /// <summary>Appends the cumulative counters to ModderLords.Compat-trace-{side}.jsonl. Returns how many methods were written.</summary>
    public static int TraceFlush()
    {
        if (!_traceInstalled) return 0;
        var records = RootTracer.Snapshot();
        if (records.Count == 0) return 0;
        var path = Log.SideFilePath("ModderLords.Compat-trace-", ".jsonl");
        if (path is null) return 0;
        File.AppendAllText(path, RootTracer.ToJsonLines(records, RootTracer.Now));
        return records.Count;
    }

    public static bool SkipOnClientPrefix(object __instance)
    {
        if (!IsClient()) return true;
        Announce("RegisterEvents skipped on client: " + __instance.GetType().FullName);
        return false;
    }

    public static bool AddMissionBehaviorPrefix(TaleWorlds.MountAndBlade.MissionBehavior missionBehavior)
    {
        if (!IsClient() || missionBehavior == null) return true;
        var name = missionBehavior.GetType().FullName ?? "";
        if (!GatedMissionTypes.Contains(name)) return true;
        Announce("mission behaviour dropped on client: " + name);
        return false;
    }

    private static readonly HashSet<string> Announced = new HashSet<string>(StringComparer.Ordinal);
    private static void Announce(string msg)
    {
        lock (Announced) { if (!Announced.Add(msg)) return; }
        Log.Info(msg);
    }

    // ---- verification counters --------------------------------------------------------------------------

    // Common campaign-event handler names; whichever a gated behaviour actually declares gets a counting postfix.
    private static readonly string[] CountedMethods =
    {
        "OnDailyTick", "OnHourlyTick", "OnWeeklyTick", "OnDailyTickSettlement", "OnDailyTickHero",
        "OnDailyTickClan", "OnDailyTickParty", "OnSettlementEntered", "OnRaidCompleted", "OnGameLoaded",
    };
    private static readonly Dictionary<string, long> Counts = new Dictionary<string, long>(StringComparer.Ordinal);
    private static readonly HashSet<string> CounterInstalled = new HashSet<string>(StringComparer.Ordinal);

    private static void InstallCounters(Type type)
    {
        foreach (var name in CountedMethods)
        {
            var m = AccessTools.Method(type, name);
            if (m == null || CounterInstalled.Contains(type.FullName + "." + name)) continue;
            try
            {
                Harmony.Patch(m, postfix: new HarmonyMethod(typeof(BehaviorGate), nameof(CountPostfix)));
                CounterInstalled.Add(type.FullName + "." + name);
            }
            catch { /* an overloaded/abstract target we cannot patch; skip it */ }
        }
    }

    public static void CountPostfix(MethodBase __originalMethod)
    {
        var key = (__originalMethod.DeclaringType?.Name ?? "?") + "." + __originalMethod.Name;
        lock (Counts) { Counts[key] = (Counts.TryGetValue(key, out var v) ? v : 0) + 1; }
    }

    /// <summary>
    /// One line for this side: whole-behaviour counters (server counts, a client stays at 0), then the generated handler
    /// gates (a client's skips count up as handlers fire) and any leaking postfix still waiting to be removed.
    /// </summary>
    public static string VerificationSummary()
    {
        var line = WholeBehaviourSummary();
        if (RelayGates.SentCount > 0) line += "; " + RelayGates.CountsSummary();
        if (_traceInstalled) line += "; " + RootTracer.CountsSummary();
        if (!Gates.IsValueCreated) return line;
        line += "; " + RecipeGates.CountsSummary();
        if (Gates.Value.PendingCount > 0) line += $"; {Gates.Value.PendingCount} leaking postfix(es) not attached yet";
        return line;
    }

    private static string WholeBehaviourSummary()
    {
        List<KeyValuePair<string, long>> snapshot;
        lock (Counts) { snapshot = Counts.Where(kv => kv.Value > 0).OrderBy(kv => kv.Key).ToList(); }
        var side = IsClient() ? "client" : "server";
        if (snapshot.Count == 0) return $"verification ({side}): gated behaviours ran 0 times so far" + (side == "client" ? " (expected on a client)" : "");
        return $"verification ({side}): " + string.Join(", ", snapshot.Select(kv => kv.Key + "=" + kv.Value));
    }
}
