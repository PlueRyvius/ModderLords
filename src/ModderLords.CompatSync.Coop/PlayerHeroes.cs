using System.Linq;
using GameInterface;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.Players;
using GameInterface.Services.Players.Data;
using TaleWorlds.CampaignSystem;
using TaleWorlds.ObjectSystem;

namespace ModderLords.CompatSync.Coop;

/// <summary>
/// Maps between Coop players and campaign heroes. Player.HeroId is a Coop object-manager id ("Hero_Player"), NOT the
/// hero's StringId ("Player"), so it must never be compared with StringId or looked up in MBObjectManager directly.
/// Always go through Coop's object manager, as every other player lookup here does.
/// </summary>
internal static class PlayerHeroes
{
    /// <summary>The Coop player controlling this hero, or null when no player does.</summary>
    public static Player? PlayerFor(IPlayerManager players, Hero hero)
    {
        if (hero == null || !ContainerProvider.TryResolve<IObjectManager>(out var objects)) return null;
        return players.Players.FirstOrDefault(p => objects.TryGetObject<Hero>(p.HeroId, out var h) && ReferenceEquals(h, hero));
    }

    /// <summary>The hero behind a Coop player hero id; falls back to MBObjectManager for a plain StringId.</summary>
    public static Hero? HeroFor(string heroId)
    {
        if (ContainerProvider.TryResolve<IObjectManager>(out var objects) && objects.TryGetObject<Hero>(heroId, out var hero) && hero != null)
            return hero;
        return MBObjectManager.Instance.GetObject<Hero>(heroId);
    }
}
