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
    private static string Step(int tick, string state) => $"[DedicatedServer] loading step @tick {tick}: manager=FinishLoadingFifthStep gameType={state}";

    [Fact]
    public void Advancing_steps_are_not_a_stall()
    {
        var d = new LoadStallDetector(T0, TimeSpan.FromSeconds(90));
        for (var i = 0; i < 20; i++)
            Assert.Null(d.Observe(Step(1600 + i, "State" + i), T0.AddSeconds(i * 30)));
    }

    [Fact]
    public void Same_step_repeating_past_the_threshold_reports_once()
    {
        var d = new LoadStallDetector(T0, TimeSpan.FromSeconds(90));
        // The real loop: the tick number climbs while the state names never change.
        Assert.Null(d.Observe(Step(1629, "LoadVisualsThirdState"), T0));
        for (var i = 1; i <= 60; i++) Assert.Null(d.Observe(Step(1629 + i, "LoadVisualsThirdState"), T0.AddSeconds(i)));

        var first = d.Check(T0.AddSeconds(120));
        Assert.NotNull(first);
        Assert.Contains("LoadVisualsThirdState", first);
        Assert.Contains("re-entered", first);
        Assert.Null(d.Check(T0.AddSeconds(300)));   // reported once, not per poll
    }

    [Fact]
    public void Silence_before_any_step_is_still_a_stall()
    {
        var d = new LoadStallDetector(T0, TimeSpan.FromSeconds(90));
        Assert.Null(d.Check(T0.AddSeconds(60)));
        Assert.Contains("never reported a loading step", d.Check(T0.AddSeconds(120)));
    }

    [Fact]
    public void Serving_disarms_it_permanently()
    {
        var d = new LoadStallDetector(T0, TimeSpan.FromSeconds(90));
        Assert.Null(d.Observe("[DedicatedServer] SERVING", T0.AddSeconds(50)));
        Assert.Null(d.Check(T0.AddSeconds(9999)));  // an idle server is not a stalled load
    }

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
