using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using ModderLords.CompatSync;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;

namespace ModderLords.CompatSync.Coop.Taom;

/// <summary>
/// Siege defence rewards for every player (TAOM-MAP P2).
///
/// TAOM grants the reward for defending a watched siege on the defending player's own machine
/// (SiegeDefenseService.OnHourlyTickLocalPlayer -> GrantReward): Clan.Influence += reward, and ChangeRelationAction with
/// the defending faction's leader. On a Coop client neither lands: Coop's ChangeRelationAction prefix returns false on
/// clients, and clan influence is server-owned synced state. So a client who defended got nothing.
///
/// On a client in a session this replaces GrantReward: TAOM's own client bookkeeping still runs (the claim is recorded
/// in _locallyClaimed, the settlement is untracked on the map), and the claim goes to the server. The server checks the
/// player's party is inside that settlement while it is besieged, grants TAOM's configured influence and relation once
/// per player per siege, and answers with TAOM's own reward message for that faction.
/// </summary>
internal sealed class SiegeDefenseComponent : ITaomComponent
{
    public const string Feature = "siege-defense";
    private const string Owner = "ModderLords.Taom.SiegeDefense";

    private static MethodInfo? _grantReward;
    private static FieldInfo? _locallyClaimed;
    private static MethodInfo? _untrack;
    private static FieldInfo? _config;
    private static MethodInfo? _getMessages;
    private static MethodInfo? _resolve;
    private static Assembly? _taom;

    /// <summary>Server: (player hero id, settlement id, siege instance) already rewarded this session.</summary>
    private static readonly HashSet<string> Rewarded = new HashSet<string>(StringComparer.Ordinal);

    public string Id => "siege-defense";

    public string? SkipReason(TaomContext context)
    {
        var t = _taom = context.Taom;
        var service = t.GetType("TAOM.Features.Siege.SiegeDefenseService", false);
        var evt = t.GetType("TAOM.Features.Siege.Models.ActiveSiegeDefenseEvent", false);
        const BindingFlags inst = BindingFlags.Instance | BindingFlags.NonPublic;
        _grantReward = evt == null ? null : service?.GetMethod("GrantReward", inst, null, new[] { evt }, null);
        _locallyClaimed = service?.GetField("_locallyClaimed", inst);
        _untrack = service?.GetMethod("UntrackSettlement", inst, null, new[] { typeof(string) }, null);
        _config = service?.GetField("_config", inst);
        _getMessages = service?.GetMethod("GetMessages", inst, null, new[] { typeof(string) }, null);
        _resolve = service?.GetMethod("Resolve", BindingFlags.Static | BindingFlags.NonPublic, null,
            new[] { typeof(string), typeof(string), typeof(string), typeof(int), typeof(int), typeof(int) }, null);

        var missing = new List<string>();
        if (_grantReward == null) missing.Add("SiegeDefenseService.GrantReward(ActiveSiegeDefenseEvent)");
        if (_locallyClaimed?.FieldType != typeof(HashSet<string>)) missing.Add("SiegeDefenseService._locallyClaimed");
        if (_untrack == null) missing.Add("SiegeDefenseService.UntrackSettlement(string)");
        if (_config == null) missing.Add("SiegeDefenseService._config");
        if (_getMessages == null) missing.Add("SiegeDefenseService.GetMessages(string)");
        if (_resolve?.ReturnType != typeof(string)) missing.Add("SiegeDefenseService.Resolve");
        return missing.Count == 0 ? null : "TAOM changed; not found: " + string.Join(", ", missing);
    }

    public string Install(TaomContext context)
    {
        if (context.IsServer)
        {
            TaomActions.Register(Feature, ServerReward);
            return "server grants siege defence rewards to players";
        }
        new Harmony(Owner).Patch(_grantReward!, prefix: new HarmonyMethod(typeof(SiegeDefenseComponent), nameof(GrantRewardPrefix)));
        return "client sends siege defence rewards to the server";
    }

    /// <summary>Client: TAOM's bookkeeping here, the grant on the server.</summary>
    private static bool GrantRewardPrefix(object __instance, object evt)
    {
        if (!TaomActions.IsCoopClient) return true;
        try
        {
            string? E(string name) => evt.GetType().GetProperty(name)?.GetValue(evt) as string;
            var settlementId = E("SettlementId") ?? "";
            ((HashSet<string>)_locallyClaimed!.GetValue(__instance)!).Add(settlementId);
            _untrack!.Invoke(__instance, new object[] { settlementId });
            TaomActions.Send!(Feature, "reward", new[] { settlementId, E("DefenderFactionId") ?? "" });
            Log.Info($"TAOM layer: siege defence reward for {settlementId} sent to the server");
        }
        catch (Exception ex) { Log.Warn("TAOM layer: siege defence reward not sent: " + ex.GetBaseException().Message); }
        return false;
    }

    /// <summary>Server, game thread, inside PlayerScope.</summary>
    private static TaomActionOutcome ServerReward(Hero hero, MobileParty? party, string op, IList<string> args)
    {
        if (op != "reward" || args.Count != 2) return TaomActionOutcome.Fail("");
        var settlementId = args[0];
        var factionId = args[1];
        var settlement = party?.CurrentSettlement;
        if (settlement == null || settlement.StringId != settlementId)
            return TaomActionOutcome.Fail("");   // not inside the settlement on the server: no reward, nothing to show
        var siege = settlement.SiegeEvent;
        if (siege == null) return TaomActionOutcome.Fail("");

        var key = hero.StringId + "|" + settlementId + "|" + System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(siege);
        if (!Rewarded.Add(key)) return TaomActionOutcome.Fail("");

        var service = TaomActions.Resolve(_taom!, "TAOM.Features.Siege.ISiegeDefenseService");
        var config = service == null ? null : _config!.GetValue(service);
        int C(string name) => config?.GetType().GetProperty(name)?.GetValue(config) is int v ? v : 0;
        var influence = C("RewardInfluence");
        var relation = C("RewardRelation");

        if (hero.Clan != null && influence != 0) hero.Clan.Influence += influence;
        var defender = Kingdom.All.FirstOrDefault(k => k.StringId == factionId);
        if (defender?.Leader != null && defender.Leader != hero && relation != 0)
            ChangeRelationAction.ApplyRelationChangeBetweenHeroes(hero, defender.Leader, relation);
        Log.Info($"TAOM layer: siege defence reward for {hero.Name} at {settlementId}: +{influence} influence, +{relation} relation");

        // TAOM's own reward line for that faction.
        var message = $"+{influence} influence, +{relation} relation for defending {settlement.Name}.";
        try
        {
            var msgs = _getMessages!.Invoke(service, new object[] { factionId });
            var template = msgs?.GetType().GetProperty("RewardMessage")?.GetValue(msgs) as string;
            if (!string.IsNullOrEmpty(template))
                message = (string)_resolve!.Invoke(null, new object[] { template!, settlementId, "", 0, influence, relation })!;
        }
        catch { }
        return new TaomActionOutcome(true, message);
    }
}
