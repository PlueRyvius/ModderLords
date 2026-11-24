using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using ModderLords.Testing;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.Settlements;
using static ModderLords.Tests.Bellum.Steps;

namespace ModderLords.Tests.Bellum;

/// <summary>
/// Steps for Bellum's claim feud questions to the player (docs/BELLUM-COOP-COVERAGE.md, stage 3, A1; the module's Feuds):
/// a feud's petition brought before the ruler, and the feud's record as Bellum keeps it.
/// </summary>
public static class FeudSteps
{
    public static void Register()
    {
        InGameTests.Register("bellum.petition", StepSide.Server, Petition);
        InGameTests.Register("bellum.feudRecord", StepSide.Server, FeudRecord);
        InGameTests.Register("bellum.takeThrone", StepSide.Server, TakeThrone);
    }

    private static object Feuds() => Behavior("BellumCivile.Behaviors.ClaimFeudBehavior") ?? throw new StepFailedException("Bellum's claim feuds are not running");

    private static object? Behavior(string typeName)
    {
        var type = FindType(typeName);
        return type == null ? null : typeof(Campaign).GetMethod(nameof(Campaign.GetCampaignBehavior))!.MakeGenericMethod(type).Invoke(Campaign.Current, null);
    }

    private static object? Prop(object o, string name) => o.GetType().GetProperty(name, Any)?.GetValue(o);

    // Bellum's own list, resolved feuds included until its next daily tick drops them.
    private static object? Record(object feuds, string id) =>
        (feuds.GetType().GetField("_feuds", Any)?.GetValue(feuds) as IEnumerable)?.Cast<object>().FirstOrDefault(r => r != null && Prop(r, "RecordId") as string == id);

