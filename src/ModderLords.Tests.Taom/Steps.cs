using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using ModderLords.Testing;
using TAOM;
using TAOM.Features.SupplyLines;
using TAOM.Features.SupplyLines.Domain;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Conversation;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Library;

namespace ModderLords.Tests.Taom;

/// <summary>
/// Steps for TAOM's co-op features, starting with Supply Lines. A step calls what TAOM's own screen calls, so in Coop
/// it takes the path a player's click does: on the player's game ModderLords' TAOM layer sends the order to the server,
/// the server places it as that player, spawns the caravan and moves it towards the player every frame.
/// </summary>
public static class Steps
{
    private static readonly TimeSpan Sync = TimeSpan.FromSeconds(30);

    public static void Register()
    {
        InGameTests.Register("TAOM.health", StepSide.Any, Health);
        InGameTests.Register("taom.supplySources", StepSide.Client, SupplySources);
        InGameTests.Register("taom.supplyOrder", StepSide.Client, SupplyOrder);
        InGameTests.Register("taom.supplyCaravans", StepSide.Server, SupplyCaravans);
        InGameTests.Register("taom.watchCaravans", StepSide.Server, WatchCaravans);
        InGameTests.Register("taom.setTime", StepSide.Server, SetTime);
        InGameTests.Register("taom.navPath", StepSide.Any, NavPath);
        InGameTests.Register("taom.findWanderer", StepSide.Client, FindWanderer);
        InGameTests.Register("taom.hireWanderer", StepSide.Client, HireWanderer);
        InGameTests.Register("taom.wandererTemplates", StepSide.Any, WandererTemplates);
    }

    /// <summary>
    /// Every wanderer's template (<c>Hero.Template</c>, which the game's wanderer introduction reads) on this side, so
    /// the server's and a player's can be compared. Reports how many have none.
    /// </summary>
    private static IEnumerable<StepWait?> WandererTemplates(StepContext ctx)
    {
        yield return ctx.Until(() => Campaign.Current != null, "the campaign");
        var wanderers = Hero.AllAliveHeroes.Where(h => h.IsWanderer).OrderBy(h => h.StringId, StringComparer.Ordinal).ToList();
        ctx.Dump("templates", string.Join("\n", wanderers.Select(h =>
            $"{h.StringId} \"{h.Name}\" template {h.Template?.StringId ?? "NONE"} original {h.CharacterObject?.OriginalCharacter?.StringId ?? "-"}")));
        var none = wanderers.Where(h => h.Template == null).ToList();
        ctx.Log($"{wanderers.Count} wanderer(s), {none.Count} without a template: {string.Join(", ", none.Take(10).Select(h => h.StringId))}");
        ctx.Report("count", wanderers.Count);
        ctx.Report("value", none.Count);
    }

    // ---- hiring a wanderer ---------------------------------------------------------------------------------

    /// <summary>
    /// A wanderer this player could hire: alive, in a town, in no party and nobody's companion. Arg <c>side</c> =
    /// <c>same</c> (default: one TAOM's Wanderer Allegiance lets this player hire), <c>other</c> (one it refuses), or
    /// <c>any</c>; <c>template</c> = <c>none</c> for one with no <c>Hero.Template</c> (TAOM's named companions).
    /// Reports <c>heroId</c>, <c>town</c>, <c>culture</c> and the allegiance verdict.
    /// </summary>
    private static IEnumerable<StepWait?> FindWanderer(StepContext ctx)
    {
        yield return ctx.Until(() => MobileParty.MainParty != null && Clan.PlayerClan != null, "this player's party");
        var side = ctx.Arg("side", "same");
        // template = none: one of TAOM's named companions, which have no Hero.Template (the player report's case).
        var noTemplate = ctx.Arg("template", "any") == "none";
        var candidates = Hero.AllAliveHeroes
            .Where(h => h.IsWanderer && h.CompanionOf == null && h.Clan == null && h.PartyBelongedTo == null && !h.IsPrisoner && h.CurrentSettlement?.IsTown == true)
            .Where(h => !noTemplate || h.Template == null)
            .Select(h => (Hero: h, Verdict: AllegianceVerdict(h)))
            .ToList();
        ctx.Dump("wanderers", string.Join("\n", candidates.Select(c =>
            $"{c.Hero.StringId} \"{c.Hero.Name}\" culture {c.Hero.Culture?.StringId} in {c.Hero.CurrentSettlement?.StringId}: {c.Verdict}")));
        var picked = candidates.FirstOrDefault(c => side switch
        {
            "same" => c.Verdict == "Allowed",
            "other" => c.Verdict.StartsWith("Refused", StringComparison.Ordinal),
            _ => true,
        });
        // A lookup that failed for every wanderer is a broken step (TAOM changed), not "nobody to hire".
        ctx.Assert(side == "any" || candidates.Count == 0 || !candidates.All(c => c.Verdict.StartsWith("unknown", StringComparison.Ordinal)),
            $"TAOM's wanderer allegiance could not be asked: {candidates.FirstOrDefault().Verdict}");
        ctx.Assert(picked.Hero != null, $"no hireable wanderer ({side}) among {candidates.Count} in towns");
        ctx.Log($"picked {picked.Hero!.StringId} \"{picked.Hero.Name}\" ({picked.Hero.Culture?.StringId}) in {picked.Hero.CurrentSettlement!.StringId}: {picked.Verdict}");
        ctx.Report("heroId", picked.Hero.StringId);
        ctx.Report("town", picked.Hero.CurrentSettlement.StringId);
        ctx.Report("culture", picked.Hero.Culture?.StringId);
        ctx.Report("verdict", picked.Verdict);
        ctx.Report("value", picked.Hero.StringId);
    }

