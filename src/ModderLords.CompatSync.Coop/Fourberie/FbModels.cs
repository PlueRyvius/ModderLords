using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using ModderLords.CompatSync;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Core;
using TaleWorlds.Localization;

namespace ModderLords.CompatSync.Coop.Fourberie;

/// <summary>
/// fourb-models (docs/FOURBERIE-LAYER-PLAN.md, phase 5). Fourberie's 14 game models adjust what the server's simulation
/// computes: a clan's daily gold (its criminal income and gang wages, only "if clan == Clan.PlayerClan"), a town's
/// loyalty and security (the bandits a player supports nearby), the daily crime change, the horde's food, speed and
/// power. On the server "the player" is an idle stand-in with an empty book, so none of that applied to anyone.
///
/// Owned answers run as their owner: a clan's gold as the player who leads it, a player's party as that player, a bandit
/// follower as the player whose book lists it, a stance as the player who rules one side. World answers that every
/// player's activity feeds (town loyalty and security, a faction's daily crime change) are the plain answer plus each
/// connected player's contribution, one line "(F) Fourberie". Only on the server; a player's game already answers with
/// its own book. Offline players' empires freeze here too: only connected players count.
/// </summary>
internal static class FbModels
{
    internal static (string Type, string Method, int Params, FbModelTable.Kind Kind)[] Methods => FbModelTable.Methods;

    private static readonly Dictionary<MethodBase, FbModelTable.Kind> Kinds = new Dictionary<MethodBase, FbModelTable.Kind>();
    [ThreadStatic] private static MethodBase? _invoking;
    private static readonly HashSet<string> Warned = new HashSet<string>(StringComparer.Ordinal);
    private static long _owned, _summed, _errors;

    internal static void Bind(IEnumerable<(MethodInfo Method, FbModelTable.Kind Kind)> found)
    {
        foreach (var (m, k) in found) Kinds[m] = k;
    }

    // ---- prefixes, one per return type --------------------------------------------------------------------------

    internal static bool ExplainedPrefix(object? __instance, object?[] __args, MethodBase __originalMethod, ref ExplainedNumber __result)
    {
        if (!Take(__instance, __args, __originalMethod, out var result)) return true;
        __result = (ExplainedNumber)result!;
        return false;
    }

    internal static bool FloatPrefix(object? __instance, object?[] __args, MethodBase __originalMethod, ref float __result)
    {
        if (!Take(__instance, __args, __originalMethod, out var result)) return true;
        __result = (float)result!;
        return false;
    }

    internal static bool BoolPrefix(object? __instance, object?[] __args, MethodBase __originalMethod, ref bool __result)
    {
        if (!Take(__instance, __args, __originalMethod, out var result)) return true;
        __result = (bool)result!;
        return false;
    }

    internal static bool TimePrefix(object? __instance, object?[] __args, MethodBase __originalMethod, ref CampaignTime __result)
    {
        if (!Take(__instance, __args, __originalMethod, out var result)) return true;
        __result = (CampaignTime)result!;
        return false;
    }

    internal static bool StancePrefix(object? __instance, object?[] __args, MethodBase __originalMethod, ref TaleWorlds.CampaignSystem.ComponentInterfaces.DiplomacyModel.DiplomacyStance? __result)
    {
        if (!Take(__instance, __args, __originalMethod, out var result)) return true;
        __result = (TaleWorlds.CampaignSystem.ComponentInterfaces.DiplomacyModel.DiplomacyStance?)result;
        return false;
    }

    /// <summary>True (with the answer) when this call was answered as its owner or summed over players; false: run as written.</summary>
    private static bool Take(object? instance, object?[] args, MethodBase method, out object? result)
    {
        result = null;
        if (ReferenceEquals(_invoking, method))
        {
            _invoking = null;   // the call this class itself made: let it through
            return false;
        }
        if (!FourberieLayer.IsServer || !Kinds.TryGetValue(method, out var kind)) return false;
        try
        {
            if (kind == FbModelTable.Kind.Summed) return Sum(instance, args, method, out result);
            var owner = OwnerOf(kind, args);
            if (owner == null || FbBooks.Current == owner.StringId || FbBooks.IsSuspended(owner.StringId)) return false;
            using var scope = FbBooks.Enter(owner, FbOwners.PartyOf(owner));
            if (scope == null) return false;
            result = Call(instance, args, method);
            _owned++;
            return true;
        }
        catch (Exception ex)
        {
            _errors++;
            var name = method.DeclaringType?.Name + "." + method.Name;
            if (Warned.Add(name)) Log.Warn($"{FourberieLayer.Tag}models: {name} failed as its owner, answered as written: {ex.GetBaseException().Message}");
            return false;
        }
    }

