using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using ModderLords.CompatSync;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;

namespace ModderLords.CompatSync.Coop.Taom;

/// <summary>
/// TAOM's Field Commission in co-op (TAOM-MAP checklist). Soldiers of your party earn merit for kills in battles won
/// against the odds; once a troop type has enough, TAOM offers to promote one of them into a companion.
///
/// TAOM runs all of it only where it is the authority. In co-op the battle runs on the clients (Coop's Missions) and
/// the server has no mission, so merit was never earned. Chosen model (as for special resources): each player's
/// client owns their merit bank. On a co-op client, TAOM's own kill counting (only kills by this client's main party
/// troops, so each kill is counted once, on the killer's machine), battle scoring and offer prompts run as they do in
/// single player. Only the promotion itself, which creates a hero and takes the soldier from the party, is sent to the
/// server, which runs TAOM's own completion for that player (PlayerScope) without touching its own (empty) merit bank.
/// Limit: the merit bank lives in the client's session state, so it starts again after a reconnect.
/// </summary>
internal sealed class FieldCommissionComponent : ITaomComponent
{
    public const string Feature = "fieldcommission";

    private static Assembly? _taom;
    private static MethodInfo? _isAuthority;
    private static MethodInfo? _onRenamed;
    private static MethodInfo? _close;
    private static MethodInfo? _completeOffer;
    private static MethodInfo? _recordPromoted;
    private static MethodInfo? _createCompanion;
    private static ConstructorInfo? _offerCtor;
    private static readonly List<MethodInfo> AuthorityWindows = new List<MethodInfo>();

    public string Id => "field-commission";

    public string? SkipReason(TaomContext context)
    {
        var t = _taom = context.Taom;
        BindBattleWatch(t);
        const BindingFlags inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        _isAuthority = t.GetType("TAOM.Features.CoopInterop.CoopSessionProvider", false)?.GetProperty("IsAuthority")?.GetGetMethod();
        var offer = t.GetType("TAOM.Features.FieldCommission.Domain.PendingPromotionOffer", false);
        _offerCtor = offer?.GetConstructor(new[] { typeof(string), typeof(string) });
        var flow = t.GetType("TAOM.Features.FieldCommission.FieldCommissionOfferFlowService", false);
        _onRenamed = offer == null ? null : flow?.GetMethod("OnRenamed", inst, null, new[] { offer, typeof(string) }, null);
        _close = flow?.GetMethod("Close", inst, null, Type.EmptyTypes, null);
        var merit = t.GetType("TAOM.Features.FieldCommission.FieldCommissionMeritService", false);
        _completeOffer = merit?.GetMethod("CompleteOffer", new[] { typeof(string) });
        _recordPromoted = merit?.GetMethod("RecordPromotedHero", new[] { typeof(string) });
        _createCompanion = t.GetType("TAOM.Adapters.HeroCommissionAdapter", false)?.GetMethods()
            .FirstOrDefaultNamed("CreateCompanionFromTroop");

        AuthorityWindows.Clear();
        var logic = t.GetType("TAOM.Features.FieldCommission.Hooks.FieldCommissionMissionLogic", false);
        var behavior = t.GetType("TAOM.Features.FieldCommission.Hooks.FieldCommissionBehavior", false);
        foreach (var m in new[]
                 {
                     logic?.GetMethod("OnAgentRemoved", inst),
                     behavior?.GetMethod("OnMapEventStarted", inst),
                     behavior?.GetMethod("OnMapEventEnded", inst),
                     behavior?.GetMethod("OnTick", inst),
                 })
            if (m != null) AuthorityWindows.Add(m);

        var missing = new List<string>();
        if (_isAuthority == null) missing.Add("CoopSessionProvider.IsAuthority");
        if (_offerCtor == null || _onRenamed == null || _close == null) missing.Add("FieldCommissionOfferFlowService.OnRenamed/Close, PendingPromotionOffer(string, string)");
        if (_completeOffer == null || _recordPromoted == null) missing.Add("FieldCommissionMeritService.CompleteOffer/RecordPromotedHero");
        if (_createCompanion?.ReturnType != typeof(string)) missing.Add("HeroCommissionAdapter.CreateCompanionFromTroop");
        if (AuthorityWindows.Count != 4) missing.Add("FieldCommission mission logic / behaviour handlers");
        return missing.Count == 0 ? null : "TAOM changed; not found: " + string.Join(", ", missing);
    }

