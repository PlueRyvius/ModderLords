using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using Common.Util;
using GameInterface;
using GameInterface.Services.ObjectManager;
using HarmonyLib;
using ModderLords.CompatSync;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Party.PartyComponents;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.ObjectSystem;

namespace ModderLords.CompatSync.Coop.Taom;

/// <summary>
/// TAOM's own map parties (a refuge, a supply caravan) on every client, and whose they are (TAOM-MAP, Refuge / Supply
/// Lines).
///
/// TAOM gives these parties its own PartyComponent classes. Coop sends a new party's component to clients only for the
/// eight vanilla component types it knows, so a client's copy of a refuge or caravan had no component: no name, no
/// owner, no warden, and every TAOM check ("is this a refuge?") failed on it. Server: when TAOM creates one of these
/// parties, the component is registered with Coop (under the id Coop gives components loaded from a save) and its
/// fields are sent to every client. Client: builds the same component, registers it under that id, and attaches it to
/// the party once the party exists there.
///
/// Both sides: TAOM's components answer "owner" and "banner" with Hero.MainHero, which on the server is the idle
/// world-generation hero and on a client is that client's player, for everyone's parties. They answer from the party's
/// clan instead (TAOM sets it to the founding player's clan when it creates the party).
/// </summary>
internal sealed class PartyComponentSyncComponent : ITaomComponent
{
    private const string Owner = "ModderLords.Taom.PartyComponents";

    private static Assembly? _taom;
    private static MethodInfo? _createParty;
    private static readonly List<Type> ComponentTypes = new List<Type>();

    /// <summary>Server: set by the server handler; sends (type, component id, party id, fields) to every client.</summary>
    internal static Action<string, string, string, List<string>>? Broadcast { get; set; }

    public string Id => "party-components";

    public string? SkipReason(TaomContext context)
    {
        _taom = context.Taom;
        ComponentTypes.Clear();
        foreach (var name in new[]
                 {
                     "TAOM.Features.Refuge.Components.RefugePartyComponent",
                     "TAOM.Features.SupplyLines.Components.SupplyCaravanComponent",
                 })
            if (context.Taom.GetType(name, false) is { } t && typeof(PartyComponent).IsAssignableFrom(t))
                ComponentTypes.Add(t);
        _createParty = typeof(MobileParty).GetMethod("CreateParty", BindingFlags.Public | BindingFlags.Static, null,
            new[] { typeof(string), typeof(PartyComponent) }, null);
        if (_createParty == null) return "MobileParty.CreateParty(string, PartyComponent) not found";
        return ComponentTypes.Count == 0 ? "TAOM changed; neither RefugePartyComponent nor SupplyCaravanComponent was found" : null;
    }

    public string Install(TaomContext context)
    {
        var h = new Harmony(Owner);
        foreach (var t in ComponentTypes)
        {
            if (t.GetProperty("PartyOwner", BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)?.GetGetMethod() is { } owner)
                h.Patch(owner, postfix: new HarmonyMethod(typeof(PartyComponentSyncComponent), nameof(OwnerPostfix)));
            if (t.GetMethod("GetDefaultComponentBanner", BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null) is { } banner)
                h.Patch(banner, postfix: new HarmonyMethod(typeof(PartyComponentSyncComponent), nameof(BannerPostfix)));
        }
        var names = string.Join(", ", ComponentTypes.Select(t => t.Name));
        if (context.IsServer)
        {
            h.Patch(_createParty!, prefix: new HarmonyMethod(typeof(PartyComponentSyncComponent), nameof(CreatePartyPrefix)),
                postfix: new HarmonyMethod(typeof(PartyComponentSyncComponent), nameof(CreatePartyPostfix)));
            return "server sends TAOM's party components to clients (" + names + "); owner = the party's clan";
        }
        return "client builds TAOM's party components the server sends (" + names + "); owner = the party's clan";
    }

    private static bool IsTaomComponent(PartyComponent? component) =>
        component != null && ComponentTypes.Contains(component.GetType());

    // ---- owner and banner, both sides ----------------------------------------------------------------

    private static void OwnerPostfix(PartyComponent __instance, ref Hero __result)
    {
        if (__instance.MobileParty?.ActualClan?.Leader is { } leader) __result = leader;
    }

    private static void BannerPostfix(PartyComponent __instance, ref Banner __result)
    {
        var clan = __instance.MobileParty?.ActualClan;
        if (clan == null) return;
        // A supply caravan flies the kingdom banner in TAOM (the owner's map faction); a refuge the clan banner.
        __result = __instance.GetType().Name == "SupplyCaravanComponent" ? clan.Kingdom?.Banner ?? clan.Banner : clan.Banner;
    }

    // ---- server --------------------------------------------------------------------------------------

    internal static string IdFor(string partyId) => "PartyComponent_" + partyId;

    /// <summary>Registered before the party exists, so Coop's own sync of the new party can name its component.</summary>
    private static void CreatePartyPrefix(string stringId, PartyComponent component)
    {
        if (!IsTaomComponent(component) || !ContainerProvider.TryResolve<IObjectManager>(out var objects)) return;
        if (!objects.Contains(component)) objects.AddExisting(IdFor(stringId), component);
    }

