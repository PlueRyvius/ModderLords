using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using ModderLords.CompatSync;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.ModuleManager;
using TaleWorlds.SaveSystem;

namespace ModderLords.CompatSync.Coop.Fourberie;

/// <summary>
/// A live check of the Fourberie layer on a real dedicated server, with no game client (docs/FOURBERIE-LAYER-PLAN.md,
/// "Verification"). Off unless MODDERLORDS_FOURBERIE_SELFTEST is set; never part of a normal session.
///
/// "run": two AI lords stand in as players (FbBooks.TestPlayers). It gives one a crime base and their gang ledger,
/// runs every Fourberie tick handler through the per-player path, relays the insurance scam's caravan and raiders,
/// raises a yes/no and a pick-from-a-list prompt (an offline player gets the defaults, never the headless "yes"), then
/// saves the campaign as "&lt;save&gt;_fbcheck".
/// "check": loading that save, the books, the base, the ledgers and the scam's parties must all be back.
///
/// Every step logs "Fourberie self-test: PASS|FAIL ..."; the last line counts them. With ..._EXIT=1 the server exits
/// when it is done (exit code 0 when everything passed).
/// </summary>
internal static class FbSelfTest
{
    private const string Env = "MODDERLORDS_FOURBERIE_SELFTEST";
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(10);
    private static DateTime? _campaignSeen;
    private static bool _done, _saving;
    private static int _pass, _fail;
    private static Assembly? _mod;

    internal static void Bind(Assembly mod) => _mod = mod;

    private static string? Mode => Environment.GetEnvironmentVariable(Env);

    internal static void Tick()
    {
        if (_done || !FourberieLayer.IsServer || Campaign.Current == null || Mode is not ("run" or "check")) return;
        _campaignSeen ??= DateTime.UtcNow;
        if (DateTime.UtcNow - _campaignSeen < Settle) return;
        _done = true;
        try
        {
            var heroes = PickHeroes();
            if (heroes.Count < 2) { Fail("need two lords with their own parties to stand in as players"); Finish(); return; }
            Log.Info($"{FourberieLayer.Tag}self-test: {Mode} with {heroes[0].StringId} and {heroes[1].StringId}");
            FbBooks.TestPlayers.AddRange(heroes);
            if (Mode == "run") Run(heroes[0], heroes[1]);
            else Check(heroes[0], heroes[1]);
        }
        catch (Exception ex) { Fail("threw: " + ex.GetBaseException()); }
        if (!_saving) Finish();
    }

    /// <summary>The same two lords every time for the same world: the first two by id that lead their own party, from different clans.</summary>
    private static List<Hero> PickHeroes() =>
        Hero.AllAliveHeroes.Where(h => h.IsLord && h.Clan != null && !h.IsFactionLeader && h.PartyBelongedTo is { } p && p.LeaderHero == h)
            .OrderBy(h => h.StringId, StringComparer.Ordinal).GroupBy(h => h.Clan).Select(g => g.First()).Take(2).ToList();

    // ---- run ---------------------------------------------------------------------------------------------------

