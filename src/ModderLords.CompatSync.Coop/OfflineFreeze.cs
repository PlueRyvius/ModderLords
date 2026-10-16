using System;
using System.Collections.Generic;
using System.Linq;
using GameInterface;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.Players;
using HarmonyLib;
using ModderLords.CompatSync;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.CharacterDevelopment;
using TaleWorlds.Core;

namespace ModderLords.CompatSync.Coop;

/// <summary>ModderLords' own settings, shown in the Mod settings tab (<see cref="StaticSettingsSource.Own"/>).</summary>
public static class ModderLordsSettings
{
    /// <summary>
    /// While a player is offline, their hero's gold and skill XP do not change. Without it, caravans, workshops and
    /// fiefs keep paying an offline player, and trading keeps raising their Trade skill.
    /// </summary>
    public static bool FreezeOfflinePlayers { get; set; }
}

/// <summary>
/// Server only: <see cref="ModderLordsSettings.FreezeOfflinePlayers"/>. Prefixes on Hero.ChangeHeroGold and
/// HeroDeveloper.AddSkillXp skip the call for the hero of a player who is not connected. Gold losses are skipped too,
/// so the player comes back to exactly the gold they left with. Host edits from the launcher still apply
/// (<see cref="Allow"/>).
/// </summary>
public static class OfflineFreeze
{
    private static readonly Harmony Harmony = new Harmony("ModderLords.Compat.OfflineFreeze");
    private static bool _registered, _installTried;
    private static HashSet<Hero> _offline = new HashSet<Hero>();
    private static int _skippedGold, _skippedXp;
    [ThreadStatic] private static int _allow;

    /// <summary>Runs a host edit that must reach an offline player's hero.</summary>
    public static void Allow(Action action)
    {
        _allow++;
        try { action(); }
        finally { _allow--; }
    }

    /// <summary>From the adapter tick: registers the setting on both sides; on the server, patches once and refreshes who is offline.</summary>
    public static void Tick()
    {
        if (!_registered)
        {
            _registered = true;
            StaticSettingsSource.Own.Add((typeof(ModderLordsSettings), "ModderLords.Compat"));
        }
        try { if (Common.ModInformation.IsClient) return; } catch { return; }
        EnsureInstalled();
        if (!ModderLordsSettings.FreezeOfflinePlayers || Campaign.Current is null) { _offline = new HashSet<Hero>(); return; }

        var offline = new HashSet<Hero>();
        if (ContainerProvider.TryResolve<IPlayerManager>(out var players) && ContainerProvider.TryResolve<IObjectManager>(out var objects))
            foreach (var p in players.Players.ToList())
            {
                if (!objects.TryGetObject<Hero>(p.HeroId, out var hero) || hero is null) continue;
                var online = players.TryGetPeer(p.ControllerId, out var peer) && peer is not null && players.IsConnected(p);
                if (!online) offline.Add(hero);
            }
        if (_skippedGold + _skippedXp > 0)
        {
            Log.Info($"offline freeze: skipped {_skippedGold} gold change(s) and {_skippedXp} XP gain(s) for {offline.Count} offline player(s)");
            _skippedGold = _skippedXp = 0;
        }
        _offline = offline;
    }

    private static void EnsureInstalled()
    {
        if (_installTried) return;
        _installTried = true;
        try
        {
            Harmony.Patch(AccessTools.Method(typeof(Hero), nameof(Hero.ChangeHeroGold)),
                prefix: new HarmonyMethod(typeof(OfflineFreeze), nameof(GoldPrefix)) { priority = Priority.First });
            Harmony.Patch(AccessTools.Method(typeof(HeroDeveloper), nameof(HeroDeveloper.AddSkillXp)),
                prefix: new HarmonyMethod(typeof(OfflineFreeze), nameof(XpPrefix)) { priority = Priority.First });
            Log.Info("offline freeze: installed");
        }
        catch (Exception ex) { Log.Warn("offline freeze: could not patch: " + ex.GetBaseException().Message); }
    }

    private static bool Frozen(Hero? hero) =>
        _allow == 0 && hero is not null && ModderLordsSettings.FreezeOfflinePlayers && _offline.Contains(hero);

    public static bool GoldPrefix(Hero __instance)
    {
        if (!Frozen(__instance)) return true;
        _skippedGold++;
        return false;
    }

    public static bool XpPrefix(HeroDeveloper __instance)
    {
        if (!Frozen(__instance.Hero)) return true;
        _skippedXp++;
        return false;
    }
}
