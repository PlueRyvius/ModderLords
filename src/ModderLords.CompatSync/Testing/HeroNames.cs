using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TaleWorlds.CampaignSystem;

namespace ModderLords.Testing;

/// <summary>
/// Finds heroes by name the way every side agrees on. A player's hero has another StringId on each side, so steps find it
/// by name, but mods can dress <see cref="Hero.Name"/> up: Bellum Civile 1.5 puts feudal titles in front of it with a
/// postfix ("Lord Smoke 6177"), and only where the title applies (on the server, not on the player's game, in run
/// 20261123-191658). The hero's own stored name (<c>Hero._name</c>) is what the game set and what Coop sends, so that is
/// what game.whoAmI reports and what a lookup matches first.
/// </summary>
public static class HeroNames
{
    private static readonly FieldInfo? NameField = typeof(Hero).GetField("_name", BindingFlags.Instance | BindingFlags.NonPublic);

    /// <summary>The hero's stored name, without any mod's additions to <see cref="Hero.Name"/>.</summary>
    public static string? Plain(Hero hero)
    {
        try
        {
            if (NameField?.GetValue(hero) is { } stored) return stored.ToString();
        }
        catch (Exception)
        {
            // fall back to the displayed name
        }
        return hero.Name?.ToString();
    }

    /// <summary>True when <paramref name="name"/> is the hero's stored name or its displayed one.</summary>
    public static bool Is(Hero hero, string name) => Plain(hero) == name || hero.Name?.ToString() == name;

    /// <summary>
    /// Every hero named <paramref name="name"/>: alive, dead or disabled, the campaign's own list, and this game's main
    /// hero (on a player's game Coop's copy of that player's hero can be missing from the lists).
    /// </summary>
    public static List<Hero> Find(string name)
    {
        var found = Hero.FindAll(h => Is(h, name))
            .Concat(Hero.AllAliveHeroes.Concat(Hero.DeadOrDisabledHeroes).Where(h => Is(h, name)))
            .Distinct().ToList();
        if (Hero.MainHero != null && Is(Hero.MainHero, name) && !found.Contains(Hero.MainHero)) found.Add(Hero.MainHero);
        return found;
    }

    /// <summary>The one hero named <paramref name="name"/>, or a failed step naming the problem.</summary>
    public static Hero Single(string name)
    {
        var found = Find(name);
        if (found.Count != 1) throw new StepFailedException(found.Count == 0 ? $"no hero named '{name}' in this world" : $"{found.Count} heroes are named '{name}'");
        return found[0];
    }
}