    private static void Run(Hero a, Hero b)
    {
        foreach (var hero in new[] { a, b })
        {
            var outcome = FbBooks.Handle(hero, hero.PartyBelongedTo, "full", Array.Empty<string>());
            Expect(outcome.Ok && outcome.Data is { Count: 1 }, $"{hero.StringId}: the full book is produced");
            foreach (var (role, _) in FbLedgerRoles.Roles)
            {
                var ledger = FbLedgers.Of(hero.StringId, role);
                Expect(ledger is { IsActive: true }, $"{hero.StringId}: ledger {role} made on the server ({ledger?.StringId})");
                Expect(ledger?.ActualClan == hero.Clan, $"{hero.StringId}: ledger {role} belongs to their clan (clan {ledger?.ActualClan?.StringId}, theirs {hero.Clan?.StringId})");
                // Coop shows every active party on the server (it has no fog of war); "invisible" is the players' games' business.
                Expect(ledger != null && ledger.Ai.IsDisabled && ledger.Position.ToVec2() == Vec2.Zero,
                    $"{hero.StringId}: ledger {role} has no AI and was never put on the map (position {ledger?.Position.ToVec2()})");
            }
        }
        Expect(FbLedgers.Of(a.StringId, "fb_crimebase_party") != FbLedgers.Of(b.StringId, "fb_crimebase_party"), "the two players' gangs are different parties");

        // A crime base for the first player, the way Fourberie sets one up.
        var town = NearestTown(a.PartyBelongedTo!, null);
        using (FbBooks.Enter(a, a.PartyBelongedTo))
        {
            Set("_crimeBase", town);
            Dict<int, int>("_crimeValue")[500] = 1;
            Dict<int, int>("_crimeValue")[5] = 1;
            ((List<string>)Get("_territoryList")!).Add(town.StringId);
            Set("_crimeBaseParty", FbLedgers.Of(a.StringId, "fb_crimebase_party"));
        }
        var ledgerA = FbLedgers.Of(a.StringId, "fb_crimebase_party")!;
        ledgerA.MemberRoster.AddToCounts(a.Culture.BasicTroop, 12);
        Expect(BookOf(b)?["_crimeBase"]?.Type == JTokenType.Null, "the second player's book did not get the first player's base");

        // Every tick handler, through the same prefix the campaign's own events go through.
        var goldBefore = a.Gold;
        foreach (var (type, method, count, reach) in FbFields.Handlers.Where(h => h.Reach == FbFields.Reach.Connected))
        {
            var args = ArgsFor(method, count, a, town);
            if (args == null) continue;
            Invoke(type, method, args);
        }
        Log.Info($"{FourberieLayer.Tag}self-test: ticks done; {FbTicks.Summary()}; first player's gold {goldBefore} -> {a.Gold}");
        Expect(FbTicks.Summary().Contains("failures 0"), "no tick handler threw (" + FbTicks.Summary() + ")");
        Expect(BookOf(a)?["_crimeBase"]?.ToString() == town.StringId, "the first player's base is still theirs after the ticks");
        Expect(BookOf(a)?["_territoryList"] is JArray t && t.Any(x => (string?)x == town.StringId), "the first player's territory list is in their book");
        Expect(FbBooks.EncodeProblems == 0, $"every book field could be written ({FbBooks.EncodeProblems} problem(s))");

        Models(a, b, seed: true);

        // The insurance scam's spawns, relayed as the player's game would send them.
        var merchant = town.Notables.FirstOrDefault(n => n.IsMerchant) ?? town.Notables.FirstOrDefault();
        var other = NearestTown(a.PartyBelongedTo!, town);
        if (merchant == null) Fail("no notable in " + town.StringId + " to own the caravan");
        else
        {
            var caravan = FbRelay.Run(a, a.PartyBelongedTo, new[] { "Fourberie.HelperSubInsuScam::SpawnCaravan", new JArray(merchant.StringId, other.StringId).ToString(Formatting.None) });
            var raiders = FbRelay.Run(a, a.PartyBelongedTo, new[] { "Fourberie.HelperSubInsuScam::SpawnBandits", new JArray(25).ToString(Formatting.None) });
            var book = BookOf(a);
            var caravanParty = Party((string?)book?["_insucaraF"]);
            var raiderParty = Party((string?)book?["_insubandF"]);
            Expect(caravan.Ok && caravanParty is { IsActive: true } && caravanParty.IsCaravan, $"relayed SpawnCaravan made a real caravan ({caravanParty?.StringId}) and the book points at it");
            Expect(raiders.Ok && raiderParty is { IsActive: true } && raiderParty.IsBandit, $"relayed SpawnBandits made real raiders ({raiderParty?.StringId}) after the caravan");
            Expect(BookOf(b)?["_insucaraF"]?.Type == JTokenType.Null, "the second player's book has no caravan");
        }

        // Prompts for a player with no game: held for them (never the headless "yes"), then the AI decides at the deadline.
        string? answer = null, disabledAnswer = null;
        List<InquiryElement>? picked = null;
        using (FbBooks.Enter(a, a.PartyBelongedTo))
        {
            InformationManager.ShowInquiry(new InquiryData("self-test", "Pay the blackmail?", true, true, "Pay", "Refuse", () => answer = "yes", () => answer = "no"));
            InformationManager.ShowInquiry(new InquiryData("self-test", "Spend influence?", true, true, "Spend", "Refuse", () => disabledAnswer = "yes", () => disabledAnswer = "no",
                isAffirmativeOptionEnabled: () => (false, "not enough influence")));
            var elements = new List<InquiryElement> { new InquiryElement("first", "First", null), new InquiryElement("second", "Second", null) };
            MBInformationManager.ShowMultiSelectionInquiry(new MultiSelectionInquiryData("self-test", "Give up one", elements, false, 1, 1, "Done", "", chosen => picked = chosen, null));
        }
        Expect(answer == null && disabledAnswer == null && picked == null && FbPrompts.WaitingFor(a.StringId) == 3,
            $"an offline player's prompts are held for them, not answered ({FbPrompts.WaitingFor(a.StringId)} waiting)");
        FbPrompts.Expire(DateTime.UtcNow.AddMinutes(11));
        Expect(answer == "yes", $"at the deadline the AI decided the yes/no prompt the way Fourberie's NPCs would: pay (got '{answer ?? "nothing"}')");
        Expect(disabledAnswer == "no", $"with its affirmative option unavailable, the AI took the other one (got '{disabledAnswer ?? "nothing"}')");
        Expect(picked is { Count: 1 } && (string)picked[0].Identifier == "first", "at the deadline the AI picked from the list");
        Expect(FbPrompts.WaitingFor(a.StringId) == 0, "nothing is left waiting after the deadline");

        // World events reach an offline player's book: their paymaster dies while they are away.
        var paymaster = Hero.AllAliveHeroes.First(h => h.IsLord && h != a && h != b && h.Clan != a.Clan && h.Clan != b.Clan);
        using (FbBooks.Enter(b, b.PartyBelongedTo)) Dict<string, string>("_stringHeroIdDico")["paymaster"] = paymaster.StringId;
        FbBooks.TestPlayers.Remove(b);
        try { Invoke("Fourberie.FourberieBehavior", "FOnheroKilled", new object?[] { paymaster, null, default(KillCharacterAction.KillCharacterActionDetail), false }); }
        finally { FbBooks.TestPlayers.Add(b); }
        Expect(BookOf(b)?["_stringHeroIdDico"]?["paymaster"] == null, "an offline player's book heard their paymaster died and dropped them");

        var json = BookOf(a)?.ToString(Formatting.None) ?? "";
        Log.Info($"{FourberieLayer.Tag}self-test: first player's book is {json.Length} characters");
        Expect(BookOf(a)?[FbLedgers.Section]?["fb_crimebase_party"]?["m"] is JArray { Count: > 0 }, "the gang's troops are in the first player's book");
        Save();
    }

