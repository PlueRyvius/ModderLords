using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Common;
using GameInterface;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.Players;
using HarmonyLib;
using ModderLords.CompatSync;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;

namespace ModderLords.CompatSync.Coop.Taom;

/// <summary>
/// Makes TAOM's Field Camp work for every player on a dedicated server (TAOM-MAP P2).
///
/// TAOM keeps camps by party id, but everything it does to one goes through "the main party": the menu's Establish,
/// Fortify, Foraging and Break act on MobileParty.MainParty, and the hourly tick (morale, foraging, breaking on capture
/// or settlement entry) only ever processes the main party's camp. On a client that is the player's party but only
/// locally; on the server it is the idle world-generation hero. So a camp pitched on a client never did anything.
/// (TAOM's own co-op patch hides the button instead.)
///
/// Client: after TAOM's own Establish / Fortify / ToggleForaging succeeds locally, or before a standing camp is broken,
/// the same operation is sent to the server. The local call still runs, which keeps TAOM's menu and overlay showing
/// the camp; if the server refuses an Establish the local camp is folded again.
/// Server: runs the same TAOM method for the sender with their hero and party standing in as the main ones, and runs
/// TAOM's hourly camp tick once per player the same way, so each player's camp gets its morale and forage.
/// Not carried: the ambush scan (a per-frame check with an on-screen prompt) stays client-local.
/// </summary>
internal sealed class FieldCampComponent : ITaomComponent
{
    private const string Owner = "ModderLords.Taom.FieldCamp";

    public string Id => "field-camp";

    public string? SkipReason(TaomContext context)
    {
        TaomFieldCamp.Bind(context.Taom);
        return TaomFieldCamp.MissingSurface;
    }

    public string Install(TaomContext context)
    {
        var harmony = new Harmony(Owner);
        if (context.IsServer)
        {
            harmony.Patch(TaomFieldCamp.HourlyTick!, postfix: new HarmonyMethod(typeof(TaomFieldCamp), nameof(TaomFieldCamp.HourlyTickPostfix)));
            return "server runs players' camp operations and ticks every player's camp hourly";
        }
        harmony.Patch(TaomFieldCamp.Establish!, postfix: new HarmonyMethod(typeof(TaomFieldCamp), nameof(TaomFieldCamp.EstablishPostfix)));
        harmony.Patch(TaomFieldCamp.Fortify!, postfix: new HarmonyMethod(typeof(TaomFieldCamp), nameof(TaomFieldCamp.FortifyPostfix)));
        harmony.Patch(TaomFieldCamp.ToggleForaging!, postfix: new HarmonyMethod(typeof(TaomFieldCamp), nameof(TaomFieldCamp.ToggleForagingPostfix)));
        harmony.Patch(TaomFieldCamp.Break!, prefix: new HarmonyMethod(typeof(TaomFieldCamp), nameof(TaomFieldCamp.BreakPrefix)));
        // The server now applies camp morale and forage for this player; the client's own hourly tick would add a
        // second, local copy. Only those two effects are silenced; the rest of the client tick (folding the camp on
        // capture or on entering a settlement) keeps TAOM's menu and overlay honest.
        foreach (var effect in new[] { TaomFieldCamp.AddMorale, TaomFieldCamp.ForageHour })
            if (effect != null)
                harmony.Patch(effect, prefix: new HarmonyMethod(typeof(TaomFieldCamp), nameof(TaomFieldCamp.SkipOnClientPrefix)));
        if (TaomFieldCamp.Stationary != null)
            harmony.Patch(TaomFieldCamp.Stationary, postfix: new HarmonyMethod(typeof(TaomFieldCamp), nameof(TaomFieldCamp.StationaryPostfix)));
        if (TaomFieldCamp.ServiceMoving != null)
            harmony.Patch(TaomFieldCamp.ServiceMoving, postfix: new HarmonyMethod(typeof(TaomFieldCamp), nameof(TaomFieldCamp.ServiceMovingPostfix)));
        if (TaomFieldCamp.Refresh != null)
            harmony.Patch(TaomFieldCamp.Refresh, postfix: new HarmonyMethod(typeof(TaomFieldCamp), nameof(TaomFieldCamp.RefreshPostfix)));
        if (TaomFieldCamp.OpenMenu != null)
            harmony.Patch(TaomFieldCamp.OpenMenu, prefix: new HarmonyMethod(typeof(TaomFieldCamp), nameof(TaomFieldCamp.OpenMenuPrefix)));
        return "client forwards camp operations to the server";
    }
}

