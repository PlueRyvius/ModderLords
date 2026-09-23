using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using ModderLords.CompatSync;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.GameMenus;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Core;
using TaleWorlds.Localization;

namespace ModderLords.CompatSync.Coop.Taom;

/// <summary>
/// TAOM's Refuge in co-op (TAOM-MAP checklist: Refuge).
///
/// A refuge is a map party TAOM spawns from a player's standing camp, led by a warden (a companion, or a soldier
/// promoted into one), holding troops, prisoners and goods. TAOM has no co-op handling for it at all. On a client,
/// founding spawns a party, may create a hero, charges gold and breaks the camp, all of which Coop keeps from reaching
/// the server; the refuge's hourly and battle logic changes rosters on every machine; and the server's single refuge
/// book is read as "the player's" by whichever player TAOM is serving.
///
/// Client: choosing a warden, upgrading and dismantling are sent to the server instead of run locally, and the refuge
/// ticks that change rosters (disband cancel, militia rally and stand-down, peace-time prisoner release, load repair)
/// do not run. The refuge book comes from the server through the state mirror; TAOM's menus, build timer and map
/// visuals run locally on that copy.
/// Server: runs founding, upgrade and dismantle for the sender (PlayerScope), each player seeing only their own
/// refuges; runs militia for a refuge as its owner; and never draws refuge visuals (SandBox.View, not on a server).
/// Both: a refuge counts toward, and can be managed by, only the player whose clan owns it.
/// Garrisoning uses TAOM's own party screen on the refuge party, which Coop's party-screen sync carries to the server.
/// </summary>
internal sealed class RefugeComponent : ITaomComponent
{
    public const string Feature = "refuge";
    private const string Owner = "ModderLords.Taom.Refuge";

    private static Assembly? _taom;
    private static Type? _service;
    private static FieldInfo? _book;
    private static MethodInfo? _canFound;
    private static MethodInfo? _found;
    private static MethodInfo? _upgrade;
    private static MethodInfo? _dismantle;
    private static MethodInfo? _getByPartyId;
    private static MethodInfo? _nearestManageable;
    private static MethodInfo? _nearestDismantlable;
    private static MethodInfo? _onMapEventStarted;
    private static MethodInfo? _onMapEventEnded;
    private static MethodInfo? _onWardenChosen;
    private static MethodInfo? _reasonText;
    private static MethodInfo? _candidates;
    private static MethodInfo? _resolveWarden;
    private static MethodInfo? _unwindPromotion;
    private static readonly List<MethodInfo> ClientSkipped = new List<MethodInfo>();
    private static readonly List<MethodInfo> Visuals = new List<MethodInfo>();

    public string Id => "refuge";