    // ---- check -------------------------------------------------------------------------------------------------

    private static void Check(Hero a, Hero b)
    {
        Expect(FbBooks.Has(a.StringId) && FbBooks.Has(b.StringId), $"both players' books came back with the save ({FbBooks.Count} book(s))");
        var book = BookOf(a);
        Expect(book?["_crimeBase"]?.Type == JTokenType.String, $"the first player's base came back ({book?["_crimeBase"]})");
        Expect(book?["_territoryList"] is JArray t && t.Count > 0, $"the first player's territory list came back ({book?["_territoryList"]})");
        Expect(Party((string?)book?["_insucaraF"]) is { IsActive: true }, "the caravan the book points at exists after loading");
        var ledger = FbLedgers.Of(a.StringId, "fb_crimebase_party");
        Expect(ledger is { IsActive: true } && ledger.MemberRoster.TotalManCount > 0, $"the gang ledger came back with its troops ({ledger?.MemberRoster.TotalManCount ?? 0})");
        Expect(Settlement.Find((string?)BookOf(b)?["_crimeBase"] ?? "")?.IsHideout == true, "the second player's base is still their hideout");
        // One more round of ticks on the loaded campaign.
        var town = Settlement.Find((string?)book?["_crimeBase"] ?? "");
        foreach (var (type, method, count, reach) in FbFields.Handlers.Where(h => h.Reach == FbFields.Reach.Connected))
        {
            var args = ArgsFor(method, count, a, town);
            if (args != null) Invoke(type, method, args);
        }
        Expect(FbTicks.Summary().Contains("failures 0"), "no tick handler threw after loading (" + FbTicks.Summary() + ")");
        Models(a, b, seed: false);
    }

    // ---- models (phase 5) ----------------------------------------------------------------------------------------