    private static object? Call(object? instance, object?[] args, MethodBase method)
    {
        _invoking = method;
        try { return method.Invoke(instance, args); }
        finally { _invoking = null; }
    }

    /// <summary>The plain answer (the stand-in's empty book) plus what each connected player's book adds to it.</summary>
    private static bool Sum(object? instance, object?[] args, MethodBase method, out object? result)
    {
        result = null;
        if (FbBooks.Current != null) return false;   // inside one player's run: that player's answer
        var players = FbOwners.Players;
        if (players.Count == 0) return false;
        var plain = (ExplainedNumber)Call(instance, args, method)!;
        var added = 0f;
        foreach (var hero in players)
        {
            if (FbBooks.IsSuspended(hero.StringId)) continue;
            using var scope = FbBooks.Enter(hero, FbOwners.PartyOf(hero));
            if (scope == null) return false;
            added += ((ExplainedNumber)Call(instance, (object?[])args.Clone(), method)!).ResultNumber - plain.ResultNumber;
        }
        if (Math.Abs(added) > 0.0001f) plain.Add(added, new TextObject("{=!}(F) Fourberie"));
        result = plain;
        _summed++;
        return true;
    }

    private static Hero? OwnerOf(FbModelTable.Kind kind, object?[] args) => kind switch
    {
        FbModelTable.Kind.Clan => args[0] is Clan c ? FbOwners.ByClan(c) : null,
        FbModelTable.Kind.Party => args[0] is MobileParty p ? FbOwners.ByParty(p) : null,
        FbModelTable.Kind.Follower => args[0] is MobileParty f ? FbOwners.ByFollower(f) : null,
        FbModelTable.Kind.PartyBase => args[0] is PartyBase pb && pb.MobileParty != null ? FbOwners.ByParty(pb.MobileParty) : null,
        FbModelTable.Kind.Survival => (args[1] as CharacterObject)?.HeroObject is { } h && FbOwners.IsPlayer(h) ? h
            : args[0] is PartyBase sp && sp.MobileParty != null ? FbOwners.ByParty(sp.MobileParty) : null,
        FbModelTable.Kind.Stance => FbOwners.RulerOf(args[0] as IFaction) ?? FbOwners.RulerOf(args[1] as IFaction),
        _ => null,
    };

    internal static string Summary() => $"model answers as their owner {_owned}, summed over players {_summed}" + (_errors > 0 ? $", failed {_errors}" : "");
}

/// <summary>
/// Server: who owns what, for FbModels. Rebuilt at most once a second (models are asked thousands of times a second).
/// Followers come from each player's book (_banditsFollowers).
/// </summary>
internal static class FbOwners
{
    private static DateTime _builtAt = DateTime.MinValue;
    private static List<Hero> _players = new List<Hero>();
    private static Dictionary<Hero, MobileParty?> _parties = new Dictionary<Hero, MobileParty?>();
    private static Dictionary<Clan, Hero> _byClan = new Dictionary<Clan, Hero>();
    private static Dictionary<MobileParty, Hero> _byParty = new Dictionary<MobileParty, Hero>();
    private static Dictionary<MobileParty, Hero> _byFollower = new Dictionary<MobileParty, Hero>();

    internal static IReadOnlyList<Hero> Players { get { Refresh(); return _players; } }

