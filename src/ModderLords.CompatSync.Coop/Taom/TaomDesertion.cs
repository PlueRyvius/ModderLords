using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using HarmonyLib;
using ModderLords.CompatSync;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;

namespace ModderLords.CompatSync.Coop.Taom;

/// <summary>
/// Special-resource upkeep desertion in co-op (TAOM-MAP P3).
///
/// When a player's special resource runs out, TAOM's daily tick removes troops from their party
/// (SpecialResourcesBehavior.ApplyDesertion -> MemberRoster.AddToCounts). That runs on the player's own machine, and
/// Coop's roster patch does not stop a client from changing a managed roster: it logs "Client attempted to
/// AddToCountsAtIndex on a managed TroopRoster" and lets the change stand locally without sending it. So the client
/// showed the troops gone while the server, which owns the party, still had them.
///
/// On a client in a session the removal is sent to the server instead, which removes at most what the party really
/// has; Coop then syncs the roster back. TAOM's own "N deserted" message still shows, with the count computed the way
/// TAOM computes it.
/// </summary>
internal sealed class DesertionRelayComponent : ITaomComponent
{
    public const string Feature = "desertion";
    private const string Owner = "ModderLords.Taom.Desertion";
    private static MethodInfo? _applyDesertion;

    public string Id => "desertion-relay";

    public string? SkipReason(TaomContext context)
    {
        var entry = context.Taom.GetType("TAOM.Features.SpecialResources.TroopDesertionEntry", false);
        var list = entry == null ? null : typeof(IReadOnlyList<>).MakeGenericType(entry);
        _applyDesertion = list == null ? null : context.Taom.GetType("TAOM.Features.SpecialResources.SpecialResourcesBehavior", false)
            ?.GetMethod("ApplyDesertion", BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { typeof(MobileParty), list }, null);
        return _applyDesertion?.ReturnType == typeof(int)
            ? null
            : "TAOM changed; not found: SpecialResourcesBehavior.ApplyDesertion(MobileParty, IReadOnlyList<TroopDesertionEntry>)";
    }

    public string Install(TaomContext context)
    {
        if (context.IsServer)
        {
            TaomActions.Register(Feature, ServerDesert);
            return "server removes deserting troops from players' parties";
        }
        new Harmony(Owner).Patch(_applyDesertion!, prefix: new HarmonyMethod(typeof(DesertionRelayComponent), nameof(ApplyDesertionPrefix)));
        return "client sends upkeep desertion to the server";
    }

    /// <summary>Client: counts as TAOM would, sends the removals, and leaves the roster to the server.</summary>
    private static bool ApplyDesertionPrefix(MobileParty party, object desertions, ref int __result)
    {
        if (!TaomActions.IsCoopClient) return true;
        __result = 0;
        if (party?.MemberRoster == null || desertions is not IEnumerable list) return false;
        var args = new List<string>();
        foreach (var entry in list)
        {
            var id = entry.GetType().GetProperty("TroopId")?.GetValue(entry) as string;
            var want = entry.GetType().GetProperty("DesertCount")?.GetValue(entry) is int n ? n : 0;
            var character = id == null ? null : CharacterObject.Find(id);
            if (character == null || want <= 0) continue;
            var index = party.MemberRoster.FindIndexOfTroop(character);
            if (index < 0) continue;
            var remove = Math.Min(want, party.MemberRoster.GetElementNumber(index));
            if (remove <= 0) continue;
            args.Add(id!);
            args.Add(remove.ToString(CultureInfo.InvariantCulture));
            __result += remove;
        }
        if (args.Count > 0)
        {
            TaomActions.Send!(Feature, "desert", args);
            Log.Info($"TAOM layer: upkeep desertion of {__result} troop(s) sent to the server");
        }
        return false;
    }

    /// <summary>Server, inside PlayerScope: removes up to the requested count of each troop from the player's own party.</summary>
    private static TaomActionOutcome ServerDesert(Hero hero, MobileParty? party, string op, IList<string> args)
    {
        if (op != "desert" || party?.MemberRoster == null) return TaomActionOutcome.Fail("");
        var removed = 0;
        for (var i = 0; i + 1 < args.Count; i += 2)
        {
            var character = CharacterObject.Find(args[i]);
            if (character == null || character.IsHero) continue;
            if (!int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var want) || want <= 0) continue;
            var index = party.MemberRoster.FindIndexOfTroop(character);
            if (index < 0) continue;
            var remove = Math.Min(want, party.MemberRoster.GetElementNumber(index));
            if (remove <= 0) continue;
            party.MemberRoster.AddToCounts(character, -remove);
            removed += remove;
        }
        if (removed > 0) Log.Info($"TAOM layer: upkeep desertion removed {removed} troop(s) from {hero.Name}'s party");
        return new TaomActionOutcome(true, "");
    }
}
