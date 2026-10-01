using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using ModderLords.CompatSync;
using Newtonsoft.Json.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Roster;
using TaleWorlds.Core;
using TaleWorlds.Localization;
using TaleWorlds.ObjectSystem;

namespace ModderLords.CompatSync.Coop.Fourberie;

/// <summary>
/// fourb-parties, ledger half (docs/FOURBERIE-LAYER-PLAN.md, phase 4). Fourberie keeps a player's gang ("Your lads")
/// and saboteurs in invisible, AI-less parties it makes with FourberieBehavior.CreateVirtualParty. Made on a player's
/// game, Coop keeps such a party local to that game (a phantom): the server's ticks cannot feed or heal it, and Coop's
/// party screen drops transfers into it.
///
/// So the server makes each player's ledger parties, once, as that player (they replicate to every game with the same
/// id), and CreateVirtualParty on either side hands back the player's existing ledger party, emptied, instead of making
/// another. What is in them (members, prisoners, warehouse items) travels in the book under "ml_ledgers", row per
/// ledger, both ways: Fourberie's own party-screen and stash callbacks fill them on the player's game, and the server's
/// ticks change them there.
/// </summary>
internal static class FbLedgers
{
    internal const string Section = "ml_ledgers";

    internal static (string Role, string Name)[] Roles => FbLedgerRoles.Roles;

    /// <summary>Player key (hero StringId) -> role -> ledger party. On a player's game there is one key, their own.</summary>
    private static readonly Dictionary<string, Dictionary<string, MobileParty>> Registry = new Dictionary<string, Dictionary<string, MobileParty>>(StringComparer.Ordinal);
    private static MethodInfo? _create;
    private static long _reused, _made, _phantoms;

    internal static void Bind(MethodInfo createVirtualParty) => _create = createVirtualParty;

    internal static void Reset() => Registry.Clear();

    private static string? PlayerKey() =>
        FourberieLayer.IsServer ? FbBooks.Current : FourberieLayer.IsCoopClient ? Hero.MainHero?.StringId : null;

    /// <summary>Self-test: a player's ledger party for a role, or null.</summary>
    internal static MobileParty? Of(string key, string role) => Find(key, role);

    private static MobileParty? Find(string key, string role) =>
        Registry.TryGetValue(key, out var roles) && roles.TryGetValue(role, out var party) && party != null && party.IsActive ? party : null;

    private static void Remember(string key, string role, MobileParty party)
    {
        if (!Registry.TryGetValue(key, out var roles)) Registry[key] = roles = new Dictionary<string, MobileParty>(StringComparer.Ordinal);
        roles[role] = party;
    }

    // ---- CreateVirtualParty ------------------------------------------------------------------------------------

    /// <summary>Prefix on CreateVirtualParty(partyNameID, partyName): the player's existing ledger, emptied, when there is one.</summary>
    internal static bool CreatePrefix(string partyNameID, TextObject partyName, ref MobileParty __result)
    {
        if (PlayerKey() is not { } key || !Roles.Any(r => r.Role == partyNameID)) return true;
        var party = Find(key, partyNameID);
        if (party == null)
        {
            if (!FourberieLayer.IsServer)
            {
                // The server makes every player's ledgers when they join; this one has not arrived yet. Fourberie gets a
                // local party so it carries on; the server's replaces it at the next book message.
                _phantoms++;
                Log.Warn($"{FourberieLayer.Tag}ledgers: '{partyNameID}' is not here from the server yet; this game uses a local one until it arrives");
            }
            return true;
        }
        party.MemberRoster.Clear();
        party.PrisonRoster.Clear();
        party.ItemRoster.Clear();
        if (partyName != null) party.Party.SetCustomName(partyName);
        __result = party;
        _reused++;
        return false;
    }

    /// <summary>Postfix: on the server, a ledger CreateVirtualParty really made is the player's from now on.</summary>
    internal static void CreatePostfix(string partyNameID, MobileParty __result)
    {
        if (!FourberieLayer.IsServer || FbBooks.Current is not { } key || __result == null || !Roles.Any(r => r.Role == partyNameID)) return;
        if (ReferenceEquals(Find(key, partyNameID), __result)) return;
        Remember(key, partyNameID, __result);
        _made++;
    }

    /// <summary>Server, inside the player's scope: makes any ledger this player does not have yet.</summary>
    internal static void EnsureForCurrentPlayer()
    {
        if (_create == null || FbBooks.Current is not { } key) return;
        foreach (var (role, name) in Roles)
        {
            if (Find(key, role) != null) continue;
            try { _create.Invoke(null, new object[] { role, new TextObject(name) }); }
            catch (Exception ex) { Log.Warn($"{FourberieLayer.Tag}ledgers: could not make '{role}': {ex.GetBaseException().Message}"); }
        }
    }

    // ---- the book section ------------------------------------------------------------------------------------

    /// <summary>What is in a player's ledgers, as the book's "ml_ledgers" section.</summary>
    internal static JObject Capture(string key)
    {
        var section = new JObject();
        if (!Registry.TryGetValue(key, out var roles)) return section;
        foreach (var pair in roles)
        {
            var party = pair.Value;
            if (party == null || !party.IsActive) continue;
            section[pair.Key] = new JObject
            {
                ["p"] = party.StringId,
                ["m"] = Troops(party.MemberRoster),
                ["q"] = Troops(party.PrisonRoster),
                ["i"] = Items(party.ItemRoster),
            };
        }
        return section;
    }

