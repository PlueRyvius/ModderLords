using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ModderLords.Testing;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Core;
using static ModderLords.Tests.Bellum.Steps;

namespace ModderLords.Tests.Bellum;

/// <summary>
/// Steps for Bellum's own actions carried out by the server (docs/BELLUM-COOP-COVERAGE.md, stage 3d; the module's
/// RemoteCalls): the player's PC calls Bellum's method as its button would, and the server carries it out.
/// </summary>
public static class ActionsSteps
{
    public static void Register()
    {
        InGameTests.Register("bellum.call", StepSide.Client, Call);
        InGameTests.Register("bellum.carried", StepSide.Server, Carried);
        InGameTests.Register("bellum.activeLaw", StepSide.Any, ActiveLaw);
        InGameTests.Register("bellum.otherFief", StepSide.Server, OtherFief);
        InGameTests.Register("bellum.advanceFabrication", StepSide.Server, AdvanceFabrication);
        InGameTests.Register("bellum.noticeShown", StepSide.Client, NoticeShown);
        InGameTests.Register("bellum.fileCrownMotion", StepSide.Client, FileCrownMotion);
        InGameTests.Register("bellum.addCouncilDecision", StepSide.Server, AddCouncilDecision);
        InGameTests.Register("bellum.hasDecision", StepSide.Any, HasDecision);
    }

    private static object? Behavior(Type type) =>
        typeof(Campaign).GetMethod(nameof(Campaign.GetCampaignBehavior))!.MakeGenericMethod(type).Invoke(Campaign.Current, null);

    private static object LawRegistry() =>
        FindType("BellumCivile.RealmLawRegistry")?.GetProperty("Instance", Any)?.GetValue(null) ?? throw new StepFailedException("Bellum has no law registry");

    private static object Laws() => Behavior(FindType("BellumCivile.Behaviors.RealmLawBehavior") ?? throw new StepFailedException("Bellum has no realm laws"))
                                     ?? throw new StepFailedException("Bellum's realm laws are not running");

    private static string Active(Kingdom kingdom, string group) =>
        Laws().GetType().GetMethod("GetActiveLawId", Any)!.Invoke(Laws(), new object[] { kingdom, group }) as string ?? "";

