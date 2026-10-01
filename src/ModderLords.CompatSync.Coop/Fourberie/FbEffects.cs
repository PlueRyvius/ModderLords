using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using ModderLords.CompatSync;
using ModderLords.CompatSync.Coop.Taom;
using Newtonsoft.Json.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Roster;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;
using TaleWorlds.ObjectSystem;

namespace ModderLords.CompatSync.Coop.Fourberie;

/// <summary>
/// fourb-effects (docs/FOURBERIE-LAYER-PLAN.md, phase 6, tier T2: trusted, maintainer decision). What a player's own
/// Fourberie does to the world on their game (a scam's payout, a bribe, a beating's relation hit, a riot's security loss,
/// loot, the troops a fight cost) mostly never reaches the server: Coop refuses gold, relations, kills and prisoners from
/// a player's game, keeps settlement and influence writes local, and never sends the player's own party changes.
///
/// On a player's game each of those calls, when it comes from Fourberie's code (FbRecordGate), is recorded; the server
/// replays it as that player with their book, and Coop sends the result to everyone. Changes Fourberie makes inside a
/// handler the server already runs are not recorded (the server made them), nor are changes caused by another recorded
/// call (its replay makes them). Renown and kingdom changes, which Coop does not guard on a player's game, are recorded
/// and not applied locally: the server's result comes back.
/// </summary>
internal static class FbEffects
{
    /// <summary>(type, method, parameter count, also skip it on the player's game).</summary>
    internal static readonly (Type Type, string Method, int Params, bool SkipLocally)[] Calls =
    {
        (typeof(GiveGoldAction), "ApplyInternal", 7, false),
        (typeof(ChangeRelationAction), "ApplyInternal", 5, false),
        (typeof(KillCharacterAction), "ApplyInternal", 5, false),
        (typeof(TakePrisonerAction), "ApplyInternal", 3, false),
        (typeof(DestroyPartyAction), "Apply", 2, false),
        (typeof(DeclareWarAction), "ApplyInternal", 3, false),
        (typeof(TeleportHeroAction), "ApplyInternal", 4, false),
        (typeof(GainRenownAction), "ApplyInternal", 3, true),
        (typeof(ChangeKingdomAction), "ApplyInternal", 7, true),
        (typeof(Hero), "AddSkillXp", 2, false),
        (typeof(Alley), "SetOwner", 1, false),
    };

    /// <summary>Fourberie's own helper for the player's trait XP; replayed whole (Hero.MainHero is the player in scope).</summary>
    internal const string TraitHelper = "Fourberie.VanillaHelperFourb::AddPlayerTraitXPAndLogEntry";

    /// <summary>(declaring type, property, target kind for the wire).</summary>
    internal static readonly (Type Type, string Property, string Kind)[] Deltas =
    {
        (typeof(Town), "Security", "town"), (typeof(Town), "Loyalty", "town"), (typeof(Town), "Prosperity", "town"),
        (typeof(Town), "FoodStocks", "town"), (typeof(Settlement), "Militia", "settlement"), (typeof(Clan), "Influence", "clan"),
    };

    private static readonly Dictionary<MethodBase, (string Key, bool SkipLocally)> Recorded = new Dictionary<MethodBase, (string, bool)>();
    private static readonly Dictionary<string, MethodBase> ByKey = new Dictionary<string, MethodBase>(StringComparer.Ordinal);
    private static readonly List<FbEffectWire.Op> Pending = new List<FbEffectWire.Op>();
    private static readonly FbRosterNet Roster = new FbRosterNet();
    private static readonly HashSet<string> Warned = new HashSet<string>(StringComparer.Ordinal);
    private static long _recorded, _sent, _applied, _refused;

    internal static string KeyOf(MethodBase m) => m.DeclaringType?.FullName + "::" + m.Name;

    internal static void Bind(MethodBase method, bool skipLocally)
    {
        var key = KeyOf(method);
        Recorded[method] = (key, skipLocally);
        ByKey[key] = method;
    }