    /// <summary>
    /// Talks to a wanderer (<c>heroId</c>) and hires them, as a player does: opens a conversation with them, steps
    /// through it the way the conversation screen does (the player's options, else continue), picking the hire line and
    /// paying, and waits for the conversation to close and the wanderer to join this player's clan (Coop applies the hire
    /// on the server and syncs it back). Every line and option is kept in the <c>conversation</c> dump, so a conversation
    /// that stops (no line for the next state) or throws shows where. Arg <c>expect</c> = <c>hired</c> (default) or
    /// <c>refused</c> (the wanderer turns the player down and the conversation goes on).
    /// The conversation is a map conversation, not the tavern scene a player meets wanderers in: the game's full
    /// backstory for a templated wanderer reads the scene's location, which a map conversation has none of, so pick a
    /// wanderer with <c>template: none</c> (or one already met) for this step.
    /// </summary>
    private static IEnumerable<StepWait?> HireWanderer(StepContext ctx)
    {
        yield return ctx.Until(() => MobileParty.MainParty != null && Campaign.Current?.ConversationManager != null, "this player's party");
        var hero = Hero.FindFirst(h => h.StringId == ctx.Arg("heroId"));
        ctx.Assert(hero != null, $"no hero {ctx.Arg("heroId")}");
        var expectHired = ctx.Arg("expect", "hired") == "hired";
        var cm = Campaign.Current.ConversationManager;
        var transcript = new StringBuilder();
        void Note(string line)
        {
            transcript.AppendLine(line);
            ModderLords.CompatSync.Log.Info("taom.hireWanderer: " + line);
        }
        Note($"talking to {hero!.StringId} \"{hero.Name}\" ({hero.Culture?.StringId}), gold {Hero.MainHero.Gold}, price {Campaign.Current.Models.CompanionHiringPriceCalculationModel.GetCompanionHiringPrice(hero)}, companions {Clan.PlayerClan.Companions.Count}/{Clan.PlayerClan.CompanionLimit}");

        CampaignMapConversation.OpenConversation(new ConversationCharacterData(CharacterObject.PlayerCharacter, PartyBase.MainParty),
            new ConversationCharacterData(hero.CharacterObject));
        yield return ctx.Until(() => cm.IsConversationInProgress && cm.OneToOneConversationHero == hero, "the conversation with the wanderer to open", TimeSpan.FromSeconds(30));
        Note($"open: [{TokenName(cm, cm.ActiveToken)}] {LastSentenceId(cm)}: {Safe(() => cm.CurrentSentenceText)}");

        // The path to the hire: the options to pick when offered, in order of preference. Anything else offered is a
        // step of the wanderer's introduction; the first clickable option that does not leave moves it on.
        string[] wanted = ["player_companion_hire_response_1", "taom_oath_accept", "main_option_faction_hire"];
        string[] leaving = ["player_is_leaving", "companion_hire_capacity_full", "player_companion_hire_response_2", "hero_special_request"];
        var pickedHire = false;
        var refused = false;
        var lastState = "";
        var sameState = 0;
        string? problem = null;
        var actions = 0;
        bool Step()
        {
            if (!cm.IsConversationInProgress) return true;
            if (problem != null) return true;
            try
            {
                var state = $"{cm.ActiveToken}/{LastSentenceId(cm)}";
                sameState = state == lastState ? sameState + 1 : 0;
                lastState = state;
                if (sameState >= 6)
                {
                    problem = $"the conversation stopped at [{TokenName(cm, cm.ActiveToken)}] after {LastSentenceId(cm)}: no option and no line moves it on";
                    return true;
                }
                if (++actions > 60) { problem = "the conversation did not close after 60 steps"; return true; }

                cm.GetPlayerSentenceOptions();
                var options = cm.CurOptions ?? [];
                if (options.Count > 0)
                    Note($"  [{TokenName(cm, cm.ActiveToken)}] options: {string.Join(" | ", options.Select(o => $"{o.Id}{(o.IsClickable ? "" : " (not clickable)")}: {o.Text}"))}");
                if (refused && options.Count > 0)
                {
                    // Refused, and the player can talk on: the conversation did not stop. Leave it.
                    var leave = options.FirstOrDefault(o => o.Id.StartsWith("player_is_leaving", StringComparison.Ordinal) && o.IsClickable);
                    if (leave.Id == null) { problem = "refused, then no way to leave the conversation"; return true; }
                    Note($"P  -> {leave.Id}");
                    cm.DoOption(leave.Id);
                }
                else if (options.Count == 0)
                {
                    cm.ContinueConversation();
                }
                else if (wanted.Select(w => options.FirstOrDefault(o => o.Id == w)).FirstOrDefault(o => o.Id != null) is { Id: not null } hire)
                {
                    if (!hire.IsClickable) { problem = $"the option {hire.Id} is offered but not clickable: {hire.HintText}"; return true; }
                    if (hire.Id is "player_companion_hire_response_1" or "taom_oath_accept") pickedHire = true;
                    Note($"P  -> {hire.Id}");
                    cm.DoOption(hire.Id);
                }
                else if (options.FirstOrDefault(o => o.IsClickable && !leaving.Any(l => o.Id.StartsWith(l, StringComparison.Ordinal))) is { Id: not null } next)
                {
                    Note($"P  -> {next.Id}");
                    cm.DoOption(next.Id);
                }
                else
                {
                    problem = $"no option leads to the hire at [{TokenName(cm, cm.ActiveToken)}]";
                    return true;
                }
                var said = LastSentenceId(cm);
                Note($"   now [{TokenName(cm, cm.ActiveToken)}] {said}: {Safe(() => cm.CurrentSentenceText)}");
                if (said.Contains("taom_wa_refuse")) refused = true;
            }
            catch (Exception ex)
            {
                problem = $"the conversation threw at [{TokenName(cm, cm.ActiveToken)}] after {LastSentenceId(cm)}: {ex}";
                return true;
            }
            return false;
        }
        while (!Step()) yield return ctx.Delay(TimeSpan.FromMilliseconds(300));
        ctx.Dump("conversation", transcript.ToString());
        if (problem != null && cm.IsConversationInProgress)
        {
            // Close what the player would be stuck in, so the rest of the run is not.
            try { cm.EndConversation(); } catch (Exception ex) { ctx.Log("closing the conversation threw: " + ex.Message); }
        }
        ctx.Assert(problem == null, problem ?? "");
        yield return ctx.Until(() => !cm.IsConversationInProgress, "the conversation to close", TimeSpan.FromSeconds(30));

        if (!expectHired)
        {
            ctx.Assert(refused && !pickedHire, refused ? "the hire went through after the refusal" : "the wanderer was not refused");
            ctx.Report("value", "refused");
            yield break;
        }
        ctx.Assert(!refused, "the wanderer refused this player (TAOM Wanderer Allegiance)");
        ctx.Assert(pickedHire, "the conversation closed before the hire was paid");
        yield return ctx.Until(() => hero.CompanionOf == Clan.PlayerClan && hero.PartyBelongedTo == MobileParty.MainParty,
            $"{hero.StringId} to join this player's clan and party (Coop hires on the server)", Sync);
        ctx.Report("value", "hired");
    }

