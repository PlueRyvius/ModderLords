using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace ModderLords.Testing;

/// <summary>
/// A Coop session's life: the server's world is kept when the launch ends (so a later launch can load it), and a
/// player leaves and joins again. Coop is reached by reflection only, as everywhere in this assembly.
/// </summary>
internal static class SessionSteps
{
    private static readonly Regex SafeName = new Regex("^[A-Za-z0-9_-]{1,48}$");

    internal static void Register()
    {
        InGameTests.Register("coop.saveWorld", StepSide.Server, SaveWorld);
        InGameTests.Register("coop.rejoin", StepSide.Client, Rejoin);
    }

    // ---- coop.saveWorld -----------------------------------------------------------------------------------------

    /// <summary>
    /// Server: asks the launcher to keep this world as test world <c>world</c>. The world kept is the one the server
    /// saves when the launch ends (the host's shutdown save, with Coop's session file of which player plays which hero
    /// beside it), which is how a host's world is saved and loaded again. A suite with <c>"after"</c> naming this one
    /// then starts the next launch's server on it. Reports <c>keptWorld</c> (the launcher reads it).
    /// <para>Coop 0.1.8's own <c>coop.debug.save.save_as</c> only queues a save, and on the dedicated server the queue
    /// never ran in 10 minutes (2026-11-21), so it is not used.</para>
    /// </summary>
    private static IEnumerable<StepWait?> SaveWorld(StepContext ctx)
    {
        yield return ctx.Until(() => Campaign.Current != null, "the campaign");
        var world = ctx.Arg("world");
        ctx.Assert(SafeName.IsMatch(world), $"world '{world}' must be 1 to 48 letters, digits, underscores or hyphens");
        ctx.Log($"the server's shutdown save will be kept as test world {world}");
        ctx.Report("keptWorld", world);
        ctx.Report("value", world);
    }

    // ---- coop.rejoin --------------------------------------------------------------------------------------------

    /// <summary>
    /// Player: leaves the session to the main menu, as the escape menu's exit does (Coop ends its session when the game
    /// ends), then joins the same server again through Coop's own automatic join, and waits for Coop's join-finished
    /// signal. Reports the hero's name before (<c>before</c>) and after (<c>value</c>, <c>heroName</c>): a player gets
    /// their own hero back.
    /// </summary>
    private static IEnumerable<StepWait?> Rejoin(StepContext ctx)
    {
        yield return ctx.Until(() => Campaign.Current != null && Hero.MainHero != null, "the campaign");
        CoopWatch.ArmJoin();
        ctx.Assert(CoopWatch.JoinUnavailable == null, "Coop's join signal cannot be watched: " + CoopWatch.JoinUnavailable);
        var before = Hero.MainHero.Name?.ToString() ?? "";
        var gold = Hero.MainHero.Gold;
        var coopMod = FindCoopMod();
        var session = coopMod.GetType().GetField("Coop", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
        var running = session?.GetType().GetProperty("Running");
        ctx.Assert(session != null && running != null, "CoopMod.Coop or its Running property is not there in this Coop");
        bool Running() => running!.GetValue(session, null) is true;
        ctx.Log($"leaving as {before} ({gold} gold); Coop running: {Running()}");

        var left = DateTime.UtcNow;
        // Read by the launcher's smoke watch: the server is about to drop this player, on purpose.
        ModderLords.CompatSync.Log.Info("coop rejoin: leaving");
        MBGameManager.EndGame();
        yield return ctx.Until(() => Campaign.Current == null && ActiveState == "InitialState" && !Running(),
            "the main menu with Coop's session ended", TimeSpan.FromMinutes(3));
        ctx.Log("at the main menu; joining again");

        // Coop's automatic join runs once per process, from the main menu; letting it run again is the Join button
        // with the same address this run started with.
        var autoStarted = coopMod.GetType().GetField("_autoStarted", BindingFlags.NonPublic | BindingFlags.Instance);
        if (autoStarted != null) autoStarted.SetValue(coopMod, false);
        else
        {
            var config = coopMod.GetType().GetField("autoConnectConfiguration", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(coopMod);
            var start = session!.GetType().GetMethods().FirstOrDefault(m => m.Name == "StartAsClient");
            ctx.Assert(config != null && start != null, "neither Coop's automatic join flag nor its join address and StartAsClient were found");
            var args = start!.GetParameters().Select((p, i) => i == 0 ? config : p.HasDefaultValue ? p.DefaultValue : null).ToArray();
            ctx.Log("StartAsClient -> " + start.Invoke(session, args));
        }

        yield return ctx.Until(() => CoopWatch.JoinReadyAt is { } at && at > left, "Coop's ClientCampaignReady after joining again", TimeSpan.FromMinutes(5));
        yield return ctx.Until(() => Campaign.Current != null && Hero.MainHero != null && ActiveState == "MapState", "the campaign map", TimeSpan.FromMinutes(2));
        ModderLords.CompatSync.Log.Info("coop rejoin: back");
        var after = Hero.MainHero.Name?.ToString() ?? "";
        ctx.Log($"joined again as {after} ({Hero.MainHero.Gold} gold) after {(DateTime.UtcNow - left).TotalSeconds:F0} s");
        ctx.Assert(after == before, $"joined again as '{after}', expected the same hero '{before}'");
        ctx.Report("before", before);
        ctx.Report("heroName", after);
        ctx.Report("value", after);
    }

    private static string ActiveState => TaleWorlds.Core.GameStateManager.Current?.ActiveState?.GetType().Name ?? "";

    /// <summary>Coop's MBSubModuleBase instance, from the module's loaded submodules.</summary>
    private static object FindCoopMod()
    {
        var module = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("TaleWorlds.MountAndBlade.Module", false)).FirstOrDefault(t => t != null)
            ?? throw new StepFailedException("TaleWorlds.MountAndBlade.Module is not loaded");
        var current = module.GetProperty("CurrentModule", BindingFlags.Public | BindingFlags.Static)?.GetValue(null, null)
            ?? throw new StepFailedException("Module.CurrentModule is null");
        // Module._subModuleBases maps each SubModuleInfo to its MBSubModuleBase: look at the dictionary's values.
        foreach (var field in module.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
        {
            var value = field.GetValue(current);
            var items = value is IDictionary map ? map.Values : value as IEnumerable;
            if (items == null || value is string) continue;
            foreach (var sub in items)
                if (sub?.GetType().FullName == "Coop.CoopMod") return sub;
        }
        throw new StepFailedException("Coop's submodule (Coop.CoopMod) is not loaded in this game");
    }
}
