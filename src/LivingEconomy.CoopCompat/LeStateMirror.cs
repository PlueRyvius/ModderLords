using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using ModderLords.CompatSync;
using ModderLords.CompatSync.Coop.Taom;
using Newtonsoft.Json.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Library;
using TaleWorlds.Localization;

namespace ModderLords.CompatSync.Coop.LivingEconomy;

/// <summary>
/// Living Economy's records follow the server to every player (docs/LIVING-ECONOMY-LAYER.md, part 4).
///
/// The mod keeps its world in its behaviours' SyncData: population by class, supply links, town and castle economies,
/// village investment and development, cultural markets, estates, treaties, events and caravan standings. A player
/// gets them once, in the save they join with; after that only the server's copy moves (its players' games no longer
/// simulate). The ledger, the settlement menus, the "can I build this?" checks and the price model all read these
/// records, so without this every player would see the economy frozen at the moment they joined, and pay prices
/// worked out from it.
///
/// This works like the TAOM layer's mirror (the mod's own SyncData, run in saving mode on the server and in loading
/// mode on players' games, through <see cref="MirrorStore"/>) with two differences for the size of these books: only
/// changed rows are sent (<see cref="LeMirrorDelta"/>), compressed, and the server sends when the campaign day turns,
/// when a player's action changed something, and otherwise at most every half minute. The same NetworkTaomState
/// message carries it, told apart by the behaviour name.
///
/// Also carried: the caravan behaviour's in-memory caravan table (escorts, reputation), which the party-size and
/// speed models and the ledger's caravan page read but the mod never saves; and a fingerprint of the mod's data files,
/// so a player whose regional or consumption profiles differ from the host's is told.
/// </summary>
internal static class LeStateMirror
{
    internal const string Prefix = "BetterEconomy.";
    internal const string FilesKey = "BetterEconomy.$files";
    private const string B = "BetterEconomy.Behaviors.";

    /// <summary>Behaviour type -> in-memory fields carried beside its SyncData (dictionaries of mod records).</summary>
    private static readonly (string Type, string[] ExtraFields)[] Mirrored =
    {
        (B + "EconomySaveBehavior", Array.Empty<string>()),
        (B + "TownEconomyCampaignBehavior", Array.Empty<string>()),
        (B + "CastleEconomyCampaignBehavior", Array.Empty<string>()),
        (B + "VillageInvestmentCampaignBehavior", Array.Empty<string>()),
        (B + "VillageDevelopmentCampaignBehavior", Array.Empty<string>()),
        (B + "CulturalMarketCampaignBehavior", Array.Empty<string>()),
        (B + "FeudalEconomyCampaignBehavior", Array.Empty<string>()),
        (B + "TradeAgreementCampaignBehavior", Array.Empty<string>()),
        (B + "EconomicEventCampaignBehavior", Array.Empty<string>()),
        (B + "CaravanCampaignBehavior", new[] { "_caravans" }),
    };

    private const string ExtraPrefix = "$field:";
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan IdleInterval = TimeSpan.FromSeconds(30);

    private static readonly Dictionary<string, Type> Types = new Dictionary<string, Type>(StringComparer.Ordinal);
    private static readonly Dictionary<string, string[]> Extras = new Dictionary<string, string[]>(StringComparer.Ordinal);
    private static readonly HashSet<string> Disabled = new HashSet<string>(StringComparer.Ordinal);

    // Server: what each client was last sent, and its sequence number.
    private static readonly Dictionary<string, (JObject Values, int Sequence)> Sent = new Dictionary<string, (JObject, int)>(StringComparer.Ordinal);
    // Client: the values it holds, and the sequence they are at.
    private static readonly Dictionary<string, (JObject Values, int Sequence)> Held = new Dictionary<string, (JObject, int)>(StringComparer.Ordinal);

    private static int _dirty;
    private static DateTime _nextCheck = DateTime.MinValue;
    private static DateTime _lastSend = DateTime.MinValue;
    private static int _lastDay = int.MinValue;
    private static DateTime _lastFullRequest = DateTime.MinValue;
    private static string _fingerprint = "";
    private static bool _fingerprintWarned;
    private static long _bytesSent;

    /// <summary>Server: set by the server handler; sends (behaviour, payload) to every client.</summary>
    internal static Action<string, string>? Broadcast { get; set; }

    /// <summary>Client: set by the client handler; asks the server for every mirrored behaviour in full.</summary>
    internal static Action? RequestFull { get; set; }

    internal static bool Bound => Types.Count > 0;

    internal static IEnumerable<string> Names => Types.Keys;

    internal static int Bind(Assembly mod)
    {
        Types.Clear();
        Extras.Clear();
        foreach (var (name, extras) in Mirrored)
            if (mod.GetType(name, false) is { } t && typeof(CampaignBehaviorBase).IsAssignableFrom(t))
            {
                Types[name] = t;
                Extras[name] = extras.Where(f => t.GetField(f, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public) != null).ToArray();
            }
        _fingerprint = Fingerprint(mod);
        return Types.Count;
    }

