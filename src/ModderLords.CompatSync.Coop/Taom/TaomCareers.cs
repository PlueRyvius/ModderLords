using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using ModderLords.CompatSync;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;

namespace ModderLords.CompatSync.Coop.Taom;

/// <summary>
/// TAOM careers in co-op: each player's client owns their career record (TAOM-MAP P3), as with special resources.
///
/// A player picks a career and its perks on their own machine (TAOM's career screen, character creation, quest tier
/// unlocks), and TAOM stores the result per hero in ICareerDataService. The server never saw any of it: its career data
/// held no record for any player, so TAOM's career perks, which apply through game models the SERVER computes (wages,
/// stats, party size...) and are looked up per hero in CareerPassiveService, were missing for every player.
///
/// The client reports its own hero's record (career, chosen perks, unlocked tiers, flags) whenever it changes; the
/// server stores it, rebuilds TAOM's perk cache, and saves it with the campaign, so a reconnect restores it. The server
/// accepts only the sender's own hero and only careers and perks that exist in TAOM's registry.
/// </summary>
internal sealed class CareerSyncComponent : ITaomComponent
{
    public const string Feature = "career";

    private static Assembly? _taom;
    private static MethodInfo? _getAll;
    private static MethodInfo? _getOrCreate;
    private static MethodInfo? _refresh;
    private static MethodInfo? _getCareer;
    private static MethodInfo? _getChoice;
    private static string _lastReported = "";
    private static DateTime _nextReport = DateTime.MinValue;

    public string Id => "career-sync";

    public string? SkipReason(TaomContext context)
    {
        var t = _taom = context.Taom;
        var data = t.GetType("TAOM.Features.CareerSystem.ICareerDataService", false);
        var registry = t.GetType("TAOM.Features.CareerSystem.ICareerRegistry", false);
        var passives = t.GetType("TAOM.Features.CareerSystem.ICareerPassiveService", false);
        _getAll = data?.GetMethod("GetAllData", Type.EmptyTypes);
        _getOrCreate = data?.GetMethod("GetOrCreateData", new[] { typeof(string) });
        _refresh = data == null || registry == null ? null : passives?.GetMethod("RefreshCache", new[] { data, registry });
        _getCareer = registry?.GetMethod("GetCareer", new[] { typeof(string) });
        _getChoice = registry?.GetMethod("GetChoice", new[] { typeof(string) });

        var missing = new List<string>();
        if (_getAll == null) missing.Add("ICareerDataService.GetAllData");
        if (_getOrCreate == null) missing.Add("ICareerDataService.GetOrCreateData");
        if (_refresh == null) missing.Add("ICareerPassiveService.RefreshCache");
        if (_getCareer == null || _getChoice == null) missing.Add("ICareerRegistry.GetCareer/GetChoice");
        return missing.Count == 0 ? null : "TAOM changed; not found: " + string.Join(", ", missing);
    }

    public string Install(TaomContext context)
    {
        if (context.IsServer)
        {
            TaomActions.Register(Feature, ServerReport);
            return "server stores each player's career and applies their perks";
        }
        return "client reports its career to the server";
    }

    private static object? Resolve(string name) => _taom == null ? null : TaomActions.Resolve(_taom, name);

    /// <summary>[career, perks csv, tiers csv, flags csv] for one hero, or null when the hero has no career record.</summary>
    private static List<string>? RecordFor(string heroId)
    {
        if (Resolve("TAOM.Features.CareerSystem.ICareerDataService") is not { } data) return null;
        if (_getAll!.Invoke(data, null) is not System.Collections.IDictionary all || !all.Contains(heroId)) return null;
        var record = all[heroId];
        if (record == null) return null;
        object? P(string name) => record.GetType().GetProperty(name)?.GetValue(record);
        var career = P("CareerStringId") as string ?? "";
        var perks = P("ChoiceIds") as IEnumerable<string> ?? Array.Empty<string>();
        var tiers = P("TierUnlocks") as IEnumerable<int> ?? Array.Empty<int>();
        var flags = P("Flags") as IEnumerable<string> ?? Array.Empty<string>();
        return new List<string>
        {
            career,
            string.Join(",", perks),
            string.Join(",", tiers.Select(t => t.ToString(CultureInfo.InvariantCulture))),
            string.Join(",", flags),
        };
    }

    /// <summary>Client, from Bridge.Tick every 3 s: reports this player's career record when it changed.</summary>
    internal static void ClientTick()
    {
        if (!TaomActions.IsCoopClient || _getAll == null || DateTime.UtcNow < _nextReport) return;
        _nextReport = DateTime.UtcNow.AddSeconds(3);
        var hero = Hero.MainHero;
        if (hero == null || Campaign.Current == null) return;
        var record = RecordFor(hero.StringId);
        if (record == null) return;
        var canonical = string.Join("|", record);
        if (canonical == _lastReported) return;
        _lastReported = canonical;
        TaomActions.Send!(Feature, "report", record);
        Log.Info($"TAOM layer: career sent to the server (career={record[0]}, perks={record[1]})");
    }

    private static List<string> Split(string csv) =>
        csv.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).Where(s => s.Length > 0).ToList();

    /// <summary>Server, inside PlayerScope: stores the sender's own career record and rebuilds TAOM's perk cache.</summary>
    private static TaomActionOutcome ServerReport(Hero hero, MobileParty? party, string op, IList<string> args)
    {
        if (op != "report" || args.Count != 4) return TaomActionOutcome.Fail("");
        var data = Resolve("TAOM.Features.CareerSystem.ICareerDataService");
        var registry = Resolve("TAOM.Features.CareerSystem.ICareerRegistry");
        var passives = Resolve("TAOM.Features.CareerSystem.ICareerPassiveService");
        if (data == null || registry == null || passives == null) return TaomActionOutcome.Fail("");

        var career = args[0];
        if (career.Length > 0 && _getCareer!.Invoke(registry, new object[] { career }) == null)
        {
            Log.Warn($"TAOM layer: career report from {hero.Name} names an unknown career '{career}'; ignored");
            return TaomActionOutcome.Fail("");
        }
        var perks = Split(args[1]);
        var unknown = perks.Where(p => _getChoice!.Invoke(registry, new object[] { p }) == null).ToList();
        if (unknown.Count > 0) Log.Warn($"TAOM layer: career report from {hero.Name} dropped unknown perk(s): {string.Join(", ", unknown)}");
        var tiers = Split(args[2]).Select(s => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : -1).Where(n => n > 0).Distinct().ToList();

        var record = _getOrCreate!.Invoke(data, new object[] { hero.StringId })!;
        void Set(string name, object value) => record.GetType().GetProperty(name)!.SetValue(record, value);
        Set("CareerStringId", career.Length > 0 ? career : null!);
        Set("ChoiceIds", perks.Except(unknown).ToList());
        Set("TierUnlocks", tiers);
        Set("Flags", Split(args[3]));
        _refresh!.Invoke(passives, new[] { data, registry });
        Log.Info($"TAOM layer: career stored for {hero.Name} (career={career}, {perks.Count - unknown.Count} perk(s), tiers={args[2]})");
        return new TaomActionOutcome(true, "");
    }
}