    // ---- player's game: recording ------------------------------------------------------------------------------

    internal static bool CallPrefix(object? __instance, object?[] __args, MethodBase __originalMethod, ref bool __state)
    {
        __state = false;
        if (!Recorded.TryGetValue(__originalMethod, out var entry)) return true;
        // The player's own party destroying something: Coop already sends that to the server.
        if (__originalMethod.Name == "Apply" && __originalMethod.DeclaringType == typeof(DestroyPartyAction) && ReferenceEquals(__args[0], PartyBase.MainParty)) return true;
        if (!FbRecordGate.TryEnter()) return true;
        __state = true;
        if (EncodeCall(__originalMethod, __instance, __args) is not { } op) return true;
        Pending.Add(op);
        _recorded++;
        return !entry.SkipLocally;
    }

    internal static void Leave(bool __state)
    {
        if (__state) FbRecordGate.Leave();
    }

    internal static void DeltaPrefix(object __instance, object?[] __args, MethodBase __originalMethod)
    {
        if (!FbRecordGate.TryEnter()) return;
        try
        {
            var property = __originalMethod.Name.Substring("set_".Length);
            var (type, _, kind) = Deltas.First(d => d.Property == property && d.Type.IsInstanceOfType(__instance));
            var before = Convert.ToSingle(type.GetProperty(property)!.GetValue(__instance));
            var change = Convert.ToSingle(__args[0]) - before;
            if (Math.Abs(change) < 0.0001f) return;
            var id = __instance switch { Town t => t.Settlement?.StringId, Settlement s => s.StringId, Clan c => c.StringId, _ => null };
            if (id == null) return;
            Pending.Add(FbEffectWire.Delta(kind, property, id, change));
            _recorded++;
        }
        catch (Exception ex) { _refused++; WarnOnce("delta", ex); }
        finally { FbRecordGate.Leave(); }
    }

    /// <summary>TroopRoster.AddToCountsAtIndex: every change to a roster goes through it; only the player's own party counts.</summary>
    internal static void TroopsPrefix(TroopRoster __instance, int index, int countChange, int woundedCountChange, int xpChange)
    {
        var main = MobileParty.MainParty;
        if (main == null || !FourberieLayer.IsCoopClient) return;
        var which = ReferenceEquals(__instance, main.MemberRoster) ? "m" : ReferenceEquals(__instance, main.PrisonRoster) ? "p" : null;
        if (which == null) return;
        var character = __instance.GetCharacterAtIndex(index);
        if (character == null || character.IsHero) return;   // heroes join and leave parties through their own actions
        if (!FbRecordGate.TryEnter()) return;
        FbRecordGate.Leave();
        Roster.Troops(which, character.StringId, countChange, woundedCountChange, xpChange);
    }

    internal static void ItemsPrefix(ItemRoster __instance, EquipmentElement rosterElement, int number)
    {
        var main = MobileParty.MainParty;
        if (main == null || !FourberieLayer.IsCoopClient || !ReferenceEquals(__instance, main.ItemRoster) || rosterElement.Item == null) return;
        if (!FbRecordGate.TryEnter()) return;
        FbRecordGate.Leave();
        Roster.Items(rosterElement.Item.StringId, rosterElement.ItemModifier?.StringId ?? "", number);
    }

    /// <summary>A call as an op, the way the player's game sends it; null when an argument cannot be written.</summary>
    internal static FbEffectWire.Op? EncodeCall(MethodBase method, object? instance, object?[] values)
    {
        var key = KeyOf(method);
        var parameters = method.GetParameters();
        var args = new JArray();
        if (!method.IsStatic && !Encode(method.DeclaringType!, instance, args, key)) return null;
        for (var i = 0; i < parameters.Length; i++)
            if (!Encode(parameters[i].ParameterType, values[i], args, key)) return null;
        return FbEffectWire.Call(key, args);
    }

    internal static MethodBase? MethodFor(string key) => ByKey.TryGetValue(key, out var m) ? m : null;

