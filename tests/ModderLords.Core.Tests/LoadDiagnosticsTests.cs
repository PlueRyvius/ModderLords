using System.Text;
using ModderLords.Core.Logs;
using ModderLords.Core.Saves;
using Xunit;

namespace ModderLords.Core.Tests;

/// <summary>
/// The three things that made the TAOM stall take two blind five-minute runs to diagnose: the save/module mismatch
/// nobody was told about, the loop nobody noticed, and the log lines that were filed as routine chatter.
/// Line texts here are copied verbatim from modderlords-launch-20260907-045345.log and -043149.log.
/// </summary>
public class LogClassifierGapTests
{
    [Theory]
    // The 104 asset failures that ended a run: filed as Engine before, so they never showed in a quiet-engine console.
    [InlineData("rgl_post_warning_line: RGL WARNING - Could not find animation: howdah_stand_bow.", LogCategory.Warning)]
    [InlineData("[04:31:57.489] Messagebox [Always Ignore?] message: Would you like to always ignore this failure?", LogCategory.Warning)]
    // The line that says the world does not match the modules loading it.
    [InlineData("[Coop] Save \"saveauto1\" module mismatch: \"Bannerlord.Harmony\" (ModuleRemovedFromGame). Forcing load anyway.", LogCategory.Warning)]
    // A swallowed crash must not change category with the exception it happens to name.
    [InlineData("[DedicatedServer] inquiry auto-accepted: \"TAOM caught a crash\" - Exception: System.NullReferenceException", LogCategory.Error)]
    [InlineData("[DedicatedServer] inquiry auto-accepted: \"TAOM caught a crash\" - Exception: System.IO.IOException", LogCategory.Error)]
    public void Classifies_the_lines_a_stalled_load_is_diagnosed_from(string line, LogCategory expected)
        => Assert.Equal(expected, LogClassifier.Classify(line).Category);

    [Fact]
    public void Dotted_namespace_exceptions_are_errors()
        => Assert.Equal(LogCategory.Error, LogClassifier.Classify("Unhandled System.IO.IOException in tick").Category);

    [Theory]
    // The eager AssemblyLoader probe misses stay noise, and successful-run milestones stay milestones.
    [InlineData("[04:53:47.496] Messagebox [ERROR] message: Cannot load: 0Harmony.dll", LogCategory.Probe)]
    [InlineData("[DedicatedServer] SERVING", LogCategory.Milestone)]
    public void Existing_classifications_are_unchanged(string line, LogCategory expected)
        => Assert.Equal(expected, LogClassifier.Classify(line).Category);
}

