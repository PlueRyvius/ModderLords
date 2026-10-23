using System;
using System.Diagnostics;
using System.Reflection;

namespace ModderLords.CompatSync.Coop.Fourberie;

/// <summary>
/// Whether a world change on a player's game is one to record for the server (fourb-effects): it must come from the
/// mod's code (a mod frame on the stack), not from code the server already runs for the player (a fanned-out handler,
/// flagged by <see cref="ServerRunPrefix"/>: recording it would apply the change twice), and not from inside another
/// recorded call (the server's replay of the outer call makes the inner change itself). Relayed methods never run on a
/// player's game, so they need no flag.
///
/// Free of game and Coop types so the tests can drive it with real Harmony and a stand-in mod.
/// </summary>
public static class FbRecordGate
{
    [ThreadStatic] private static int _depth;
    [ThreadStatic] private static int _serverRun;

    /// <summary>The mod's assembly; null = never record.</summary>
    public static Assembly? Mod { get; set; }

    /// <summary>True on a player's game in a co-op session; null = never record.</summary>
    public static Func<bool>? IsCoopClient { get; set; }

    /// <summary>
    /// Prefix/finalizer pair for every mod method the server runs itself (fanned-out handlers): while one runs on this
    /// thread nothing is recorded. A flag rather than a stack check, because Harmony's replacement for a patched method
    /// does not show in a stack trace at all.
    /// </summary>
    public static void ServerRunPrefix() => _serverRun++;

    public static void ServerRunFinalizer()
    {
        if (_serverRun > 0) _serverRun--;
    }

    public static bool Recording => _depth > 0;

    /// <summary>
    /// Call from a prefix. True: record this call, and call <see cref="Leave"/> when it ends (a finalizer). False: do not
    /// record, and do not call Leave.
    /// </summary>
    public static bool TryEnter()
    {
        if (_depth > 0 || _serverRun > 0 || Mod == null || IsCoopClient?.Invoke() != true || !FromMod()) return false;
        _depth++;
        return true;
    }

    public static void Leave()
    {
        if (_depth > 0) _depth--;
    }

    /// <summary>A frame of the mod's own code is on the stack.</summary>
    private static bool FromMod()
    {
        var frames = new StackTrace(2, false).GetFrames();
        if (frames == null) return false;
        foreach (var frame in frames)
            if (frame.GetMethod()?.DeclaringType?.Assembly == Mod) return true;
        return false;
    }
}