    internal static long Applied => _applied;
    internal static long Refused => _refused;

    private static bool Encode(Type type, object? value, JArray args, string key)
    {
        if (FbBookCodec.TryEncodeValue(type, value, FbGameRefs.Instance, out var token, out var problem)) { args.Add(token); return true; }
        if (Warned.Add("encode|" + key)) Log.Warn($"{FourberieLayer.Tag}effects: {key} cannot be sent ({problem}); it stays on this game");
        return false;
    }

    /// <summary>Player's game, every frame: sends recorded changes; the party's net changes only outside missions.</summary>
    internal static void ClientTick()
    {
        if (!FourberieLayer.IsCoopClient) return;
        if (Mission.Current == null && !Roster.IsEmpty) Pending.AddRange(Roster.Drain());
        if (Pending.Count == 0 || TaomActions.Send == null) return;
        while (Pending.Count > 0)
        {
            var batch = Pending.Take(FbEffectWire.MaxOps).ToList();
            Pending.RemoveRange(0, batch.Count);
            TaomActions.Send(FourberieLayer.Feature, "effects", new[] { FbEffectWire.Pack(batch) });
            _sent += batch.Count;
        }
    }

    // ---- server: replay ------------------------------------------------------------------------------------------

    /// <summary>Server, game thread, inside TaomActions.Run: replays a player's recorded changes as that player.</summary>
    internal static TaomActionOutcome Apply(Hero hero, MobileParty? party, IList<string> args)
    {
        var ops = args.Count == 1 ? FbEffectWire.Unpack(args[0]) : null;
        if (ops == null) { _refused++; return TaomActionOutcome.Fail(""); }
        var running = TaomActions.Running;
        TaomActions.Running = false;   // what the replay shows reaches the player through the notice forwarder
        try
        {
            using var scope = FbBooks.Enter(hero, party);
            party ??= hero.PartyBelongedTo;
            foreach (var op in ops)
            {
                try
                {
                    if (ApplyOne(op, party)) _applied++;
                    else _refused++;
                }
                catch (Exception ex)
                {
                    _refused++;
                    WarnOnce("apply|" + op.Kind + op.Key, ex);
                }
            }
        }
        finally { TaomActions.Running = running; }
        return new TaomActionOutcome(true, "");
    }

