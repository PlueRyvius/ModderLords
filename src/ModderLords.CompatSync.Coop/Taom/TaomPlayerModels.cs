using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using ModderLords.CompatSync;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;

namespace ModderLords.CompatSync.Coop.Taom;

/// <summary>
/// TAOM game models that treat "the player" differently, computed on the server for real players (TAOM-MAP P4).
///
/// The server computes every game model, and these TAOM models decide "is this the player?" by comparing with
/// Hero.MainHero / MobileParty.MainParty, which on a dedicated server is the idle world-generation hero. So every real
/// player got TAOM's AI rules: pregnancy chance (player/spouse rule), volunteer recruitment across alignments, the
/// prisoner-recruitment morale waiver, trade limits for their caravans, and whether their party can sail when naval
/// travel is player-only. Server only: when one of these runs for a connected player's hero, party or clan, it runs
/// with that player standing in as "the player" (PlayerScope), exactly as it would in single player.
/// </summary>
internal sealed class PlayerModelsComponent : ITaomComponent
{
    private const string Owner = "ModderLords.Taom.PlayerModels";

    private static readonly List<(MethodInfo Method, string Prefix)> Targets = new List<(MethodInfo, string)>();

    public string Id => "player-models";

    public string? SkipReason(TaomContext context)
    {
        if (!context.IsServer) return "client";
        Targets.Clear();
        var t = context.Taom;
        void Add(string type, string method, Type[] args, string prefix)
        {
            if (t.GetType(type, false)?.GetMethod(method, BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly, null, args, null) is { } m)
                Targets.Add((m, prefix));
        }
        Add("TAOM.Features.RaceAge.Models.TaomPregnancyModel", "GetDailyChanceOfPregnancyForHero", new[] { typeof(Hero) }, nameof(PregnancyPrefix));
        Add("TAOM.Features.TroopProgression.Models.TaomVolunteerModel", "MaximumIndexHeroCanRecruitFromHero",
            new[] { typeof(Hero), typeof(Hero), typeof(int) }, nameof(BuyerPrefix));
        Add("TAOM.Features.PrisonerRecruitment.Models.TaomPrisonerRecruitmentCalculationModel", "GetPrisonerRecruitmentMoraleEffect",
            new[] { typeof(PartyBase), typeof(CharacterObject), typeof(int) }, nameof(PartyBasePrefix));
        Add("TAOM.Features.CulturalFeats.Models.TaomCaravanModel", "GetInitialTradeGold",
            new[] { typeof(Hero), typeof(bool), typeof(bool) }, nameof(OwnerPrefix));
        Add("TAOM.Features.CulturalFeats.Models.TaomCaravanModel", "GetMaxGoldToSpendOnOneItemCategory",
            new[] { typeof(MobileParty), typeof(TaleWorlds.Core.ItemCategory) }, nameof(CaravanPrefix));
        Add("TAOM.Features.NavalTravel.Models.TaomPartyNavigationModel", "HasNavalNavigationCapability",
            new[] { typeof(MobileParty) }, nameof(MobilePartyPrefix));
        return Targets.Count == 0 ? "TAOM changed; none of the player-sensitive models were found" : null;
    }

    public string Install(TaomContext context)
    {
        _gameThread = System.Threading.Thread.CurrentThread.ManagedThreadId;
        var h = new Harmony(Owner);
        var finalizer = new HarmonyMethod(typeof(PlayerModelsComponent), nameof(CloseScope));
        foreach (var (method, prefix) in Targets)
            h.Patch(method, prefix: new HarmonyMethod(typeof(PlayerModelsComponent), prefix), finalizer: finalizer);
        return "server computes " + string.Join(", ", Targets.Select(x => x.Method.DeclaringType!.Name + "." + x.Method.Name)) +
               " with the player concerned as TAOM's player";
    }

    // ---- who is a player (cached per second; these run in hot model paths) ------------------------------

