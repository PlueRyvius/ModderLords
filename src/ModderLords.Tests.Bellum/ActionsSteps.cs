using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ModderLords.Testing;
using TaleWorlds.CampaignSystem;
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
    /// lawOther:GROUP (a law of the group other than the realm's), lawActive:GROUP). In co-op the module sends the call to
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
        var field = FindType("BellumCivileCoopCompat.RemoteCalls")?.GetField("Carried", Any) ?? throw new StepFailedException("the compat module has no RemoteCalls (older than 1.6.0)");
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