    private static bool ApplyOne(FbEffectWire.Op op, MobileParty? party)
    {
        switch (op.Kind)
        {
            case "call":
            {
                if (!ByKey.TryGetValue(op.Key, out var method)) return false;
                var parameters = method.GetParameters();
                var offset = method.IsStatic ? 0 : 1;
                if (op.Args.Count != parameters.Length + offset) return false;
                var problems = new List<string>();
                object? instance = null;
                if (offset == 1 && (!FbBookCodec.TryDecodeValue(method.DeclaringType!, op.Args[0], FbGameRefs.Instance, problems, "this", out instance) || instance == null)) return false;
                var values = new object?[parameters.Length];
                for (var i = 0; i < parameters.Length; i++)
                    if (!FbBookCodec.TryDecodeValue(parameters[i].ParameterType, op.Args[i + offset], FbGameRefs.Instance, problems, parameters[i].Name, out values[i])
                        || (values[i] == null && op.Args[i + offset].Type != JTokenType.Null)) return false;
                if (!Plausible(method, values)) return false;
                method.Invoke(instance, values);
                return true;
            }
            case "delta":
            {
                var colon = op.Key.IndexOf(':');
                var kind = op.Key.Substring(0, colon);
                var property = op.Key.Substring(colon + 1);
                object? target = kind switch
                {
                    "town" => Settlement.Find(op.Target)?.Town,
                    "settlement" => Settlement.Find(op.Target),
                    "clan" => Clan.All.FirstOrDefault(c => c.StringId == op.Target),
                    _ => null,
                };
                var prop = target?.GetType().GetProperty(property);
                if (target == null || prop == null) return false;
                prop.SetValue(target, Convert.ToSingle(prop.GetValue(target)) + op.Amount);
                return true;
            }
            case "troops":
            {
                var roster = op.Key == "m" ? party?.MemberRoster : party?.PrisonRoster;
                var character = MBObjectManager.Instance.GetObject<CharacterObject>(op.Target);
                if (roster == null || character == null || character.IsHero) return false;
                // TroopRosterElement.WoundedNumber dereferences its character, so a missing troop is never read.
                var index = roster.FindIndexOfTroop(character);
                var haveCount = index >= 0 ? roster.GetElementNumber(index) : 0;
                var haveWounded = index >= 0 ? roster.GetElementWoundedNumber(index) : 0;
                var count = (int)op.Amount;
                if (count < 0) count = Math.Max(count, -haveCount);   // never below none
                var wounded = Math.Max(op.Wounded, -haveWounded);
                if (count == 0 && wounded == 0 && op.Xp == 0) return true;
                if (index < 0 && count <= 0) return true;              // nothing to take away or give XP to
                roster.AddToCounts(character, count, false, wounded, Math.Max(op.Xp, 0));
                return true;
            }
            case "items":
            {
                var item = MBObjectManager.Instance.GetObject<ItemObject>(op.Target);
                if (party == null || item == null) return false;
                var modifier = op.Modifier.Length > 0 ? MBObjectManager.Instance.GetObject<ItemModifier>(op.Modifier) : null;
                var element = new EquipmentElement(item, modifier);
                var amount = (int)op.Amount;
                if (amount < 0) amount = Math.Max(amount, -party.ItemRoster.GetElementNumber(party.ItemRoster.FindIndexOfElement(element)));
                if (amount != 0) party.ItemRoster.AddToCounts(element, amount);
                return true;
            }
            default:
                return false;
        }
    }

    /// <summary>Trusted co-op, but nothing absurd: a single gold transfer, relation change, XP or renown gain stays believable.</summary>
    private static bool Plausible(MethodBase method, object?[] values)
    {
        switch (method.DeclaringType?.Name, method.Name)
        {
            case ("GiveGoldAction", _): return values[4] is int gold && gold >= 0 && gold <= 10_000_000;
            case ("ChangeRelationAction", _): return values[2] is int relation && Math.Abs(relation) <= 100;
            case ("Hero", "AddSkillXp"): return values[1] is float xp && xp >= 0 && xp <= 100_000;
            case ("GainRenownAction", _): return values[1] is float renown && Math.Abs(renown) <= 10_000;
            case ("KillCharacterAction", _): return values[0] is Hero { IsAlive: true };
            default: return true;
        }
    }

    private static void WarnOnce(string what, Exception ex)
    {
        if (Warned.Add(what)) Log.Warn($"{FourberieLayer.Tag}effects: {what} failed: {ex.GetBaseException().Message}");
    }

    internal static string Summary() => FourberieLayer.IsServer
        ? $"player changes applied {_applied}, refused {_refused}"
        : $"changes recorded {_recorded}, sent {_sent}";
}

internal sealed class FbEffectsComponent : IFbComponent
{
    private const string Owner = "ModderLords.Fourberie.Effects";
    private readonly List<(MethodBase Method, bool Skip)> _calls = new List<(MethodBase, bool)>();
    private readonly List<MethodInfo> _setters = new List<MethodInfo>();
    private readonly List<MethodInfo> _serverRun = new List<MethodInfo>();

    public string Id => "effects";