    private static void CreatePartyPostfix(MobileParty __result, PartyComponent component)
    {
        if (__result == null || !IsTaomComponent(component) || Broadcast == null) return;
        try
        {
            if (!ContainerProvider.TryResolve<IObjectManager>(out var objects) || !objects.TryGetId(component, out var id)) return;
            var fields = Encode(component);
            Broadcast(component.GetType().FullName!, id, __result.StringId, fields);
            Log.Info($"TAOM layer: sent {component.GetType().Name} of party '{__result.StringId}' to clients ({fields.Count / 3} field(s))");
        }
        catch (Exception ex) { Log.Warn("TAOM layer: could not send a TAOM party component: " + ex.GetBaseException().Message); }
    }

    private static IEnumerable<FieldInfo> SyncedFields(Type t) =>
        t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .Where(f => !f.IsLiteral && !f.Name.Contains("<"));

    /// <summary>The component's own fields as (name, kind, value) triples; fields of other types are left out.</summary>
    internal static List<string> Encode(object component)
    {
        var result = new List<string>();
        foreach (var f in SyncedFields(component.GetType()))
        {
            var value = f.GetValue(component);
            if (value == null) continue;
            if (value is string s) result.AddRange(new[] { f.Name, "s", s });
            else if (value is MBObjectBase o) result.AddRange(new[] { f.Name, "o", o.StringId });
            else if (f.FieldType.IsPrimitive || f.FieldType.IsEnum)
                result.AddRange(new[] { f.Name, "v", Convert.ToString(f.FieldType.IsEnum ? Convert.ToInt64(value) : value, CultureInfo.InvariantCulture)! });
        }
        return result;
    }

    // ---- client --------------------------------------------------------------------------------------

    private static readonly FieldInfo? PartyComponentField = AccessTools.Field(typeof(MobileParty), "_partyComponent");
    private static readonly MethodInfo? ComponentPartySetter = AccessTools.PropertySetter(typeof(PartyComponent), "MobileParty");
    private static readonly List<(PartyComponent Component, string PartyId, DateTime Until)> Pending = new List<(PartyComponent, string, DateTime)>();

    /// <summary>Client, game thread: builds the component the server sent and attaches it when the party is here.</summary>
    internal static void ClientCreate(string typeName, string componentId, string partyId, IList<string> fields)
    {
        var type = ComponentTypes.FirstOrDefault(t => t.FullName == typeName);
        if (type == null) { Log.Warn($"TAOM layer: the server sent party component {typeName}, which this client does not know; update ModderLords/TAOM"); return; }
        if (!ContainerProvider.TryResolve<IObjectManager>(out var objects)) return;
        if (objects.TryGetObject<object>(componentId, out var existing) && existing is PartyComponent known)
        {
            Pending.Add((known, partyId, DateTime.UtcNow.AddSeconds(60)));
            TryAttachPending();
            return;
        }
        var component = (PartyComponent)FormatterServices.GetUninitializedObject(type);
        Decode(component, fields, objects);
        objects.AddExisting(componentId, component);
        Pending.Add((component, partyId, DateTime.UtcNow.AddSeconds(60)));
        TryAttachPending();
    }

    internal static void Decode(object component, IList<string> fields, IObjectManager? objects)
    {
        var byName = SyncedFields(component.GetType()).ToDictionary(f => f.Name, StringComparer.Ordinal);
        for (var i = 0; i + 2 < fields.Count; i += 3)
        {
            if (!byName.TryGetValue(fields[i], out var f)) continue;
            var kind = fields[i + 1];
            var raw = fields[i + 2];
            object? value = kind switch
            {
                "s" => raw,
                "v" when f.FieldType.IsEnum => Enum.ToObject(f.FieldType, long.Parse(raw, CultureInfo.InvariantCulture)),
                "v" => Convert.ChangeType(raw, f.FieldType, CultureInfo.InvariantCulture),
                "o" => ResolveObject(f.FieldType, raw, objects),
                _ => null,
            };
            if (value != null) f.SetValue(component, value);
        }
    }

    private static object? ResolveObject(Type type, string id, IObjectManager? objects)
    {
        if (objects != null && (objects.TryGetObject<object>(type.Name + "_" + id, out var found) || objects.TryGetObject<object>(id, out found))
            && type.IsInstanceOfType(found))
            return found;
        if (type == typeof(Hero)) return Campaign.Current?.CampaignObjectManager?.Find<Hero>(id);
        if (type == typeof(Settlement)) return Settlement.Find(id);
        return null;
    }

    /// <summary>Client, from Bridge.Tick: attaches components whose party has arrived since.</summary>
    internal static void ClientTick()
    {
        if (Pending.Count > 0) TryAttachPending();
    }

    private static void TryAttachPending()
    {
        for (var i = Pending.Count - 1; i >= 0; i--)
        {
            var (component, partyId, until) = Pending[i];
            var party = MobileParty.All.FirstOrDefault(p => p != null && p.StringId == partyId);
            if (party == null)
            {
                if (DateTime.UtcNow > until)
                {
                    Pending.RemoveAt(i);
                    Log.Warn($"TAOM layer: party '{partyId}' never arrived for its {component.GetType().Name}; not attached");
                }
                continue;
            }
            Pending.RemoveAt(i);
            try
            {
                using (new AllowedThread())
                {
                    if (party.PartyComponent != component) PartyComponentField?.SetValue(party, component);
                    if (component.MobileParty != party) ComponentPartySetter?.Invoke(component, new object[] { party });
                }
                party.Party?.SetVisualAsDirty();
                Log.Info($"TAOM layer: {component.GetType().Name} attached to party '{partyId}'");
            }
            catch (Exception ex) { Log.Warn($"TAOM layer: could not attach {component.GetType().Name} to '{partyId}': {ex.GetBaseException().Message}"); }
        }
    }
}