    public string Install(TaomContext context)
    {
        var h = new Harmony("ModderLords.Taom.FieldCommission");
        h.Patch(_isAuthority!, postfix: new HarmonyMethod(typeof(FieldCommissionComponent), nameof(AuthorityPostfix)));
        if (context.IsServer)
        {
            var skip = new HarmonyMethod(typeof(FieldCommissionComponent), nameof(ServerMeritSkip));
            h.Patch(_completeOffer!, prefix: skip);
            h.Patch(_recordPromoted!, prefix: skip);
            h.Patch(_createCompanion!, postfix: new HarmonyMethod(typeof(FieldCommissionComponent), nameof(CapturePostfix)));
            TaomActions.Register(Feature, ServerAction);
            return "server completes players' promotions";
        }
        foreach (var m in AuthorityWindows)
            h.Patch(m, prefix: new HarmonyMethod(typeof(FieldCommissionComponent), nameof(OpenWindow)),
                finalizer: new HarmonyMethod(typeof(FieldCommissionComponent), nameof(CloseWindow)));
        h.Patch(_onRenamed!, prefix: new HarmonyMethod(typeof(FieldCommissionComponent), nameof(RenamedPrefix)));
        TaomActions.RegisterClientApply(Feature, ClientApply);
        return "client earns merit and gets offers; promotions complete on the server";
    }

    private static object? Resolve(string name) => _taom == null ? null : TaomActions.Resolve(_taom, name);

    // ---- client: TAOM's own merit logic, with this client as the authority for its own player ------------

    [ThreadStatic] private static int _window;

    private static void OpenWindow(out bool __state)
    {
        __state = TaomActions.IsCoopClient;
        if (__state) _window++;
    }

    private static Exception? CloseWindow(bool __state, Exception? __exception)
    {
        if (__state) _window--;
        return __exception;
    }

    private static void AuthorityPostfix(ref bool __result)
    {
        if (_window > 0 || _serverPromoting) __result = true;
    }

    private static object? _pendingFlow;

    /// <summary>Client: the player accepted and named the promotion; the server creates the companion.</summary>
    private static bool RenamedPrefix(object __instance, object offer, string chosenName)
    {
        if (!TaomActions.IsCoopClient) return true;
        var troopId = offer.GetType().GetProperty("TroopId")?.GetValue(offer) as string ?? "";
        _pendingFlow = __instance;
        TaomActions.Send!(Feature, "promote", new[] { troopId, chosenName ?? "" });
        Log.Info($"TAOM layer: field-commission promotion of {troopId} sent to the server");
        return false;
    }

    /// <summary>Client: [troopId, heroId] on success, [troopId] on refusal. Books the promotion and closes the offer.</summary>
    private static void ClientApply(IList<string> data)
    {
        var merit = Resolve("TAOM.Features.FieldCommission.IFieldCommissionMeritService");
        if (data.Count >= 2 && merit != null)
        {
            _completeOffer!.Invoke(merit, new object[] { data[0] });
            _recordPromoted!.Invoke(merit, new object[] { data[1] });
        }
        if (_pendingFlow != null) { _close!.Invoke(_pendingFlow, null); _pendingFlow = null; }
    }

    // ---- client: the battle's start and end, which Coop does not raise on a client -----------------------

    private static Type? _behaviorType;
    private static FieldInfo? _tracked;
    private static FieldInfo? _meritField;
    private static FieldInfo? _configField;
    private static MethodInfo? _onStarted;
    private static MethodInfo? _endBattle;
    private static TaleWorlds.CampaignSystem.MapEvents.MapEvent? _watched;
    private static TaleWorlds.Core.BattleSideEnum _watchedSide;

    internal static void BindBattleWatch(Assembly taom)
    {
        const BindingFlags inst = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        _behaviorType = taom.GetType("TAOM.Features.FieldCommission.Hooks.FieldCommissionBehavior", false);
        _tracked = _behaviorType?.GetField("_trackedMapEvent", inst);
        _meritField = _behaviorType?.GetField("_merit", inst);
        _configField = _behaviorType?.GetField("_configProvider", inst);
        _onStarted = _behaviorType?.GetMethod("OnMapEventStarted", inst);
        _endBattle = taom.GetType("TAOM.Features.FieldCommission.IFieldCommissionMeritService", false)?.GetMethod("EndBattle", new[] { typeof(bool) });
    }

    private static object? Behavior()
    {
        if (_behaviorType == null || Campaign.Current == null) return null;
        var getter = typeof(Campaign).GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefaultGeneric("GetCampaignBehavior");
        return getter?.MakeGenericMethod(_behaviorType).Invoke(Campaign.Current, null);
    }

