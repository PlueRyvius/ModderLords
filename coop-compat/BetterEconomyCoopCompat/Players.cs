using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Core;

namespace BetterEconomyCoopCompat;

/// <summary>Who the players are on the server, by clan. Shared by the owner scope, the player checks and the treaty filter.</summary>
internal static class Players
{
    private static DateTime _cachedAt = DateTime.MinValue;
    private static readonly Dictionary<Clan, Hero?> ByClan = new Dictionary<Clan, Hero?>();

    /// <summary>
    /// The player hero who belongs to <paramref name="clan"/>, or null for an AI clan. Coop's own "is this a player's
    /// hero?" answer counts offline players too, so their towns are not handed to the AI while they are away.
    /// </summary>
    internal static Hero? For(Clan? clan)
    {
        if (clan == null) return null;
        if ((DateTime.UtcNow - _cachedAt).TotalSeconds >= 1)
        {
            _cachedAt = DateTime.UtcNow;
            ByClan.Clear();
        }
        if (ByClan.TryGetValue(clan, out var known)) return known;
        Hero? found = null;
        try
        {
            foreach (var hero in clan.Heroes)
                if (Coop.IsPlayerHero(hero)) { found = hero; break; }
        }
        catch { }
        ByClan[clan] = found;
        return found;
    }

    internal static bool IsPlayerClan(Clan? clan) => For(clan) != null;

    /// <summary>A kingdom a player leads, or whose ruling clan is a player's: the AI must not make its trade treaties.</summary>
    internal static bool IsPlayerLedKingdom(Kingdom? kingdom) =>
        kingdom != null && (kingdom.Leader != null && (Coop.IsPlayerHero(kingdom.Leader) || IsPlayerClan(kingdom.Leader.Clan))
                            || IsPlayerClan(RulingClan(kingdom)));

    private static PropertyInfo? _rulingClan;
    private static bool _rulingClanLooked;

    /// <summary>Kingdom.RulingClan, read by name so a game version without it just falls back to the leader's clan.</summary>
    private static Clan? RulingClan(Kingdom kingdom)
    {
        if (!_rulingClanLooked)
        {
            _rulingClanLooked = true;
            _rulingClan = typeof(Kingdom).GetProperty("RulingClan", BindingFlags.Public | BindingFlags.Instance);
        }
        try { return _rulingClan?.GetValue(kingdom) as Clan; } catch { return null; }
    }

    // The "any player" helpers the comparison rewriter calls in place of "is this the player?".
    public static bool AnyPlayerHero(Hero? hero) => Coop.IsPlayerHero(hero);
    public static bool AnyPlayerClan(Clan? clan) => IsPlayerClan(clan);
    public static bool AnyPlayerParty(MobileParty? party) => party != null && (Coop.IsPlayerHero(party.LeaderHero) || IsPlayerClan(party.ActualClan));
    public static bool AnyPlayerPartyBase(PartyBase? party) => party?.MobileParty is { } mobile && AnyPlayerParty(mobile);
}

/// <summary>
/// Swaps "the player" to one player for the duration of a call, and restores it even when the call throws:
/// Hero.MainHero follows Game.PlayerTroop, Clan.PlayerClan the campaign's PlayerDefaultFaction, MobileParty.MainParty
/// the campaign's MainParty. Coop keeps its own "resolved main hero" too (read by name), which is set alongside.
/// </summary>
internal sealed class PlayerScope : IDisposable
{
    private static readonly MethodInfo? MainPartySetter = AccessTools.PropertySetter(typeof(Campaign), "MainParty");
    private static readonly MethodInfo? FactionSetter = AccessTools.PropertySetter(typeof(Campaign), "PlayerDefaultFaction");
    private static readonly FieldInfo? Resolved = ResolvedField();

    private readonly Game? _game;
    private readonly Campaign? _campaign;
    private readonly BasicCharacterObject? _troop;
    private readonly MobileParty? _party;
    private readonly Clan? _faction;
    private readonly object? _resolved;
    private readonly Hero? _previous;

    [ThreadStatic] private static int _depth;
    [ThreadStatic] private static Hero? _current;

    /// <summary>The player standing in as "the player" on this thread, or null outside any scope.</summary>
    internal static Hero? CurrentHero => _current;

    /// <summary>True while some call is running with one player standing in as "the player".</summary>
    internal static bool Active => _depth > 0;

    public PlayerScope(Hero hero, MobileParty? party)
    {
        _depth++;
        _previous = _current;
        _current = hero;
        _game = Game.Current;
        _campaign = Campaign.Current;
        _troop = _game?.PlayerTroop;
        _party = _campaign?.MainParty;
        _faction = _campaign is null ? null : Clan.PlayerClan;
        _resolved = Resolved?.GetValue(null);

        if (_game != null) _game.PlayerTroop = hero.CharacterObject;
        if (_campaign != null)
        {
            if (party != null) MainPartySetter?.Invoke(_campaign, new object[] { party });
            FactionSetter?.Invoke(_campaign, new object[] { hero.Clan });
        }
        Resolved?.SetValue(null, hero);
    }

    public void Dispose()
    {
        _depth--;
        _current = _previous;
        Resolved?.SetValue(null, _resolved);
        if (_campaign != null)
        {
            FactionSetter?.Invoke(_campaign, new object?[] { _faction });
            if (_party != null) MainPartySetter?.Invoke(_campaign, new object[] { _party });
        }
        if (_game != null) _game.PlayerTroop = _troop;
    }

    private static FieldInfo? ResolvedField()
    {
        var t = AccessTools.TypeByName("GameInterface.Services.Heroes.Patches.ResolvedMainHeroContext");
        return t is null ? null : AccessTools.Field(t, "ResolvedMainHero");
    }
}