    public string? SkipReason(TaomContext context)
    {
        var t = _taom = context.Taom;
        _service = t.GetType("TAOM.Features.Refuge.RefugeService", false);
        var data = t.GetType("TAOM.Features.Refuge.Domain.RefugeData", false);
        var reason = t.GetType("TAOM.Features.Refuge.RefugeBlockReason", false);
        var wardens = t.GetType("TAOM.Features.Refuge.IWardenService", false);
        var candidate = t.GetType("TAOM.Features.Refuge.WardenCandidate", false);
        var menus = t.GetType("TAOM.Features.Refuge.Hooks.RefugeMenuController", false);
        var visuals = t.GetType("TAOM.Features.Refuge.Visuals.RefugeVisualService", false);
        const BindingFlags inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        _book = _service?.GetField("_refuges", inst);
        _canFound = _service?.GetMethod("CanFound", Type.EmptyTypes);
        _found = reason == null ? null : _service?.GetMethod("Found", new[] { typeof(string), reason.MakeByRefType() });
        _upgrade = data == null ? null : _service?.GetMethod("Upgrade", new[] { data });
        _dismantle = data == null ? null : _service?.GetMethod("Dismantle", new[] { data });
        _getByPartyId = _service?.GetMethod("GetByPartyId", new[] { typeof(string) });
        _nearestManageable = _service?.GetMethod("NearestManageable", Type.EmptyTypes);
        _nearestDismantlable = _service?.GetMethod("NearestDismantlable", Type.EmptyTypes);
        _onMapEventStarted = _service?.GetMethod("OnMapEventStarted", new[] { typeof(string) });
        _onMapEventEnded = _service?.GetMethod("OnMapEventEnded", new[] { typeof(string) });
        _onWardenChosen = menus?.GetMethod("OnWardenChosen", inst, null, new[] { typeof(List<InquiryElement>) }, null);
        _reasonText = reason == null ? null : menus?.GetMethod("ReasonText", inst, null, new[] { reason, typeof(int) }, null);
        _candidates = wardens?.GetMethod("Candidates", Type.EmptyTypes);
        _resolveWarden = candidate == null ? null : wardens?.GetMethod("ResolveWarden",
            new[] { candidate, typeof(bool).MakeByRefType(), typeof(string).MakeByRefType() });
        _unwindPromotion = wardens?.GetMethod("UnwindPromotion", new[] { typeof(string), typeof(string) });

        ClientSkipped.Clear();
        foreach (var (name, args) in new (string, Type[])[]
                 {
                     ("HourlyTick", Type.EmptyTypes), ("OnMapEventStarted", new[] { typeof(string) }),
                     ("OnMapEventEnded", new[] { typeof(string) }), ("OnPeaceMade", Type.EmptyTypes),
                     ("OnPartyDisbandStarted", new[] { typeof(string) }), ("OnGameLoaded", Type.EmptyTypes),
                 })
            if (_service?.GetMethod(name, args) is { } m) ClientSkipped.Add(m);
        Visuals.Clear();
        foreach (var name in new[] { "Show", "Remove", "ClearAll", "TickWind" })
            if (visuals?.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly) is { } m)
                Visuals.Add(m);