    /// <summary>
    /// Player: calls Bellum's <c>method</c> ("Type.Method", e.g. RealmLawBehavior.TryApplyPlayerLaw) as its button does,
    /// with <c>args</c> (| separated: playerClan, playerHero, playerRealm, text:..., null, true, false, int:N, enum:Name,
    /// lawOther:GROUP (a law of the group other than the realm's), lawActive:GROUP, fiefTitle (the title of settlement <c>fief</c>)). In co-op the module sends the call to
    /// the server. Reports value (what the call answered here) and law (the law picked, if any).
    /// </summary>
    private static IEnumerable<StepWait?> Call(StepContext ctx)
    {
        var parts = ctx.Arg("method").Split('.');
        var type = FindType("BellumCivile." + parts[0]) ?? FindType("BellumCivile.Behaviors." + parts[0]) ?? throw new StepFailedException($"Bellum has no {parts[0]}");
        var method = type.GetMethods(Any).FirstOrDefault(m => m.Name == parts[1]) ?? throw new StepFailedException($"Bellum has no {ctx.Arg("method")}");
        var realm = Clan.PlayerClan?.Kingdom;
        var tokens = ctx.Arg("args", "").Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries);
        var parameters = method.GetParameters();
        var values = new object?[parameters.Length];
        var law = "";
        for (int i = 0, t = 0; i < parameters.Length; i++)
        {
            if (parameters[i].IsOut) continue;
            var token = t < tokens.Length ? tokens[t++] : "null";
            values[i] = token switch
            {
                "playerClan" => Clan.PlayerClan,
                "playerHero" => Hero.MainHero,
                "playerRealm" => realm,
                "null" => null,
                "true" => true,
                "false" => false,
                _ when token.StartsWith("text:") => token.Substring(5),
                _ when token.StartsWith("int:") => int.Parse(token.Substring(4), CultureInfo.InvariantCulture),
                _ when token.StartsWith("enum:") => Enum.Parse(parameters[i].ParameterType, token.Substring(5)),
                "fiefTitle" => FiefTitle(ctx.Arg("fief")),
                _ when token.StartsWith("lawActive:") => Active(realm ?? throw new StepFailedException("the player has no realm"), token.Substring(10)),
                _ when token.StartsWith("lawOther:") => law = OtherLaw(realm ?? throw new StepFailedException("the player has no realm"), token.Substring(9)),
                _ => throw new StepFailedException($"unknown argument '{token}'"),
            };
        }
        var instance = method.IsStatic ? null : Behavior(type) ?? type.GetProperty("Instance", Any)?.GetValue(null) ?? throw new StepFailedException($"{type.Name} is not running");
        var result = method.Invoke(instance, values);
        var said = string.Join(" ", parameters.Select((p, i) => p.IsOut ? values[i]?.ToString() : null).Where(s => !string.IsNullOrEmpty(s)));
        ctx.Log($"{ctx.Arg("method")}: {result ?? "(void)"} {said}");
        yield return null;
        ctx.Report("value", result?.ToString() ?? "");
        ctx.Report("law", law);
    }

    private static object FiefTitle(string fief)
    {
        var titles = Behavior(FindType("BellumCivile.Behaviors.FeudalTitleBehavior") ?? throw new StepFailedException("Bellum has no titles"));
        var resolve = FindType("BellumCivile.CheatCommands")?.GetMethod("ResolveFeudalTitleForDebug", Any) ?? throw new StepFailedException("Bellum has no ResolveFeudalTitleForDebug");
        return resolve.Invoke(null, new object?[] { titles, fief }) ?? throw new StepFailedException($"{fief} has no feudal title");
    }

    /// <summary>Server: a castle or town of realm <c>kingdom</c>, held by neither the ruler nor the hero's clan (<c>heroName</c>), whose title Bellum lets that clan claim.</summary>
    private static IEnumerable<StepWait?> OtherFief(StepContext ctx)
    {
        var kingdom = KingdomById(ctx.Arg("kingdom")) ?? throw new StepFailedException($"no kingdom '{ctx.Arg("kingdom")}'");
        var mine = HeroNames.Single(ctx.Arg("heroName")).Clan;
        // One whose title Bellum lets the player's clan claim (a neighbouring, liege or subordinate title).
        var fief = kingdom.Settlements.Where(st => (st.IsCastle || st.IsTown) && st.OwnerClan != null && st.OwnerClan != mine && st.OwnerClan != kingdom.RulingClan)
            .OrderBy(st => st.IsTown ? 1 : 0).ThenBy(st => st.StringId)
            .FirstOrDefault(st => mine == null || FabricationTarget(mine, st, ctx.Log) != null) ?? throw new StepFailedException($"no fief in {kingdom.StringId} the player's clan could claim");
        ctx.Report("value", fief.StringId);
        yield break;
    }

    /// <summary>
    /// Server: the claim fabrication of the hero's clan (<c>heroName</c>) advances by <c>amount</c> (Bellum's own
    /// DebugAdvanceFabrication). At each milestone it passes (33%, 66%) Bellum rolls whether the title's holder discovers
    /// the plot, from the plotter's and the holder's Steward and Roguery; a discovery stops it there with a setback instead
    /// of the stage's report. The plotter's Steward and Roguery are first raised to <c>stealth</c> (default 300) each, which
    /// takes that chance to Bellum's floor of 2%: a random new character otherwise failed it about one run in five.
    /// </summary>
    private static IEnumerable<StepWait?> AdvanceFabrication(StepContext ctx)
    {
        var hero = HeroNames.Single(ctx.Arg("heroName"));
        var clan = hero.Clan ?? throw new StepFailedException("the hero has no clan");
        var type = FindType("BellumCivile.Behaviors.FeudalClaimFabricationBehavior") ?? throw new StepFailedException("Bellum has no claim fabrication");
        var fabrication = Behavior(type) ?? throw new StepFailedException("Bellum's claim fabrication is not running");
        var stealth = ctx.ArgInt("stealth", 300);
        foreach (var plotter in new[] { hero, clan.Leader }.Where(h => h != null).Distinct())
        {
            plotter.SetSkillValue(DefaultSkills.Steward, Math.Max(stealth, plotter.GetSkillValue(DefaultSkills.Steward)));
            plotter.SetSkillValue(DefaultSkills.Roguery, Math.Max(stealth, plotter.GetSkillValue(DefaultSkills.Roguery)));
        }
        ctx.Log($"plotter's Steward and Roguery at least {stealth} each");
        var args = new object?[] { clan, float.Parse(ctx.Arg("amount", "0.5"), CultureInfo.InvariantCulture), null };
        var ok = type.GetMethod("DebugAdvanceFabrication", Any)!.Invoke(fabrication, args);
        ctx.Log($"advanced: {ok} {args[2]}");
        yield return null;
        ctx.Report("value", args[2]?.ToString() ?? "");
    }

    /// <summary>Player: waits for a notice from the server containing <c>contains</c>.</summary>
    private static IEnumerable<StepWait?> NoticeShown(StepContext ctx)
    {
        var recent = FindType("BellumCivileCoopCompat.Agency")?.GetProperty("Recent", Any) ?? throw new StepFailedException("the compat module has no Agency");
        string? seen = null;
        yield return ctx.Until(() => (seen = (recent.GetValue(null) as List<string> ?? new List<string>())
                .FirstOrDefault(r => r.StartsWith("notice ") && r.IndexOf(ctx.Arg("contains"), StringComparison.OrdinalIgnoreCase) >= 0)) != null,
            $"a notice with '{ctx.Arg("contains")}'", TimeSpan.FromSeconds(ctx.ArgInt("timeoutSeconds", 60)));
        ctx.Log(seen!);
        ctx.Report("value", seen!);
    }

    /// <summary>
    /// Player (a ruler): files the first court motion Bellum's crown actions offer that the player can pay for, as the Crown
    /// panel's last confirmation does (CourtAgendaBehavior.FilePlayerCrownBusiness with a motion from AvailablePlayerMotions).
    /// In co-op the module sends it to the server. Reports value (the motion's kind) and label.
    /// </summary>
    private static IEnumerable<StepWait?> FileCrownMotion(StepContext ctx)
    {
        var type = FindType("BellumCivile.Behaviors.CourtAgendaBehavior") ?? throw new StepFailedException("Bellum has no court agenda");
        var court = Behavior(type) ?? throw new StepFailedException("Bellum's court agenda is not running");
        var realm = Clan.PlayerClan?.Kingdom ?? throw new StepFailedException("the player has no realm");
        var draft = type.GetMethod("NewPlayerCrownBusiness", Any)!.Invoke(court, new object[] { realm });
        var offered = (type.GetMethod("AvailablePlayerMotions", Any)!.Invoke(court, new object?[] { draft, null }) as IEnumerable)?.Cast<object>().ToList() ?? new List<object>();
        object? Member(object o, string name) => o.GetType().GetField(name, Any)?.GetValue(o) ?? o.GetType().GetProperty(name, Any)?.GetValue(o);
        var influence = Clan.PlayerClan.Influence;
        ctx.Log("offered: " + string.Join(", ", offered.Select(m => $"{Member(m, "Kind")} ({Member(m, "FilingCost")})")));
        var skip = new HashSet<string>(ctx.Arg("skip", "").Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries));
        var motion = offered.FirstOrDefault(m => !skip.Contains(Member(m, "Kind") as string ?? "") && Convert.ToInt32(Member(m, "FilingCost")) <= influence)
                     ?? throw new StepFailedException($"Bellum offers the player no court motion they can pay for ({offered.Count} offered, influence {influence:0})");
        var filed = type.GetMethod("FilePlayerCrownBusiness", Any)!.Invoke(court, new[] { realm, motion });
        ctx.Log($"filed {Member(motion, "Kind")}: {filed}");
        yield return null;
        ctx.Report("value", Member(motion, "Kind")?.ToString() ?? "");
        ctx.Report("label", Member(motion, "Label")?.ToString() ?? "");
    }

    /// <summary>
    /// Server: a lord of realm <c>kingdom</c> proposes a privy council appointment (Bellum's own
    /// PrivyCouncilAppointmentDecision, for the first office), added to the realm's decisions as Bellum adds it.
    /// </summary>
    private static IEnumerable<StepWait?> AddCouncilDecision(StepContext ctx)
    {
        var kingdom = KingdomById(ctx.Arg("kingdom")) ?? throw new StepFailedException($"no kingdom '{ctx.Arg("kingdom")}'");
        var type = FindType("BellumCivile.PrivyCouncilAppointmentDecision") ?? throw new StepFailedException("Bellum has no PrivyCouncilAppointmentDecision");
        var office = Enum.GetValues(FindType("BellumCivile.PrivyCouncilOffice") ?? throw new StepFailedException("Bellum has no PrivyCouncilOffice")).GetValue(0);
        var proposer = kingdom.Clans.Where(c => c != null && !c.IsEliminated && !c.IsMinorFaction && c.Leader != null && c != kingdom.RulingClan)
            .OrderByDescending(c => c.Tier).FirstOrDefault() ?? kingdom.RulingClan;
        var ctor = type.GetConstructor(new[] { typeof(Clan), office.GetType() }) ?? throw new StepFailedException("PrivyCouncilAppointmentDecision has changed shape");
        var decision = (TaleWorlds.CampaignSystem.Election.KingdomDecision)ctor.Invoke(new[] { proposer, office });
        kingdom.AddDecision(decision, true);
        ctx.Log($"{proposer.StringId} proposes a {office} appointment in {kingdom.StringId}; allowed: {decision.IsAllowed()}");
        yield return null;
        ctx.Report("value", type.Name);
    }

    /// <summary>Either side: waits until realm <c>kingdom</c> has an open decision of type <c>type</c> (a short name).</summary>
    private static IEnumerable<StepWait?> HasDecision(StepContext ctx)
    {
        var kingdom = KingdomById(ctx.Arg("kingdom")) ?? throw new StepFailedException($"no kingdom '{ctx.Arg("kingdom")}'");
        string Read() => string.Join(",", kingdom.UnresolvedDecisions.Select(d => d.GetType().Name));
        yield return ctx.Until(() => kingdom.UnresolvedDecisions.Any(d => d.GetType().Name == ctx.Arg("type")),
            $"{kingdom.StringId} to have a {ctx.Arg("type")}", TimeSpan.FromSeconds(ctx.ArgInt("timeoutSeconds", 30)));
        ctx.Report("value", Read());
    }

    private static string OtherLaw(Kingdom realm, string group)
    {
        var active = Active(realm, group);
        var inGroup = LawRegistry().GetType().GetMethod("InGroup", Any)!.Invoke(LawRegistry(), new object[] { group }) as IEnumerable
                      ?? throw new StepFailedException($"no law group '{group}'");
        return inGroup.Cast<object>().Select(d => d.GetType().GetProperty("Id", Any)?.GetValue(d) as string).FirstOrDefault(id => id != null && id != active)
               ?? throw new StepFailedException($"law group '{group}' has no other law");
    }

    /// <summary>Server: waits until the module carried out <c>expect</c> for a player ("RealmLawBehavior.TryApplyPlayerLaw done").</summary>
    private static IEnumerable<StepWait?> Carried(StepContext ctx)
    {
        var field = FindType("BellumCivileCoopCompat.RemoteCalls")?.GetField("Carried", Any) ?? throw new StepFailedException("the compat module has no RemoteCalls (older than 1.5.0)");
        var carried = (List<string>)field.GetValue(null)!;
        bool Seen() { lock (carried) return carried.Contains(ctx.Arg("expect")); }
        yield return ctx.Until(Seen, $"the server to carry out {ctx.Arg("expect")}", TimeSpan.FromSeconds(ctx.ArgInt("timeoutSeconds", 60)));
        lock (carried) ctx.Report("value", string.Join(", ", carried));
    }

    /// <summary>Either side: the active law of <c>group</c> in realm <c>kingdom</c>, waited for until it is <c>expect</c>.</summary>
    private static IEnumerable<StepWait?> ActiveLaw(StepContext ctx)
    {
        var kingdom = KingdomById(ctx.Arg("kingdom")) ?? throw new StepFailedException($"no kingdom '{ctx.Arg("kingdom")}'");
        var group = ctx.Arg("group", "succession");
        if (ctx.HasArg("expect"))
            yield return ctx.Until(() => Active(kingdom, group) == ctx.Arg("expect"), $"{kingdom.StringId}'s {group} law to be {ctx.Arg("expect")}",
                TimeSpan.FromSeconds(ctx.ArgInt("timeoutSeconds", 60)));
        var value = Active(kingdom, group);
        if (ctx.HasArg("expect"))
            ctx.Assert(value == ctx.Arg("expect"), $"{kingdom.StringId}'s {group} law is {value}, expected {ctx.Arg("expect")}");
        ctx.Report("value", value);
    }
}