public class LoadStallDetectorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 7, 4, 53, 45, TimeSpan.Zero);
    private static readonly TimeSpan Quick = TimeSpan.FromSeconds(90);
    private static string Step(int tick, string state) => $"[DedicatedServer] loading step @tick {tick}: manager=FinishLoadingFifthStep gameType={state}";

    [Fact]
    public void Advancing_steps_are_not_a_stall()
    {
        var d = new LoadStallDetector(T0, Quick);
        for (var i = 0; i < 20; i++)
            Assert.Null(d.Observe(Step(1600 + i, "State" + i), T0.AddSeconds(i * 30)));
    }

    [Fact]
    public void A_step_re_entered_past_the_threshold_is_called_a_stall()
    {
        var d = new LoadStallDetector(T0, Quick);
        // The real loop: the tick number climbs while the state names never change.
        Assert.Null(d.Observe(Step(1629, "LoadVisualsThirdState"), T0));
        for (var i = 1; i <= 60; i++) Assert.Null(d.Observe(Step(1629 + i, "LoadVisualsThirdState"), T0.AddSeconds(i)));

        var first = d.Check(T0.AddSeconds(120));
        Assert.NotNull(first);
        Assert.StartsWith("WARNING", first);
        Assert.Contains("LoadVisualsThirdState", first);
        Assert.Contains("re-entered", first);
    }

    /// <summary>
    /// Silence is much weaker evidence than repetition: it is what a legitimately slow mod looks like from outside,
    /// so it must not be dressed up as an error. TAOM's authors put its load at up to two hours.
    /// </summary>
    [Fact]
    public void Silence_alone_is_reported_as_a_heads_up_not_a_warning()
    {
        var d = new LoadStallDetector(T0, Quick);
        Assert.Null(d.Check(T0.AddSeconds(60)));
        var msg = d.Check(T0.AddSeconds(120));
        Assert.NotNull(msg);
        Assert.DoesNotContain("WARNING", msg);
        Assert.Contains("still loading", msg);
        Assert.Contains("nothing has been logged", msg);
        Assert.Contains("not an error", msg);
    }

    /// <summary>
    /// The real TAOM failure logs its map-scene loop continuously while the loading step never advances, so claiming
    /// "nothing has been logged" would have been plainly false to anyone watching the console scroll past.
    /// </summary>
    [Fact]
    public void Busy_but_not_advancing_does_not_claim_silence()
    {
        var d = new LoadStallDetector(T0, Quick);
        Assert.Null(d.Observe(Step(1629, "LoadVisualsThirdState"), T0));
        for (var i = 1; i <= 40; i++)
            Assert.Null(d.Observe("[DedicatedServer] reading Main_map...", T0.AddSeconds(i)));

        var msg = d.Check(T0.AddSeconds(120));
        Assert.NotNull(msg);
        Assert.DoesNotContain("nothing has been logged", msg);
        Assert.Contains("still logging", msg);
        Assert.Contains("has not advanced", msg);
    }

    /// <summary>A single warning then permanent silence reads like a verdict; a slow load wants a heartbeat.</summary>
    [Fact]
    public void It_keeps_reporting_at_doubling_intervals()
    {
        var d = new LoadStallDetector(T0, Quick);
        Assert.NotNull(d.Check(T0.AddSeconds(90)));    // first at the threshold
        Assert.Null(d.Check(T0.AddSeconds(120)));      // not again until double
        Assert.NotNull(d.Check(T0.AddSeconds(180)));
        Assert.Null(d.Check(T0.AddSeconds(300)));
        Assert.NotNull(d.Check(T0.AddSeconds(360)));   // and again at quadruple
    }

    [Fact]
    public void Progress_resets_the_heartbeat()
    {
        var d = new LoadStallDetector(T0, Quick);
        Assert.NotNull(d.Check(T0.AddSeconds(90)));
        Assert.Null(d.Observe(Step(2000, "SomewhereNew"), T0.AddSeconds(100)));
        Assert.Null(d.Check(T0.AddSeconds(150)));      // clock restarted from the new step
        Assert.NotNull(d.Check(T0.AddSeconds(200)));
    }

    [Fact]
    public void Serving_disarms_it_permanently()
    {
        var d = new LoadStallDetector(T0, Quick);
        Assert.Null(d.Observe("[DedicatedServer] SERVING", T0.AddSeconds(50)));
        Assert.Null(d.Check(T0.AddSeconds(99999)));    // an idle server is not a stalled load
    }

    /// <summary>A mod known to load for hours can turn the warning off rather than be nagged by it.</summary>
    [Fact]
    public void Off_reports_nothing_ever()
    {
        var d = new LoadStallDetector(T0, LoadStallDetector.Off);
        Assert.False(d.IsEnabled);
        Assert.Null(d.Check(T0.AddHours(3)));
        Assert.Null(d.Observe(Step(1, "X"), T0));
        Assert.Null(d.Observe(Step(1, "X"), T0.AddHours(3)));
    }

    [Fact]
    public void The_default_leaves_room_for_a_slow_mod()
    {
        // The known-good stack reaches SERVING in about 70 seconds; the old 90-second default fired during
        // healthy heavy loads, which is the fastest way to train someone to ignore a warning.
        Assert.True(LoadStallDetector.DefaultThreshold >= TimeSpan.FromMinutes(5));
        Assert.Null(new LoadStallDetector(T0).Check(T0.AddMinutes(4)));
    }

    [Theory]
    [InlineData("120", 120)]
    [InlineData("off", 0)]
    [InlineData("none", 0)]
    [InlineData("0", 0)]
    public void Thresholds_can_be_given_as_text(string text, int expectedSeconds)
        => Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), LoadStallDetector.ParseThreshold(text));

    /// <summary>A typo must be reportable, not silently equivalent to turning the warning off.</summary>
    [Theory]
    [InlineData("later")]
    [InlineData("-5")]
    [InlineData("")]
    public void An_unparseable_threshold_is_null(string text) => Assert.Null(LoadStallDetector.ParseThreshold(text));

    [Fact]
    public void Step_identity_ignores_the_tick_number()
    {
        Assert.Equal(LoadStallDetector.ExtractStep(Step(1, "X")), LoadStallDetector.ExtractStep(Step(5000, "X")));
        Assert.Null(LoadStallDetector.ExtractStep("[DedicatedServer] reading Main_map..."));
    }
}

