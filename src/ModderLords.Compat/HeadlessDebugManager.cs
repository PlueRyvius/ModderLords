using System;
using TaleWorlds.Library;

namespace ModderLords.Compat;

/// <summary>
/// Wraps the <see cref="IDebugManager"/> the host already installed, so a blocking message box cannot end a headless
/// run.
///
/// Measured failure this exists for: with LOTRLOME_Armory enabled the server logged 104 "Could not find animation"
/// warnings, each followed by a `Messagebox [Always Ignore?] message: Would you like to always ignore this failure?`,
/// and then exited -1. Nobody can answer a message box on a dedicated server, so the question is unanswerable by
/// construction and the only outcomes are hang or abort.
///
/// It is a decorator, never a replacement. <c>DedicatedServer.Core.dll</c> installs its own manager and the official
/// host depends on it for crash reporting and logging, so every member forwards to <see cref="_inner"/>. Exactly one
/// is swallowed — <see cref="ShowMessageBox"/> — and it is logged the first time each mod triggers it, so the console
/// still shows what went wrong instead of the failure disappearing.
///
/// Deliberately left forwarding: <see cref="AbortGame"/>, <see cref="Assert"/> and <see cref="ShowError"/>. An abort
/// is a decision the engine has already made, and swallowing it would trade a clean exit for a hang. This guard makes
/// an unanswerable question non-fatal; it does not try to make a broken server keep running.
/// </summary>
internal sealed class HeadlessDebugManager : IDebugManager
{
    private readonly IDebugManager _inner;

    private HeadlessDebugManager(IDebugManager inner) => _inner = inner;

    /// <summary>
    /// Wraps whatever manager is installed. Returns null when there is none to decorate, or when it is already ours —
    /// installing twice would nest the wrapper on every module load.
    ///
    /// Installing once is not enough. Measured on a real run: this wraps MBDebugManager during OnSubModuleLoad, and
    /// about four thousand log lines later the host prints "managed DebugManager installed" and assigns its own,
    /// throwing the wrapper away long before the campaign loads. Whoever writes last wins, so <see cref="Reassert"/>
    /// re-wraps whenever something else has taken the property over.
    /// </summary>
    public static string? Install()
    {
        var current = Debug.DebugManager;
        if (current is null) return null;
        if (current is HeadlessDebugManager) return "already installed";
        Debug.DebugManager = new HeadlessDebugManager(current);
        return current.GetType().FullName;
    }

    /// <summary>
    /// Re-wraps if the property has been reassigned since. Returns the type it wrapped, or null when nothing needed
    /// doing — which is the case on all but a handful of frames, so this is an <c>isinst</c> and a return.
    /// </summary>
    public static string? Reassert()
    {
        var current = Debug.DebugManager;
        if (current is null || current is HeadlessDebugManager) return null;
        Debug.DebugManager = new HeadlessDebugManager(current);
        return current.GetType().FullName;
    }

    /// <summary>
    /// Stops an asset warning from ending the process.
    ///
    /// Measured: with LOTRLOME_Armory enabled the run logs 137 "Could not find animation" warnings and 68
    /// `Messagebox [Always Ignore?]` prompts, and exits -1 — and <b>none</b> of them reach
    /// <see cref="ShowMessageBox"/>. They are raised by the native rgl layer and printed straight to stdout, in the
    /// same `Messagebox [...] message:` shape as the loader's "Cannot load:" probes, so decorating the managed
    /// IDebugManager cannot see them. These two engine toggles are what decides whether the native side treats a
    /// warning as fatal, which is why they are here and not left as a note.
    ///
    /// Only ever called on a dedicated server, where the prompt is unanswerable by construction: there is no one to
    /// click "Always Ignore", so the choice is between continuing and exiting, not between ignoring and asking.
    /// </summary>
    public static string ReleaseNativeAssertions()
    {
        var done = new System.Collections.Generic.List<string>();
        try { TaleWorlds.Engine.Utilities.SetAssertionsAndWarningsSetExitCode(false); done.Add("assertions/warnings no longer set the exit code"); }
        catch (Exception ex) { done.Add("SetAssertionsAndWarningsSetExitCode failed: " + ex.GetBaseException().Message); }
        try { TaleWorlds.Engine.Utilities.SetCrashOnAsserts(false); done.Add("asserts no longer crash"); }
        catch (Exception ex) { done.Add("SetCrashOnAsserts failed: " + ex.GetBaseException().Message); }

        // Not crashing is not the same as not dumping. Measured 2026-09-11: a TAOM creation run raised 227 asset
        // warnings and the engine wrote a full ~540 MB crash report for each one -- 100 GB, one every 3.5 seconds,
        // which is the entire 15-minute creation budget spent on minidumps. The run never reached phase=armed.
        //
        // Deliberately narrow: only WARNINGS stop dumping. The blunter switches next door --
        // Utilities.SetDisableDumpGeneration and MBDebug.SetDumpGenerationDisabled -- also suppress the dump for a
        // genuine native crash, and we measured the cost of that: a 0xC0000005 access violation on a client join
        // (2026-09-11 23:49) left no dump to read. An asset warning is noise; an access violation is the evidence.
        // The crash-folder rotation in Preflight is what bounds the folder now, not blanket suppression.
        //
        // Both are public statics on the pinned reference assemblies; DebugManagerProbe prints their exact
        // signatures, so a game update that renames one shows up as a diff in that probe's output rather than as
        // a silent return to 100 GB. Wrapped separately: one missing toggle must not skip the other.
        try { TaleWorlds.Engine.Utilities.SetCreateDumpOnWarnings(false); done.Add("warnings no longer write a dump"); }
        catch (Exception ex) { done.Add("SetCreateDumpOnWarnings failed: " + ex.GetBaseException().Message); }
        try { TaleWorlds.Engine.Utilities.SetCrashOnWarnings(false); done.Add("warnings no longer crash"); }
        catch (Exception ex) { done.Add("SetCrashOnWarnings failed: " + ex.GetBaseException().Message); }

        return string.Join("; ", done);
    }

