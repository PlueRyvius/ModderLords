using System;
using System.Linq;
using System.Reflection;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.Players;
using HarmonyLib;
using ModderLords.Operations;
using TaleWorlds.CampaignSystem;

namespace ModderLords.CompatSync.Coop.Operations;

/// <summary>Changes only callback authority and the AI-clan predicate. The original award loop and settings survive.</summary>
public sealed class ClansResourceAdderAdapter : ICompatibilityAdapter
{
    private const string Owner = "ModderLords.Operations.ClansResourceAdder.v1";
    private readonly Harmony harmony = new Harmony(Owner);
    private readonly ResourceAdderPolicy policy = new ResourceAdderPolicy();
    private MethodInfo? award;
    private MethodInfo? predicate;
    private static ClansResourceAdderAdapter? current;
    public static IPlayerManager? Players { private get; set; }
    public static IObjectManager? Objects { private get; set; }
    public string Id => "clans-resource-adder.v1";
    public AdapterReadiness Readiness { get; private set; } = AdapterReadiness.Waiting;
    public string Detail { get; private set; } = "Waiting for the campaign player registry";
    public bool ValidateTargets(out string reason)
    {
        var type = AccessTools.TypeByName("ClansResourceAdder.ResourcesAdderEvents");
        award = type == null ? null : AccessTools.DeclaredMethod(type, "AddResources", Type.EmptyTypes);
        predicate = type == null ? null : AccessTools.DeclaredMethod(type, "is_ai_clan", new[] { typeof(Clan) });
        if (award == null || predicate == null || award.ReturnType != typeof(void) || predicate.ReturnType != typeof(bool))
        { reason = "Exact resource-adder signatures are missing"; return false; }
        foreach (var method in new[] { award, predicate })
        {
            var patches = Harmony.GetPatchInfo(method);
            if (patches != null && patches.Owners.Any(o => o != Owner)) { reason = "Another Harmony owner already patches " + method.Name; return false; }
        }
        reason = ""; return true;
    }
    public void Install()
    {
        if (!ValidateTargets(out var reason)) { Readiness = AdapterReadiness.Failed; Detail = reason; throw new InvalidOperationException(reason); }
        if (current != null) throw new InvalidOperationException("Resource adapter already installed");
        current = this;
        try
        {
            harmony.Patch(award!, prefix: new HarmonyMethod(typeof(ClansResourceAdderAdapter), nameof(AwardPrefix)));
            harmony.Patch(predicate!, prefix: new HarmonyMethod(typeof(ClansResourceAdderAdapter), nameof(IsAiPrefix)));
        }
        catch { Dispose(); throw; }
    }
    private static bool AwardPrefix()
    {
        var adapter = current;
        if (adapter == null || !OperationRuntime.SessionActive) return true;
        if (!Common.ModInformation.IsServer) return false;
        try
        {
            if (!OperationRuntime.CheckReadiness()) return adapter.Degrade(OperationRuntime.Failure);
            if (Campaign.Current == null || Players == null || Objects == null) return adapter.Degrade("Campaign ownership registry is unavailable");
            var clans = Players.Players.Select(p =>
            {
                Hero hero;
                // Registered players include disconnected players. Use their live hero's clan after ownership changes.
                return Objects.TryGetObject<Hero>(p.HeroId, out hero) && hero?.Clan != null ? hero.Clan.StringId : null;
            }).ToArray();
            if (!adapter.policy.UpdateOwners(clans)) return adapter.Degrade("Some registered player clans are unresolved");
            if (!adapter.ValidateTargets(out var reason)) return adapter.Degrade(reason);
            var day = (long)Math.Floor(CampaignTime.Now.ToDays);
            if (!adapter.policy.TryBeginAward(true, true, Campaign.Current, day)) return false;
            adapter.Readiness = AdapterReadiness.Ready; adapter.Detail = "Campaign authority; all registered player clans excluded";
            return adapter.policy.MayRun(true, true);
        }
        catch (Exception ex) { return adapter.Degrade("Ownership resolution failed: " + ex.GetType().Name); }
    }
    private bool Degrade(string reason)
    {
        if (Detail != reason) Log.Warn("resource-adder suspended: " + reason);
        Detail = reason; Readiness = AdapterReadiness.Degraded; policy.UpdateOwners(null); return false;
    }
    private static bool IsAiPrefix(Clan clan, ref bool __result)
    {
        if (current == null || !OperationRuntime.SessionActive) return true;
        __result = Common.ModInformation.IsServer && current.policy.IsAiClan(clan?.StringId ?? "", true, false);
        return false;
    }
    public void Dispose()
    {
        if (award != null) harmony.Unpatch(award, HarmonyPatchType.All, Owner);
        if (predicate != null) harmony.Unpatch(predicate, HarmonyPatchType.All, Owner);
        current = null; Players = null; Objects = null; Readiness = AdapterReadiness.Disabled;
    }
}
