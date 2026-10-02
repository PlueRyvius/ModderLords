using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Common;
using GameInterface;
using GameInterface.Services.Kingdoms;
using GameInterface.Services.ObjectManager;
using HarmonyLib;
using ModderLords.CompatSync;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Election;
using TaleWorlds.ObjectSystem;

namespace ModderLords.CompatSync.Coop;

/// <summary>
/// Kingdom votes of a mod's own type (Bellum Civile's privy council appointment, and any other mod's) reach players.
/// <para>
/// Coop sends a new kingdom decision to clients through a converter that knows only vanilla's eleven types; for any
/// other it throws "Type of kingdom decision: X is not supported". The server keeps the decision, so every player
/// connected at that moment lacks it, and from then on their kingdom's list of pending decisions no longer lines up
/// with the server's. Coop refers to decisions by their position in that list (votes, round status, results,
/// removal), so later votes in that kingdom land on the wrong decision for those players. Players who join afterwards
/// get it from the save and are fine.
/// </para>
/// <para>
/// Server: a decision Coop cannot convert (exact type, or a base type it knows, see TAOM's fief vote) is sent as its
/// type name and saved fields instead. Client: the decision is rebuilt from them (the mod is installed there too) and
/// added through Coop's own client path, so it sits at the same position and Coop's vote rounds work on it.
/// </para>
/// </summary>
public static class ModKingdomDecisions
{
    private static readonly Harmony Harmony = new Harmony("ModderLords.Compat.ModKingdomDecisions");
    private static bool _installTried;
    private static FieldInfo? _converterField, _table;
    private static PropertyInfo? _what;

    /// <summary>Server: (kingdom id, type name, fields, ignore influence cost, random number) to every client.</summary>
    public static Action<string, string, List<string>, bool, float>? Broadcast;

    /// <summary>Patches Coop's server decision handler once. Safe to call every tick.</summary>
    public static void EnsureInstalled()
    {
        if (_installTried) return;
        _installTried = true;
        if (!Operations.OperationProcessSide.IsServer) return;
        try
        {
            var handler = AccessTools.TypeByName("Coop.Core.Server.Services.Kingdoms.Handlers.ServerKingdomHandler");
            var target = handler == null ? null : AccessTools.Method(handler, "HandleLocalDecisionAdded");
            _converterField = handler == null ? null : AccessTools.Field(handler, "kingdomDecisionDataConverter");
            var converter = AccessTools.TypeByName("GameInterface.Services.Kingdoms.KingdomDecisionDataConverter");
            _table = converter == null ? null : AccessTools.Field(converter, "supportedConversions");
            if (target == null || _converterField == null || _table == null)
            {
                Log.Warn("mod kingdom votes: Coop's decision sync has changed shape; votes of a mod's own type stay server-only");
                return;
            }
            Harmony.Patch(target, prefix: new HarmonyMethod(typeof(ModKingdomDecisions), nameof(Prefix)));
            Log.Info("mod kingdom votes: kingdom decisions of a mod's own type are sent to players");
        }
        catch (Exception ex) { Log.Warn("mod kingdom votes not installed: " + ex.GetBaseException().Message); }
    }

    private static bool Prefix(object __instance, object obj)
    {
        try
        {
            _what ??= AccessTools.Property(obj.GetType(), "What");
            var what = _what.GetValue(obj);
            var decision = Traverse.Create(what).Field("Decision").GetValue<KingdomDecision>();
            if (decision == null || Known(__instance, decision.GetType())) return true;
            var kingdom = Traverse.Create(what).Field("Kingdom").GetValue<Kingdom>();
            if (kingdom == null) return false;
            var fields = Encode(decision, out var skipped);
            Broadcast?.Invoke(kingdom.StringId, decision.GetType().FullName!, fields,
                Traverse.Create(what).Field("IgnoreInfluenceCost").GetValue<bool>(),
                Traverse.Create(what).Field("RandomNumber").GetValue<float>());
            Log.Info($"mod kingdom votes: sent {decision.GetType().Name} in '{kingdom.StringId}' to players ({fields.Count / 3} field(s)"
                + (skipped.Count > 0 ? ", not sent: " + string.Join(", ", skipped) : "") + ")");
        }
        catch (Exception ex) { Log.Warn("mod kingdom votes: could not send a decision: " + ex.GetBaseException().Message); }
        return false;
    }

