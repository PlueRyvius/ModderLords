using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using ModderLords.CompatSync;
using ModderLords.CompatSync.Coop.Taom;
using TaleWorlds.Library;

namespace ModderLords.CompatSync.Coop;

/// <summary>
/// On a co-op player's game, Bellum Civile's Peace Parley is switched off and vanilla's peace vote is left alone.
///
/// <para>Peace in co-op is made by the kingdom vote: Coop runs it as a timed round, counts each player's vote on the
/// server and applies the result there. Bellum's war-and-peace revamp replaces every step of that on the game it runs
/// in. It retires the vanilla vote and opens its own parley, and Sign Treaty ends in
/// <c>MakePeaceAction</c>, which Coop refuses from a player's game ("awaiting server replication"). So the treaty was
/// signed on that one game and nowhere else.</para>
///
/// <para>Reported 2026-10-11 by a host and a player in the same kingdom, three ways: a forced parley ("Unconditional
/// Surrender") that both were shown and neither could leave; a war that stayed on after the treaty was signed twice;
/// and the kingdom screen opening on a peace vote over and over. The last is Bellum's "stale vanilla peace decision"
/// fallback: it hides the vote and removes the decision, Coop refuses the removal on a player's game, and the screen
/// finds the same decision again.</para>
///
/// <para>What players lose: Bellum's treaty terms (reparations, fiefs, marriages) for their own realm. The server's
/// Bellum keeps its parley code as it is; no kingdom there is "the player's", so nothing here applies to it. Its guard
/// on peace it did not make is a separate matter: see <see cref="BellumVotePeace"/>.</para>
/// </summary>
public static class BellumPeaceInCoop
{
    private static readonly Harmony Harmony = new Harmony("ModderLords.Compat.BellumPeaceInCoop");
    private static bool _installTried, _told;
    private static int _answered;

    /// <summary>A player's game in a co-op session. Bellum on its own, or on the server, is left exactly as it is.</summary>
    private static bool Active => !Operations.OperationProcessSide.IsServer && TaomActions.IsCoopClient;

    /// <summary>How many times Bellum was answered this session, for the verification line.</summary>
    public static int Answered => _answered;

    /// <summary>Patches the listed methods once Bellum is loaded. Safe to call every tick.</summary>
    public static void EnsureInstalled()
    {
        if (_installTried) return;
        _installTried = true;
        if (Operations.OperationProcessSide.IsServer) return;
        if (AccessTools.TypeByName(BellumPeaceTargets.Treaty) == null) return;   // Bellum is not loaded

        var missing = new List<string>();
        var patched = 0;
        foreach (var target in BellumPeaceTargets.All)
        {
            var type = AccessTools.TypeByName(target.Type);
            var methods = type == null
                ? new List<MethodInfo>()
                : type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                    .Where(m => m.Name == target.Method && Fits(m, target.Answer)).ToList();
            if (methods.Count == 0) { missing.Add(Short(target)); continue; }
            foreach (var method in methods)
            {
                try
                {
                    Harmony.Patch(method, prefix: new HarmonyMethod(typeof(BellumPeaceInCoop), PrefixFor(target.Answer)) { priority = Priority.First });
                    patched++;
                }
                catch (Exception ex) { missing.Add(Short(target) + " (" + ex.GetBaseException().Message + ")"); }
            }
        }
        Log.Info($"Bellum peace in co-op: on a player's game peace goes through the kingdom vote; Bellum's parley is off ({patched} method(s) answered)");
        if (missing.Count > 0)
            Log.Warn("Bellum peace in co-op: not found in this Bellum version, so its parley may still open: " + string.Join(", ", missing));
    }

    private static bool Fits(MethodInfo method, BellumPeaceAnswer answer) =>
        answer == BellumPeaceAnswer.Nothing ? method.ReturnType == typeof(void) : method.ReturnType == typeof(bool);

    private static string PrefixFor(BellumPeaceAnswer answer) => answer switch
    {
        BellumPeaceAnswer.No => nameof(No),
        BellumPeaceAnswer.RunOriginal => nameof(RunOriginal),
        _ => nameof(Nothing),
    };

    private static string Short(BellumPeaceTargets.Target target) => target.Type.Substring(target.Type.LastIndexOf('.') + 1) + "." + target.Method;

    private static bool No(ref bool __result)
    {
        if (!Active) return true;
        __result = false;
        Tell();
        return false;
    }

    private static bool RunOriginal(ref bool __result)
    {
        if (!Active) return true;
        __result = true;
        _answered++;
        return false;
    }

    private static bool Nothing()
    {
        if (!Active) return true;
        _answered++;
        return false;
    }

    /// <summary>Said once a session: Bellum would have opened a treaty screen here, and the player should know why not.</summary>
    private static void Tell()
    {
        _answered++;
        if (_told) return;
        _told = true;
        try
        {
            InformationManager.DisplayMessage(new InformationMessage(
                "Bellum Civile's peace parley is not available in co-op. Peace for your realm is decided by the kingdom vote."));
        }
        catch (Exception ex) { Log.Warn("Bellum peace in co-op: could not show the notice: " + ex.GetBaseException().Message); }
        Log.Info("Bellum peace in co-op: Bellum tried to open or queue a peace parley on this player's game; answered no (said once)");
    }
}
