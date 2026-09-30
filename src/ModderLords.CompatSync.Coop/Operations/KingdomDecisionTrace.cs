using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using ModderLords.Operations;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Election;

namespace ModderLords.CompatSync.Coop.Operations;

/// <summary>
/// Validation-only trace of the kingdom decision/policy pipeline on both sides. Bellum was shown (by an A/B run with
/// and without it) to stop resolved player policy votes from reaching clients, and no single patch explains it
/// statically, so this records every add/remove of decisions and policies, who called it, whether Coop's
/// original-allowed scope was active, and what Coop's client-side ApplyResolved received and found. Installed only
/// in the explicit Bellum validation run; it observes and never changes a result.
/// </summary>
internal static class KingdomDecisionTrace
{
    private const string HarmonyId = "ModderLords.Compat.KingdomDecisionTrace";
    private static readonly Harmony harmony = new Harmony(HarmonyId);
    private static bool installed;
    private static Func<bool>? originalAllowed;

    public static void Install()
    {
        if (installed || Environment.GetEnvironmentVariable(OperationValidationMode.EnvironmentVariable) != OperationValidationMode.Bellum131Token) return;
        installed = true;
        var first = new HarmonyMethod(typeof(KingdomDecisionTrace), nameof(KingdomPrefix)) { priority = Priority.First };
        foreach (var name in new[] { "AddDecision", "RemoveDecision", "AddPolicy", "RemovePolicy" })
            TryPatch(AccessTools.Method(typeof(Kingdom), name), first);
        TryPatch(AccessTools.Method(typeof(KingdomPolicyDecision), "ApplyChosenOutcome"),
            new HarmonyMethod(typeof(KingdomDecisionTrace), nameof(OutcomePrefix)) { priority = Priority.First });
        // MODDERLORDS_TRACE_ELECTION=0 leaves KingdomElection unpatched, to rule out the trace itself changing patch order.
        if (Environment.GetEnvironmentVariable("MODDERLORDS_TRACE_ELECTION") != "0")
        foreach (var name in new[] { "ApplyChosenOutcome", "HandleInfluenceCosts", "ApplySecondaryEffects" })
            TryPatch(AccessTools.Method(typeof(KingdomElection), name),
                new HarmonyMethod(typeof(KingdomDecisionTrace), nameof(StepPrefix)) { priority = Priority.First },
                new HarmonyMethod(typeof(KingdomDecisionTrace), nameof(StepPostfix)) { priority = Priority.Last },
                new HarmonyMethod(typeof(KingdomDecisionTrace), nameof(StepFinalizer)));
        var manager = AccessTools.TypeByName("GameInterface.Services.Kingdoms.KingdomDecisionVoteManager");
        TryPatch(manager == null ? null : AccessTools.Method(manager, "ApplyResolved"),
            new HarmonyMethod(typeof(KingdomDecisionTrace), nameof(ApplyResolvedPrefix)) { priority = Priority.First },
            new HarmonyMethod(typeof(KingdomDecisionTrace), nameof(ApplyResolvedPostfix)));
        var policy = AccessTools.TypeByName("GameInterface.Policies.CallOriginalPolicy") ?? AccessTools.AllTypes().FirstOrDefault(t => t.Name == "CallOriginalPolicy");
        var allowed = policy == null ? null : AccessTools.Method(policy, "IsOriginalAllowed");
        if (allowed != null) originalAllowed = () => { try { return (bool)allowed.Invoke(null, null); } catch { return false; } };
        Log.Info("[decision-trace] installed; original-allowed probe " + (originalAllowed != null ? "available" : "missing"));
        DumpSharedPatches();
    }

    /// <summary>
    /// Every method more than one mod patches, with each patch's owner and run order. This is the ground truth for
    /// how Bellum and Coop interact, including patches whose targets are chosen in code and so invisible statically.
    /// </summary>
    private static void DumpSharedPatches()
    {
        try
        {
            int count = 0;
            foreach (var method in Harmony.GetAllPatchedMethods().ToList())
            {
                var info = Harmony.GetPatchInfo(method);
                if (info == null) continue;
                var all = info.Prefixes.Select(p => ("pre", p)).Concat(info.Postfixes.Select(p => ("post", p)))
                    .Concat(info.Transpilers.Select(p => ("trans", p))).Concat(info.Finalizers.Select(p => ("fin", p))).ToList();
                var owners = all.Select(x => Family(x.p.owner)).Distinct().ToList();
                if (!owners.Contains("Bellum") || owners.Count < 2) continue;
                count++;
                Log.Info($"[decision-trace] {Side} shared {method.DeclaringType?.Name}.{method.Name}: " + string.Join("; ",
                    all.OrderBy(x => x.Item1).ThenByDescending(x => x.p.priority).ThenBy(x => x.p.index)
                       .Select(x => $"{x.Item1}#{x.p.index} p{x.p.priority} {Family(x.p.owner)}:{x.p.PatchMethod.DeclaringType?.Name}.{x.p.PatchMethod.Name}")));
            }
            Log.Info($"[decision-trace] {Side} {count} method(s) patched by Bellum and another mod");
        }
        catch (Exception ex) { Log.Warn("[decision-trace] shared patch dump failed: " + ex.GetBaseException().Message); }
    }

