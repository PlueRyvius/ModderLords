using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using TaleWorlds.Library;

namespace ModderLords.Testing;

/// <summary>
/// Start-up errors are failures, even when the mod started (docs/INGAME-TEST-HARNESS.md, decision 25). In a test run
/// every on-screen message and pop-up is kept from the moment ModderLords.Compat is constructed, and <c>game.noErrors</c>
/// fails on those that look like errors. Modular Smithing 2 v0.9.49 showed "catalog load failed" on screen while its
/// patches and behaviours were in place, so <c>mods.started</c> passed.
/// </summary>
internal static class ErrorSteps
{
    private static readonly object Gate = new object();
    private static readonly List<string> Seen = new List<string>();
    private static bool _armed;
    /// <summary>How many messages the last check covered, for a later check that looks at new ones only.</summary>
    private static int _checked;
    private static DateTime _nextArm;

    internal static void Register()
    {
        InGameTests.Register("game.noErrors", StepSide.Any, NoErrors);
        InGameTests.Register("game.showMessage", StepSide.Any, ShowMessage);
        InGameTests.Register("game.sawMessage", StepSide.Any, SawMessage);
    }

    /// <summary>
    /// Waits until an on-screen message or pop-up containing <c>text</c> has been shown on this game at any time in the
    /// run (messages are kept from the start, so one shown while the game joined counts), and reports it. For a mod
    /// that must tell the player something: "that is off in co-op", say.
    /// </summary>
    private static IEnumerable<StepWait?> SawMessage(StepContext ctx)
    {
        var text = ctx.Arg("text");
        string? found = null;
        yield return ctx.Until(() =>
        {
            lock (Gate) found = Seen.FirstOrDefault(m => m.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0);
            return found != null;
        }, $"a message containing '{text}'", TimeSpan.FromSeconds(ctx.ArgInt("timeoutSeconds", 30)));
        ctx.Report("value", found);
    }

    /// <summary>Shows <c>text</c> as an on-screen message, as a mod does; for checking game.noErrors itself.</summary>
    private static IEnumerable<StepWait?> ShowMessage(StepContext ctx)
    {
        InformationManager.DisplayMessage(new InformationMessage(ctx.Arg("text")));
        yield break;
    }

    /// <summary>
    /// Listens to the game's message and pop-up events. Safe to call every tick: InformationManager.Clear() drops every
    /// listener, so this re-adds itself once a second.
    /// </summary>
    internal static void Arm(DateTime now)
    {
        AnswerKnown();
        if (_armed && now < _nextArm) return;
        _armed = true;
        _nextArm = now.AddSeconds(1);
        InformationManager.DisplayMessageInternal -= OnMessage;
        InformationManager.DisplayMessageInternal += OnMessage;
        InformationManager.OnShowInquiry -= OnInquiry;
        InformationManager.OnShowInquiry += OnInquiry;
        InformationManager.OnShowTextInquiry -= OnTextInquiry;
        InformationManager.OnShowTextInquiry += OnTextInquiry;
    }

    private static void Add(string kind, string? text)
    {
        lock (Gate) Seen.Add($"{DateTime.Now:HH:mm:ss} {kind}: {text}");
    }

    private static void OnMessage(InformationMessage message) => Add("message", message?.Information);
    private static void OnInquiry(InquiryData data, bool pause, bool prioritize)
    {
        Add("pop-up", $"{data?.TitleText}: {data?.Text}");
        if (data != null && (IsKnown(data) || IsNotice(data)))
            lock (Gate) Pending.Add(data);
    }

    /// <summary>
    /// Pop-ups a mod shows that a test run answers by itself: they tell or ask the player something about their own
    /// setup, and left open they pause the game. Matched on the title and the start of the text. Answered with the
    /// negative button when there is one, else the only one. TAOM 2.0.41 shows the first three together as a game loads.
    /// </summary>
    private static readonly (string Title, string Text)[] KnownPopups =
    {
        ("TAOM: battle performance settings", ""),      // "Keep mine": leaves the ragdoll and corpse options as they are
        ("TAOM: the last game ended unexpectedly", ""), // the run closed the last game without TAOM's clean exit
        ("TAOM", "TAOM.Dependencies already provides"), // "Ok": the profile also loads the Workshop Harmony, ButterLib, ...
    };

    internal static bool IsKnown(InquiryData data) => KnownPopups.Any(k =>
        (data.TitleText ?? "").StartsWith(k.Title, StringComparison.OrdinalIgnoreCase)
        && (data.Text ?? "").StartsWith(k.Text, StringComparison.Ordinal));

    /// <summary>
    /// A notice: one button and nothing to choose (TAOM 2.0.41 shows one per new feature, "Hidden Hosts" as a battle
    /// ends). Left open it pauses the game, so a run closes it as the player would; it stays in the messages all the same.
    /// The one button can be either: Retinues' "Version Change" notice shows only its negative button, as "Ok".
    /// </summary>
    internal static bool IsNotice(InquiryData data) => data.IsNegativeOptionShown != data.IsAffirmativeOptionShown;

