using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ModderLords.Testing;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Library;
using static ModderLords.Tests.Bellum.Steps;

namespace ModderLords.Tests.Bellum;

/// <summary>
/// Steps for player agency (docs/BELLUM-COOP-COVERAGE.md, stage 3): the module's channel between a player's PC and the
/// server (Agency), and Bellum's questions put to the player concerned (Prompts). Read by reflection like <see cref="Steps"/>.
/// </summary>
public static class AgencySteps
{
    public static void Register()
    {
        InGameTests.Register("bellum.agency", StepSide.Any, ChannelOpen);
        InGameTests.Register("bellum.agencyEcho", StepSide.Client, Echo);
        InGameTests.Register("bellum.answerSeconds", StepSide.Server, AnswerSeconds);
        InGameTests.Register("bellum.promptShown", StepSide.Client, PromptShown);
        InGameTests.Register("bellum.answer", StepSide.Client, AnswerPrompt);
    }

    private static Type Agency() => FindType("BellumCivileCoopCompat.Agency") ?? throw new StepFailedException("the compat module has no player agency (older than 1.4.0)");

    private static Type Prompts() => FindType("BellumCivileCoopCompat.Prompts") ?? throw new StepFailedException("the compat module has no prompts (older than 1.4.0)");

    private static List<string> Recent() => Agency().GetProperty("Recent", Any)?.GetValue(null) as List<string> ?? new List<string>();

    /// <summary>Waits until this side's agency channel is open (Coop's session built the module's network part).</summary>
    private static IEnumerable<StepWait?> ChannelOpen(StepContext ctx)
    {
        var agency = Agency();
        bool Open() => agency.GetProperty("Channel", Any)?.GetValue(null) != null;
        yield return ctx.Until(Open, "the module's agency channel to open", TimeSpan.FromSeconds(ctx.ArgInt("timeoutSeconds", 30)));
        ctx.Report("value", "open");
    }

    /// <summary>
    /// Player: sends an echo request to the server, which answers with the name of the hero it says sent it. Fails unless
    /// the answer names this player's hero (the server tells who sent a request; a message cannot claim to be anyone).
    /// </summary>
    private static IEnumerable<StepWait?> Echo(StepContext ctx)
    {
        var token = "t" + DateTime.UtcNow.Ticks;
        var send = Agency().GetMethod("ToServer", Any) ?? throw new StepFailedException("Agency has no ToServer");
        ctx.Assert(send.Invoke(null, new object[] { "echo", new[] { token } }) is true, "the request could not be sent (no channel)");
        string? reply = null;
        yield return ctx.Until(() => (reply = Recent().FirstOrDefault(r => r.StartsWith("echo ") && r.EndsWith("|" + token))) != null,
            "the server's echo", TimeSpan.FromSeconds(20));
        var name = reply!.Substring(5).Split('|')[0];
        var mine = Hero.MainHero?.Name?.ToString() ?? "";
        ctx.Log($"the server says the request came from {name}; this player is {mine}");
        // Bellum puts a feudal title in front of a hero's name ("Captain ..."), so the server's name may carry one.
        ctx.Assert(name == mine || name.EndsWith(" " + mine), $"the server took the request as {name}'s, not {mine}'s");
        ctx.Report("value", name);
    }

    /// <summary>Server: how long a player has to answer one of Bellum's questions (<c>seconds</c>).</summary>
    private static IEnumerable<StepWait?> AnswerSeconds(StepContext ctx)
    {
        Prompts().GetField("AnswerSeconds", Any)!.SetValue(null, (double)ctx.ArgInt("seconds"));
        ctx.Report("value", ctx.ArgInt("seconds"));
        yield break;
    }

    /// <summary>
    /// Player: waits for one of Bellum's questions from the server and closes it on screen (the next step answers it the
    /// way its buttons do). Reports its id (value) and its title.
    /// </summary>
    private static IEnumerable<StepWait?> PromptShown(StepContext ctx)
    {
        // The first question this run has not used yet (it may have arrived before this step began).
        var used = ctx.State.TryGetValue("bellum.prompts", out var u) && u is HashSet<string> set ? set : new HashSet<string>();
        ctx.State["bellum.prompts"] = used;
        string? prompt = null;
        yield return ctx.Until(() => (prompt = Recent().Where(r => r.StartsWith("prompt ")).FirstOrDefault(r => !used.Contains(r.Substring(7).Split('|')[0]))) != null,
            "a question from the server", TimeSpan.FromSeconds(ctx.ArgInt("timeoutSeconds", 60)));
        var parts = prompt!.Substring(7).Split('|');
        used.Add(parts[0]);
        ctx.Log($"asked: {parts.ElementAtOrDefault(1)}: {parts.ElementAtOrDefault(2)} [{parts.ElementAtOrDefault(3)} / {parts.ElementAtOrDefault(4)}]");
        yield return null;
        InformationManager.HideInquiry();
        ctx.Report("value", parts[0]);
        ctx.Report("title", parts.ElementAtOrDefault(1) ?? "");
    }

    /// <summary>Player: answers question <c>id</c> yes or no (<c>answer</c>), as its buttons do.</summary>
    private static IEnumerable<StepWait?> AnswerPrompt(StepContext ctx)
    {
        var answer = Prompts().GetMethod("Answer", Any) ?? throw new StepFailedException("Prompts has no Answer");
        var yes = ctx.Arg("answer", "no") == "yes";
        ctx.Assert(answer.Invoke(null, new object[] { ctx.Arg("id"), yes }) is true, "the answer could not be sent");
        ctx.Report("value", yes ? "yes" : "no");
        yield break;
    }
}
