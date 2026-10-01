using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using GameInterface;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.Players;
using HarmonyLib;
using ModderLords.CompatSync;
using ModderLords.CompatSync.Coop.LivingEconomy;
using ModderLords.CompatSync.Coop.Taom;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.ObjectSystem;

namespace ModderLords.CompatSync.Coop.Fourberie;

/// <summary>
/// Server: every player's Fourberie book (fourb-book in docs/FOURBERIE-LAYER-PLAN.md), entering one around a call, saving
/// them all with the campaign, and keeping each in step with that player's game (FbBookSync).
/// </summary>
internal static class FbBooks
{
    /// <summary>The key, inside FourberieBehavior's own save data, that holds every player's book.</summary>
    internal const string SaveKey = "modderlords_fourberie_books";
    internal const int MaxPayload = 4 * 1024 * 1024;
    private const double SendEverySeconds = 2;

    private sealed class Entry
    {
        public object?[]? Values;
        public string? PendingJson;
        public readonly FbServerLedger Ledger = new FbServerLedger();
        public bool Dirty;
    }

    private static List<FieldInfo>? _fields;
    private static StaticBookSchema? _schema;
    private static StaticBookSwitch? _switch;
    private static IReadOnlyList<(string Name, Type Type)>? _shape;
    private static readonly Dictionary<string, Entry> Entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
    private static readonly HashSet<string> Warned = new HashSet<string>(StringComparer.Ordinal);
    private static DateTime _nextSend = DateTime.MinValue;
    private static long _scoped, _pushed, _reports;

    internal static bool Bound => _fields != null;

    /// <summary>The player whose book is installed in Fourberie's statics right now, or null.</summary>
    internal static string? Current => _switch?.Current;

    internal static void Bind(List<FieldInfo> fields) => _fields = fields;

    /// <summary>True when this player's book is installed further down the stack, under another player's.</summary>
    internal static bool IsSuspended(string key) => _switch?.IsSuspended(key) == true;

    private static bool EnsureSchema()
    {
        if (_schema != null) return true;
        if (_fields == null || Campaign.Current == null) return false;
        _schema = new StaticBookSchema(_fields, FbFields.Persisted);
        _switch = new StaticBookSwitch(_schema);
        _shape = FbBookCodec.PersistedShape(_schema);
        return true;
    }

    // ---- scope ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// Runs as <paramref name="hero"/>: the hero stands in as the player (ServerRelay.PlayerScope) and their book is in
    /// Fourberie's statics. Null when the books cannot be used yet (no campaign).
    /// </summary>
    internal static IDisposable? Enter(Hero hero, MobileParty? party)
    {
        if (hero == null || !EnsureSchema()) return null;
        var key = hero.StringId;
        var entry = EntryFor(key);
        var player = new ServerRelay.PlayerScope(hero, party ?? hero.PartyBelongedTo);
        IDisposable book;
        try
        {
            book = _switch!.Enter(key, () => ValuesOf(key, entry), v => { entry.Values = v; entry.Dirty = true; });
        }
        catch
        {
            player.Dispose();
            throw;
        }
        _scoped++;
        return new Both(book, player);
    }

    private sealed class Both : IDisposable
    {
        private IDisposable? _book;
        private IDisposable? _player;

        public Both(IDisposable book, IDisposable player)
        {
            _book = book;
            _player = player;
        }

        public void Dispose()
        {
            try { _book?.Dispose(); }
            finally
            {
                _book = null;
                _player?.Dispose();
                _player = null;
            }
        }
    }

    private static Entry EntryFor(string key)
    {
        if (!Entries.TryGetValue(key, out var entry)) Entries[key] = entry = new Entry();
        return entry;
    }

    private static object?[] ValuesOf(string key, Entry entry)
    {
        if (entry.Values != null) return entry.Values;
        var values = _schema!.Fresh();
        if (entry.PendingJson != null)
        {
            var problems = new List<string>();
            try
            {
                var decoded = FbBookCodec.Decode(JObject.Parse(entry.PendingJson), _shape!, FbGameRefs.Instance, problems, i => values[_schema.Persisted[i]]);
                for (var i = 0; i < decoded.Length; i++) values[_schema.Persisted[i]] = decoded[i];
            }
            catch (JsonException ex) { problems.Add("saved book is not JSON: " + ex.Message); }
            Report(key, "loading the saved book", problems);
            entry.PendingJson = null;
        }
        return entry.Values = values;
    }