    /// <summary>Server: a player's action changed the records; send at the next check instead of waiting.</summary>
    internal static void MarkDirty() => Interlocked.Exchange(ref _dirty, 1);

    /// <summary>A new session starts from nothing sent or held.</summary>
    internal static void Reset()
    {
        Sent.Clear();
        Held.Clear();
        _nextCheck = DateTime.MinValue;
        _lastSend = DateTime.MinValue;
        _lastDay = int.MinValue;
    }

    // ---- server ---------------------------------------------------------------------------------------------------

    /// <summary>Server, from Bridge.Tick: sends what changed, when the day turned, an action happened, or it has been a while.</summary>
    internal static void ServerTick()
    {
        if (!LivingEconomyLayer.Active || !LivingEconomyLayer.IsServer || Broadcast == null || Campaign.Current == null || Types.Count == 0) return;
        var now = DateTime.UtcNow;
        if (now < _nextCheck) return;
        _nextCheck = now + CheckInterval;
        var day = (int)CampaignTime.Now.ToDays;
        var dirty = Interlocked.Exchange(ref _dirty, 0) == 1;
        if (!dirty && day == _lastDay && now - _lastSend < IdleInterval) return;
        _lastDay = day;
        _lastSend = now;
        foreach (var name in Types.Keys)
        {
            if (Capture(name) is not { } current) continue;
            string payload;
            if (!Sent.TryGetValue(name, out var last))
            {
                Sent[name] = (current, 1);
                payload = LeMirrorDelta.Pack(LeMirrorDelta.KindFull, 0, 1, current);
            }
            else
            {
                if (LeMirrorDelta.Diff(last.Values, current) is not { } changes) continue;
                var next = last.Sequence + 1;
                Sent[name] = (current, next);
                payload = LeMirrorDelta.Pack(LeMirrorDelta.KindDelta, last.Sequence, next, changes);
            }
            _bytesSent += payload.Length;
            Broadcast(name, payload);
        }
    }

    /// <summary>
    /// Server, game thread: every mirrored behaviour in full for one joining (or resyncing) player, at the sequence the
    /// other players hold, so the deltas that follow apply to it too. Also the data-file fingerprint.
    /// </summary>
    internal static List<(string Behaviour, string Payload)> CaptureForJoin()
    {
        var result = new List<(string, string)>();
        if (!LivingEconomyLayer.Active || !LivingEconomyLayer.IsServer || Campaign.Current == null) return result;
        foreach (var name in Types.Keys)
        {
            if (!Sent.TryGetValue(name, out var last))
            {
                if (Capture(name) is not { } current) continue;
                last = (current, 1);
                Sent[name] = last;
            }
            result.Add((name, LeMirrorDelta.Pack(LeMirrorDelta.KindFull, 0, last.Sequence, last.Values)));
        }
        result.Add((FilesKey, _fingerprint));
        return result;
    }

    private static JObject? Capture(string name)
    {
        if (Disabled.Contains(name) || LivingEconomyLayer.Behavior(Types[name]) is not { } behavior) return null;
        try
        {
            var store = MirrorStore.ForSaving();
            behavior.SyncData(store);
            var values = JObject.Parse(store.Json);
            foreach (var field in Extras[name])
            {
                var f = behavior.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!;
                var value = WithLock(behavior, () => JToken.FromObject(f.GetValue(behavior) ?? new object(), TaomRecords.Serializer));
                values[ExtraPrefix + field] = value;
            }
            return values;
        }
        catch (Exception ex)
        {
            Disabled.Add(name);
            Log.Warn(LivingEconomyLayer.Tag + $"state mirror for {LivingEconomyLayer.Short(name)} is off: {ex.GetBaseException().Message}");
            return null;
        }
    }

    // ---- client ---------------------------------------------------------------------------------------------------

    /// <summary>Client, game thread: one mirror message from the server.</summary>
    internal static void ClientApply(string name, string payload)
    {
        if (name == FilesKey) { CompareFingerprint(payload); return; }
        if (!Types.TryGetValue(name, out var type))
        {
            Log.Warn(LivingEconomyLayer.Tag + $"state mirror got {LivingEconomyLayer.Short(name)}, which this client does not mirror; update ModderLords");
            return;
        }
        if (!LeMirrorDelta.TryUnpack(payload, out var kind, out var baseSequence, out var sequence, out var values))
        {
            Log.Warn(LivingEconomyLayer.Tag + $"state mirror could not read {LivingEconomyLayer.Short(name)} from the server");
            return;
        }
        JObject merged;
        if (kind == LeMirrorDelta.KindFull)
        {
            merged = values;
        }
        else
        {
            if (!Held.TryGetValue(name, out var held) || held.Sequence != baseSequence)
            {
                AskForFull($"{LivingEconomyLayer.Short(name)} delta {baseSequence}->{sequence} does not follow what this game holds ({(Held.TryGetValue(name, out var h) ? h.Sequence.ToString() : "nothing")})");
                return;
            }
            merged = held.Values;
            LeMirrorDelta.Apply(merged, values);
        }
        Held[name] = (merged, sequence);
        Load(name, type, merged);
    }