    private static string AllegianceVerdict(Hero wanderer)
    {
        try
        {
            // By name, both shapes: TAOM 2.0.28 registers IWandererAllegianceService with
            // Evaluate(wandererId, wandererCulture, kingdomId, playerCulture); 2.0.41 dropped the interface, registers the
            // concrete WandererAllegianceService, and takes Evaluate(wandererId, wandererCulture, SideIds playerSide),
            // which its own dialog fills from FactionSides.OfPlayer().
            var taom = typeof(IoC).Assembly;
            var type = taom.GetType("TAOM.Features.WandererAllegiance.WandererAllegianceService", false) is { } concrete
                       && taom.GetType("TAOM.Features.WandererAllegiance.IWandererAllegianceService", false) == null
                ? concrete
                : taom.GetType("TAOM.Features.WandererAllegiance.IWandererAllegianceService", false);
            if (type == null) return "unknown (no WandererAllegianceService)";
            var resolve = typeof(IoC).GetMethod("Resolve", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static, null, Type.EmptyTypes, null);
            var service = resolve?.MakeGenericMethod(type).Invoke(null, null);
            var evaluate = type.GetMethods().FirstOrDefault(m => m.Name == "Evaluate" && m.GetParameters().Length is 3 or 4);
            if (service == null || evaluate == null) return "unknown (service or Evaluate not found)";
            object?[] args;
            if (evaluate.GetParameters().Length == 4)
            {
                var clan = Clan.PlayerClan;
                args = new object?[] { wanderer.StringId, wanderer.Culture?.StringId, clan?.Kingdom?.StringId,
                    clan?.Culture?.StringId ?? Hero.MainHero?.Culture?.StringId };
            }
            else
            {
                var ofPlayer = taom.GetType("TAOM.Adapters.FactionSides", false)?.GetMethod("OfPlayer", Type.EmptyTypes);
                if (ofPlayer == null) return "unknown (no FactionSides.OfPlayer)";
                args = new object?[] { wanderer.StringId, wanderer.Culture?.StringId, ofPlayer.Invoke(null, null) };
            }
            return evaluate.Invoke(service, args)?.ToString() ?? "null";
        }
        catch (Exception ex) { return "unknown (" + ex.GetType().Name + ")"; }
    }