    /// <summary>
    /// Makes a player's ledgers hold what <paramref name="section"/> says. On a player's game it also learns which parties
    /// are their ledgers. A row naming a party that is not this player's ledger (server) or does not exist (client) is ignored.
    /// </summary>
    internal static void Apply(string key, JToken? section, ICollection<string> problems)
    {
        if (section is not JObject rows) return;
        foreach (var row in rows.Properties())
        {
            if (!Roles.Any(r => r.Role == row.Name) || row.Value is not JObject ledger) continue;
            var id = (string?)ledger["p"];
            MobileParty? party;
            if (FourberieLayer.IsServer)
            {
                party = Find(key, row.Name);
                if (party == null || party.StringId != id) continue;   // only the player's own, never one named by the player
            }
            else
            {
                party = id == null ? null : MobileParty.All.FirstOrDefault(p => p.StringId == id);
                if (party == null) { problems.Add(row.Name + ": party '" + id + "' not here yet"); continue; }
                Remember(key, row.Name, party);
                // A ledger is bookkeeping, never on the map; Fourberie hides it the same way when it makes one.
                if (party.IsVisible) party.IsVisible = false;
            }
            SetTroops(party.MemberRoster, ledger["m"], problems, row.Name);
            SetTroops(party.PrisonRoster, ledger["q"], problems, row.Name);
            SetItems(party.ItemRoster, ledger["i"], problems, row.Name);
        }
    }

    /// <summary>Server, loading: which parties are a player's ledgers, from the saved book's section.</summary>
    internal static void Restore(string key, JToken? section)
    {
        if (section is not JObject rows) return;
        foreach (var row in rows.Properties())
        {
            var id = (string?)row.Value["p"];
            var party = id == null ? null : MobileParty.All.FirstOrDefault(p => p.StringId == id);
            if (party != null && Roles.Any(r => r.Role == row.Name)) Remember(key, row.Name, party);
        }
    }

    private static JArray Troops(TroopRoster roster) =>
        new JArray(roster.GetTroopRoster().Where(e => e.Character != null && e.Number > 0)
            .Select(e => new JArray(e.Character.StringId, e.Number, e.WoundedNumber, e.Xp)));

    private static JArray Items(ItemRoster roster)
    {
        var list = new JArray();
        for (var i = 0; i < roster.Count; i++)
        {
            var e = roster.GetElementCopyAtIndex(i);
            if (e.EquipmentElement.Item == null || e.Amount <= 0) continue;
            list.Add(new JArray(e.EquipmentElement.Item.StringId, e.EquipmentElement.ItemModifier?.StringId, e.Amount));
        }
        return list;
    }

    private static void SetTroops(TroopRoster roster, JToken? rows, ICollection<string> problems, string where)
    {
        if (rows is not JArray list || JToken.DeepEquals(Troops(roster), list)) return;
        var wanted = new List<(CharacterObject Character, int Number, int Wounded, int Xp)>();
        foreach (var row in list)
        {
            if (row is not JArray { Count: 4 } r || r[0].Type != JTokenType.String) { problems.Add(where + ": bad troop row"); return; }
            var character = MBObjectManager.Instance.GetObject<CharacterObject>((string)r[0]!);
            if (character == null) { problems.Add(where + ": troop '" + r[0] + "' not found"); continue; }
            var number = Math.Max(0, (int)r[1]!);
            if (number > 0) wanted.Add((character, number, Math.Min(number, Math.Max(0, (int)r[2]!)), Math.Max(0, (int)r[3]!)));
        }
        roster.Clear();
        foreach (var (character, number, wounded, xp) in wanted) roster.AddToCounts(character, number, false, wounded, xp);
    }

    private static void SetItems(ItemRoster roster, JToken? rows, ICollection<string> problems, string where)
    {
        if (rows is not JArray list || JToken.DeepEquals(Items(roster), list)) return;
        var wanted = new List<(EquipmentElement Element, int Amount)>();
        foreach (var row in list)
        {
            if (row is not JArray { Count: 3 } r || r[0].Type != JTokenType.String) { problems.Add(where + ": bad item row"); return; }
            var item = MBObjectManager.Instance.GetObject<ItemObject>((string)r[0]!);
            if (item == null) { problems.Add(where + ": item '" + r[0] + "' not found"); continue; }
            var modifier = r[1].Type == JTokenType.String ? MBObjectManager.Instance.GetObject<ItemModifier>((string)r[1]!) : null;
            var amount = Math.Max(0, (int)r[2]!);
            if (amount > 0) wanted.Add((new EquipmentElement(item, modifier), amount));
        }
        roster.Clear();
        foreach (var (element, amount) in wanted) roster.AddToCounts(element, amount);
    }

    internal static string Summary() => $"ledgers reused {_reused}, made {_made}" + (_phantoms > 0 ? $", local stand-ins {_phantoms}" : "");
}

/// <summary>Both sides: CreateVirtualParty hands out the player's own ledger parties.</summary>
internal sealed class FbLedgersComponent : IFbComponent
{
    private const string Owner = "ModderLords.Fourberie.Ledgers";
    private MethodInfo? _create;

    public string Id => "ledgers";

    public string? SkipReason(FbContext context)
    {
        if (context.Fields == null) return "no books: " + context.FieldsProblem;
        _create = context.Method(FbFields.Behavior, "CreateVirtualParty", 2);
        return _create == null ? "FourberieBehavior.CreateVirtualParty not found" : null;
    }

    public string Install(FbContext context)
    {
        FbLedgers.Bind(_create!);
        new Harmony(Owner).Patch(_create,
            prefix: new HarmonyMethod(typeof(FbLedgers), nameof(FbLedgers.CreatePrefix)) { priority = Priority.First },
            postfix: new HarmonyMethod(typeof(FbLedgers), nameof(FbLedgers.CreatePostfix)));
        return context.IsServer
            ? "each player's gang and saboteur parties are made once on the server and reused"
            : "Fourberie uses this player's gang and saboteur parties from the server";
    }
}
