using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using ModderLords.CompatSync;
using ModderLords.CompatSync.Coop.Admin;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Library;
using TaleWorlds.ObjectSystem;

namespace ModderLords.CompatSync.Coop.Fourberie;

/// <summary>
/// <c>modderlords.agents</c>, typed in the host's server console: shows and sets a player's Fourberie agents (saboteurs,
/// spies, assassins). For putting a player's numbers right after something went wrong, such as the saboteur party that
/// kept growing before FbLedgers ignored Coop's copies.
/// <para>
/// The change is made in the player's book, as that player (FbBooks.Enter), the way the server's own ticks change it. It
/// reaches the player's game with the next book message, within a couple of seconds.
/// </para>
/// </summary>
internal static class FbAgentsAdmin
{
    internal const string SaboteurTroop = "fb_saboteur_tier_1";
    private const int Max = 100000;

    /// <summary>What can be set: the name typed, Fourberie's _crimeValue key, and what it is.</summary>
    private static readonly (string Name, int Key, string What)[] Counters =
    {
        ("saboteurs", 300, "saboteurs ready"),
        ("saboteurs-training", 301, "saboteurs in training"),
        ("spies", 310, "spies ready"),
        ("spies-training", 311, "spies in training"),
        ("assassins", 320, "assassins ready"),
        ("assassins-training", 321, "assassins in training"),
    };

    private const string PartyName = "saboteur-party";
    private const string PartyTrainingName = "saboteur-party-training";

    private static string Usage =>
        "Usage: modderlords.agents [player name] [what number]\n"
        + "  modderlords.agents                     every player's agents\n"
        + "  modderlords.agents <player>            one player's agents\n"
        + "  modderlords.agents <player> <what> <n> set one of them, where <what> is "
        + string.Join(", ", Counters.Select(c => c.Name).Concat(new[] { PartyName, PartyTrainingName }));

    [CommandLineFunctionality.CommandLineArgumentFunction("agents", AdminWire.CommandGroup)]
    public static string AgentsCommand(List<string> args)
    {
        try
        {
            if (!FourberieLayer.Active || !FourberieLayer.IsServer || !FbBooks.Bound) return "Fourberie is not running on this server.";
            if (Campaign.Current == null) return "No campaign is loaded.";
            if (FbBooks.Current != null) return "A player's Fourberie book is in use right now; try again.";

            var players = FbBooks.Everyone();
            if (args.Count == 0)
            {
                if (players.Count == 0) return "No player has Fourberie agents yet.";
                return string.Join("\n", players.Select(p => Describe(p.Hero, p.Party)));
            }

            string? what = null;
            var number = 0;
            var nameArgs = args;
            if (args.Count >= 3 && int.TryParse(args[args.Count - 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
            {
                what = args[args.Count - 2].ToLowerInvariant();
                nameArgs = args.Take(args.Count - 2).ToList();
            }
            var name = string.Join(" ", nameArgs);
            var matches = players.Where(p => string.Equals(p.Hero.Name?.ToString(), name, StringComparison.OrdinalIgnoreCase)
                || string.Equals(p.Hero.StringId, name, StringComparison.Ordinal)).ToList();
            if (matches.Count == 0)
                return $"No player called \"{name}\". Players: {string.Join(", ", players.Select(p => p.Hero.Name?.ToString()))}\n{Usage}";
            if (matches.Count > 1) return $"More than one player is called \"{name}\"; use their hero id: {string.Join(", ", matches.Select(p => p.Hero.StringId))}";
            var (hero, party) = matches[0];

            if (what == null) return Describe(hero, party);
            if (number < 0 || number > Max) return $"The number must be between 0 and {Max}.";
            var error = Set(hero, party, what, number);
            if (error != null) return error + "\n" + Usage;
            Log.Info($"host set {hero.Name}'s Fourberie {what} to {number}");
            return "Done. " + Describe(hero, party);
        }
        catch (Exception ex)
        {
            Log.Warn($"admin command agents failed: {ex}");
            return ex.GetBaseException().Message;
        }
    }

    private static string? Set(Hero hero, MobileParty? party, string what, int number)
    {
        var counter = Counters.FirstOrDefault(c => c.Name == what);
        if (counter.Name == null && what != PartyName && what != PartyTrainingName) return $"Unknown \"{what}\".";
        using var scope = FbBooks.Enter(hero, party);
        if (scope == null) return "The players' Fourberie books cannot be used yet.";
        if (FbBooks.Field("_crimeValue")?.GetValue(null) is not Dictionary<int, int> values) return "Fourberie's agent counts were not found.";
        if (counter.Name != null)
        {
            if (number == 0) values.Remove(counter.Key);
            else values[counter.Key] = number;
            return null;
        }

        var field = FbBooks.Field("_agentsParty");
        var saboteur = MBObjectManager.Instance.GetObject<CharacterObject>(SaboteurTroop);
        if (field == null || saboteur == null) return "Fourberie's saboteur party was not found.";
        var agents = field.GetValue(null) as MobileParty;
        if (agents != null && !agents.IsActive) agents = null;

        if (what == PartyTrainingName)
        {
            var training = agents == null ? 0 : agents.MemberRoster.TotalManCount - agents.MemberRoster.GetTroopCount(saboteur);
            if (number > training) return $"{hero.Name} has {training} troop(s) training in the saboteur party; this can only lower that.";
            var remove = training - number;
            foreach (var element in agents!.MemberRoster.GetTroopRoster().Where(e => e.Character != saboteur && !e.Character.IsHero).Reverse().ToList())
            {
                if (remove == 0) break;
                var take = Math.Min(remove, element.Number);
                agents.MemberRoster.AddToCounts(element.Character, -take);
                remove -= take;
            }
            return null;
        }

        if (agents == null)
        {
            if (number == 0) return null;
            agents = FbLedgers.MakeSaboteurParty();
            if (agents == null) return "The saboteur party could not be made.";
            field.SetValue(null, agents);
        }
        agents.MemberRoster.AddToCounts(saboteur, number - agents.MemberRoster.GetTroopCount(saboteur));
        return null;
    }

    private static string Describe(Hero hero, MobileParty? party)
    {
        using var scope = FbBooks.Enter(hero, party);
        if (scope == null) return $"{hero.Name}: the players' Fourberie books cannot be used yet.";
        var values = FbBooks.Field("_crimeValue")?.GetValue(null) as Dictionary<int, int>;
        int Value(int key) => values != null && values.TryGetValue(key, out var v) ? v : 0;
        var text = new StringBuilder();
        text.Append($"{hero.Name} ({hero.StringId}): saboteurs {Value(300)} (training {Value(301)}), spies {Value(310)} (training {Value(311)}), "
            + $"assassins {Value(320)} (training {Value(321)})");
        var agents = FbBooks.Field("_agentsParty")?.GetValue(null) as MobileParty;
        var saboteur = MBObjectManager.Instance.GetObject<CharacterObject>(SaboteurTroop);
        if (agents is { IsActive: true } && saboteur != null)
        {
            var count = agents.MemberRoster.GetTroopCount(saboteur);
            text.Append($", saboteur party {count} / 20 (training {agents.MemberRoster.TotalManCount - count})");
        }
        else text.Append(", no saboteur party");
        return text.ToString();
    }
}