    /// <summary>
    /// Server: a lord of realm <c>kingdom</c> fabricates a claim on a fief another lord of the realm holds (<c>fief</c>, or
    /// one an AI vassal holds), starts a claim feud over it, and brings the petition before the ruler at once (Bellum's
    /// own cheats, then ClaimFeudBehavior.ResolvePetition). Reports value (the feud's record id), claimant and holder.
    /// </summary>
    private static IEnumerable<StepWait?> Petition(StepContext ctx)
    {
        var kingdom = KingdomById(ctx.Arg("kingdom")) ?? throw new StepFailedException($"no kingdom '{ctx.Arg("kingdom")}'");
        var feuds = Feuds();
        var titles = Behavior("BellumCivile.Behaviors.FeudalTitleBehavior") ?? throw new StepFailedException("Bellum's titles are not running");
        var fabrication = Behavior("BellumCivile.Behaviors.FeudalClaimFabricationBehavior") ?? throw new StepFailedException("Bellum's claim fabrication is not running");
        var resolveTitle = FindType("BellumCivile.CheatCommands")?.GetMethod("ResolveFeudalTitleForDebug", Any) ?? throw new StepFailedException("Bellum has no ResolveFeudalTitleForDebug");
        var fabricate = fabrication.GetType().GetMethod("TryStartFabrication", Any) ?? throw new StepFailedException("Bellum has no TryStartFabrication");
        var advance = fabrication.GetType().GetMethod("DebugAdvanceFabrication", Any) ?? throw new StepFailedException("Bellum has no DebugAdvanceFabrication");
        var start = feuds.GetType().GetMethod("TryForceStartFeud", Any) ?? throw new StepFailedException("Bellum has no TryForceStartFeud");
        var resolve = feuds.GetType().GetMethod("ResolvePetition", Any) ?? throw new StepFailedException("Bellum has no ResolvePetition");

        bool Lord(Clan c) => c != null && !c.IsEliminated && !c.IsMinorFaction && c.Leader != null && c.Leader.IsAlive && c != kingdom.RulingClan;
        // A lord (not a player) and a fief another lord holds (or <c>fief</c>) whose title Bellum lets that lord claim.
        var fiefs = ctx.HasArg("fief") ? new List<Settlement> { Settlement.Find(ctx.Arg("fief")) ?? throw new StepFailedException($"no settlement '{ctx.Arg("fief")}'") }
            : kingdom.Settlements.Where(s => (s.IsCastle || s.IsTown) && s.OwnerClan != null && Lord(s.OwnerClan)).OrderBy(s => s.IsTown ? 1 : 0).ThenBy(s => s.StringId).ToList();
        Settlement? fief = null;
        Clan? chosen = null;
        foreach (var candidate in fiefs)
        {
            chosen = kingdom.Clans.Where(c => Lord(c) && c != candidate.OwnerClan && !IsPlayerClan(c)).OrderByDescending(c => c.Tier)
                .FirstOrDefault(c => FabricationTarget(c, candidate) != null);
            if (chosen != null) { fief = candidate; break; }
        }
        if (fief == null || chosen == null)
            throw new StepFailedException($"no lord of {kingdom.StringId} could claim a fief another lord holds");
        var holder = fief.OwnerClan;
        if (ctx.HasArg("fief"))
        {
            // A fief given in a test keeps its old title holder in Bellum: make its owner hold the title too, as owning it
            // outright would, so a claim on it can be pressed against the owner.
            var held = resolveTitle.Invoke(null, new object[] { titles, fief.StringId });
            if (held != null)
            {
                held.GetType().GetMethod("SetDeJureHolder", Any)?.Invoke(held, new object[] { holder.StringId });
                held.GetType().GetMethod("SetDeFactoHolder", Any)?.Invoke(held, new object[] { holder.StringId });
                foreach (var invalidate in titles.GetType().GetMethods(Any).Where(m => m.Name.StartsWith("Invalidate") && m.Name.EndsWith("Cache") && m.GetParameters().Length == 0))
                    invalidate.Invoke(titles, null);
            }
        }

        // Bellum raises no private banners while the realm fights a foreign war: end its wars as Bellum's treaties do.
        if (Kingdom.All.Any(k => k != null && k != kingdom && !k.IsEliminated && kingdom.IsAtWarWith(k)))
        {
            var peace = FindType("BellumCivile.BellumPeaceResolutionContext")?.GetMethod("RunBellumPeaceResolution", Any)
                ?? throw new StepFailedException("Bellum has no BellumPeaceResolutionContext.RunBellumPeaceResolution");
            foreach (var enemy in Kingdom.All.Where(k => k != null && k != kingdom && !k.IsEliminated && kingdom.IsAtWarWith(k)).ToList())
                peace.Invoke(null, new object[] { (Action)(() => MakePeaceAction.Apply(kingdom, enemy)) });
            yield return ctx.Delay(TimeSpan.FromSeconds(2));
        }

        var title = resolveTitle.Invoke(null, new object[] { titles, fief.StringId }) ?? throw new StepFailedException($"{fief.StringId} has no feudal title");
        foreach (var claimant in new[] { chosen })
        {
            var fab = new object?[] { claimant, title, false, null, null };
            var fabricated = fabricate.Invoke(fabrication, fab) is true;
            var adv = new object?[] { claimant, 1f, null };
            if (fabricated) advance.Invoke(fabrication, adv);
            ctx.Log($"{claimant.StringId}: claim on {fief.StringId}: {(fabricated ? "fabricated; " + adv[2] : "not started (" + fab[4] + ")")}");
            var report = new object?[] { claimant, null };
            var started = start.Invoke(feuds, report) is true;
            ctx.Log($"{claimant.StringId} against {holder.StringId} over {fief.StringId}: {report[1]}");
            if (!started)
            {
                // Why Bellum found no target: the title's holder as Bellum sees it, and its own check on that clan.
                var valid = feuds.GetType().GetMethod("IsValidFeudClan", Any);
                var why = new object?[] { holder, null };
                var ok = valid?.Invoke(null, why);
                ctx.Log($"title {Prop(title, "TitleId")}: de facto {Prop(title, "DeFactoHolderClanId")}, de jure {Prop(title, "DeJureHolderClanId")}; "
                        + $"{holder.StringId} in {holder.Kingdom?.StringId}, mercenary {holder.IsUnderMercenaryService}; valid feud clan {ok} ({why[1]})");
                continue;
            }
            var record = (feuds.GetType().GetMethod("GetActiveFeuds", Any)?.Invoke(feuds, null) as IEnumerable)?.Cast<object>()
                .FirstOrDefault(r => Prop(r, "ClaimantClanId") as string == claimant.StringId && Prop(r, "HolderClanId") as string == holder.StringId);
            if (record == null) { ctx.Log($"{claimant.StringId}'s feud is over another title"); continue; }
            var targetTitle = titles.GetType().GetMethod("GetTitle", Any, null, new[] { typeof(string) }, null)?.Invoke(titles, new[] { Prop(record, "TargetTitleId") }) ?? title;
            var ready = Enum.Parse(FindType("BellumCivile.ClaimFeudState")!, "PetitionReady");
            record.GetType().GetMethod("SetState", Any)!.Invoke(record, new[] { ready });
            resolve.Invoke(feuds, new[] { titles, record, claimant, holder, targetTitle });
            var id = (string)Prop(record, "RecordId")!;
            ctx.Log($"petition {id}: {Prop(record, "State")}, judgment {Prop(record, "Judgment")}");
            ctx.Report("value", id);
            ctx.Report("claimant", claimant.StringId);
            ctx.Report("holder", holder.StringId);
            yield break;
        }
        throw new StepFailedException($"no lord of {kingdom.StringId} would feud with {holder.StringId} over {fief.StringId}");
    }