    private static void Report(string key, string what, List<string> problems)
    {
        if (problems.Count == 0) return;
        Log.Warn($"{FourberieLayer.Tag}{what} for '{key}': {problems.Count} problem(s): {FourberieLayer.Some(problems, 3)}");
    }

    // ---- players ---------------------------------------------------------------------------------------------------

    /// <summary>Players connected right now, with their parties. Ticks run only for these (offline players' empires freeze).</summary>
    internal static List<(Hero Hero, MobileParty? Party)> Connected()
    {
        var list = new List<(Hero, MobileParty?)>();
        if (!ContainerProvider.TryResolve<IPlayerManager>(out var manager) || !ContainerProvider.TryResolve<IObjectManager>(out var objects)) return list;
        foreach (var p in manager.Players.ToList())
        {
            if (!manager.TryGetPeer(p.ControllerId, out var peer) || peer == null) continue;
            if (!objects.TryGetObject<Hero>(p.HeroId, out var hero) || hero == null) continue;
            objects.TryGetObject<MobileParty>(p.MobilePartyId, out var party);
            list.Add((hero, party));
        }
        return list;
    }

    /// <summary>Every hero with a book, connected or not, plus connected players without one yet. World events reach all of these.</summary>
    internal static List<(Hero Hero, MobileParty? Party)> Everyone()
    {
        var list = Connected();
        var seen = new HashSet<string>(list.Select(p => p.Hero.StringId), StringComparer.Ordinal);
        foreach (var key in Entries.Keys.ToList())
        {
            if (seen.Contains(key)) continue;
            var hero = MBObjectManager.Instance.GetObject<Hero>(key);
            if (hero == null || !hero.IsAlive) continue;
            list.Add((hero, hero.PartyBelongedTo));
        }
        return list;
    }

    // ---- save ----------------------------------------------------------------------------------------------------