        var missing = new List<string>();
        if (_book == null || !TaomRecords.IsRecordBook(_book.FieldType)) missing.Add("RefugeService._refuges");
        if (_canFound == null || _found == null) missing.Add("RefugeService.CanFound/Found(string, out RefugeBlockReason)");
        if (_upgrade?.ReturnType != typeof(bool) || _dismantle == null) missing.Add("RefugeService.Upgrade/Dismantle(RefugeData)");
        if (_getByPartyId == null || _nearestManageable == null || _nearestDismantlable == null) missing.Add("RefugeService.GetByPartyId/NearestManageable/NearestDismantlable");
        if (_onMapEventStarted == null || _onMapEventEnded == null) missing.Add("RefugeService.OnMapEventStarted/Ended(string)");
        if (ClientSkipped.Count != 6) missing.Add("RefugeService tick handlers");
        if (_onWardenChosen == null) missing.Add("RefugeMenuController.OnWardenChosen(List<InquiryElement>)");
        if (_candidates == null || _resolveWarden == null || _unwindPromotion == null) missing.Add("IWardenService.Candidates/ResolveWarden/UnwindPromotion");
        if (Visuals.Count != 4) missing.Add("RefugeVisualService.Show/Remove/ClearAll/TickWind");
        return missing.Count == 0 ? null : "TAOM changed; not found: " + string.Join(", ", missing);
    }

    public string Install(TaomContext context)
    {
        var h = new Harmony(Owner);
        // Both sides: only your own refuges count toward your limit and appear in your refuge menu.
        h.Patch(_canFound!, prefix: new HarmonyMethod(typeof(RefugeComponent), nameof(NarrowPrefix)),
            finalizer: new HarmonyMethod(typeof(RefugeComponent), nameof(NarrowFinalizer)));
        h.Patch(_nearestManageable!, postfix: new HarmonyMethod(typeof(RefugeComponent), nameof(OwnRowPostfix)));
        h.Patch(_nearestDismantlable!, postfix: new HarmonyMethod(typeof(RefugeComponent), nameof(OwnRowPostfix)));

        if (context.IsServer)
        {
            foreach (var visual in Visuals)
                h.Patch(visual, transpiler: new HarmonyMethod(typeof(TaomFieldCamp), nameof(TaomFieldCamp.EmptyBodyTranspiler)));
            h.Patch(_onMapEventStarted!, prefix: new HarmonyMethod(typeof(RefugeComponent), nameof(AsOwnerPrefix)));
            h.Patch(_onMapEventEnded!, prefix: new HarmonyMethod(typeof(RefugeComponent), nameof(AsOwnerPrefix)));
            TaomActions.Register(Feature, ServerAction);
            return "server founds, upgrades and dismantles players' refuges; militia runs as the owner; no visuals";
        }

        h.Patch(_onWardenChosen!, prefix: new HarmonyMethod(typeof(RefugeComponent), nameof(WardenChosenPrefix)));
        h.Patch(_upgrade!, prefix: new HarmonyMethod(typeof(RefugeComponent), nameof(UpgradePrefix)));
        h.Patch(_dismantle!, prefix: new HarmonyMethod(typeof(RefugeComponent), nameof(DismantlePrefix)));
        foreach (var m in ClientSkipped)
            h.Patch(m, prefix: new HarmonyMethod(typeof(RefugeComponent), nameof(ClientSkipPrefix)));
        TaomActions.RegisterClientApply(Feature, ClientApply);
        TaomStateMirror.OnApplied("TAOM.Features.Refuge.Hooks.RefugeCampaignBehavior", AfterMirror);
        return "client sends refuge founding, upgrade and dismantle to the server; refuge ticks run on the server";
    }

    private static object? Resolve(string name) => _taom == null ? null : TaomActions.Resolve(_taom, name);
    private static object? Service() => Resolve("TAOM.Features.Refuge.IRefugeService");

    private static string? PartyIdOf(object? row) => row?.GetType().GetField("PartyId")?.GetValue(row) as string;

    // ---- both sides ------------------------------------------------------------------------------------

    private static void NarrowPrefix(object __instance, out IDictionary? __state)
    {
        __state = null;
        if (_book!.GetValue(__instance) is not IDictionary full) return;
        __state = full;
        var own = (IDictionary)Activator.CreateInstance(full.GetType())!;
        foreach (DictionaryEntry e in full)
            if (TaomOwners.IsCurrentPlayers(TaomOwners.FindParty(e.Key as string)))
                own[e.Key] = e.Value;
        _book.SetValue(__instance, own);
    }

    private static Exception? NarrowFinalizer(object __instance, IDictionary? __state, Exception? __exception)
    {
        if (__state != null) _book!.SetValue(__instance, __state);
        return __exception;
    }

    /// <summary>Another player's refuge is not yours to manage or dismantle.</summary>
    private static void OwnRowPostfix(ref object? __result)
    {
        if (__result != null && !TaomOwners.IsCurrentPlayers(TaomOwners.FindParty(PartyIdOf(__result))))
            __result = null;
    }

    // ---- server ----------------------------------------------------------------------------------------

    /// <summary>Militia is drawn from the owner's culture: run the rally and stand-down with the owner as the player.</summary>
    private static bool AsOwnerPrefix(object __instance, string partyId, MethodBase __originalMethod)
    {
        if (ServerRelay.PlayerScope.Active) return true;
        if (TaomOwners.OwnerOf(TaomOwners.FindParty(partyId)) is not { } owner) return true;
        using (new ServerRelay.PlayerScope(owner.Hero, owner.Party))
            __originalMethod.Invoke(__instance, new object[] { partyId });
        return false;
    }

    private static string Reason(object reason, int cost)
    {
        try
        {
            var behavior = Campaign.Current?.CampaignBehaviorManager?.GetBehaviors<CampaignBehaviorBase>()
                .FirstOrDefault(b => b.GetType().FullName == "TAOM.Features.Refuge.Hooks.RefugeCampaignBehavior");
            var controller = behavior?.GetType().GetField("_menuController", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(behavior);
            if (controller != null && _reasonText!.Invoke(controller, new[] { reason, cost }) is TextObject text)
                return text.ToString();
        }
        catch { }
        return "You cannot do that here (" + reason + ").";
    }

    private static int Cost(string property)
    {
        var settings = Resolve("TAOM.Features.Refuge.IRefugeSettingsProvider");
        return settings?.GetType().GetProperty(property)?.GetValue(settings) is int n ? n : 0;
    }

    /// <summary>Server, inside PlayerScope for the sender.</summary>
    private static TaomActionOutcome ServerAction(Hero hero, MobileParty? party, string op, IList<string> args)
    {
        if (party == null) return TaomActionOutcome.Fail("Your party was not found on the server.");
        var service = Service();
        if (service == null) return TaomActionOutcome.Fail("TAOM's refuge service is not available on the server.");
        switch (op)
        {
            case "found":
                return args.Count == 2 ? Found(service, hero, args[0], args[1] == "1") : TaomActionOutcome.Fail("");
            case "upgrade":
            case "dismantle":
            {
                if (args.Count != 1) return TaomActionOutcome.Fail("");
                var row = _getByPartyId!.Invoke(service, new object[] { args[0] });
                var refugeParty = TaomOwners.FindParty(args[0]);
                if (row == null || refugeParty == null) return TaomActionOutcome.Fail(Reason(Enum.ToObject(_found!.GetParameters()[1].ParameterType.GetElementType()!, 8), 0));
                if (!TaomOwners.IsCurrentPlayers(refugeParty)) return TaomActionOutcome.Fail("That refuge belongs to another player.");
                if (op == "upgrade")
                {
                    return _upgrade!.Invoke(service, new[] { row }) is true
                        ? new TaomActionOutcome(true, new TextObject("{=taom_rf_upgrading}Rebuilding the refuge into a stronghold - your company must stay until it is done.").ToString())
                        : TaomActionOutcome.Fail(new TextObject("{=taom_rf_upgrade_no_gold}Not enough gold to rebuild the refuge into a stronghold.").ToString());
                }
                if (refugeParty.MapEvent != null) return TaomActionOutcome.Fail("The refuge is fighting; it cannot be dismantled now.");
                _dismantle!.Invoke(service, new[] { row });
                Log.Info($"TAOM layer: refuge '{args[0]}' dismantled for {hero.Name}");
                return new TaomActionOutcome(true, new TextObject("{=taom_rf_dismantled}The refuge is dismantled; its garrison and stores return to your party.").ToString());
            }
            default:
                return TaomActionOutcome.Fail("");
        }
    }

    /// <summary>The server half of TAOM's warden picker: RefugeMenuController.OnWardenChosen without its menu checks.</summary>
    private static TaomActionOutcome Found(object service, Hero hero, string candidateId, bool isCompanion)
    {
        var none = Enum.ToObject(_found!.GetParameters()[1].ParameterType.GetElementType()!, 0);
        var precheck = _canFound!.Invoke(service, null)!;
        if (!precheck.Equals(none)) return TaomActionOutcome.Fail(Reason(precheck, Cost("FoundCost")));

        var wardens = Resolve("TAOM.Features.Refuge.IWardenService");
        if (wardens == null) return TaomActionOutcome.Fail("TAOM's warden service is not available on the server.");
        object? candidate = null;
        if (_candidates!.Invoke(wardens, null) is IEnumerable all)
            foreach (var c in all)
            {
                var t = c.GetType();
                if (t.GetField("Id")?.GetValue(c) as string == candidateId && t.GetField("IsCompanion")?.GetValue(c) is bool comp && comp == isCompanion)
                { candidate = c; break; }
            }
        if (candidate == null) return TaomActionOutcome.Fail("That warden is no longer available.");

        var resolveArgs = new object?[] { candidate, false, null };
        var wardenId = _resolveWarden!.Invoke(wardens, resolveArgs) as string;
        var promoted = resolveArgs[1] is true;
        var fromTroop = resolveArgs[2] as string;
        if (wardenId == null) return TaomActionOutcome.Fail(new TextObject("{=taom_rf_promote_failed}Could not assign that warden.").ToString());

        var foundArgs = new object?[] { wardenId, none };
        var row = _found!.Invoke(service, foundArgs);
        if (row == null)
        {
            if (promoted) _unwindPromotion!.Invoke(wardens, new object?[] { wardenId, fromTroop });
            return TaomActionOutcome.Fail(foundArgs[1]!.Equals(none) ? "The refuge could not be founded." : Reason(foundArgs[1]!, Cost("FoundCost")));
        }
        row.GetType().GetField("WardenPromoted")?.SetValue(row, promoted);
        row.GetType().GetField("PromotedFromTroopId")?.SetValue(row, fromTroop);
        var partyId = PartyIdOf(row) ?? "";
        Log.Info($"TAOM layer: refuge '{partyId}' founded for {hero.Name} (warden {wardenId}{(promoted ? ", promoted from " + fromTroop : "")})");
        return new TaomActionOutcome(true,
            new TextObject("{=taom_rf_founded}Refuge founded - garrison it, then it will be raised.").ToString(),
            new List<string> { "founded", partyId });
    }

    // ---- client ----------------------------------------------------------------------------------------

    /// <summary>Client, in a session: never run locally (the server does this for everyone).</summary>
    private static bool ClientSkipPrefix() => !TaomActions.IsCoopClient;

    private static bool WardenChosenPrefix(List<InquiryElement> selected)
    {
        if (!TaomActions.IsCoopClient) return true;
        var candidate = selected?.FirstOrDefault()?.Identifier;
        if (candidate == null) return false;
        var t = candidate.GetType();
        var id = t.GetField("Id")?.GetValue(candidate) as string ?? "";
        var companion = t.GetField("IsCompanion")?.GetValue(candidate) is true;
        TaomActions.Send!(Feature, "found", new[] { id, companion ? "1" : "0" });
        Log.Info($"TAOM layer: refuge founding sent to the server (warden {id}, {(companion ? "companion" : "promotion")})");
        return false;
    }

    private static bool UpgradePrefix(object refuge, ref bool __result)
    {
        if (!TaomActions.IsCoopClient) return true;
        TaomActions.Send!(Feature, "upgrade", new[] { PartyIdOf(refuge) ?? "" });
        __result = true;   // TAOM's menu then says the rebuild started and closes; a refusal comes back in red
        return false;
    }

    private static bool DismantlePrefix(object refuge)
    {
        if (!TaomActions.IsCoopClient) return true;
        TaomActions.Send!(Feature, "dismantle", new[] { PartyIdOf(refuge) ?? "" });
        return false;
    }

    private static string? _depositPartyId;
    private static DateTime _depositUntil;

    /// <summary>Client, after the server founded the refuge: fold the local camp, leave the camp menu, open the deposit screen.</summary>
    private static void ClientApply(IList<string> data)
    {
        if (data.Count < 2 || data[0] != "founded") return;
        TaomFieldCamp.UndoLocalEstablish();
        try { GameMenu.ExitToLast(); } catch { }
        _depositPartyId = data[1];
        _depositUntil = DateTime.UtcNow.AddSeconds(20);
    }

    /// <summary>Client, from Bridge.Tick: TAOM opens the refuge's party screen right after founding; do it once the party is here.</summary>
    internal static void ClientTick()
    {
        if (_depositPartyId == null) return;
        if (DateTime.UtcNow > _depositUntil) { _depositPartyId = null; return; }
        var party = TaomOwners.FindParty(_depositPartyId);
        if (party?.PartyComponent == null || party.PartyComponent.GetType().Name != "RefugePartyComponent") return;
        _depositPartyId = null;
        try { Helpers.PartyScreenHelper.OpenScreenAsManageTroopsAndPrisoners(party); }
        catch (Exception ex) { Log.Warn("TAOM layer: could not open the refuge's party screen: " + ex.GetBaseException().Message); }
    }

    private static HashSet<string> _mirroredRows = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>Client: a refuge the server no longer has (dismantled, destroyed) takes its map layout with it.</summary>
    private static void AfterMirror(CampaignBehaviorBase behavior)
    {
        var service = Service();
        if (service == null || _book?.GetValue(service) is not IDictionary book) return;
        var now = new HashSet<string>(book.Keys.Cast<object>().Select(k => k.ToString()!), StringComparer.Ordinal);
        var visuals = Resolve("TAOM.Features.Refuge.IRefugeVisualService");
        var remove = visuals?.GetType().GetMethod("Remove", new[] { typeof(string) });
        foreach (var gone in _mirroredRows.Where(k => !now.Contains(k)))
            try { remove?.Invoke(visuals, new object[] { gone }); } catch { }
        _mirroredRows = now;
    }
}
