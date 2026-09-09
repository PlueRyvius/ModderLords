using System;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;

namespace ModderLords.Compat;

/// <summary>
/// Starts a NEW campaign inside the dedicated server, saves it, and exits — so a modded server can obtain a
/// world containing its own mods' objects.
///
/// Why this is needed: the server can only ever *load* a save, and the only world it can make for itself is a
/// copy of the vanilla <c>default_new_game.sav</c>. Loading a world that predates the mods is what produces
/// the failures measured on 2026-09-08 — 3,838 <c>Null object reference found with ID: &lt;TAOM item&gt;</c>
/// lines, then a <c>NullReferenceException</c> in <c>NavigationCache.Deserialize</c> repeated 159,872 times
/// over 2h15m. A world generated with the mods loaded gets its objects from module XML instead of from a
/// save, which is the hypothesis this exists to test.
///
/// Nothing here is patched or rewritten. The whole path is public API:
/// <c>MBGameManager.StartNewGame(new SandBoxGameManager(creator))</c>, then
/// <c>Campaign.Current.SaveHandler.SaveAs(name)</c>. Only <c>SandBoxGameManager</c> itself needs reflection,
/// because <c>SandBox.dll</c> is not in the reference-assembly package this module compiles against — the
/// same resolve-by-name, skip-if-absent idiom <see cref="Guards"/> uses throughout.
/// </summary>
internal sealed class WorldCreator
{
    private readonly CreateWorldPolicy _policy;
    private bool _saveRequested;
    private long _lastSize = -1;
    private DateTime _sizeStableSince = DateTime.MaxValue;
    private int _readyTicks;

    /// <summary>The save must stop growing for this long before it is considered flushed to disk.</summary>
    private static readonly TimeSpan SizeStableFor = TimeSpan.FromSeconds(3);

    /// <summary>Consecutive ticks the campaign must look ready before we believe it.</summary>
    private const int ReadyTicksRequired = 5;

    internal WorldCreator(CreateWorldPolicy policy) => _policy = policy;

    internal bool IsEnabled => _policy.IsEnabled;

    /// <summary>Reads the mode from the environment. Returns null when it was not asked for, which is the norm.</summary>
    internal static WorldCreator TryCreate()
    {
        var policy = CreateWorldPolicy.FromEnvironment(Environment.GetEnvironmentVariable);
        return new WorldCreator(policy);
    }

    internal void Arm()
    {
        // Fresh file per run: the harness reads the newest run, not an accumulation of every attempt.
        try { File.Delete(SidecarPath()); } catch { }
        Say(_policy.Arm(DateTime.UtcNow));
    }

    /// <summary>
    /// Drives the whole thing from the module tick. Every branch is cheap and the first line returns
    /// immediately when the mode is off, which is every normal server launch.
    /// </summary>
    internal void Tick()
    {
        if (!_policy.IsEnabled || !_policy.IsArmed) return;
        if (_policy.Current == CreateWorldPolicy.Phase.Saved || _policy.Current == CreateWorldPolicy.Phase.Failed) return;

        var now = DateTime.UtcNow;
        if (_policy.HasTimedOut(now)) { Finish(_policy.TimeoutMessage(now)); return; }

        try
        {
            switch (_policy.Current)
            {
                case CreateWorldPolicy.Phase.Armed:
                    if (_policy.ShouldStart(now, GameIsRunning())) Start(now);
                    else if (_policy.Current == CreateWorldPolicy.Phase.Failed) Finish(null);
                    break;

                case CreateWorldPolicy.Phase.Starting:
                    if (Campaign.Current != null) Say(_policy.Advance(CreateWorldPolicy.Phase.CampaignCreated, now));
                    break;

                case CreateWorldPolicy.Phase.CampaignCreated:
                    if (MapIsReady()) { _readyTicks++; } else { _readyTicks = 0; }
                    if (_readyTicks >= ReadyTicksRequired) Say(_policy.Advance(CreateWorldPolicy.Phase.MapReady, now));
                    break;

                case CreateWorldPolicy.Phase.MapReady:
                    BeginSave(now);
                    break;

                case CreateWorldPolicy.Phase.Saving:
                    CheckSaveFinished(now);
                    break;
            }
        }
        catch (Exception ex)
        {
            Finish(_policy.Fail(DateTime.UtcNow, ex.GetBaseException().Message));
        }
    }

    // ---- engine facts -------------------------------------------------------------------------------

    /// <summary>True when the host has already started a game; we add a world, we never race one.</summary>
    private static bool GameIsRunning()
    {
        try { return Game.Current != null || Campaign.Current != null; }
        catch { return false; }
    }

    /// <summary>
    /// The campaign has finished loading when the active game state is the map. Matched by type name rather
    /// than type, because MapState lives in SandBox which this assembly cannot reference.
    /// </summary>
    private static bool MapIsReady()
    {
        try
        {
            if (Campaign.Current == null) return false;
            var state = Game.Current?.GameStateManager?.ActiveState;
            return state != null && state.GetType().Name == "MapState";
        }
        catch { return false; }
    }

    // ---- the reflection shim ------------------------------------------------------------------------