    public string? SkipReason(FbContext context)
    {
        if (context.Fields == null) return "no books: " + context.FieldsProblem;
        _calls.Clear();
        _setters.Clear();
        _serverRun.Clear();
        var missing = new List<string>();
        foreach (var (type, name, count, skip) in FbEffects.Calls)
        {
            var m = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .FirstOrDefault(x => x.Name == name && x.GetParameters().Length == count);
            if (m == null) missing.Add(type.Name + "." + name); else _calls.Add((m, skip));
        }
        if (context.Method("Fourberie.VanillaHelperFourb", "AddPlayerTraitXPAndLogEntry", 4) is { } trait) _calls.Add((trait, false));
        else missing.Add("VanillaHelperFourb.AddPlayerTraitXPAndLogEntry");
        foreach (var (type, property, _) in FbEffects.Deltas)
            if (FbPatchTargets.DeclaredSetter(type, property) is { } setter) _setters.Add(setter); else missing.Add(type.Name + "." + property);
        foreach (var (type, method, count, _) in FbFields.Handlers)
            if (context.Method(type, method, count) is { } h) _serverRun.Add(h);
        if (missing.Count > 0) Log.Warn(FourberieLayer.Tag + "effects: not found, not carried: " + FourberieLayer.Some(missing));
        return null;
    }

    public string Install(FbContext context)
    {
        foreach (var (m, skip) in _calls) FbEffects.Bind(m, skip);
        if (context.IsServer) return $"{_calls.Count} kinds of player change, settlement and clan numbers and party contents are replayed here as the player";

        FbRecordGate.Mod = context.Mod;
        FbRecordGate.IsCoopClient = () => FourberieLayer.IsCoopClient;
        var h = new Harmony(Owner);
        // Above Coop's own prefixes, which refuse most of these on a player's game and would stop later prefixes.
        var first = Priority.First + 300;
        var leave = new HarmonyMethod(typeof(FbEffects), nameof(FbEffects.Leave));
        // One patch that fails is named and skipped; the rest still go on (a throw here used to leave every later patch out).
        var failed = new List<string>();
        void Patch(MethodBase? m, string what, HarmonyMethod? prefix, HarmonyMethod? finalizer = null)
        {
            try
            {
                if (m == null) throw new MissingMethodException(what);
                h.Patch(m, prefix: prefix, finalizer: finalizer);
            }
            catch (Exception ex) { failed.Add(what + " (" + ex.GetBaseException().Message + ")"); }
        }
        foreach (var (m, _) in _calls)
            Patch(m, m.DeclaringType?.Name + "." + m.Name, new HarmonyMethod(typeof(FbEffects), nameof(FbEffects.CallPrefix)) { priority = first }, leave);
        foreach (var s in _setters)
            Patch(s, s.DeclaringType?.Name + "." + s.Name, new HarmonyMethod(typeof(FbEffects), nameof(FbEffects.DeltaPrefix)) { priority = first });
        Patch(AccessTools.Method(typeof(TroopRoster), nameof(TroopRoster.AddToCountsAtIndex)), "TroopRoster.AddToCountsAtIndex",
            new HarmonyMethod(typeof(FbEffects), nameof(FbEffects.TroopsPrefix)) { priority = first });
        Patch(AccessTools.Method(typeof(ItemRoster), nameof(ItemRoster.AddToCounts), new[] { typeof(EquipmentElement), typeof(int) }), "ItemRoster.AddToCounts",
            new HarmonyMethod(typeof(FbEffects), nameof(FbEffects.ItemsPrefix)) { priority = first });
        // Handlers the server runs for the player also fire here (a settlement entered): what they change is not sent again.
        foreach (var m in _serverRun)
            Patch(m, m.DeclaringType?.Name + "." + m.Name, new HarmonyMethod(typeof(FbRecordGate), nameof(FbRecordGate.ServerRunPrefix)) { priority = Priority.First },
                new HarmonyMethod(typeof(FbRecordGate), nameof(FbRecordGate.ServerRunFinalizer)));
        if (failed.Count > 0) Log.Warn(FourberieLayer.Tag + "effects: " + failed.Count + " patch(es) failed, those changes are not carried: " + FourberieLayer.Some(failed));
        return $"{_calls.Count} kinds of world change, {_setters.Count} settlement and clan numbers and this player's party are sent to the server when Fourberie makes them"
            + (failed.Count > 0 ? $" ({failed.Count} not carried, see warning)" : "");
    }
}