    /// <summary>Known pop-ups and notices shown and not yet answered, oldest first.</summary>
    private static readonly List<InquiryData> Pending = new List<InquiryData>();

    /// <summary>
    /// The pop-up on screen: the game shows one at a time and queues the rest (GauntletQueryManager._activeQueryData,
    /// a static). Null where that is not found (a server has no Gauntlet UI): then the oldest pending one is taken as shown.
    /// </summary>
    private static readonly Lazy<System.Reflection.FieldInfo?> ActiveField = new Lazy<System.Reflection.FieldInfo?>(() => AppDomain.CurrentDomain.GetAssemblies()
        .Where(a => a.GetName().Name == "TaleWorlds.MountAndBlade.GauntletUI")
        .Select(a => a.GetType("TaleWorlds.MountAndBlade.GauntletUI.GauntletQueryManager", false))
        .FirstOrDefault(t => t != null)
        ?.GetField("_activeQueryData", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic));

    /// <summary>
    /// Answers a known pop-up once it is the one on screen. HideInquiry closes whatever is showing, so answering one that
    /// is still queued behind another would close the wrong one. One per tick; the next one shows after it.
    /// </summary>
    private static void AnswerKnown()
    {
        InquiryData? data;
        lock (Gate)
        {
            var active = ActiveField.Value;
            if (active == null)
            {
                if (Pending.Count == 0) return;
                data = Pending[0];
                Pending.RemoveAt(0);
            }
            else
            {
                // What is on screen, even one the listener missed: InformationManager.Clear() drops the listener, and a
                // pop-up shown before it is re-added a second later is never reported. Retinues' "Version Change"
                // notice, shown as a world loads, stayed open that way and kept campaign time paused (Retinues suites,
                // 2026-11-22).
                data = active.GetValue(null) as InquiryData;
                if (data == null) return;
                if (!Pending.Remove(data))
                {
                    if (!IsKnown(data) && !IsNotice(data)) return;
                    Add("pop-up", $"{data.TitleText}: {data.Text}");
                }
            }
        }
        try
        {
            InformationManager.HideInquiry();
            var negative = data.IsNegativeOptionShown && data.NegativeAction != null;
            if (negative) data.NegativeAction!.Invoke();
            else data.AffirmativeAction?.Invoke();
            Add("answered", $"{data.TitleText}: {(negative ? data.NegativeText : data.AffirmativeText)}");
        }
        catch (Exception ex) { Add("answer failed", $"{data.TitleText}: {ex.GetType().Name} {ex.Message}"); }
    }
    private static void OnTextInquiry(TextInquiryData data, bool pause, bool prioritize) => Add("text pop-up", $"{data?.TitleText}: {data?.Text}");

    /// <summary>
    /// Fails on any message or pop-up so far that matches an error pattern: the default set plus <c>patterns</c> (a
    /// JSON array). With <c>new: true</c>, only messages since the last check (the run's closing check, after the core
    /// suite's). Writes every message to <c>messages-&lt;side&gt;.txt</c>; the game's own error log
    /// (<c>rgl_log_errors_&lt;pid&gt;.txt</c>) and the last lines of its log as evidence only: vanilla's asserts make
    /// the error log too noisy to fail on.
    /// </summary>
    private static IEnumerable<StepWait?> NoErrors(StepContext ctx)
    {
        List<string> seen;
        lock (Gate)
        {
            seen = Seen.Skip(ctx.Arg("new", "false") == "true" ? Math.Min(_checked, Seen.Count) : 0).ToList();
            _checked = Seen.Count;
        }
        var patterns = ErrorPatterns.From(ctx.Arg("patterns", ""));
        var errors = ErrorPatterns.Matches(seen, patterns);
        var side = ctx.Side.Replace(':', '-');
        ctx.Dump("messages-" + side, seen.Count == 0 ? "(no messages)" : string.Join(Environment.NewLine, seen));
        var logs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Mount and Blade II Bannerlord", "logs");
        var pid = Process.GetCurrentProcess().Id;
        if (Read(Path.Combine(logs, $"rgl_log_errors_{pid}.txt"), ctx) is { } errorLog) ctx.Dump("game-errors-" + side, errorLog);
        if (Read(Path.Combine(logs, $"rgl_log_{pid}.txt"), ctx) is { } gameLog)
            ctx.Dump("game-log-tail-" + side, string.Join(Environment.NewLine, gameLog.Split('\n').Reverse().Take(60).Reverse()));
        ctx.Report("messages", seen.Count);
        ctx.Report("errors", errors.Count);
        ctx.Report("value", errors.Count == 0 ? "none" : string.Join(" | ", errors));
        foreach (var e in errors.Take(10)) ctx.Log(e);
        ctx.Assert(errors.Count == 0, $"{errors.Count} on-screen error(s), first: {errors.FirstOrDefault()}");
        yield break;
    }

    /// <summary>A log the game still has open, or null (said in the step's log) when it cannot be read.</summary>
    private static string? Read(string path, StepContext ctx)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream, Encoding.UTF8))
                return reader.ReadToEnd();
        }
        catch (Exception ex) { ctx.Log($"could not read {Path.GetFileName(path)}: {ex.Message}"); return null; }
    }
}