    /// <summary>
    /// The server's own answers, asked outside any player's run, must carry each player's Fourberie: the second player's
    /// hideout gang draws wages in their clan's daily gold (and nobody else's), and a town near the bandits they support
    /// loses security.
    /// </summary>
    private static void Models(Hero a, Hero b, bool seed)
    {
        var ledgerB = FbLedgers.Of(b.StringId, "fb_crimebase_party");
        if (seed)
        {
            var hideout = Settlement.All.Where(s => s.IsHideout).OrderBy(s => s.Position.DistanceSquared(b.PartyBelongedTo!.Position)).First();
            using (FbBooks.Enter(b, b.PartyBelongedTo))
            {
                Set("_crimeBase", hideout);
                Set("_crimeBaseParty", ledgerB);
                // What Fourberie's daily tick adds for a hideout base (its slave and mine shares).
                Dict<int, int>("_crimeValue")[500] = 1;
                Dict<int, int>("_crimeValue")[1000] = 0;
                Dict<int, int>("_crimeValue")[1001] = 0;
                Dict<int, int>("_crimeValue")[1500] = 0;
            }
            ledgerB?.MemberRoster.AddToCounts(b.Culture.BasicTroop, 20);
        }
        var finance = Campaign.Current!.Models.ClanFinanceModel;
        var goldB = Lines(finance.CalculateClanGoldChange(b.Clan, true, false, false));
        var goldA = Lines(finance.CalculateClanGoldChange(a.Clan, true, false, false));
        var stranger = Clan.All.FirstOrDefault(c => c != a.Clan && c != b.Clan && !c.IsEliminated && c.Leader != null && !c.IsBanditFaction);
        var goldStranger = stranger == null ? new List<string>() : Lines(finance.CalculateClanGoldChange(stranger, true, false, false));
        Expect(goldB.Any(l => l.Contains("Lads Wage")), $"the second player's hideout gang draws wages in their clan's daily gold ({string.Join(" | ", goldB.Where(l => l.Contains("(F)")))})");
        Expect(!goldA.Any(l => l.Contains("Lads Wage")), "the first player's clan pays no hideout wages (their base is a town)");
        Expect(!goldStranger.Any(l => l.Contains("(F)")), $"a clan no player leads gets no Fourberie lines ({stranger?.StringId})");

        // Security: a town near a hideout whose bandits the second player supports.
        // On the server no hideout counts as discovered; Fourberie only counts discovered ones, so spot the one used.
        var pair = (from h in Hideout.All
                    where h.Settlement.Culture != null
                    from t in Town.AllTowns
                    where t.OwnerClan != null && !t.OwnerClan.IsRebelClan && t.OwnerClan != b.Clan && t.MapFaction?.Leader != b
                          && h.Settlement.Position.Distance(t.Settlement.Position) <= 70f
                    select (Hideout: h, Town: t)).FirstOrDefault();
        if (pair.Town == null) { Log.Info(FourberieLayer.Tag + "self-test: no visible hideout near a town; the summed-model check is skipped"); return; }
        if (seed)
        {
            pair.Hideout.IsSpotted = true;
            pair.Hideout.Settlement.IsVisible = true;
            using (FbBooks.Enter(b, b.PartyBelongedTo))
                Dict<string, int>("_supportedBandits")[pair.Hideout.Settlement.Culture.StringId] = 1500;
        }
        if (!pair.Hideout.Settlement.IsVisible) { Log.Info(FourberieLayer.Tag + "self-test: the hideout did not stay discovered after loading; the summed-model check is skipped"); return; }
        var security = Lines(Campaign.Current.Models.SettlementSecurityModel.CalculateSecurityChange(pair.Town, true));
        Expect(security.Any(l => l.Contains("(F) Fourberie")), $"{pair.Town.Settlement.StringId}'s security carries the bandits the second player supports ({string.Join(" | ", security)})");
    }

    private static List<string> Lines(ExplainedNumber number) => number.GetLines().Select(l => l.name + " " + l.number).ToList();

    // ---- helpers -----------------------------------------------------------------------------------------------