    private static DateTime _builtAt = DateTime.MinValue;
    private static Dictionary<Hero, (Hero Hero, MobileParty? Party)> _byHero = new Dictionary<Hero, (Hero, MobileParty?)>();
    private static Dictionary<MobileParty, (Hero Hero, MobileParty? Party)> _byParty = new Dictionary<MobileParty, (Hero, MobileParty?)>();
    private static Dictionary<Clan, (Hero Hero, MobileParty? Party)> _byClan = new Dictionary<Clan, (Hero, MobileParty?)>();

    private static void Refresh()
    {
        if ((DateTime.UtcNow - _builtAt).TotalSeconds < 1) return;
        _builtAt = DateTime.UtcNow;
        var byHero = new Dictionary<Hero, (Hero, MobileParty?)>();
        var byParty = new Dictionary<MobileParty, (Hero, MobileParty?)>();
        var byClan = new Dictionary<Clan, (Hero, MobileParty?)>();
        foreach (var p in PlayerContextComponent.Players())
        {
            byHero[p.Hero] = p;
            if (p.Party != null) byParty[p.Party] = p;
            if (p.Hero.Clan != null && !byClan.ContainsKey(p.Hero.Clan)) byClan[p.Hero.Clan] = p;
        }
        _byHero = byHero; _byParty = byParty; _byClan = byClan;
    }

    /// <summary>The thread the layer installed on (the game thread, via Bridge.Tick).</summary>
    private static int _gameThread;

    /// <summary>
    /// PlayerScope swaps the campaign's main party and player clan for everyone, so it is only opened on the game
    /// thread. The engine evaluates some models (party navigation, AI) from parallel party ticks; those calls keep
    /// TAOM's plain answer rather than race the swap.
    /// </summary>
    private static IDisposable? Open((Hero Hero, MobileParty? Party)? player) =>
        player is { } p && p.Hero != Hero.MainHero && System.Threading.Thread.CurrentThread.ManagedThreadId == _gameThread
            ? new ServerRelay.PlayerScope(p.Hero, p.Party)
            : null;

    private static (Hero, MobileParty?)? ByHero(Hero? hero)
    {
        if (hero == null || ServerRelay.PlayerScope.Active || System.Threading.Thread.CurrentThread.ManagedThreadId != _gameThread) return null;
        Refresh();
        return _byHero.TryGetValue(hero, out var p) ? p : null;
    }

    private static (Hero, MobileParty?)? ByParty(MobileParty? party)
    {
        if (party == null || ServerRelay.PlayerScope.Active || System.Threading.Thread.CurrentThread.ManagedThreadId != _gameThread) return null;
        Refresh();
        return _byParty.TryGetValue(party, out var p) ? p : null;
    }

    private static (Hero, MobileParty?)? ByClan(Clan? clan)
    {
        if (clan == null || ServerRelay.PlayerScope.Active || System.Threading.Thread.CurrentThread.ManagedThreadId != _gameThread) return null;
        Refresh();
        return _byClan.TryGetValue(clan, out var p) ? p : null;
    }

    // ---- prefixes (open the scope) and the shared finalizer (closes it) ----------------------------------

    private static void PregnancyPrefix(Hero hero, out IDisposable? __state) =>
        __state = Open(ByHero(hero) ?? ByHero(hero?.Spouse));

    private static void BuyerPrefix(Hero buyerHero, out IDisposable? __state) => __state = Open(ByHero(buyerHero));

    private static void OwnerPrefix(Hero owner, out IDisposable? __state) => __state = Open(ByHero(owner));

    private static void PartyBasePrefix(PartyBase party, out IDisposable? __state) =>
        __state = Open(ByParty(party?.MobileParty) ?? ByHero(party?.LeaderHero));

    private static void CaravanPrefix(MobileParty caravan, out IDisposable? __state) => __state = Open(ByClan(caravan?.Owner?.Clan));

    private static void MobilePartyPrefix(MobileParty mobileParty, out IDisposable? __state) => __state = Open(ByParty(mobileParty));

    private static Exception? CloseScope(IDisposable? __state, Exception? __exception)
    {
        __state?.Dispose();
        return __exception;
    }
}
