using System;
using System.IO;
using System.Reflection;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;
using TaleWorlds.SaveSystem;
using TaleWorlds.Library;
using TaleWorlds.ModuleManager;

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
/// WorldCreationHooks routes the initialized host's load request through Coop's StartNewGame API and
/// finalizes a seed character without UI. This class verifies readiness, saves, and reports completion.
/// Installed binaries are never rewritten; the routing patches live only in a creation process.
/// </summary>
internal sealed class WorldCreator
{
    private readonly CreateWorldPolicy _policy;
    private bool _saveRequested;
    private volatile bool _saveCompleted;
    private bool _saveSucceeded;
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
        if (_policy.IsArmed) return; // The engine can call the initial-screen hook on multiple frames.
        // Fresh file per run: the harness reads the newest run, not an accumulation of every attempt.
        try { File.Delete(SidecarPath()); } catch { }
        Say(_policy.Arm(DateTime.UtcNow));
        try { WorldCreationHooks.Install(this); }
        catch (Exception ex) { Finish(_policy.Fail(DateTime.UtcNow, "unsupported world-creation route: " + ex.GetBaseException().Message)); }
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
                    // Wait for the official host to initialize its container, save driver, native guards,
                    // parallel driver and loading loop. Intercept only its final LoadGame request.
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

    internal void StartFromHost(object gameStateInterface, MethodInfo startNewGame)
    {
        var now = DateTime.UtcNow;
        if (!_policy.ShouldStart(now, GameIsRunning())) { Finish(_policy.Fail(now, "host already started a game")); return; }
        Say(_policy.Advance(CreateWorldPolicy.Phase.Starting, now, "official server initialized; redirecting LoadGame to StartNewGame"));
        try { WorldCreationHooks.AllowWorldInitialization(); startNewGame.Invoke(gameStateInterface, null); }
        catch (Exception ex) { Finish(_policy.Fail(now, ex.GetBaseException().ToString())); }
    }

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

    // ---- saving -------------------------------------------------------------------------------------

    /// <summary>
    /// Uses the host's direct save path: its native autosave guard suppresses SaveHandler.SetSaveArgs.
    /// Require both a successful completion callback and stable, nonempty output.
    /// </summary>
    private void BeginSave(DateTime now)
    {
        if (File.Exists(SavePath(_policy.SaveName)))
            throw new IOException("Save already exists; refusing to overwrite: " + _policy.SaveName);
        Say(_policy.Advance(CreateWorldPolicy.Phase.Saving, now, _policy.SaveName));
        _saveRequested = true;
        _lastSize = -1;
        _sizeStableSince = DateTime.MaxValue;
        var metadataMethod = typeof(MBSaveLoad).GetMethod("GetSaveMetaData",
            BindingFlags.Static | BindingFlags.NonPublic, null, new[] { typeof(CampaignSaveMetaDataArgs) }, null);
        if (metadataMethod == null || metadataMethod.ReturnType != typeof(MetaData))
            throw new NotSupportedException("Unsupported MBSaveLoad.GetSaveMetaData(CampaignSaveMetaDataArgs) signature");
        var metadata = (MetaData)metadataMethod.Invoke(null, new object[] { Campaign.Current.SaveHandler.GetSaveMetaData() });
        // The host also repairs empty headless version fields from the installed Native module.
        var nativeVersion = ModuleHelper.GetModuleInfo("Native")?.Version ?? ApplicationVersion.Empty;
        if (nativeVersion.Major <= 0) throw new NotSupportedException("Native module version is unavailable");
        foreach (var key in new[] { "ApplicationVersion", "NewGameVersion" })
            if (ApplicationVersion.FromString(metadata[key]).Major <= 0) metadata[key] = nativeVersion.ToString();
        CampaignEventDispatcher.Instance.OnBeforeSave();
        Game.Current.Save(metadata, _policy.SaveName, new AsyncFileSaveDriver(), result =>
        {
            _saveSucceeded = (int)result == 0;
            Log.Info("worldcreate: save callback=" + result);
            _saveCompleted = true;
        });
    }

    private void CheckSaveFinished(DateTime now)
    {
        if (!_saveRequested || !_saveCompleted) return;
        if (!_saveSucceeded) throw new IOException("Engine save callback reported failure");
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
        var requested = Environment.GetEnvironmentVariable("MODDERLORDS_CREATE_WORLD_LOG");
        if (!string.IsNullOrWhiteSpace(requested)) return Path.GetFullPath(requested);
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                               "ModderLords", "logs");
        return Path.Combine(dir, "worldcreate-latest.log");
    }

    private static void Say(string? line)
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
        WorldCreationHooks.Finish();
        var code = _policy.ExitCode;
        Say($"worldcreate: exiting with {code}");
        try { Console.Out.Flush(); } catch { }
        Environment.Exit(code);
    }
}