    private static bool IsPlayerClan(Clan clan) =>
        FindType("GameInterface.Services.Heroes.Extensions.HeroExtensions")?.GetMethod("IsPlayerHero", Any) is { } isPlayer
        && clan.Heroes.Any(h => isPlayer.Invoke(null, new object[] { h }) is true);

    /// <summary>
    /// Server: claim feud <c>id</c> as Bellum keeps it, "state|judgment", waited for until its judgment is
    /// <c>expectJudgment</c>, or is not <c>notJudgment</c>, or its state is not <c>notState</c>. Reports it.
    /// </summary>
    private static IEnumerable<StepWait?> FeudRecord(StepContext ctx)
    {
        var feuds = Feuds();
        var id = ctx.Arg("id");
        string Read()
        {
            var record = Record(feuds, id);
            return record == null ? "gone|gone" : $"{Prop(record, "State")}|{Prop(record, "Judgment")}";
        }
        var timeout = TimeSpan.FromSeconds(ctx.ArgInt("timeoutSeconds", 60));
        bool Done()
        {
            var parts = Read().Split('|');
            return (!ctx.HasArg("expectJudgment") || parts[1] == ctx.Arg("expectJudgment"))
                   && (!ctx.HasArg("notJudgment") || parts[1] != ctx.Arg("notJudgment"))
                   && (!ctx.HasArg("notState") || parts[0] != ctx.Arg("notState"));
        }
        yield return ctx.Until(Done, $"feud {id} ({Read()})", timeout);
        var value = Read();
        ctx.Assert(Done(), $"feud {id} is {value}");
        ctx.Report("value", value);
    }

    /// <summary>
    /// Server: the hero's clan (<c>heroName</c>) joins realm <c>kingdom</c> (default: the largest lords' realm) and takes its
    /// throne (ChangeRulingClanAction), so the player rules a realm of neighbouring lords. Reports the realm.
    /// </summary>
    private static IEnumerable<StepWait?> TakeThrone(StepContext ctx)
    {
        var clan = HeroNames.Single(ctx.Arg("heroName")).Clan ?? throw new StepFailedException("the hero has no clan");
        var kingdom = ctx.HasArg("kingdom") ? KingdomById(ctx.Arg("kingdom")) ?? throw new StepFailedException($"no kingdom '{ctx.Arg("kingdom")}'")
            : Kingdom.All.Where(k => k != null && !k.IsEliminated && k.RulingClan != null && !HasPlayers(k) && !k.StringId.Contains("_rebels"))
                .OrderByDescending(k => k.Clans.Count).First();
        if (clan.Kingdom != kingdom)
            ChangeKingdomAction.ApplyByJoinToKingdom(clan, kingdom, default, false);
        yield return ctx.Until(() => clan.Kingdom == kingdom, $"{clan.StringId} to join {kingdom.StringId}", TimeSpan.FromSeconds(30));
        ChangeRulingClanAction.Apply(kingdom, clan);
        yield return ctx.Until(() => kingdom.RulingClan == clan, $"{clan.StringId} to rule {kingdom.StringId}", TimeSpan.FromSeconds(30));
        ctx.Report("value", kingdom.StringId);
    }
}