    internal static MobileParty? PartyOf(Hero hero) { Refresh(); return _parties.TryGetValue(hero, out var p) ? p : hero.PartyBelongedTo; }
    internal static bool IsPlayer(Hero hero) { Refresh(); return _parties.ContainsKey(hero); }
    internal static Hero? ByClan(Clan clan) { Refresh(); return _byClan.TryGetValue(clan, out var h) ? h : null; }
    internal static Hero? ByParty(MobileParty party) { Refresh(); return _byParty.TryGetValue(party, out var h) ? h : null; }
    internal static Hero? ByFollower(MobileParty party) { Refresh(); return _byFollower.TryGetValue(party, out var h) ? h : null; }

    /// <summary>The connected player who rules this faction, if one does.</summary>
    internal static Hero? RulerOf(IFaction? faction)
    {
        if (faction?.Leader is not { } leader) return null;
        Refresh();
        return _parties.ContainsKey(leader) ? leader : null;
    }

    private static void Refresh()
    {
        if ((DateTime.UtcNow - _builtAt).TotalSeconds < 1) return;
        _builtAt = DateTime.UtcNow;
        var connected = FbBooks.Connected();
        _players = connected.Select(p => p.Hero).ToList();
        _parties = new Dictionary<Hero, MobileParty?>();
        _byClan = new Dictionary<Clan, Hero>();
        _byParty = new Dictionary<MobileParty, Hero>();
        _byFollower = new Dictionary<MobileParty, Hero>();
        foreach (var (hero, party) in connected)
        {
            _parties[hero] = party;
            if (hero.Clan != null && !_byClan.ContainsKey(hero.Clan)) _byClan[hero.Clan] = hero;
            if (party != null) _byParty[party] = hero;
            if (FbBooks.Peek(hero.StringId, "_banditsFollowers") is IEnumerable followers)
                foreach (var f in followers.OfType<MobileParty>()) _byFollower[f] = hero;
        }
    }

    internal static void Invalidate() => _builtAt = DateTime.MinValue;
}

internal sealed class FbModelsComponent : IFbComponent
{
    private const string Owner = "ModderLords.Fourberie.Models";
    private readonly List<(MethodInfo, FbModelTable.Kind)> _found = new List<(MethodInfo, FbModelTable.Kind)>();

    public string Id => "models";

    public string? SkipReason(FbContext context)
    {
        if (!context.IsServer) return "client (a player's game answers with its own book)";
        if (context.Fields == null) return "no books: " + context.FieldsProblem;
        _found.Clear();
        var missing = new List<string>();
        foreach (var (type, name, count, kind) in FbModels.Methods)
        {
            var m = context.Method(type, name, count);
            if (m == null) missing.Add(type.Substring(type.LastIndexOf('.') + 1) + "." + name);
            else _found.Add((m, kind));
        }
        if (_found.Count == 0) return "Fourberie changed; none of its model methods were found";
        if (missing.Count > 0) Log.Warn(FourberieLayer.Tag + "models: not found (Fourberie changed?), these answer for nobody: " + FourberieLayer.Some(missing));
        return null;
    }

    public string Install(FbContext context)
    {
        FbModels.Bind(_found);
        var h = new Harmony(Owner);
        foreach (var (m, _) in _found)
        {
            var prefix = m.ReturnType == typeof(ExplainedNumber) ? nameof(FbModels.ExplainedPrefix)
                : m.ReturnType == typeof(float) ? nameof(FbModels.FloatPrefix)
                : m.ReturnType == typeof(bool) ? nameof(FbModels.BoolPrefix)
                : m.ReturnType == typeof(CampaignTime) ? nameof(FbModels.TimePrefix)
                : m.ReturnType == typeof(TaleWorlds.CampaignSystem.ComponentInterfaces.DiplomacyModel.DiplomacyStance?) ? nameof(FbModels.StancePrefix)
                : null;
            if (prefix == null) { Log.Warn($"{FourberieLayer.Tag}models: {m.Name} returns {m.ReturnType.Name}; left as written"); continue; }
            h.Patch(m, prefix: new HarmonyMethod(typeof(FbModels), prefix) { priority = Priority.First });
        }
        var summed = _found.Count(f => f.Item2 == FbModelTable.Kind.Summed);
        return $"{_found.Count - summed} model answer(s) given as their owner, {summed} summed over connected players";
    }
}
