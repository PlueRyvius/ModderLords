using System;
using System.Collections.Generic;
using System.Linq;
using GameInterface;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.Players;
using ModderLords.CompatSync;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.CharacterDevelopment;

namespace ModderLords.CompatSync.Coop;

/// <summary>
/// Server only: <see cref="ModderLordsSettings.BothPerks"/>. Every tick, each player's hero and the heroes of their clan
/// (family, companions) get the other perk of every pair they hold one perk of. It runs on the server because Coop
/// keeps a perk opened on a player's game on that game only, while the server's copy is what counts: HP regen, for
/// one, stops at the server's MaxHitPoints. HeroDeveloper.AddPerk is what Coop's own character-screen handler calls on
/// the server, so Coop sends each perk to every player. Turning the rule off stops new grants; perks already given stay.
/// </summary>
public static class BothPerks
{
    private static Campaign? _pairsFor;
    private static List<PerkObject> _paired = new List<PerkObject>();

    /// <summary>From the adapter tick.</summary>
    public static void Tick()
    {
        try { if (Common.ModInformation.IsClient) return; } catch { return; }
        if (!ModderLordsSettings.BothPerks || Campaign.Current is not { } campaign) return;
        if (!ContainerProvider.TryResolve<IPlayerManager>(out var players) || !ContainerProvider.TryResolve<IObjectManager>(out var objects)) return;
        if (!ReferenceEquals(_pairsFor, campaign))
        {
            _pairsFor = campaign;
            _paired = PerkObject.All.Where(p => p.AlternativePerk != null).ToList();
        }

        var heroes = new HashSet<Hero>();
        foreach (var p in players.Players.ToList())
        {
            if (!objects.TryGetObject<Hero>(p.HeroId, out var hero) || hero is null) continue;
            heroes.Add(hero);
            if (hero.Clan is not { } clan) continue;
            foreach (var h in clan.Heroes) heroes.Add(h);
            foreach (var h in clan.Companions) heroes.Add(h);
        }

        foreach (var hero in heroes)
        {
            if (!hero.IsAlive || hero.HeroDeveloper is not { } developer) continue;
            var given = new List<string>();
            foreach (var perk in _paired)
            {
                var other = perk.AlternativePerk;
                if (!hero.GetPerkValue(perk) || hero.GetPerkValue(other)) continue;
                try
                {
                    developer.AddPerk(other);
                    given.Add(other.StringId);
                }
                catch (Exception ex) { Log.Warn($"both perks: could not give {hero.Name} {other.StringId}: {ex.GetBaseException().Message}"); }
            }
            if (given.Count > 0) Log.Info($"both perks: gave {hero.Name} {given.Count} perk(s): {string.Join(", ", given)}");
        }
    }
}