internal static class TaomFieldCamp
{
    public const string OpEstablish = "establish";
    public const string OpFortify = "fortify";
    public const string OpForaging = "foraging";
    public const string OpBreak = "break";

    private static Type? _serviceInterface;
    private static Type? _campType;
    private static MethodInfo? _resolve;
    private static PropertyInfo? _playerCamp;
    private static FieldInfo? _activation;

    internal static MethodInfo? Establish { get; private set; }
    internal static MethodInfo? Fortify { get; private set; }
    internal static MethodInfo? ToggleForaging { get; private set; }
    internal static MethodInfo? Break { get; private set; }
    internal static MethodInfo? HourlyTick { get; private set; }
    internal static MethodInfo? OpenMenu { get; private set; }
    internal static MethodInfo? AddMorale { get; private set; }
    internal static MethodInfo? Stationary { get; private set; }
    internal static MethodInfo? Refresh { get; private set; }
    internal static MethodInfo? ServiceMoving { get; private set; }
    private static PropertyInfo? _canMakeCamp;
    internal static MethodInfo? ForageHour { get; private set; }
    internal static string? MissingSurface { get; private set; } = "not bound";

    /// <summary>Set by the client handler while a session is live: sends one operation to the server.</summary>
    internal static Action<string, int>? Send { get; set; }

    /// <summary>True while this layer itself is calling TAOM, so the patches do not forward their own calls.</summary>
    [ThreadStatic] private static bool _replaying;
    private static bool _tickingPlayers;