    /// <summary>The one interception: log who asked, then carry on without the modal.</summary>
    public void ShowMessageBox(string lpText, string lpCaption, uint uType)
    {
        Guards.AnnounceExternal("Debug.ShowMessageBox");
        Log.Warn($"message box suppressed (no one can answer it on a server): {lpCaption}: {lpText}");
    }

    // ---- everything else forwards, unchanged ---------------------------------------------------------

    public void ShowWarning(string message) => _inner.ShowWarning(message);
    public void ShowError(string message) => _inner.ShowError(message);

    /// <summary>On unless MODDERLORDS_ASSERT_THROTTLE=0.</summary>
    private static readonly bool ThrottleEnabled = Environment.GetEnvironmentVariable("MODDERLORDS_ASSERT_THROTTLE")?.Trim() != "0";
    private static readonly AssertThrottlePolicy Throttle = new AssertThrottlePolicy();

    /// <summary>The 30-second summary line for asserts held back since the last call, or null.</summary>
    public static string? TakeThrottleSummary() => ThrottleEnabled ? Throttle.TakeSummary() : null;

    /// <summary>
    /// A failed assert that repeats at the same site is forwarded only occasionally. See <see cref="AssertThrottlePolicy"/>
    /// for the measured storm this stops; the first occurrences of every site still reach the host unchanged.
    /// </summary>
    public void Assert(bool condition, string message, string callerFile, string callerMethod, int callerLine)
    {
        if (!condition && ThrottleEnabled)
        {
            if (!Throttle.ShouldForward(AssertThrottlePolicy.KeyFor(callerFile, callerMethod, callerLine), out var total)) return;
            if (total > AssertThrottlePolicy.ForwardFirst) message = $"{message} [repeat #{total:N0}; repeats are held back, see ModderLords.Compat summary]";
        }
        _inner.Assert(condition, message, callerFile, callerMethod, callerLine);
    }
    /// <summary>
    /// Forwarded, but never with <c>getDump</c>. A silent assert that asks for a dump costs ~540 MB and several
    /// seconds; an asset-warning storm turns that into the whole run. The assert itself still reaches the host and
    /// is still logged — only the minidump is declined. See <see cref="ReleaseNativeAssertions"/>, which handles
    /// the native side; this covers the managed one.
    /// </summary>
    public void SilentAssert(bool condition, string message, bool getDump, string callerFile, string callerMethod, int callerLine)
    {
        if (getDump) Guards.AnnounceExternal("Debug.SilentAssert(getDump)");
        if (!condition && ThrottleEnabled && !Throttle.ShouldForward(AssertThrottlePolicy.KeyFor(callerFile, callerMethod, callerLine), out _)) return;
        _inner.SilentAssert(condition, message, getDump: false, callerFile, callerMethod, callerLine);
    }
    public void Print(string message, int debugFilter, Debug.DebugColor color, ulong debugColor)
        => _inner.Print(message, debugFilter, color, debugColor);
    public void PrintError(string error, string stackTrace, ulong debugFilter) => _inner.PrintError(error, stackTrace, debugFilter);
    public void PrintWarning(string warning, ulong debugFilter) => _inner.PrintWarning(warning, debugFilter);
    public void DisplayDebugMessage(string message) => _inner.DisplayDebugMessage(message);
    public void WatchVariable(string name, object value) => _inner.WatchVariable(name, value);
    public void WriteDebugLineOnScreen(string message) => _inner.WriteDebugLineOnScreen(message);
    public void RenderDebugLine(Vec3 position, Vec3 direction, uint color, bool depthCheck, float time)
        => _inner.RenderDebugLine(position, direction, color, depthCheck, time);
    public void RenderDebugSphere(Vec3 position, float radius, uint color, bool depthCheck, float time)
        => _inner.RenderDebugSphere(position, radius, color, depthCheck, time);
    public void RenderDebugText3D(Vec3 position, string text, uint color, int screenPosOffsetX, int screenPosOffsetY, float time)
        => _inner.RenderDebugText3D(position, text, color, screenPosOffsetX, screenPosOffsetY, time);
    public void RenderDebugFrame(MatrixFrame frame, float lineLength, float time) => _inner.RenderDebugFrame(frame, lineLength, time);
    public void RenderDebugText(float screenX, float screenY, string text, uint color, float time)
        => _inner.RenderDebugText(screenX, screenY, text, color, time);
    public void RenderDebugRectWithColor(float left, float bottom, float right, float top, uint color)
        => _inner.RenderDebugRectWithColor(left, bottom, right, top, color);
    public Vec3 GetDebugVector() => _inner.GetDebugVector();
    public void SetDebugVector(Vec3 vec) => _inner.SetDebugVector(vec);
    public void SetCrashReportCustomString(string customString) => _inner.SetCrashReportCustomString(customString);
    public void SetCrashReportCustomStack(string customStack) => _inner.SetCrashReportCustomStack(customStack);
    public void SetTestModeEnabled(bool testModeEnabled) => _inner.SetTestModeEnabled(testModeEnabled);
    public void AbortGame() => _inner.AbortGame();
    public void DoDelayedexit(int returnCode) => _inner.DoDelayedexit(returnCode);
    public void ReportMemoryBookmark(string message) => _inner.ReportMemoryBookmark(message);
}
