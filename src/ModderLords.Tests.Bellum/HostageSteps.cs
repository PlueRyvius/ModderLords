using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using ModderLords.Testing;
using TaleWorlds.CampaignSystem;
using static ModderLords.Tests.Bellum.Steps;

namespace ModderLords.Tests.Bellum;

/// <summary>
/// Steps for Bellum's treaty hostages (stage 4d, HostagePactBehavior): a hostage pact that leaves another realm's hostage
/// in a player ruler's keeping, and what became of the hostage.
/// </summary>
public static class HostageSteps
{
    private const string Behaviour = "BellumCivile.Behaviors.HostagePactBehavior";

    public static void Register()
    {
        InGameTests.Register("bellum.hostagePact", StepSide.Server, HostagePact);
        InGameTests.Register("bellum.heldAsHostage", StepSide.Server, HeldAsHostage);
        InGameTests.Register("bellum.alive", StepSide.Any, Alive);
    }

    private static object? Pacts() =>
        FindType(Behaviour) is { } type ? typeof(Campaign).GetMethod(nameof(Campaign.GetCampaignBehavior))!.MakeGenericMethod(type).Invoke(Campaign.Current, null) : null;

    /// <summary>
    /// Server: a hostage pact between the realm player <c>heroName</c> rules and another realm at peace with it (not
    /// <c>skip</c>, kingdom ids "a|b"), the other realm sending one of its ruler's kin as Bellum would choose
    /// (TreatyHostageEligibility), held in the player's own keep (Bellum's TryEstablish, as a peace treaty's hostage term
    /// does). Reports value (the hostage's id) and kingdom (the other realm's).
    /// </summary>
    private static IEnumerable<StepWait?> HostagePact(StepContext ctx)
    {
        var player = HeroNames.Single(ctx.Arg("heroName"));
        var mine = player.Clan?.Kingdom is { } k && k.RulingClan == player.Clan ? k : throw new StepFailedException("the player rules no realm");
        var pacts = Pacts() ?? throw new StepFailedException("Bellum's hostage pacts are not running");
        var candidates = FindType("BellumCivile.TreatyHostageEligibility")?.GetMethod("GetCandidates", Any)
            ?? throw new StepFailedException("Bellum has no TreatyHostageEligibility.GetCandidates");
        var establish = pacts.GetType().GetMethod("TryEstablish", Any) ?? throw new StepFailedException("Bellum has no HostagePactBehavior.TryEstablish");
        var skip = new HashSet<string>(ctx.Arg("skip", "").Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries));
        var tried = new List<string>();
        foreach (var other in Kingdom.All.Where(o => o != null && o != mine && !o.IsEliminated && o.RulingClan != null && !o.StringId.Contains("_rebels")
                     && !skip.Contains(o.StringId) && !mine.IsAtWarWith(o)).OrderBy(o => o.StringId))
        {
            var hostage = ((candidates.Invoke(null, new object[] { other }) as IEnumerable)?.Cast<object>() ?? Enumerable.Empty<object>())
                .Select(c => c.GetType().GetProperty("Hero", Any)?.GetValue(c) ?? c.GetType().GetField("Hero", Any)?.GetValue(c))
                .OfType<Hero>().OrderBy(h => h.StringId).FirstOrDefault();
            if (hostage == null)
            {
                tried.Add(other.StringId + " (no hostage)");
                continue;
            }
            if (establish.Invoke(pacts, new object?[] { other, mine, hostage, null }) is true)
            {
                yield return ctx.Until(() => hostage.IsPrisoner, $"{hostage.StringId} to be held", Sync);
                ctx.Log($"{other.StringId} sends {hostage.StringId} to {mine.StringId}, held at {hostage.PartyBelongedToAsPrisoner?.Name}");
                ctx.Report("value", hostage.StringId);
                ctx.Report("kingdom", other.StringId);
                yield break;
            }
            tried.Add(other.StringId + " (refused)");
        }
        throw new StepFailedException($"no realm made a hostage pact with {mine.StringId}: {string.Join(", ", tried)}");
    }

    /// <summary>
    /// Server: whether a hostage pact still holds hero <c>heroId</c> ("yes" or "no"; Bellum's GetProtectedPact), waited
    /// for to be <c>expect</c>.
    /// </summary>
    private static IEnumerable<StepWait?> HeldAsHostage(StepContext ctx)
    {
        var hero = Hero.FindFirst(h => h.StringId == ctx.Arg("heroId")) ?? throw new StepFailedException($"no hero '{ctx.Arg("heroId")}'");
        var pacts = Pacts() ?? throw new StepFailedException("Bellum's hostage pacts are not running");
        var protectedPact = pacts.GetType().GetMethod("GetProtectedPact", Any) ?? throw new StepFailedException("Bellum has no GetProtectedPact");
        string Read() => protectedPact.Invoke(pacts, new object[] { hero }) != null ? "yes" : "no";
        if (ctx.HasArg("expect"))
            yield return ctx.Until(() => Read() == ctx.Arg("expect"), $"{hero.StringId} held under a pact: {ctx.Arg("expect")}", Sync);
        ctx.Report("value", Read());
    }

    /// <summary>Either side: whether hero <c>heroId</c> is alive ("yes" or "no"), waited for to be <c>expect</c>.</summary>
    private static IEnumerable<StepWait?> Alive(StepContext ctx)
    {
        var hero = Hero.FindFirst(h => h.StringId == ctx.Arg("heroId")) ?? throw new StepFailedException($"no hero '{ctx.Arg("heroId")}'");
        string Read() => hero.IsAlive ? "yes" : "no";
        if (ctx.HasArg("expect"))
            yield return ctx.Until(() => Read() == ctx.Arg("expect"), $"{hero.StringId} alive: {ctx.Arg("expect")}", Sync);
        ctx.Assert(!ctx.HasArg("expect") || Read() == ctx.Arg("expect"), $"{hero.StringId} alive: {Read()}");
        ctx.Report("value", Read());
    }
}