    internal static void Bind(Assembly taom)
    {
        var service = taom.GetType("TAOM.Features.FieldCamp.CampService", false);
        _serviceInterface = taom.GetType("TAOM.Features.FieldCamp.ICampService", false);
        _campType = taom.GetType("TAOM.Features.FieldCamp.Domain.CampType", false);
        _resolve = taom.GetType("TAOM.IoC", false)?.GetMethod("Resolve", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
        Establish = _campType == null ? null : service?.GetMethod("Establish", new[] { _campType });
        Fortify = service?.GetMethod("Fortify", Type.EmptyTypes);
        ToggleForaging = service?.GetMethod("ToggleForaging", Type.EmptyTypes);
        Break = service?.GetMethod("BreakPlayerCamp", Type.EmptyTypes);
        HourlyTick = service?.GetMethod("HourlyTick", Type.EmptyTypes);
        _playerCamp = service?.GetProperty("PlayerCamp");
        const BindingFlags inst = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        ServiceMoving = service?.GetMethod("IsMainPartyMoving", inst, null, Type.EmptyTypes, null);
        AddMorale = service?.GetMethod("AddMoraleToMainParty", inst, null, new[] { typeof(float) }, null);
        var state = taom.GetType("TAOM.Features.FieldCamp.Domain.CampState", false);
        ForageHour = state == null ? null : service?.GetMethod("ForageHour", inst, null, new[] { state }, null);
        var overlay = taom.GetType("TAOM.Features.FieldCamp.UI.FieldCampOverlayVM", false);
        OpenMenu = overlay?.GetMethod("ExecuteOpenCampMenu", Type.EmptyTypes);
        _activation = overlay?.GetField("_activation", BindingFlags.Instance | BindingFlags.NonPublic);
        Refresh = overlay?.GetMethod("Refresh", Type.EmptyTypes);
        _canMakeCamp = overlay?.GetProperty("CanMakeCamp");
        Stationary = taom.GetType("TAOM.Features.FieldCamp.UI.MapScreenCampMenuActivationQuery", false)
            ?.GetProperty("IsMainPartyStationary")?.GetGetMethod();

        var missing = new List<string>();
        if (_serviceInterface == null) missing.Add("ICampService");
        if (_campType == null || !_campType.IsEnum) missing.Add("CampType");
        if (_resolve == null || !_resolve.IsGenericMethodDefinition) missing.Add("TAOM.IoC.Resolve<T>()");
        if (Establish?.ReturnType != typeof(bool)) missing.Add("CampService.Establish(CampType)");
        if (Fortify?.ReturnType != typeof(bool)) missing.Add("CampService.Fortify()");
        if (ToggleForaging?.ReturnType != typeof(bool)) missing.Add("CampService.ToggleForaging()");
        if (Break == null) missing.Add("CampService.BreakPlayerCamp()");
        if (HourlyTick == null) missing.Add("CampService.HourlyTick()");
        if (_playerCamp == null) missing.Add("CampService.PlayerCamp");
        if (ServiceMoving?.ReturnType != typeof(bool)) missing.Add("CampService.IsMainPartyMoving()");
        if (AddMorale == null) missing.Add("CampService.AddMoraleToMainParty(float)");
        if (ForageHour == null) missing.Add("CampService.ForageHour(CampState)");
        MissingSurface = missing.Count == 0 ? null : "TAOM changed; not found: " + string.Join(", ", missing);
    }

    // ---- client ------------------------------------------------------------------------------------

    internal static void EstablishPostfix(bool __result, object type)
    {
        if (__result) Forward(OpEstablish, Convert.ToInt32(type));
    }

    internal static void FortifyPostfix(bool __result)
    {
        if (__result) Forward(OpFortify, 0);
    }

    internal static void ToggleForagingPostfix(bool __result)
    {
        if (__result) Forward(OpForaging, 0);
    }

    internal static void BreakPrefix(object __instance)
    {
        // Sent before the local call removes the camp; nothing is sent when there is no camp to break.
        if (_playerCamp?.GetValue(__instance) != null) Forward(OpBreak, 0);
    }

    // ---- the Make Camp button on a co-op client ------------------------------------------------------

    private static TaleWorlds.Library.Vec2 _lastPosition;
    private static DateTime _stillSince = DateTime.MaxValue;
    private const double StillSeconds = 0.5;

    /// <summary>
    /// TAOM enables Make Camp only while MobileParty.MainParty.IsMoving is false. For the main party that is vanilla's
    /// !Campaign.IsMainPartyWaiting, refreshed only by the campaign's own map-time tick from the party's movement
    /// target; on a Coop client the server moves the party, the local target is stale, and "moving" can stay true while
    /// the party stands still. The button is IsEnabled="@CanMakeCamp", and a disabled Gauntlet button lets the click
    /// through to the map, so the player walks instead of camping (reported 2026-09-22). During a session a client
    /// answers from what is on screen instead: the party has not moved for half a second.
    /// </summary>
    internal static void StationaryPostfix(ref bool __result)
    {
        if (Send == null || __result) return;
        __result = SeenStill();
    }

    /// <summary>
    /// CampService has its own copy of the same check (IsMainPartyMoving), used by CanEstablish (so the menu would
    /// refuse with "moving") and by the per-frame move guard (so a standing camp would prompt "break camp and move?"
    /// straight away). Same on-screen answer during a session.
    /// </summary>
    internal static void ServiceMovingPostfix(ref bool __result)
    {
        if (Send == null || !__result) return;
        __result = !SeenStill();
    }

    /// <summary>The main party's on-screen position has not changed for <see cref="StillSeconds"/>.</summary>
    private static bool SeenStill()
    {
        var party = MobileParty.MainParty;
        if (party == null) return false;
        var here = party.Position.ToVec2();
        var now = DateTime.UtcNow;
        if ((here - _lastPosition).LengthSquared > 1E-06f) { _lastPosition = here; _stillSince = now; return false; }
        if (_stillSince == DateTime.MaxValue) _stillSince = now;
        return (now - _stillSince).TotalSeconds >= StillSeconds;
    }

    private static bool? _lastCanMakeCamp;

    /// <summary>Client diagnostic: when the button's enabled state flips, log which of TAOM's five guards held.</summary>
    internal static void RefreshPostfix(object __instance)
    {
        try
        {
            if (_canMakeCamp?.GetValue(__instance) is not bool can || can == _lastCanMakeCamp) return;
            _lastCanMakeCamp = can;
            var q = _activation?.GetValue(__instance);
            if (q == null) return;
            bool P(string name) => q.GetType().GetProperty(name)?.GetValue(q) is true;
            Log.Info("TAOM layer: field-camp button " + (can ? "enabled" : "disabled") + ": mapScreenClear=" + P("IsMapScreenClear") +
                     " stationary=" + P("IsMainPartyStationary") + " inSettlement=" + P("IsMainPartyInSettlement") +
                     " inEncounter=" + P("IsMainPartyInEncounter") + " disorganized=" + P("IsMainPartyDisorganized"));
        }
        catch { }
    }

    /// <summary>Client, during a live session only (Send is set): the server applies this effect instead.</summary>
    internal static bool SkipOnClientPrefix() => Send == null;

    private static void Forward(string op, int campType)
    {
        if (_replaying || Send == null) return;
        try { Send(op, campType); }
        catch (Exception ex) { Log.Warn("TAOM layer: field-camp could not send " + op + ": " + ex.GetBaseException().Message); }
    }

    /// <summary>
    /// Diagnostic, client: TAOM greys the camp button out unless five conditions hold, and says nothing about which
    /// one failed. Logged on every click so a "button does nothing" report names the reason.
    /// </summary>
    internal static void OpenMenuPrefix(object __instance)
    {
        try
        {
            var q = _activation?.GetValue(__instance);
            if (q == null) return;
            bool P(string name) => q.GetType().GetProperty(name)?.GetValue(q) is true;
            Log.Info("TAOM layer: field-camp button clicked: mapScreenClear=" + P("IsMapScreenClear") +
                     " stationary=" + P("IsMainPartyStationary") + " inSettlement=" + P("IsMainPartyInSettlement") +
                     " inEncounter=" + P("IsMainPartyInEncounter") + " disorganized=" + P("IsMainPartyDisorganized"));
        }
        catch { }
    }

    /// <summary>Client: the server refused an Establish, so the camp the menu just raised locally is folded again.</summary>
    internal static void UndoLocalEstablish()
    {
        var service = Service();
        if (service == null || _playerCamp?.GetValue(service) == null) return;
        _replaying = true;
        try { Break!.Invoke(service, null); }
        finally { _replaying = false; }
    }

    // ---- server ------------------------------------------------------------------------------------

    /// <summary>Server, game thread: runs one camp operation for a player. Returns whether TAOM accepted it.</summary>
    internal static (bool Ran, string Detail) ServerRun(Hero hero, MobileParty? party, string op, int campType)
    {
        if (MissingSurface != null) return (false, "field-camp is off on the server: " + MissingSurface);
        if (party == null) return (false, "the player's party was not found on the server");
        var service = Service();
        if (service == null) return (false, "TAOM's camp service is not registered");

        object? result;
        _replaying = true;
        try
        {
            using (new ServerRelay.PlayerScope(hero, party))
            {
                result = op switch
                {
                    OpEstablish => Establish!.Invoke(service, new[] { Enum.ToObject(_campType!, campType) }),
                    OpFortify => Fortify!.Invoke(service, null),
                    OpForaging => ToggleForaging!.Invoke(service, null),
                    OpBreak => Break!.Invoke(service, null),
                    _ => "unknown",
                };
            }
        }
        finally { _replaying = false; }

        if (result is "unknown") return (false, "unknown camp operation '" + op + "'");
        var ran = result is not bool b || b;
        return (ran, ran ? $"{op} ran for {hero.Name}" : $"TAOM refused {op} for {hero.Name} on the server");
    }

    /// <summary>
    /// Server: after TAOM's hourly tick for its own (idle) party, run it once more for each player whose party has a
    /// camp, with that player standing in as the main party. The camp dictionary is keyed by party id, so each run
    /// sees that player's camp.
    /// </summary>
    internal static void HourlyTickPostfix(object __instance)
    {
        if (_tickingPlayers || _replaying) return;
        if (!ContainerProvider.TryResolve<IPlayerManager>(out var players) || !ContainerProvider.TryResolve<IObjectManager>(out var objects)) return;
        _tickingPlayers = true;
        try
        {
            foreach (var player in players.Players.ToList())
            {
                if (!objects.TryGetObject<Hero>(player.HeroId, out var hero) || hero == null || hero == Hero.MainHero) continue;
                objects.TryGetObject<MobileParty>(player.MobilePartyId, out var party);
                if (party == null) continue;
                try
                {
                    using (new ServerRelay.PlayerScope(hero, party))
                    {
                        if (_playerCamp!.GetValue(__instance) == null) continue;
                        HourlyTick!.Invoke(__instance, null);
                    }
                }
                catch (Exception ex) { Log.Warn($"TAOM layer: field-camp hourly tick for {hero.Name} failed: {ex.GetBaseException().Message}"); }
            }
        }
        finally { _tickingPlayers = false; }
    }

    private static object? Service() => _resolve?.MakeGenericMethod(_serviceInterface!).Invoke(null, null);
}