    private static readonly System.Reflection.FieldInfo? SentencesField =
        typeof(TaleWorlds.CampaignSystem.Conversation.ConversationManager).GetField("_sentences", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
    private static readonly System.Reflection.FieldInfo? CurrentSentenceField =
        typeof(TaleWorlds.CampaignSystem.Conversation.ConversationManager).GetField("_currentSentence", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
    private static readonly System.Reflection.FieldInfo? StateMapField =
        typeof(TaleWorlds.CampaignSystem.Conversation.ConversationManager).GetField("stateMap", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

    /// <summary>The id of the line said last ("AI"/"P" by who said it), for the transcript.</summary>
    private static string LastSentenceId(TaleWorlds.CampaignSystem.Conversation.ConversationManager cm)
    {
        if (SentencesField?.GetValue(cm) is not List<TaleWorlds.CampaignSystem.Conversation.ConversationSentence> sentences
            || CurrentSentenceField?.GetValue(cm) is not int i || i < 0 || i >= sentences.Count) return "-";
        return (sentences[i].IsPlayer ? "P " : "AI ") + sentences[i].Id;
    }

    private static string TokenName(TaleWorlds.CampaignSystem.Conversation.ConversationManager cm, int token) =>
        StateMapField?.GetValue(cm) is Dictionary<string, int> map && map.FirstOrDefault(kv => kv.Value == token).Key is { } name ? name : token.ToString(CultureInfo.InvariantCulture);

    private static string Safe(Func<string> read)
    {
        try { return read(); } catch (Exception ex) { return "(" + ex.GetType().Name + ")"; }
    }

    /// <summary>
    /// Calls the map's pathfinder between two settlements' gates exactly as TAOM's caravan tick does
    /// (SupplyCaravanService.ComputeNavPathPoints: faces from <c>new CampaignVec2(pos, true)</c>, agent radius 0.1, no
    /// excluded faces, cost multiplier 1), with the region switch costs given (<c>landToSea</c>, <c>seaToLand</c>;
    /// TAOM passes 100 and 100). Reports the path's size. A side whose engine crashes here never answers.
    /// </summary>
    private static IEnumerable<StepWait?> NavPath(StepContext ctx)
    {
        yield return ctx.Until(() => Campaign.Current?.MapSceneWrapper != null, "the campaign map scene");
        var from = Settlement.Find(ctx.Arg("from"));
        var to = Settlement.Find(ctx.Arg("to"));
        ctx.Assert(from != null && to != null, $"no settlement {(from == null ? ctx.Arg("from") : ctx.Arg("to"))}");
        var a = from!.GatePosition.ToVec2();
        var b = to!.GatePosition.ToVec2();
        var fromFace = new CampaignVec2(a, true).Face;
        var toFace = new CampaignVec2(b, true).Face;
        var landToSea = ctx.ArgInt("landToSea", 100);
        var seaToLand = ctx.ArgInt("seaToLand", 100);
        ctx.Log($"from {from.StringId} ({a.x:0.00},{a.y:0.00}) face {fromFace.FaceIndex} to {to.StringId} ({b.x:0.00},{b.y:0.00}) face {toFace.FaceIndex}, region switch costs {landToSea}/{seaToLand}");
        // Written before the call, so a crash leaves the attempt in this side's ModderLords log.
        ModderLords.CompatSync.Log.Info($"taom.navPath: calling GetPathBetweenAIFaces {from.StringId} -> {to.StringId} faces {fromFace.FaceIndex} -> {toFace.FaceIndex} costs {landToSea}/{seaToLand}");
        var path = new NavigationPath();
        var found = Campaign.Current.MapSceneWrapper.GetPathBetweenAIFaces(fromFace, toFace, a, b, 0.1f, path, null, 1f, landToSea, seaToLand);
        ModderLords.CompatSync.Log.Info($"taom.navPath: returned {found}, {path.Size} point(s)");
        ctx.Report("found", found);
        ctx.Report("value", path.Size);
    }

    // ---- health --------------------------------------------------------------------------------------------

    /// <summary>TAOM's version and whether its Supply Lines services resolve, compared across sides by mods.health.</summary>
    private static IEnumerable<StepWait?> Health(StepContext ctx)
    {
        yield return ctx.Until(() => Campaign.Current != null, "the campaign");
        var version = typeof(IoC).Assembly.GetName().Version?.ToString() ?? "?";
        var orders = IoC.Resolve<ISupplyOrderService>();
        var sources = IoC.Resolve<ISupplySourceService>();
        ctx.Assert(orders != null && sources != null, "TAOM's Supply Lines services did not resolve (IoC.Resolve returned null)");
        ctx.Report("value", "TAOM " + version + ", supply lines ready");
    }

    // ---- the player's side ---------------------------------------------------------------------------------

    /// <summary>The supply sources TAOM's order screen lists for this player, nearest first.</summary>
    private static IEnumerable<StepWait?> SupplySources(StepContext ctx)
    {
        yield return ctx.Until(() => MobileParty.MainParty != null, "this player's party");
        var sources = IoC.Resolve<ISupplySourceService>().GetSources();
        ctx.Dump("sources", Describe(sources));
        ctx.Report("count", sources.Count);
        ctx.Report("orderable", sources.Count(s => s.CanOrder));
    }

    /// <summary>
    /// Places a supply order as the order screen's Confirm does (SupplyOrderScreenVM.ExecuteConfirm: the quantities
    /// picked, then ISupplyOrderService.TryPlaceOrder). Args: <c>source</c> = <c>here</c> (the settlement the party is
    /// in), <c>village</c> (the nearest orderable village other than that), or a settlement id; <c>goods</c> = how many
    /// of the first good on offer (default 1). Waits for the server's answer on screen.
    /// </summary>
    private static IEnumerable<StepWait?> SupplyOrder(StepContext ctx)
    {
        yield return ctx.Until(() => MobileParty.MainParty != null, "this player's party");
        var here = MobileParty.MainParty.CurrentSettlement?.StringId;
        var sourceService = IoC.Resolve<ISupplySourceService>();
        var sources = sourceService.GetSources();
        ctx.Dump("sources", Describe(sources));

        var wanted = ctx.Arg("source", "here");
        SupplySourceInfo? source = wanted switch
        {
            "here" => sources.FirstOrDefault(s => s.SettlementId == here && string.IsNullOrEmpty(s.HeroId)),
            "village" => sources
                .Where(s => s.CanOrder && string.IsNullOrEmpty(s.HeroId) && s.SettlementId != here && Settlement.Find(s.SettlementId)?.IsVillage == true)
                .OrderBy(s => s.Distance).FirstOrDefault(),
            _ => sources.FirstOrDefault(s => s.SettlementId == wanted && string.IsNullOrEmpty(s.HeroId)),
        };
        ctx.Assert(source != null, $"no supply source '{wanted}' among {sources.Count} listed (the party is in {here ?? "no settlement"})");
        ctx.Assert(source!.CanOrder, $"{source.SettlementId} cannot take an order: {source.DisabledReason}");

        var goods = new Dictionary<string, int>(StringComparer.Ordinal);
        var good = sourceService.GetGoods(source).FirstOrDefault(g => g.Available > 0);
        ctx.Assert(good != null, $"{source.SettlementId} offers no goods");
        goods[good!.Id] = Math.Min(good.Available, ctx.ArgInt("goods", 1));
        ctx.Log($"ordering {goods[good.Id]} {good.Id} from {source.SettlementId} ({source.Distance:0.0} away), party in {here ?? "no settlement"}");

        var answers = new List<string>();
        void OnMessage(InformationMessage m)
        {
            var text = m.Information ?? "";
            if (text.IndexOf("supply", StringComparison.OrdinalIgnoreCase) >= 0 || text.IndexOf("caravan", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("order", StringComparison.OrdinalIgnoreCase) >= 0)
                answers.Add(text);
        }
        InformationManager.DisplayMessageInternal += OnMessage;
        try
        {
            var placed = IoC.Resolve<ISupplyOrderService>().TryPlaceOrder(source, goods, new Dictionary<string, int>(), SupplyEscortOption.None, out var failReason, false);
            ctx.Assert(placed != null, $"TAOM refused the order on this game: {failReason}");
            yield return ctx.Until(() => answers.Count > 0, "the server's answer to the supply order", Sync);
        }
        finally { InformationManager.DisplayMessageInternal -= OnMessage; }
        ctx.Log("answer: " + string.Join(" | ", answers));
        ctx.Assert(answers.Any(a => a.IndexOf("set out", StringComparison.OrdinalIgnoreCase) >= 0 || a.IndexOf("on the way", StringComparison.OrdinalIgnoreCase) >= 0),
            $"the server did not dispatch the caravan: {string.Join(" | ", answers)}");
        ctx.Report("source", source.SettlementId);
        ctx.Report("value", "dispatched");
    }

    /// <summary>
    /// Sets campaign time on the server through Coop's own console command, as the smoke test does: <c>mode</c> =
    /// <c>play</c>, <c>fast</c> or <c>stop</c>. (Setting the mode on the player's game does not reach the server: run 2
    /// of this suite left time stopped.)
    /// </summary>
    private static IEnumerable<StepWait?> SetTime(StepContext ctx)
    {
        yield return ctx.Until(() => Campaign.Current != null, "the campaign");
        var (coopMode, expected) = ctx.Arg("mode") switch
        {
            "play" => ("Play_1x", CampaignTimeControlMode.StoppablePlay),
            "fast" => ("Play_2x", CampaignTimeControlMode.StoppableFastForward),
            "stop" => ("Pause", CampaignTimeControlMode.Stop),
            var other => throw new StepFailedException($"mode '{other}' is not play, fast or stop"),
        };
        const string command = "coop.debug.set_time_mode";
        ctx.Assert(CommandLineFunctionality.HasFunctionForCommand(command), $"the console command {command} is not registered in this game");
        var output = CommandLineFunctionality.CallFunction(command, new List<string> { coopMode }, out var found);
        ctx.Log($"{command} {coopMode} -> {output}");
        ctx.Assert(found, $"the console command {command} was not found when called");
        bool Applied() => expected == CampaignTimeControlMode.Stop
            ? Campaign.Current.TimeControlMode == CampaignTimeControlMode.Stop
            : Campaign.Current.TimeControlMode != CampaignTimeControlMode.Stop;
        yield return ctx.Until(Applied, $"campaign time mode {expected}", TimeSpan.FromSeconds(10));
        ctx.Report("value", Campaign.Current.TimeControlMode.ToString());
    }

    // ---- the server's side ---------------------------------------------------------------------------------

    /// <summary>
    /// The server's whole order book (every player's), waiting until at least <c>atLeast</c> caravans are on the road.
    /// Reports each one's order, source, owner clan and position.
    /// </summary>
    private static IEnumerable<StepWait?> SupplyCaravans(StepContext ctx)
    {
        var atLeast = ctx.ArgInt("atLeast", 1);
        yield return ctx.Until(() => InTransit().Count >= atLeast, $"{atLeast} supply caravan(s) on the road on the server", Sync);
        var caravans = InTransit();
        ctx.Dump("caravans", string.Join("\n", caravans.Select(c => DescribeCaravan(c.Order, c.Party))));
        ctx.Report("value", caravans.Count);
    }

    /// <summary>
    /// Lets campaign time run for <c>seconds</c> (real time, default 60) while sampling every supply caravan's position
    /// each second, as TAOM's per-frame tick moves them. Fails if campaign time did not advance (the caravans were never
    /// asked to move), and reports how far each one travelled. A server that dies here is the crash this module looks
    /// for; the run reports the server's exit.
    /// </summary>
    private static IEnumerable<StepWait?> WatchCaravans(StepContext ctx)
    {
        var seconds = ctx.ArgInt("seconds", 60);
        var start = CampaignTime.Now;
        var startedAt = DateTime.UtcNow;
        var lastSample = DateTime.MinValue;
        var first = new Dictionary<string, Vec2>();
        var last = new Dictionary<string, Vec2>();
        var log = new StringBuilder();
        bool Sample()
        {
            var now = DateTime.UtcNow;
            if (now - lastSample >= TimeSpan.FromSeconds(1))
            {
                lastSample = now;
                foreach (var (order, party) in InTransit())
                {
                    if (party == null) continue;
                    var p = party.GetPosition2D;
                    if (!first.ContainsKey(order.OrderId)) first[order.OrderId] = p;
                    last[order.OrderId] = p;
                    log.AppendLine($"{(now - startedAt).TotalSeconds:0}s {CampaignTime.Now} {order.OrderId} {party.StringId} at {p.x:0.00},{p.y:0.00} {order.StatusEnum}");
                }
            }
            return now - startedAt >= TimeSpan.FromSeconds(seconds);
        }
        yield return ctx.Until(Sample, $"{seconds} s of campaign time with the caravans moving", TimeSpan.FromSeconds(seconds + 30));
        ctx.Dump("caravan-positions", log.ToString());
        var hours = (CampaignTime.Now - start).ToHours;
        ctx.Report("hours", hours.ToString("0.00", CultureInfo.InvariantCulture));
        foreach (var id in first.Keys)
            ctx.Log($"{id}: moved {first[id].Distance(last[id]):0.00} from {first[id].x:0.00},{first[id].y:0.00}");
        ctx.Assert(hours > 0, $"campaign time did not advance in {seconds} s (time mode {Campaign.Current.TimeControlMode}), so the caravans were not moved");
        var atLeast = ctx.ArgInt("atLeast", 0);
        ctx.Assert(first.Count >= atLeast, $"saw {first.Count} supply caravan(s) on the road, expected at least {atLeast}");
        ctx.Report("value", first.Count);
    }

    // ---- helpers -------------------------------------------------------------------------------------------

    private static List<(SupplyOrder Order, MobileParty? Party)> InTransit()
    {
        IoC.Resolve<ISupplyOrderService>().SaveInto(out var orders, out _);
        return orders.Values
            .Where(o => o.StatusEnum == SupplyOrderStatus.InTransit)
            .Select(o => (o, (MobileParty?)MobileParty.All.FirstOrDefault(p => p.StringId == o.CaravanPartyId)))
            .ToList();
    }

    private static string DescribeCaravan(SupplyOrder order, MobileParty? party)
    {
        var where = party == null ? "no party" : $"{party.StringId} clan {party.ActualClan?.StringId ?? "none"} at {party.GetPosition2D.x:0.00},{party.GetPosition2D.y:0.00}";
        return $"{order.OrderId} from {(string.IsNullOrEmpty(order.SourceHeroId) ? order.SourceSettlementId : "lord " + order.SourceHeroId)}: {where}, planned {order.PlannedHours:0.0} h";
    }

    private static string Describe(IReadOnlyList<SupplySourceInfo> sources) =>
        string.Join("\n", sources.OrderBy(s => s.Distance).Select(s =>
            $"{s.SettlementId}{(string.IsNullOrEmpty(s.HeroId) ? "" : " lord " + s.HeroId)} \"{s.DisplayName}\" {s.Distance:0.0} {(s.CanOrder ? "can order" : "cannot: " + s.DisabledReason)}"));
}
