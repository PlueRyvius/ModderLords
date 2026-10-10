using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using ModderLords.CompatSync;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.MountAndBlade;

namespace ModderLords.CompatSync.Coop.Fourberie;

/// <summary>
/// "Break free" in the captivity menu (FourbEscapeBehavior). Fourberie frees the player with PlayerCaptivity.EndCaptivity
/// and then starts the prison-break mission. In co-op that call frees the player on their own game only: Coop sends a
/// release to the server from EndCaptivityAction, which EndCaptivity does not go through, so the server kept the hero
/// in the dungeon whatever the mission's outcome.
///
/// Here the release also goes to the server, the way Coop's own captivity menus send it (EndCaptivityAction on a
/// player's game asks the server), right after Fourberie has freed the player locally. That matches single player,
/// where the player is free from the moment the attempt starts. Being caught then puts the player back in the dungeon
/// through the game's own "taken prisoner" menu, which Coop already carries to the server.
///
/// The server's answer makes Coop leave the captivity menus and end the settlement encounter. By then Fourberie has
/// started a new encounter for the mission, so that cleanup is skipped while the attempt runs; Fourberie's local
/// release has already done it.
/// </summary>
internal static class FbEscape
{
    internal const string EscapeBehavior = "Fourberie.FourbEscapeBehavior";
    internal const string CoopClientHandler = "GameInterface.Services.PlayerCaptivityService.Handlers.PlayerCaptivityClientHandler";

    /// <summary>Coop's two reactions to a release that end the encounter on the player's game.</summary>
    internal static readonly string[] CleanupMethods = { "ReleaseMainParty", "Handle_NetworkPlayerCaptivityEnded" };

    /// <summary>How long the cleanup is held off when the mission never starts (it normally starts within a second or two).</summary>
    private static readonly TimeSpan StartWindow = TimeSpan.FromSeconds(30);

    private static Assembly? _mod;
    private static bool _attempting, _sawMission, _asking;
    private static DateTime _started;
    private static long _attempts, _held;

    internal static void Bind(Assembly mod) => _mod = mod;

    /// <summary>Before Fourberie's EndCaptivity: is this the player's own break-free attempt in a co-op session?</summary>
    internal static void EndCaptivityPrefix(ref bool __state)
    {
        __state = !_asking && FourberieLayer.IsCoopClient && Hero.MainHero is { IsPrisoner: true } && FromMod();
    }

    /// <summary>
    /// After it: the player is free on their own game, so ask the server for the same. Asked afterwards because Coop's
    /// sender clears the local captor, which the game's own release still reads.
    /// </summary>
    internal static void EndCaptivityPostfix(bool __state)
    {
        if (!__state || Hero.MainHero is not { } hero) return;
        _attempting = true;
        _sawMission = false;
        _started = DateTime.UtcNow;
        _attempts++;
        Log.Info($"{FourberieLayer.Tag}escape: {hero.Name} is breaking free; asking the server to release them");
        _asking = true;
        try { EndCaptivityAction.ApplyByEscape(hero, null, false); }
        catch (Exception ex) { Log.Warn($"{FourberieLayer.Tag}escape: the release could not be sent to the server: {ex.GetBaseException().Message}"); }
        finally { _asking = false; }
    }

    /// <summary>Prefix on Coop's release cleanup: skipped while a break-free attempt is under way.</summary>
    internal static bool CleanupPrefix(MethodBase __originalMethod)
    {
        if (!Attempting()) return true;
        _held++;
        Log.Info($"{FourberieLayer.Tag}escape: held back Coop's {__originalMethod.Name} during the prison break");
        return false;
    }

    /// <summary>From "Break free" until the mission it starts has ended, or for <see cref="StartWindow"/> when none starts.</summary>
    private static bool Attempting()
    {
        if (!_attempting) return false;
        if (Mission.Current != null) { _sawMission = true; return true; }
        if (_sawMission || DateTime.UtcNow - _started > StartWindow) _attempting = false;
        return _attempting;
    }

    private static bool FromMod()
    {
        if (_mod == null) return false;
        var frames = new StackTrace(2, false).GetFrames();
        return frames != null && frames.Any(f => f.GetMethod()?.DeclaringType?.Assembly == _mod);
    }

    internal static string Summary() => _attempts > 0 ? $", break-free attempts {_attempts} (Coop cleanups held {_held})" : "";
}

internal sealed class FbEscapeComponent : IFbComponent
{
    private const string Owner = "ModderLords.Fourberie.Escape";
    private MethodInfo? _endCaptivity;
    private readonly List<MethodInfo> _cleanups = new List<MethodInfo>();
    private readonly List<MethodInfo> _breakFree = new List<MethodInfo>();

    public string Id => "escape";

    public string? SkipReason(FbContext context)
    {
        if (context.IsServer) return "server (Break free is a menu on a player's game)";
        _endCaptivity = AccessTools.Method(typeof(PlayerCaptivity), nameof(PlayerCaptivity.EndCaptivity));
        if (_endCaptivity == null) return "PlayerCaptivity.EndCaptivity not found";
        if (context.Type(FbEscape.EscapeBehavior) is not { } behavior) return FbEscape.EscapeBehavior + " not found";
        _breakFree.Clear();
        _breakFree.AddRange(FbGaps.Callers(behavior, _endCaptivity));
        if (_breakFree.Count == 0) return "no Break free option calls PlayerCaptivity.EndCaptivity (Fourberie changed?)";

        _cleanups.Clear();
        var handler = AccessTools.TypeByName(FbEscape.CoopClientHandler);
        foreach (var name in FbEscape.CleanupMethods)
            if (handler != null && AccessTools.Method(handler, name) is { } m) _cleanups.Add(m);
        return null;
    }

    public string Install(FbContext context)
    {
        FbEscape.Bind(context.Mod);
        var h = new Harmony(Owner);
        if (_cleanups.Count < FbEscape.CleanupMethods.Length)
        {
            // Without holding Coop's cleanup back the mission's encounter would be ended under it, so the option is
            // refused rather than left to free the player on their own game only.
            FbGaps.Bind(context.Mod);
            var block = new HarmonyMethod(typeof(FbGaps), nameof(FbGaps.BlockPrefix)) { priority = Priority.First };
            foreach (var m in _breakFree) h.Patch(m, prefix: block);
            return "Break free is not available in co-op with this Coop version (its captivity handler changed)";
        }

        h.Patch(_endCaptivity!,
            prefix: new HarmonyMethod(typeof(FbEscape), nameof(FbEscape.EndCaptivityPrefix)),
            postfix: new HarmonyMethod(typeof(FbEscape), nameof(FbEscape.EndCaptivityPostfix)));
        var hold = new HarmonyMethod(typeof(FbEscape), nameof(FbEscape.CleanupPrefix)) { priority = Priority.First };
        foreach (var m in _cleanups) h.Patch(m, prefix: hold);
        return $"Break free also releases the player on the server ({_breakFree.Count} option(s)); Coop's release cleanup is held during the mission";
    }
}