public class SaveModuleCheckTests
{
    private static string WriteSave(string json)
    {
        var tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".sav");
        var body = Encoding.UTF8.GetBytes(json);
        using var fs = File.Create(tmp);
        fs.Write(BitConverter.GetBytes(body.Length));
        fs.Write(body);
        return tmp;
    }

    /// <summary>saveauto1 as it actually was: built by the non-TAOM stack, then launched with the TAOM one.</summary>
    private const string SaveAuto1 =
        """{"List":{"Modules":"Native;SandBoxCore;Sandbox;Coop;Bannerlord.Harmony;Bannerlord.ButterLib;ModularSmithing2","Module_Bannerlord.Harmony":"v2.4.2.0","Module_Bannerlord.ButterLib":"v2.9.18.0","Module_ModularSmithing2":"v0.9.30.0"}}""";

    [Fact]
    public void Reports_the_removed_and_added_modules_that_break_a_world()
    {
        var path = WriteSave(SaveAuto1);
        try
        {
            var messages = SaveModuleCheck.Messages(path, new Dictionary<string, string>
            {
                ["TAOM"] = "v1.0.0", ["TAOM.Dependencies"] = "v1.0.0", ["TAOM_Map"] = "v1.0.0",
            });
            var warning = Assert.Single(messages);
            Assert.StartsWith("WARNING", warning);
            Assert.Contains("Bannerlord.Harmony", warning);      // removed
            Assert.Contains("TAOM_Map", warning);                // added
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void A_matching_module_set_says_nothing()
    {
        var path = WriteSave(SaveAuto1);
        try
        {
            Assert.Empty(SaveModuleCheck.Messages(path, new Dictionary<string, string>
            {
                ["Bannerlord.Harmony"] = "v2.4.2", ["Bannerlord.ButterLib"] = "v2.9.18", ["ModularSmithing2"] = "v0.9.30",
            }));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void A_version_bump_alone_is_noted_but_not_severe()
    {
        var path = WriteSave(SaveAuto1);
        try
        {
            var messages = SaveModuleCheck.Messages(path, new Dictionary<string, string>
            {
                ["Bannerlord.Harmony"] = "v2.4.2", ["Bannerlord.ButterLib"] = "v2.9.18", ["ModularSmithing2"] = "v0.9.31",
            });
            var note = Assert.Single(messages);
            Assert.DoesNotContain("WARNING", note);
            Assert.Contains("ModularSmithing2 was v0.9.30.0, this launch has v0.9.31", note);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void An_unnamed_save_is_checked_against_coops_autosave()
    {
        // The motivating bug: the documented repro command names no save, and Coop loads saveauto1 anyway. A check
        // that only ran when --save was passed would have missed the very failure it was written for.
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            File.Move(WriteSave(SaveAuto1), Path.Combine(dir, SaveModuleCheck.CoopAutoSaveName + ".sav"));
            var warning = Assert.Single(SaveModuleCheck.MessagesForLaunch(dir, null,
                new Dictionary<string, string> { ["TAOM"] = "v1.0.0" }));
            Assert.Contains("WARNING", warning);
            Assert.Contains("no save was named", warning);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void An_explicitly_named_save_is_not_annotated_as_implicit()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            File.Move(WriteSave(SaveAuto1), Path.Combine(dir, "mysave.sav"));
            var warning = Assert.Single(SaveModuleCheck.MessagesForLaunch(dir, "mysave",
                new Dictionary<string, string> { ["TAOM"] = "v1.0.0" }));
            Assert.Contains("save 'mysave'", warning);
            Assert.DoesNotContain("no save was named", warning);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void A_first_launch_with_no_autosave_yet_says_nothing()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try { Assert.Empty(SaveModuleCheck.MessagesForLaunch(dir, null, new Dictionary<string, string> { ["TAOM"] = "v1.0.0" })); }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Server_infrastructure_is_never_reported_as_missing()
    {
        // Coop, DedicatedServer.* and the launcher's own modules are in every save and in no profile's mod list.
        // Reported, they would fire on every single launch and train the warning away.
        var path = WriteSave("""{"List":{"Modules":"Native;Coop;DedicatedServer.Windows;ModderLords.Compat;ModularSmithing2","Module_ModularSmithing2":"v0.9.30.0"}}""");
        try { Assert.Empty(SaveModuleCheck.Messages(path, new Dictionary<string, string> { ["ModularSmithing2"] = "v0.9.30" })); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void A_save_that_does_not_exist_yet_is_not_a_mismatch()
        => Assert.Empty(SaveModuleCheck.Messages(
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".sav"),
            new Dictionary<string, string> { ["TAOM"] = "v1.0.0" }));

    [Fact]
    public void An_unreadable_save_warns_rather_than_throwing()
    {
        var tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".sav");
        File.WriteAllBytes(tmp, new byte[] { 1, 2, 3 });
        try { Assert.Contains("could not be read", Assert.Single(SaveModuleCheck.Messages(tmp, new Dictionary<string, string>()))); }
        finally { File.Delete(tmp); }
    }
}