    private static void Load(string name, Type type, JObject values)
    {
        if (LivingEconomyLayer.Behavior(type) is not { } behavior) return;
        try
        {
            var saved = new JObject(values.Properties().Where(p => !p.Name.StartsWith(ExtraPrefix, StringComparison.Ordinal)));
            behavior.SyncData(MirrorStore.ForLoading(saved.ToString(Newtonsoft.Json.Formatting.None)));
            foreach (var field in Extras[name])
                if (values[ExtraPrefix + field] is JObject rows) ReplaceRows(behavior, field, rows);
            AfterLoad(behavior);
        }
        catch (Exception ex) { Log.Warn(LivingEconomyLayer.Tag + $"state mirror could not apply {LivingEconomyLayer.Short(name)}: {ex.GetBaseException().Message}"); }
    }

    /// <summary>Refills an in-memory dictionary in place (the field is readonly), under the behaviour's own lock.</summary>
    private static void ReplaceRows(object behavior, string field, JObject rows)
    {
        var f = behavior.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        if (f?.GetValue(behavior) is not IDictionary dict || !f.FieldType.IsGenericType) return;
        var valueType = f.FieldType.GetGenericArguments()[1];
        WithLock(behavior, () =>
        {
            dict.Clear();
            foreach (var row in rows.Properties())
                dict[row.Name] = row.Value.ToObject(valueType, TaomRecords.Serializer);
            return 0;
        });
    }

    /// <summary>Caches the mod keeps per day would otherwise hold pre-load numbers until tomorrow.</summary>
    private static void AfterLoad(CampaignBehaviorBase behavior)
    {
        if (behavior.GetType().GetField("_cache", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(behavior) is IDictionary own) own.Clear();
        if (behavior.GetType().Name != "EconomySaveBehavior") return;
        var prices = Campaign.Current?.Models?.TradeItemPriceFactorModel;
        if (prices?.GetType().GetField("_cache", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(prices) is IDictionary priceCache) priceCache.Clear();
    }

    private static T WithLock<T>(object behavior, Func<T> body)
    {
        var gate = behavior.GetType().GetField("_stateLock", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(behavior);
        if (gate == null) return body();
        lock (gate) return body();
    }

    private static void AskForFull(string why)
    {
        if (DateTime.UtcNow - _lastFullRequest < TimeSpan.FromSeconds(10) || RequestFull == null) return;
        _lastFullRequest = DateTime.UtcNow;
        Log.Info(LivingEconomyLayer.Tag + "state mirror asks the server for everything again: " + why);
        RequestFull();
    }

    // ---- data files ----------------------------------------------------------------------------------------------

    /// <summary>"file=hash;..." over the mod's data files that settings sync does not carry.</summary>
    private static string Fingerprint(Assembly mod)
    {
        try
        {
            var bin = Path.GetDirectoryName(mod.Location) ?? "";
            var data = Path.GetFullPath(Path.Combine(bin, "..", "..", "ModuleData"));
            var parts = new List<string>();
            foreach (var file in new[] { "better_economy_regional_profiles.xml", "better_economy_class_consumption_profiles.xml" })
            {
                var path = Path.Combine(data, file);
                if (!File.Exists(path)) { parts.Add(file + "=missing"); continue; }
                using var sha = SHA1.Create();
                parts.Add(file + "=" + BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(path))).Replace("-", "").Substring(0, 12));
            }
            return string.Join(";", parts);
        }
        catch (Exception ex) { return "unreadable:" + ex.GetType().Name; }
    }

    private static void CompareFingerprint(string server)
    {
        if (_fingerprintWarned || string.IsNullOrEmpty(server) || server == _fingerprint) return;
        _fingerprintWarned = true;
        Log.Warn(LivingEconomyLayer.Tag + $"this game's Living Economy data files differ from the host's (host: {server}; here: {_fingerprint})");
        try
        {
            InformationManager.DisplayMessage(new InformationMessage(new TextObject(
                "{=ml_le_files_differ}[LivingEconomy] Your regional or consumption profiles differ from the host's. Prices on your screen may not match the server's; reinstall Living Economy to match the host.").ToString(), Colors.Yellow));
        }
        catch { }
    }

    internal static string Summary() =>
        LivingEconomyLayer.IsServer
            ? $"state mirror: {Sent.Count} book(s) sent, {_bytesSent / 1024} KB so far" + (Disabled.Count > 0 ? $", {Disabled.Count} off" : "")
            : $"state mirror: {Held.Count} book(s) held" + (Disabled.Count > 0 ? $", {Disabled.Count} off" : "");
}

internal sealed class LeStateMirrorComponent : ILeComponent
{
    public string Id => "state-mirror";

    public string? SkipReason(LeContext context) =>
        LeStateMirror.Bind(context.Mod) == 0 ? "none of Living Economy's saved behaviours were found" : null;

    public string Install(LeContext context) =>
        (context.IsServer ? "server sends " : "client receives ") + LivingEconomyLayer.Some(LeStateMirror.Names.Select(LivingEconomyLayer.Short), 12);
}
