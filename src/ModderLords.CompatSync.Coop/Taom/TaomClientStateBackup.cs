using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ModderLords.CompatSync;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;

namespace ModderLords.CompatSync.Coop.Taom;

/// <summary>
/// TAOM state a co-op client owns, kept by the server so it survives a reconnect (TAOM-MAP checklist).
///
/// - Equipment presets: TAOM keeps them per hero and saves them with the campaign, but a client's presets only ever
///   lived in its local copy of the campaign, which is thrown away on disconnect. The client reports its own hero's
///   presets when they change; the server puts them in TAOM's own per-hero preset store, so they are saved with the
///   server's campaign and arrive in the world a reconnecting player joins.
/// - Field Commission merit (client-owned since #129): TAOM's merit bank is one per campaign, and the server's own
///   bank is idle (the server never fights a battle of its own). The client reports its bank; the server keeps each
///   player's rows in its bank under "heroId|troopId" keys (saved by TAOM with the campaign) and hands them back when
///   the player joins again. Companions a player promoted are kept as-is and returned by clan.
/// </summary>
internal sealed class ClientStateBackupComponent : ITaomComponent
{
    public const string PresetsFeature = "presets";
    public const string MeritFeature = "fcstate";

    private static Assembly? _taom;
    private static Type? _presetType;
    private static MethodInfo? _exportMerits, _importMerits, _exportDeclined, _importDeclined, _exportPromoted, _importPromoted;
    private static PropertyInfo? _presetState;

    public string Id => "client-state-backup";

    public string? SkipReason(TaomContext context)
    {
        var t = _taom = context.Taom;
        _presetType = t.GetType("TAOM.Features.EquipPresets.Models.HoNEquipmentPreset", false);
        _presetState = t.GetType("TAOM.Features.EquipPresets.IEquipmentPresetService", false)?.GetProperty("SerializableState");
        var merit = t.GetType("TAOM.Features.FieldCommission.IFieldCommissionMeritService", false);
        _exportMerits = merit?.GetMethod("ExportMerits", Type.EmptyTypes);
        _importMerits = merit?.GetMethod("ImportMerits", new[] { typeof(Dictionary<string, int>) });
        _exportDeclined = merit?.GetMethod("ExportDeclinedMarks", Type.EmptyTypes);
        _importDeclined = merit?.GetMethod("ImportDeclinedMarks", new[] { typeof(Dictionary<string, int>) });
        _exportPromoted = merit?.GetMethod("ExportPromotedHeroIds", Type.EmptyTypes);
        _importPromoted = merit?.GetMethod("ImportPromotedHeroIds", new[] { typeof(List<string>) });

        var missing = new List<string>();
        if (_presetType == null || _presetState == null || !typeof(IDictionary).IsAssignableFrom(_presetState.PropertyType)) missing.Add("IEquipmentPresetService.SerializableState");
        if (_exportMerits == null || _importMerits == null || _exportDeclined == null || _importDeclined == null || _exportPromoted == null || _importPromoted == null)
            missing.Add("IFieldCommissionMeritService Export/Import");
        return missing.Count == 0 ? null : "TAOM changed; not found: " + string.Join(", ", missing);
    }

    public string Install(TaomContext context)
    {
        if (context.IsServer)
        {
            TaomActions.Register(PresetsFeature, ServerPresets);
            TaomActions.Register(MeritFeature, ServerMerit);
            return "server keeps players' equipment presets and Field Commission merit with the campaign";
        }
        TaomActions.RegisterClientApply(MeritFeature, ClientRestoreMerit);
        return "client backs up its equipment presets and Field Commission merit on the server";
    }

    private static object? Resolve(string name) => _taom == null ? null : TaomActions.Resolve(_taom, name);
    private static object? Merit() => Resolve("TAOM.Features.FieldCommission.IFieldCommissionMeritService");
    private static IDictionary? PresetState() =>
        Resolve("TAOM.Features.EquipPresets.IEquipmentPresetService") is { } s ? _presetState!.GetValue(s) as IDictionary : null;

    // ---- client ----------------------------------------------------------------------------------------

    private static DateTime _next = DateTime.MinValue;
    private static string _lastPresets = "", _lastMerit = "";
    private static bool _fetched, _restored;
    private static Campaign? _campaign;

    /// <summary>Client, from Bridge.Tick every 5 s: asks for its merit once per session, then reports changes.</summary>
    internal static void ClientTick()
    {
        if (!TaomActions.IsCoopClient || _taom == null || DateTime.UtcNow < _next) return;
        _next = DateTime.UtcNow.AddSeconds(5);
        var hero = Hero.MainHero;
        if (hero == null || Campaign.Current == null) return;
        if (Campaign.Current != _campaign)
        {
            _campaign = Campaign.Current;
            _fetched = _restored = false;
            _lastPresets = _lastMerit = "";
        }
        try
        {
            if (!_fetched)
            {
                _fetched = true;
                TaomActions.Send!(MeritFeature, "fetch", Array.Empty<string>());
                return;   // report only after the server's copy was restored, so a fresh session never overwrites it
            }
            if (PresetState() is { } presets && presets.Contains(hero.StringId))
            {
                var json = JsonConvert.SerializeObject(presets[hero.StringId]);
                if (json != _lastPresets)
                {
                    _lastPresets = json;
                    TaomActions.Send!(PresetsFeature, "report", new[] { json });
                }
            }
            if (_restored && Merit() is { } merit)
            {
                var json = new JObject
                {
                    ["merits"] = JObject.FromObject(_exportMerits!.Invoke(merit, null)!),
                    ["declined"] = JObject.FromObject(_exportDeclined!.Invoke(merit, null)!),
                    ["promoted"] = JArray.FromObject(_exportPromoted!.Invoke(merit, null)!),
                }.ToString(Formatting.None);
                if (json != _lastMerit)
                {
                    _lastMerit = json;
                    TaomActions.Send!(MeritFeature, "report", new[] { json });
                }
            }
        }
        catch (Exception ex) { Log.Warn("TAOM layer: client-state backup failed: " + ex.GetBaseException().Message); }
    }

