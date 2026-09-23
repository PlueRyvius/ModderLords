using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Common;
using GameInterface;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.Players;
using GameInterface.Services.Players.Data;
using HarmonyLib;
using LiteNetLib;
using Newtonsoft.Json.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;

namespace ModderLords.CompatSync.Coop;

/// <summary>
/// Server: runs a relayed player action as the player who sent it. Only methods in this server's own recipe are run,
/// every town, settlement, hero, clan or party they name must belong to that player's clan, requests are rate-limited
/// per player, and the call happens on the game thread with the player's hero, party and clan standing in for "the
/// player" (<c>Hero.MainHero</c> follows <c>Game.PlayerTroop</c>, <c>Clan.PlayerClan</c> follows the campaign's
/// PlayerDefaultFaction, <c>MobileParty.MainParty</c> the campaign's MainParty).
/// </summary>
public static class ServerRelay
{
    private static readonly HashSet<string> Allowed = new HashSet<string>(StringComparer.Ordinal);
    private static readonly Dictionary<NetPeer, Queue<DateTime>> Recent = new Dictionary<NetPeer, Queue<DateTime>>();
    // A backstop: clients already send only a control's settled value (RelayCoalescer).
    private const int PerSecond = 20;

    public static string LoadAllowList(string recipesJson)
    {
        try
        {
            foreach (var mod in JObject.Parse(recipesJson)["Mods"] as JArray ?? new JArray())
                foreach (var t in mod["Relays"] as JArray ?? new JArray())
                    Allowed.Add(t.ToString());
        }
        catch (Exception ex) { return "recipe parse failed: " + ex.Message; }
        return Allowed.Count + " relayed action(s) allowed";
    }

    public static void Handle(NetPeer peer, string method, IList<string>? kinds, IList<string>? values, Action<bool, string> reply)
    {
        if (!Allowed.Contains(method)) { reply(false, "not a relayed action in this server's recipe"); return; }
        if (!RateOk(peer)) { reply(false, "too many requests"); return; }
        if (!ContainerProvider.TryResolve<IPlayerManager>(out var players) || !players.TryGetPlayer(peer, out var player) || player is null)
        {
            reply(false, "the sender is not a known player");
            return;
        }
        GameThread.RunSafe(() => Run(player, method, kinds ?? new List<string>(), values ?? new List<string>(), reply), false, "ModderLords relay");
    }

    private static void Run(Player player, string method, IList<string> kinds, IList<string> values, Action<bool, string> reply)
    {
        try
        {
            if (!ContainerProvider.TryResolve<IObjectManager>(out var objects) || !objects.TryGetObject<Hero>(player.HeroId, out var hero) || hero?.Clan is null)
            {
                reply(false, "the player's hero was not found");
                return;
            }
            objects.TryGetObject<MobileParty>(player.MobilePartyId, out var party);

            var target = RecipeGates.Resolve(method).OfType<MethodInfo>().FirstOrDefault(m => m.GetParameters().Length == kinds.Count);
            if (target is null) { reply(false, "the method was not found"); return; }
            if (!RelayCodec.TryDecode(kinds, values, target.GetParameters(), out var args, out var why)) { reply(false, why); return; }
            if (OwnershipProblem(args, hero.Clan) is { } problem) { reply(false, problem); return; }

            object? receiver = null;
            if (!target.IsStatic && (receiver = Receiver(target.DeclaringType!)) is null)
            {
                reply(false, "no instance of " + target.DeclaringType!.Name + " to run it on");
                return;
            }
            using (new PlayerScope(hero, party))
            using (RelayGates.Relaying())
                target.Invoke(receiver, args);
            reply(true, "ran for " + hero.Name);
        }
        catch (Exception ex)
        {
            var inner = ex is TargetInvocationException { InnerException: { } ie } ? ie : ex;
            reply(false, "failed: " + inner.GetBaseException().Message);
        }
    }

    private static string? OwnershipProblem(object?[] args, Clan clan)
    {
        var named = 0;
        foreach (var a in args)
        {
            Clan? owner;
            switch (a)
            {
                case Town t: owner = t.OwnerClan; break;
                case Settlement s: owner = s.OwnerClan; break;
                case Hero h: owner = h.Clan; break;
                case Clan c: owner = c; break;
                case MobileParty p: owner = p.ActualClan; break;
                default: continue;
            }
            if (owner != clan) return "the player's clan does not own " + a;
            named++;
        }
        return named == 0 ? "the call names nothing the player owns" : null;
    }

    private static object? Receiver(Type type)
    {
        foreach (var name in new[] { "Instance", "Current" })
        {
            var p = AccessTools.Property(type, name);
            if (p?.GetGetMethod(true) is { IsStatic: true } && p.GetValue(null, null) is { } v && type.IsInstanceOfType(v)) return v;
        }
        if (typeof(CampaignBehaviorBase).IsAssignableFrom(type) && Campaign.Current != null)
            return AccessTools.Method(typeof(Campaign), "GetCampaignBehavior")?.MakeGenericMethod(type).Invoke(Campaign.Current, null);
        return null;
    }

    private static bool RateOk(NetPeer peer)
    {
        lock (Recent)
        {
            if (!Recent.TryGetValue(peer, out var q)) Recent[peer] = q = new Queue<DateTime>();
            var now = DateTime.UtcNow;
            while (q.Count > 0 && (now - q.Peek()).TotalSeconds > 1) q.Dequeue();
            if (q.Count >= PerSecond) return false;
            q.Enqueue(now);
            return true;
        }
    }

    /// <summary>Swaps "the player" to one player for the duration of a call, and restores it even when the call throws.</summary>
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

        [ThreadStatic] private static int _depth;

        /// <summary>True while some call is running with one player standing in as "the player".</summary>
        internal static bool Active => _depth > 0;

        public PlayerScope(Hero hero, MobileParty? party)
        {
            _depth++;
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
}