    /// <summary>Postfix on FourberieBehavior.SyncData: every player's book rides along under its own key.</summary>
    internal static void SyncDataPostfix(IDataStore dataStore)
    {
        try
        {
            if (dataStore.IsSaving)
            {
                if (Current != null) Log.Warn(FourberieLayer.Tag + "saving while '" + Current + "''s book is installed; Fourberie's own data in this save is that player's");
                var books = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var pair in Entries)
                {
                    if (pair.Value.Values == null) { if (pair.Value.PendingJson != null) books[pair.Key] = pair.Value.PendingJson; continue; }
                    books[pair.Key] = Encode(pair.Key, pair.Value).ToString(Formatting.None);
                }
                dataStore.SyncData(SaveKey, ref books);
            }
            else
            {
                Dictionary<string, string>? books = null;
                dataStore.SyncData(SaveKey, ref books);
                Entries.Clear();
                Warned.Clear();
                if (books != null)
                    foreach (var pair in books) EntryFor(pair.Key).PendingJson = pair.Value;
                Log.Info($"{FourberieLayer.Tag}books: {books?.Count ?? 0} player book(s) in this save");
            }
        }
        catch (Exception ex)
        {
            Log.Warn(FourberieLayer.Tag + "books could not be " + (dataStore.IsSaving ? "saved" : "loaded") + ": " + ex.GetBaseException().Message);
        }
    }

    private static JObject Encode(string key, Entry entry)
    {
        var values = ValuesOf(key, entry);
        var problems = new List<string>();
        var json = FbBookCodec.Encode(_schema!.Persisted.Select(i => (_schema.Fields[i].Name, _schema.Fields[i].FieldType, values[i])),
            FbGameRefs.Instance, problems);
        if (problems.Count > 0 && Warned.Add(key + "|encode")) Report(key, "writing the book", problems);
        return json;
    }

    // ---- keeping players' games in step ------------------------------------------------------------------------

    /// <summary>Server, game thread: the shared action channel's handler for feature "fourberie".</summary>
    internal static TaomActionOutcome Handle(Hero hero, MobileParty? party, string op, IList<string> args)
    {
        if (!EnsureSchema()) return TaomActionOutcome.Fail("");
        var key = hero.StringId;
        var entry = EntryFor(key);
        switch (op)
        {
            case "full":
                return new TaomActionOutcome(true, "", new[] { entry.Ledger.Full(Encode(key, entry)) });
            case "report":
            {
                if (args.Count != 1 || args[0].Length > MaxPayload
                    || !LeMirrorDelta.TryUnpack(args[0], out var kind, out _, out _, out var reported) || kind != LeMirrorDelta.KindDelta)
                    return TaomActionOutcome.Fail("");
                if (Current == key) return TaomActionOutcome.Fail("");   // never happens on the game thread; never overwrite a running book
                var merged = entry.Ledger.Merge(Encode(key, entry), reported);
                var problems = new List<string>();
                var values = ValuesOf(key, entry);
                var decoded = FbBookCodec.Decode(merged, _shape!, FbGameRefs.Instance, problems, i => values[_schema!.Persisted[i]]);
                for (var i = 0; i < decoded.Length; i++) values[_schema!.Persisted[i]] = decoded[i];
                if (problems.Count > 0 && Warned.Add(key + "|report")) Report(key, "a reported change", problems);
                _reports++;
                return new TaomActionOutcome(true, "");
            }
            default:
                return TaomActionOutcome.Fail("");
        }
    }

    /// <summary>Server, every frame: sends each connected player the rows the server changed in their book.</summary>
    internal static void ServerTick()
    {
        if (!FourberieLayer.IsServer || _schema == null || TaomActions.Push == null || DateTime.UtcNow < _nextSend) return;
        _nextSend = DateTime.UtcNow.AddSeconds(SendEverySeconds);
        try
        {
            foreach (var (hero, _) in Connected())
            {
                if (!Entries.TryGetValue(hero.StringId, out var entry) || !entry.Dirty) continue;
                entry.Dirty = false;
                var payload = entry.Ledger.DeltaIfChanged(Encode(hero.StringId, entry));
                if (payload == null) continue;
                TaomActions.Push(hero, FourberieLayer.Feature, new[] { payload });
                _pushed++;
            }
        }
        catch (Exception ex)
        {
            Log.Warn(FourberieLayer.Tag + "book send failed: " + ex.GetBaseException().Message);
        }
    }

    internal static string Summary() => $"books {Entries.Count}, runs as a player {_scoped}, sent {_pushed}, reports {_reports}";
}

/// <summary>fourb-book: the books, saved with the campaign, and the server's half of keeping players' games in step.</summary>
internal sealed class FbBooksComponent : IFbComponent
{
    private const string Owner = "ModderLords.Fourberie.Books";
    private MethodInfo? _syncData;

    public string Id => "books";

    public string? SkipReason(FbContext context)
    {
        if (!context.IsServer) return "client (its own statics are its book; see mirror)";
        if (context.Fields == null) return "Fourberie changed: " + context.FieldsProblem;
        if (FbGameRefs.Problem is { } p) return p;
        _syncData = context.Method(FbFields.Behavior, "SyncData", 1);
        return _syncData == null ? "FourberieBehavior.SyncData not found" : null;
    }

    public string Install(FbContext context)
    {
        FbBooks.Bind(context.Fields!);
        new Harmony(Owner).Patch(_syncData, postfix: new HarmonyMethod(typeof(FbBooksComponent), nameof(Postfix)));
        TaomActions.Register(FourberieLayer.Feature, FbBooks.Handle);
        return $"{FbFields.Persisted.Length} saved and {context.Fields!.Count - FbFields.Persisted.Length} scratch field(s) per player, saved under '{FbBooks.SaveKey}'";
    }

    private static void Postfix(IDataStore dataStore) => FbBooks.SyncDataPostfix(dataStore);
}