    /// <summary>True when Coop converts this type itself: the exact type, or a base type (TAOM's fief vote converts those).</summary>
    private static bool Known(object handler, Type type)
    {
        if (_table!.GetValue(_converterField!.GetValue(handler)) is not IDictionary table) return true;
        for (var t = type; t != null && t != typeof(KingdomDecision); t = t.BaseType)
            if (table.Contains(t)) return true;
        return false;
    }

    // ---- game-type kinds for the codec: o game object id, l list of ids, t CampaignTime ticks.

    private static readonly FieldInfo Ticks = AccessTools.Field(typeof(CampaignTime), "_numTicks");

    internal static List<string> Encode(object decision, out List<string> skipped) =>
        DecisionFieldCodec.Encode(decision, out skipped, (f, value) =>
            value is MBObjectBase o ? ("o", o.StringId)
            : value is CampaignTime ? ("t", ((long)Ticks.GetValue(value)).ToString(CultureInfo.InvariantCulture))
            : DecisionFieldCodec.ListElement(f.FieldType) is { } element && typeof(MBObjectBase).IsAssignableFrom(element)
                ? ("l", string.Join(",", ((IEnumerable)value).Cast<MBObjectBase>().Select(x => x?.StringId ?? "")))
                : null);

    private static object Decode(Type type, IList<string> fields) =>
        DecisionFieldCodec.Decode(type, fields, (f, kind, raw) => kind switch
        {
            "o" => Find(f.FieldType, raw) ?? throw new InvalidOperationException($"{f.FieldType.Name} '{raw}' not found"),
            "t" => MakeTime(long.Parse(raw, CultureInfo.InvariantCulture)),
            "l" => MakeList(f.FieldType, raw),
            _ => null,
        });

    private static object MakeTime(long ticks)
    {
        object time = default(CampaignTime);
        Ticks.SetValue(time, ticks);
        return time;
    }

    private static object MakeList(Type listType, string raw)
    {
        var list = (IList)Activator.CreateInstance(listType)!;
        var element = DecisionFieldCodec.ListElement(listType)!;
        if (raw.Length > 0)
            foreach (var id in raw.Split(','))
                list.Add(id.Length == 0 ? null : Find(element, id) ?? throw new InvalidOperationException($"{element.Name} '{id}' not found"));
        return list;
    }

    // ---- client --------------------------------------------------------------------------------------

    /// <summary>Client, game thread: rebuilds the server's decision and adds it the way Coop adds the ones it converts.</summary>
    public static void ClientAdd(string kingdomId, string typeName, IList<string> fields, bool ignoreInfluenceCost, float randomNumber)
    {
        var type = AccessTools.TypeByName(typeName);
        if (type == null || !typeof(KingdomDecision).IsAssignableFrom(type))
        {
            Log.Warn($"mod kingdom votes: the server sent a {typeName} vote, which this game does not have; is the same mod version installed?");
            return;
        }
        if (!ContainerProvider.TryResolve<IKingdomInterface>(out var kingdoms))
        {
            Log.Warn("mod kingdom votes: Coop's kingdom interface is not available; " + type.Name + " not added");
            return;
        }
        var kingdom = Find(typeof(Kingdom), kingdomId) as Kingdom;
        if (kingdom == null) { Log.Warn($"mod kingdom votes: kingdom '{kingdomId}' not found; {type.Name} not added"); return; }
        try
        {
            var decision = (KingdomDecision)Decode(type, fields);
            kingdoms.RunAddDecision(kingdom, decision, ignoreInfluenceCost, randomNumber);
            Log.Info($"mod kingdom votes: {type.Name} added in '{kingdomId}'");
        }
        catch (Exception ex) { Log.Warn($"mod kingdom votes: could not add {type.Name} in '{kingdomId}': {ex.GetBaseException().Message}"); }
    }

    private static object? Find(Type type, string id)
    {
        if (ContainerProvider.TryResolve<IObjectManager>(out var objects)
            && objects.TryGetObject<object>(id, out var found) && type.IsInstanceOfType(found))
            return found;
        if (type == typeof(Kingdom)) return Kingdom.All.FirstOrDefault(k => k.StringId == id);
        if (type == typeof(Clan)) return Clan.All.FirstOrDefault(c => c.StringId == id);
        if (type == typeof(Hero)) return Campaign.Current?.CampaignObjectManager?.Find<Hero>(id);
        return MBObjectManager.Instance?.GetObject(type.Name, id) is { } o && type.IsInstanceOfType(o) ? o : null;
    }
}