    private static object?[]? ArgsFor(string method, int count, Hero hero, Settlement? town)
    {
        if (count == 0) return Array.Empty<object>();
        return method switch
        {
            "DailyTickSet" or "FOnDailyTickSettlement" when town != null => new object[] { town },
            "DailyTickHero" or "PitDailyTickHero" => new object[] { hero },
            "FOnDailyTickParty" when hero.PartyBelongedTo != null => new object[] { hero.PartyBelongedTo },
            "DailyTickClan" => new object[] { hero.Clan },
            _ => null,   // event handlers with game-made arguments (battles, settlements entered) are left to real events
        };
    }

    private static void Invoke(string typeName, string method, object?[] args)
    {
        var type = _mod?.GetType(typeName, false);
        var m = type?.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .FirstOrDefault(x => x.Name == method && x.GetParameters().Length == args.Length);
        if (m == null) { Fail(typeName + "." + method + " not found"); return; }
        object? instance = null;
        if (!m.IsStatic)
        {
            var get = typeof(Campaign).GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .First(x => x.Name == "GetCampaignBehavior" && x.IsGenericMethodDefinition && x.GetParameters().Length == 0);
            instance = get.MakeGenericMethod(type!).Invoke(Campaign.Current, null);
            if (instance == null) { Fail(typeName + " behaviour not in the campaign"); return; }
        }
        try { m.Invoke(instance, args); }
        catch (Exception ex) { Fail(type!.Name + "." + method + " threw: " + ex.GetBaseException().Message); }
    }

    private static object? Get(string field) => FbBooks.Field(field)?.GetValue(null);
    private static void Set(string field, object? value) => FbBooks.Field(field)?.SetValue(null, value);
    private static Dictionary<TK, TV> Dict<TK, TV>(string field) => (Dictionary<TK, TV>)Get(field)!;
    private static JObject? BookOf(Hero hero) => FbBooks.Json(hero);
    private static MobileParty? Party(string? id) => id == null ? null : MobileParty.All.FirstOrDefault(p => p.StringId == id);

    private static Settlement NearestTown(MobileParty party, Settlement? except) =>
        Settlement.All.Where(s => s.IsTown && s != except).OrderBy(s => s.Position.DistanceSquared(party.Position)).First();

    private static void Expect(bool ok, string what)
    {
        if (ok) { _pass++; Log.Info($"{FourberieLayer.Tag}self-test: PASS {what}"); }
        else Fail(what);
    }

    private static void Fail(string what)
    {
        _fail++;
        Log.Warn($"{FourberieLayer.Tag}self-test: FAIL {what}");
    }

    private static void Save()
    {
        var name = (Campaign.Current?.UniqueGameId ?? "fourberie") + "_fbcheck";
        try
        {
            var saveName = Environment.GetEnvironmentVariable(Env + "_SAVE") ?? name;
            var metadataMethod = typeof(MBSaveLoad).GetMethod("GetSaveMetaData", BindingFlags.Static | BindingFlags.NonPublic, null, new[] { typeof(CampaignSaveMetaDataArgs) }, null);
            var metadata = (MetaData)metadataMethod!.Invoke(null, new object[] { Campaign.Current!.SaveHandler.GetSaveMetaData() })!;
            var nativeVersion = ModuleHelper.GetModuleInfo("Native")?.Version ?? ApplicationVersion.Empty;
            foreach (var key in new[] { "ApplicationVersion", "NewGameVersion" })
                if (ApplicationVersion.FromString(metadata[key]).Major <= 0) metadata[key] = nativeVersion.ToString();
            _saving = true;
            CampaignEventDispatcher.Instance.OnBeforeSave();
            Game.Current.Save(metadata, saveName, new AsyncFileSaveDriver(), result =>
            {
                Expect((int)result == 0, $"the campaign saved as '{saveName}' with the books in it ({result})");
                Finish();
            });
        }
        catch (Exception ex)
        {
            Fail("save threw: " + ex.GetBaseException().Message);
            Finish();
        }
    }

    private static void Finish()
    {
        _saving = false;
        Log.Info($"{FourberieLayer.Tag}self-test: DONE {Mode}: {_pass} passed, {_fail} failed");
        // A self-test run is a one-shot process: end it once the result (and the save) is written.
        if (Environment.GetEnvironmentVariable(Env + "_EXIT") == "1")
        {
            Log.Info($"{FourberieLayer.Tag}self-test: exiting the server ({Env}_EXIT=1)");
            Environment.Exit(_fail == 0 ? 0 : 1);
        }
    }
}