    /// <summary>Client: the server's copy of this player's merit bank (or "none").</summary>
    private static void ClientRestoreMerit(IList<string> data)
    {
        _restored = true;
        if (data.Count < 1 || Merit() is not { } merit) return;
        // Always replace: the world a client joins carries the server's bank, i.e. every player's "heroId|troopId"
        // rows, which must not stay in this client's bank as if they were its own.
        var o = data[0] == "none" ? new JObject() : JObject.Parse(data[0]);
        _importMerits!.Invoke(merit, new object[] { o["merits"]?.ToObject<Dictionary<string, int>>() ?? new Dictionary<string, int>() });
        _importDeclined!.Invoke(merit, new object[] { o["declined"]?.ToObject<Dictionary<string, int>>() ?? new Dictionary<string, int>() });
        _importPromoted!.Invoke(merit, new object[] { o["promoted"]?.ToObject<List<string>>() ?? new List<string>() });
        Log.Info("TAOM layer: Field Commission merit restored from the server");
    }

    // ---- server ----------------------------------------------------------------------------------------

    private static TaomActionOutcome ServerPresets(Hero hero, MobileParty? party, string op, IList<string> args)
    {
        if (op != "report" || args.Count != 1 || PresetState() is not { } state) return TaomActionOutcome.Fail("");
        var listType = typeof(List<>).MakeGenericType(_presetType!);
        var list = JsonConvert.DeserializeObject(args[0], listType);
        if (list == null) return TaomActionOutcome.Fail("");
        state[hero.StringId] = list;
        return new TaomActionOutcome(true, "");
    }

    internal static string Prefix(Hero hero) => hero.StringId + "|";

    private static TaomActionOutcome ServerMerit(Hero hero, MobileParty? party, string op, IList<string> args)
    {
        if (Merit() is not { } merit) return TaomActionOutcome.Fail("");
        var prefix = Prefix(hero);
        var merits = (Dictionary<string, int>)_exportMerits!.Invoke(merit, null)!;
        var declined = (Dictionary<string, int>)_exportDeclined!.Invoke(merit, null)!;
        var promoted = (List<string>)_exportPromoted!.Invoke(merit, null)!;

        if (op == "fetch")
        {
            var own = Strip(merits, prefix);
            var ownDeclined = Strip(declined, prefix);
            var ownPromoted = promoted.Where(id => Campaign.Current.CampaignObjectManager.Find<Hero>(id)?.Clan is { } c && c == hero.Clan).ToList();
            var none = own.Count == 0 && ownDeclined.Count == 0 && ownPromoted.Count == 0;
            var json = new JObject
            {
                ["merits"] = JObject.FromObject(own), ["declined"] = JObject.FromObject(ownDeclined), ["promoted"] = JArray.FromObject(ownPromoted),
            }.ToString(Formatting.None);
            return new TaomActionOutcome(true, "", new List<string> { none ? "none" : json });
        }
        if (op != "report" || args.Count != 1) return TaomActionOutcome.Fail("");

        var o = JObject.Parse(args[0]);
        Replace(merits, prefix, o["merits"]?.ToObject<Dictionary<string, int>>());
        Replace(declined, prefix, o["declined"]?.ToObject<Dictionary<string, int>>());
        foreach (var id in o["promoted"]?.ToObject<List<string>>() ?? new List<string>())
            if (!promoted.Contains(id) && Campaign.Current.CampaignObjectManager.Find<Hero>(id)?.Clan == hero.Clan)
                promoted.Add(id);
        _importMerits!.Invoke(merit, new object[] { merits });
        _importDeclined!.Invoke(merit, new object[] { declined });
        _importPromoted!.Invoke(merit, new object[] { promoted });
        return new TaomActionOutcome(true, "");
    }

    private static Dictionary<string, int> Strip(Dictionary<string, int> all, string prefix) =>
        all.Where(p => p.Key.StartsWith(prefix, StringComparison.Ordinal))
           .ToDictionary(p => p.Key.Substring(prefix.Length), p => p.Value, StringComparer.Ordinal);

    private static void Replace(Dictionary<string, int> all, string prefix, Dictionary<string, int>? mine)
    {
        foreach (var key in all.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList()) all.Remove(key);
        if (mine == null) return;
        foreach (var p in mine)
            if (!string.IsNullOrEmpty(p.Key) && p.Key.IndexOf('|') < 0) all[prefix + p.Key] = p.Value;
    }
}
