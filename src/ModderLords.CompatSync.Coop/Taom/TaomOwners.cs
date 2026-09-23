using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;

namespace ModderLords.CompatSync.Coop.Taom;

/// <summary>
/// Whose TAOM record is whose. TAOM keeps one book per feature (refuges, supply orders) for "the player"; on a co-op
/// server that one book holds every player's rows, and TAOM's code reads all of it as the current player's. A row
/// belongs to the player whose clan owns the row's map party (TAOM sets the party's clan to the founding player's clan).
/// </summary>
internal static class TaomOwners
{
    internal static MobileParty? FindParty(string? partyId)
    {
        if (string.IsNullOrEmpty(partyId)) return null;
        foreach (var p in MobileParty.All)
            if (p != null && string.Equals(p.StringId, partyId, StringComparison.Ordinal))
                return p;
        return null;
    }

    /// <summary>Server: the connected player (hero, party) whose clan owns this party, if any.</summary>
    internal static (Hero Hero, MobileParty? Party)? OwnerOf(MobileParty? party)
    {
        var clan = party?.ActualClan;
        if (clan == null) return null;
        foreach (var p in PlayerContextComponent.Players())
            if (p.Hero.Clan == clan)
                return p;
        return null;
    }

    /// <summary>The party belongs to "the player" as TAOM currently sees it (the local player, or the player in scope).</summary>
    internal static bool IsCurrentPlayers(MobileParty? party) =>
        party?.ActualClan != null && party.ActualClan == Clan.PlayerClan;

    /// <summary>
    /// Runs <paramref name="run"/> with the book in <paramref name="field"/> narrowed to the rows <paramref name="keep"/>
    /// accepts, then writes what the run did (rows added, changed or removed) back into the full book. TAOM's own code
    /// runs unchanged; it simply cannot see other players' rows while it runs.
    /// </summary>
    internal static void WithNarrowedBook(object service, FieldInfo field, Func<object, bool> keep, Action run)
    {
        if (field.GetValue(service) is not IDictionary full) { run(); return; }
        var subset = (IDictionary)Activator.CreateInstance(full.GetType())!;
        foreach (DictionaryEntry e in full)
            if (e.Value != null && keep(e.Value))
                subset[e.Key] = e.Value;
        var before = subset.Keys.Cast<object>().ToList();
        field.SetValue(service, subset);
        try { run(); }
        finally
        {
            // The run may have replaced the book object itself; merge from whatever it holds now.
            var after = field.GetValue(service) as IDictionary ?? subset;
            field.SetValue(service, full);
            foreach (var key in before)
                if (!after.Contains(key))
                    full.Remove(key);
            foreach (DictionaryEntry e in after)
                full[e.Key] = e.Value;
        }
    }
}