    private static string Family(string owner) =>
        owner.IndexOf("bellum", StringComparison.OrdinalIgnoreCase) >= 0 ? "Bellum"
        : owner.StartsWith("ModderLords", StringComparison.Ordinal) ? "ModderLords"
        : owner.IndexOf("coop", StringComparison.OrdinalIgnoreCase) >= 0 || owner.StartsWith("GameInterface", StringComparison.Ordinal) ? "Coop"
        : owner;

    private static void TryPatch(MethodBase? target, HarmonyMethod prefix, HarmonyMethod? postfix = null, HarmonyMethod? finalizer = null)
    {
        if (target == null) { Log.Warn("[decision-trace] target missing"); return; }
        try { harmony.Patch(target, prefix: prefix, postfix: postfix, finalizer: finalizer); }
        catch (Exception ex) { Log.Warn("[decision-trace] could not patch " + target.Name + ": " + ex.GetBaseException().Message); }
    }

    private static string Side => OperationProcessSide.IsServer ? "server" : "client";
    private static string Allowed => originalAllowed == null ? "?" : originalAllowed() ? "allowed" : "gated";

    private static string Callers()
    {
        var frames = new StackTrace(2, false).GetFrames() ?? Array.Empty<StackFrame>();
        return string.Join(" < ", frames.Select(f => f.GetMethod()).Where(m => m != null && m.DeclaringType != null
                && !m.DeclaringType.FullName!.StartsWith("HarmonyLib", StringComparison.Ordinal)
                && !m.DeclaringType.FullName.StartsWith("System.", StringComparison.Ordinal))
            .Take(6).Select(m => m!.DeclaringType!.Name + "." + m.Name));
    }

    private static void KingdomPrefix(Kingdom __instance, MethodBase __originalMethod, object[] __args)
    {
        try
        {
            var arg = __args.Length > 0 ? __args[0] : null;
            var what = arg is KingdomPolicyDecision d ? "policy-decision " + d.Policy?.StringId
                : arg is KingdomDecision k ? k.GetType().Name
                : arg is PolicyObject p ? p.StringId : arg?.GetType().Name ?? "null";
            Log.Info($"[decision-trace] {Side} {__originalMethod.Name} kingdom={__instance?.StringId} {what} decisions={__instance?.UnresolvedDecisions?.Count} policies={__instance?.ActivePolicies?.Count} scope={Allowed} via {Callers()}");
        }
        catch { }
    }

    private static bool sharedDumpedAtResolution;

    private static void StepPrefix(MethodBase __originalMethod, KingdomDecision ____decision)
    {
        try
        {
            if (!sharedDumpedAtResolution && __originalMethod.Name == "ApplyChosenOutcome") { sharedDumpedAtResolution = true; DumpSharedPatches(); }
            Log.Info($"[decision-trace] {Side} enter KingdomElection.{__originalMethod.Name} {____decision?.GetType().Name} kingdom={____decision?.Kingdom?.StringId}");
        }
        catch { }
    }

    private static void StepPostfix(MethodBase __originalMethod) =>
        Log.Info($"[decision-trace] {Side} exit KingdomElection.{__originalMethod.Name}");

    private static Exception? StepFinalizer(MethodBase __originalMethod, Exception? __exception)
    {
        if (__exception != null)
            Log.Warn($"[decision-trace] {Side} KingdomElection.{__originalMethod.Name} THREW {__exception.GetType().Name}: {__exception.Message} | {__exception.StackTrace?.Replace(Environment.NewLine, " | ")}");
        return __exception;
    }

    private static void OutcomePrefix(KingdomPolicyDecision __instance)
    {
        try { Log.Info($"[decision-trace] {Side} ApplyChosenOutcome policy={__instance?.Policy?.StringId} kingdom={__instance?.Kingdom?.StringId} scope={Allowed} via {Callers()}"); }
        catch { }
    }

    private static void ApplyResolvedPrefix(string kingdomId, int decisionIndex, int outcomeIndex, out int __state)
    {
        __state = -1;
        try
        {
            var kingdom = Kingdom.All.FirstOrDefault(k => k.StringId == kingdomId || "Kingdom_" + k.StringId == kingdomId);
            __state = kingdom?.UnresolvedDecisions?.Count ?? -1;
            var at = kingdom != null && decisionIndex >= 0 && decisionIndex < __state ? kingdom.UnresolvedDecisions[decisionIndex].GetType().Name : "none";
            Log.Info($"[decision-trace] {Side} ApplyResolved kingdomId={kingdomId} index={decisionIndex} outcome={outcomeIndex} localDecisions={__state} atIndex={at}");
        }
        catch (Exception ex) { Log.Warn("[decision-trace] ApplyResolved prefix: " + ex.GetBaseException().Message); }
    }

    private static void ApplyResolvedPostfix(string kingdomId, int __state)
    {
        try
        {
            var kingdom = Kingdom.All.FirstOrDefault(k => k.StringId == kingdomId || "Kingdom_" + k.StringId == kingdomId);
            Log.Info($"[decision-trace] {Side} ApplyResolved done kingdomId={kingdomId} decisions {__state} -> {kingdom?.UnresolvedDecisions?.Count} policies={kingdom?.ActivePolicies?.Count}");
        }
        catch { }
    }
}