    /// <summary>
    /// Client, from Bridge.Tick. Coop finalizes a battle on the server only (a client's MapEvent.FinalizeEventAux is
    /// refused), and the game raises MapEventEnded from there, so TAOM's merit window never closed on a client and
    /// merit never banked. The client watches its own party's battle instead: on entering it, TAOM's start handler
    /// runs if TAOM did not already see the start; on leaving it, the battle is scored as TAOM scores it (won = the
    /// winning side is the one the party fought on), unless TAOM's own end handler already closed the window.
    /// </summary>
    internal static void ClientTick()
    {
        if (!TaomActions.IsCoopClient || _tracked == null || _onStarted == null || _endBattle == null) return;
        var party = MobileParty.MainParty;
        if (party == null) return;
        var current = party.MapEvent;
        try
        {
            if (current != null && _watched == null)
            {
                _watched = current;
                _watchedSide = party.MapEventSide?.MissionSide ?? TaleWorlds.Core.BattleSideEnum.None;
                if (Behavior() is { } behavior && _tracked.GetValue(behavior) == null)
                {
                    _window++;
                    try { _onStarted.Invoke(behavior, new object?[] { current, current.AttackerSide?.LeaderParty, current.DefenderSide?.LeaderParty }); }
                    finally { _window--; }
                }
                return;
            }
            if (_watched != null && current != _watched)
            {
                var ended = _watched;
                _watched = null;
                if (Behavior() is not { } behavior || _tracked.GetValue(behavior) != ended) return;   // TAOM closed it itself
                _tracked.SetValue(behavior, null);
                var enabled = _configField?.GetValue(behavior) is { } cfg
                    && cfg.GetType().GetMethod("GetConfig")?.Invoke(cfg, null) is { } config
                    && config.GetType().GetProperty("Enabled")?.GetValue(config) is true;
                var won = enabled && _watchedSide != TaleWorlds.Core.BattleSideEnum.None && ended.WinningSide == _watchedSide;
                var merit = _meritField?.GetValue(behavior);
                if (merit == null) return;
                _window++;
                try { _endBattle.Invoke(merit, new object[] { won }); }
                finally { _window--; }
                Log.Info($"TAOM layer: field-commission battle scored on this client (won={won})");
            }
        }
        catch (Exception ex) { Log.Warn("TAOM layer: field-commission battle watch failed: " + ex.GetBaseException().Message); }
    }

    // ---- server ----------------------------------------------------------------------------------------

    [ThreadStatic] private static bool _serverPromoting;
    [ThreadStatic] private static string? _createdHeroId;

    /// <summary>Server: the merit bank is the client's; TAOM's completion must not book against the server's.</summary>
    private static bool ServerMeritSkip() => !_serverPromoting;

    private static void CapturePostfix(string __result)
    {
        if (_serverPromoting) _createdHeroId = __result;
    }

    private static TaomActionOutcome ServerAction(Hero hero, MobileParty? party, string op, IList<string> args)
    {
        if (op != "promote" || args.Count != 2) return TaomActionOutcome.Fail("");
        var troopId = args[0];
        var fail = new TaomActionOutcome(false, "", new List<string> { troopId });
        var flow = Resolve("TAOM.Features.FieldCommission.IFieldCommissionOfferFlowService");
        var troop = CharacterObject.Find(troopId);
        if (flow == null || troop == null || party == null) return new TaomActionOutcome(false, "The promotion could not be completed on the server.", fail.Data);

        _serverPromoting = true;
        _createdHeroId = null;
        try
        {
            var offer = _offerCtor!.Invoke(new object[] { troopId, troop.Name.ToString() });
            _onRenamed!.Invoke(flow, new[] { offer, string.IsNullOrWhiteSpace(args[1]) ? null : args[1] });
        }
        finally { _serverPromoting = false; }

        if (string.IsNullOrEmpty(_createdHeroId))
            return new TaomActionOutcome(false, "The soldier could not be promoted (they may have left the party).", fail.Data);
        Log.Info($"TAOM layer: {troopId} promoted to companion '{_createdHeroId}' for {hero.Name}");
        return new TaomActionOutcome(true, "", new List<string> { troopId, _createdHeroId! });
    }
}

internal static class MethodListExtensions
{
    internal static MethodInfo? FirstOrDefaultGeneric(this MethodInfo[] methods, string name)
    {
        foreach (var m in methods)
            if (m.Name == name && m.IsGenericMethodDefinition && m.GetParameters().Length == 0) return m;
        return null;
    }

    internal static MethodInfo? FirstOrDefaultNamed(this MethodInfo[] methods, string name)
    {
        foreach (var m in methods)
            if (m.Name == name && m.DeclaringType == m.ReflectedType) return m;
        return null;
    }
}