    /// <summary>
    /// Builds a <c>SandBoxGameManager</c> around a campaign creator and hands it to the engine. The ctor is
    /// found by looking for the one whose single parameter is a delegate, rather than by hardcoding the
    /// nested delegate's name, so a rename in SandBox.dll degrades to a clear message instead of a miss.
    /// </summary>
    private void Start(DateTime now)
    {
        Say(_policy.Advance(CreateWorldPolicy.Phase.Starting, now));

        var gmType = AccessTools.TypeByName("SandBox.SandBoxGameManager");
        if (gmType == null) { Finish(_policy.Fail(now, "SandBox.SandBoxGameManager not found")); return; }

        var ctor = gmType.GetConstructors()
            .Select(c => new { Ctor = c, Params = c.GetParameters() })
            .Where(x => x.Params.Length == 1 && typeof(Delegate).IsAssignableFrom(x.Params[0].ParameterType))
            .Select(x => x.Ctor)
            .FirstOrDefault();
        if (ctor == null)
        {
            Finish(_policy.Fail(now, "no SandBoxGameManager ctor takes a campaign-creator delegate"));
            return;
        }

        var delegateType = ctor.GetParameters()[0].ParameterType;
        var invoke = delegateType.GetMethod("Invoke");
        // Logged unconditionally: if the bind below fails, this line is what says why.
        Say($"worldcreate: creator delegate {delegateType.FullName} " +
                 $"({string.Join(", ", invoke.GetParameters().Select(p => p.ParameterType.Name).ToArray())}) -> {invoke.ReturnType.Name}");

        Delegate creator;
        try
        {
            var factory = typeof(WorldCreator).GetMethod(nameof(CreateCampaign), BindingFlags.NonPublic | BindingFlags.Static);
            creator = Delegate.CreateDelegate(delegateType, factory);
        }
        catch (Exception ex)
        {
            Finish(_policy.Fail(now, "cannot bind the campaign creator: " + ex.GetBaseException().Message));
            return;
        }

        var manager = ctor.Invoke(new object[] { creator }) as MBGameManager;
        if (manager == null) { Finish(_policy.Fail(now, "SandBoxGameManager is not an MBGameManager")); return; }

        MBGameManager.StartNewGame(manager);
    }

    /// <summary>
    /// The campaign the engine will run. Bound as the creator delegate above, whose signature is
    /// <c>Campaign Invoke()</c> — no parameters, confirmed from SandBox.dll's metadata. <c>Campaign</c> is
    /// public and compile-time available, so only the delegate type around it needs reflection.
    /// </summary>
    private static Campaign CreateCampaign() => new Campaign(CampaignGameMode.Campaign);

    // ---- saving -------------------------------------------------------------------------------------

    /// <summary>
    /// <c>SaveHandler.SaveAs</c> rather than <c>MBSaveLoad.SaveAsCurrentGame</c>: the latter's completion
    /// callback takes a ValueTuple, which net472 cannot bind without an extra package reference this module
    /// deliberately does not have. Polling the file for a stable size is both dependency-free and a better
    /// guarantee anyway — it proves the bytes reached the disk rather than that a callback ran.
    /// </summary>
    private void BeginSave(DateTime now)
    {
        Say(_policy.Advance(CreateWorldPolicy.Phase.Saving, now, _policy.SaveName));
        _saveRequested = true;
        _lastSize = -1;
        _sizeStableSince = DateTime.MaxValue;
        Campaign.Current.SaveHandler.SaveAs(_policy.SaveName);
    }

    private void CheckSaveFinished(DateTime now)
    {
        if (!_saveRequested) return;
        var path = SavePath(_policy.SaveName);
        if (!File.Exists(path)) return;

        long size;
        try { size = new FileInfo(path).Length; } catch { return; }
        if (size <= 0) return;

        if (size != _lastSize) { _lastSize = size; _sizeStableSince = now; return; }
        if (now - _sizeStableSince < SizeStableFor) return;

        Say(_policy.Advance(CreateWorldPolicy.Phase.Saved, now));
        Finish(_policy.Succeeded(path, size));
    }

    /// <summary>
    /// Where the engine's save driver puts a save. BANNERLORD_USER_DIR is set by the launcher and is what
    /// points the server at its own Game Saves rather than the player's.
    /// </summary>
    private static string SavePath(string name)
    {
        var dir = Environment.GetEnvironmentVariable("BANNERLORD_USER_DIR");
        if (string.IsNullOrEmpty(dir))
            dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                               "Mount and Blade II Bannerlord");
        return Path.Combine(dir, "Game Saves", name + ".sav");
    }

    // ---- finishing ----------------------------------------------------------------------------------

    /// <summary>
    /// Where the phase lines are mirrored. The engine writes to the same stdout without newline discipline
    /// and splices our lines in half -- a real run produced "worldcreate: phase=sta" -- so the contract the
    /// harness checks cannot live on stdout alone. Fixed name: the harness reads it without coordination.
    /// </summary>
    internal static string SidecarPath()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                               "ModderLords", "logs");
        return Path.Combine(dir, "worldcreate-latest.log");
    }

    private static void Say(string line)
    {
        if (line == null) return;
        Log.Info(line);
        try
        {
            var path = SidecarPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.AppendAllText(path, DateTime.Now.ToString("HH:mm:ss.fff") + "  " + line + Environment.NewLine);
        }
        catch { /* the sidecar is a convenience; never let it break the run */ }
    }

    /// <summary>
    /// Ends the run deliberately. The exit code distinguishes "created, this stop was on purpose" from a
    /// crash, which matters because the launcher otherwise treats every non-zero code as a failure.
    /// </summary>
    private void Finish(string finalLine)
    {
        Say(finalLine);
        var code = _policy.ExitCode;
        Say($"worldcreate: exiting with {code}");
        try { Console.Out.Flush(); } catch { }
        Environment.Exit(code);
    }
}
