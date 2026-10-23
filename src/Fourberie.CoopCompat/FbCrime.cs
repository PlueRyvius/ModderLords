using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using ModderLords.CompatSync;
using Newtonsoft.Json.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;

namespace ModderLords.CompatSync.Coop.Fourberie;

/// <summary>
/// fourb-crime (docs/FOURBERIE-LAYER-PLAN.md, phase 6b). Crime rating is the main hero's, one number per kingdom and clan;
/// in co-op Coop syncs the kingdoms' one number to everyone and switches vanilla's daily crime change off altogether
/// (DisableCrimeCampaignBehavior, upstream issue #3234: "the crime rating mechanic is missing"). Fourberie's whole game is
/// crime, so here it becomes each player's own:
///
/// - Server: entering a player's scope swaps every faction's crime rating to that player's (the auto-property's backing
///   field, so Coop broadcasts nothing); inside a scope, a crime write (ChangeCrimeRatingAction runs whole: its notice,
///   its war past the threshold) lands in the backing field, so it stays that player's. Once a day each connected player
///   gets vanilla's daily crime change as that player (decay, owned alleys, Fourberie's territories and base).
/// - The book carries each player's ratings ("ml_crime"), saved with the campaign and mirrored both ways; a player's game
///   shows their own ratings, and what its own crimes change there is reported back.
///
/// Known limit: a crime write the server makes outside any player's run (none in vanilla co-op, which has the crime
/// behaviour off) still goes to Coop's shared number and reaches every player's game.
/// </summary>
internal static class FbCrime
{
    internal const string Section = "ml_crime";

    private static FieldInfo? _kingdomField, _clanField;
    private static readonly Dictionary<string, Dictionary<string, float>> Books = new Dictionary<string, Dictionary<string, float>>(StringComparer.Ordinal);
    private static int _lastDay = -1;
    private static long _kept, _days;

    internal static string? Bind()
    {
        _kingdomField = AccessTools.Field(typeof(Kingdom), "<MainHeroCrimeRating>k__BackingField");
        _clanField = AccessTools.Field(typeof(Clan), "<MainHeroCrimeRating>k__BackingField");
        return _kingdomField == null || _clanField == null ? "MainHeroCrimeRating's backing fields were not found" : null;
    }

    internal static void Reset()
    {
        Books.Clear();
        _lastDay = -1;
    }

    private static IEnumerable<IFaction> Factions() => Kingdom.All.Cast<IFaction>().Concat(Clan.All.Where(c => !c.IsBanditFaction));

    private static float Get(IFaction f) => f is Kingdom ? (float)_kingdomField!.GetValue(f) : (float)_clanField!.GetValue(f);

    private static void Set(IFaction f, float value)
    {
        if (f is Kingdom) _kingdomField!.SetValue(f, value); else _clanField!.SetValue(f, value);
    }

    /// <summary>Every faction's rating that is not zero, by faction id.</summary>
    internal static Dictionary<string, float> Live()
    {
        var live = new Dictionary<string, float>(StringComparer.Ordinal);
        if (_kingdomField == null) return live;
        foreach (var f in Factions())
        {
            var v = Get(f);
            if (Math.Abs(v) > 0.0001f) live[f.StringId] = v;
        }
        return live;
    }

    /// <summary>Makes every faction's rating what <paramref name="ratings"/> says (zero where it says nothing).</summary>
    internal static void Install(IReadOnlyDictionary<string, float> ratings)
    {
        if (_kingdomField == null) return;
        foreach (var f in Factions())
        {
            ratings.TryGetValue(f.StringId, out var wanted);
            if (Math.Abs(Get(f) - wanted) > 0.0001f) Set(f, wanted);
        }
    }

    // ---- server: the swap around a player's scope ---------------------------------------------------------------

    /// <summary>Server: puts <paramref name="key"/>'s ratings in place; dispose to keep what changed and put back what was there.</summary>
    internal static IDisposable Enter(string key)
    {
        if (_kingdomField == null) return Nothing.Instance;
        var outside = Live();
        Install(Books.TryGetValue(key, out var mine) ? mine : new Dictionary<string, float>());
        return new Exit(key, outside);
    }

    private sealed class Exit : IDisposable
    {
        private readonly string _key;
        private Dictionary<string, float>? _outside;

        public Exit(string key, Dictionary<string, float> outside)
        {
            _key = key;
            _outside = outside;
        }

        public void Dispose()
        {
            if (_outside == null) return;
            Books[_key] = Live();
            Install(_outside);
            _outside = null;
        }
    }

    private sealed class Nothing : IDisposable
    {
        public static readonly Nothing Instance = new Nothing();
        public void Dispose() { }
    }

    /// <summary>Prefix on MainHeroCrimeRating's setters: inside a player's run on the server, the write stays theirs.</summary>
    internal static bool SetterPrefix(object __instance, float value)
    {
        if (!FourberieLayer.IsServer || FbBooks.Current == null || _kingdomField == null) return true;
        Set((IFaction)__instance, value);
        _kept++;
        return false;
    }

    /// <summary>Server, every frame: once per campaign day, vanilla's daily crime change for each connected player, as them.</summary>
    internal static void ServerTick()
    {
        if (!FourberieLayer.IsServer || _kingdomField == null || Campaign.Current == null) return;
        var day = (int)CampaignTime.Now.ToDays;
        if (_lastDay < 0) { _lastDay = day; return; }
        if (day == _lastDay) return;
        _lastDay = day;
        Daily();
    }

    internal static void Daily()
    {
        foreach (var (hero, party) in FbBooks.Connected())
        {
            if (FbBooks.IsSuspended(hero.StringId)) continue;
            using var scope = FbBooks.Enter(hero, party);
            if (scope == null) return;
            foreach (var f in Factions().Where(f => !f.IsEliminated).ToList())
            {
                var change = f is Kingdom k ? k.DailyCrimeRatingChange : ((Clan)f).DailyCrimeRatingChange;
                if (Math.Abs(change) > 0.0001f) ChangeCrimeRatingAction.Apply(f, change, false);
            }
        }
        _days++;
    }

    // ---- the book section ------------------------------------------------------------------------------------

    internal static JObject Capture(string key)
    {
        var section = new JObject();
        var ratings = FbBooks.Current == key ? Live() : Books.TryGetValue(key, out var mine) ? mine : null;
        if (ratings != null)
            foreach (var pair in ratings.OrderBy(p => p.Key, StringComparer.Ordinal)) section[pair.Key] = Math.Round(pair.Value, 3);
        return section;
    }

    /// <summary>Server: a player's ratings from their saved or reported book.</summary>
    internal static void Store(string key, JToken? section)
    {
        if (section is not JObject rows) return;
        Books[key] = Read(rows);
    }

    /// <summary>Player's game: shows the book's ratings as this player's.</summary>
    internal static void ApplyOnClient(JToken? section)
    {
        if (section is JObject rows && _kingdomField != null) Install(Read(rows));
    }

    /// <summary>Player's game: this player's ratings as they stand, for the book.</summary>
    internal static JObject CaptureOnClient()
    {
        var section = new JObject();
        foreach (var pair in Live().OrderBy(p => p.Key, StringComparer.Ordinal)) section[pair.Key] = Math.Round(pair.Value, 3);
        return section;
    }

    private static Dictionary<string, float> Read(JObject rows)
    {
        var ratings = new Dictionary<string, float>(StringComparer.Ordinal);
        foreach (var row in rows.Properties())
            if (row.Value.Type is JTokenType.Float or JTokenType.Integer && (float)row.Value is var v && !float.IsNaN(v) && !float.IsInfinity(v) && v >= 0 && v <= 1000)
                ratings[row.Name] = v;
        return ratings;
    }

    /// <summary>Self-test: a player's stored rating with one faction.</summary>
    internal static float Of(string key, string factionId) =>
        FbBooks.Current == key ? Live().TryGetValue(factionId, out var live) ? live : 0f
        : Books.TryGetValue(key, out var mine) && mine.TryGetValue(factionId, out var v) ? v : 0f;

    internal static string Summary() => $"crime kept per player {_kept}, daily crime days {_days}";
}

internal sealed class FbCrimeComponent : IFbComponent
{
    private const string Owner = "ModderLords.Fourberie.Crime";

    public string Id => "crime";

    public string? SkipReason(FbContext context) => context.Fields == null ? "no books: " + context.FieldsProblem : FbCrime.Bind();

    public string Install(FbContext context)
    {
        if (!context.IsServer) return "this player's crime ratings follow their book";
        var h = new Harmony(Owner);
        // Above Coop's own setter prefix, which would send the number to every player.
        var prefix = new HarmonyMethod(typeof(FbCrime), nameof(FbCrime.SetterPrefix)) { priority = Priority.First + 300 };
        h.Patch(FbPatchTargets.DeclaredSetter(typeof(Kingdom), nameof(Kingdom.MainHeroCrimeRating)), prefix: prefix);
        h.Patch(FbPatchTargets.DeclaredSetter(typeof(Clan), nameof(Clan.MainHeroCrimeRating)), prefix: prefix);
        return "each player's crime ratings are their own, kept in their book, with vanilla's daily change run for each of them";
    }
}
