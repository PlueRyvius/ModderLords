using ModderLords.Coop.Launch;
using ModderLords.Core.Launch;
using Xunit;

namespace ModderLords.Core.Tests;

public class CreationDiagnosticsTests
{
    [Fact]
    public void Captures_both_streams_plan_phases_and_completion_manifest()
    {
        using var fixture = new Fixture();
        string logPath, stdoutPath, stderrPath, manifestPath;
        IReadOnlyList<string> phases;
        using (var diagnostics = CreationDiagnostics.Start(fixture.Logs, fixture.Plan, "taom-test"))
        {
            diagnostics.Attach(1234);
            diagnostics.Record(new EngineLine(DateTimeOffset.UtcNow, OutputStream.Stdout, "worldcreate: phase=campaign-created"));
            diagnostics.Record(new EngineLine(DateTimeOffset.UtcNow, OutputStream.Stdout, "worldcreate: map scene=A.G clans=8 settlements=10"));
            diagnostics.Record(new EngineLine(DateTimeOffset.UtcNow, OutputStream.Stderr, "rgl_post_warning_line: RGL WARNING - Could not find animation: rider_warg_idle_2"));
            diagnostics.Record(new EngineLine(DateTimeOffset.UtcNow, OutputStream.Stderr, "Messagebox [Always Ignore?] message: Would you like to always ignore this failure?"));
            diagnostics.Complete(11, timedOut: false, saveExists: true);
            logPath = diagnostics.LogPath;
            stdoutPath = diagnostics.StdoutPath;
            stderrPath = diagnostics.StderrPath;
            manifestPath = diagnostics.ManifestPath;
            phases = diagnostics.Phases;
        }

        Assert.True(File.Exists(logPath));
        Assert.True(File.Exists(stdoutPath));
        Assert.True(File.Exists(stderrPath));
        var combined = File.ReadAllText(logPath);
        Assert.Contains("process-started pid=1234", combined);
        Assert.Contains("Stdout", combined);
        Assert.Contains("Warning(ExpectedMissingAnimation)", combined);
        Assert.Contains("rider_warg_idle_2", combined);
        Assert.DoesNotContain("secret-value", combined);
        Assert.Equal(["campaign-created"], phases);

        var json = File.ReadAllText(manifestPath);
        Assert.Contains("\"exitCode\": 11", json);
        Assert.Contains("\"saveExists\": true", json);
        Assert.Contains("campaign-created", json);
        Assert.Contains("actualMapSceneType", json);
        Assert.Contains("A.G", json);
        Assert.Contains("savePath", json);
        Assert.Contains("expectedMissingAnimation", json);
        Assert.Contains("messageboxPrompts", json);
        Assert.Contains("TAOM", json);
    }

    [Fact]
    public void Every_session_gets_unique_files_so_old_phase_logs_cannot_be_reused()
    {
        using var fixture = new Fixture();
        using var first = CreationDiagnostics.Start(fixture.Logs, fixture.Plan, "first");
        using var second = CreationDiagnostics.Start(fixture.Logs, fixture.Plan, "second");

        Assert.NotEqual(first.SessionId, second.SessionId);
        Assert.NotEqual(first.ManifestPath, second.ManifestPath);
        Assert.True(File.Exists(first.ManifestPath));
        Assert.True(File.Exists(second.ManifestPath));
    }

    [Fact]
    public void Late_engine_lines_after_dispose_are_ignored()
    {
        using var fixture = new Fixture();
        var diagnostics = CreationDiagnostics.Start(fixture.Logs, fixture.Plan, "late-line");
        diagnostics.Dispose();

        var exception = Record.Exception(() =>
        {
            diagnostics.Record(new EngineLine(DateTimeOffset.UtcNow, OutputStream.Stdout, "worldcreate: phase=map-ready"));
            diagnostics.RecordEvent("late callback");
            diagnostics.Complete(11, timedOut: false, saveExists: true);
        });

        Assert.Null(exception);
    }

    [Fact]
    public void Bundle_root_switches_hook_and_compat_lookup_for_release_reproduction()
    {
        using var fixture = new Fixture();
        var bin = Path.Combine(fixture.Root, "bin");
        Directory.CreateDirectory(bin);
        var hook = Path.Combine(bin, HookSetup.HookFileName);
        File.WriteAllText(hook, "release hook");
        var compat = Path.Combine(fixture.Root, "compat", LaunchSession.CompatModuleId);
        Directory.CreateDirectory(compat);
        File.WriteAllText(Path.Combine(compat, "SubModule.xml"),
            "<Module><Name value='compat'/><Id value='DedicatedServer.ModderLordsCompat'/><Version value='v1.0.0'/><SubModules/></Module>");
        var previous = LaunchSession.BundledRoot;
        try
        {
            LaunchSession.BundledRoot = fixture.Root;
            Assert.Equal(hook, HookSetup.LocateHook());
            Assert.Equal(LaunchSession.CompatModuleId, LaunchSession.LocateCompatModule()?.Id);
        }
        finally { LaunchSession.BundledRoot = previous; }
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "ModderLords-creation-diagnostics", Guid.NewGuid().ToString("N"));
        public string Logs => Path.Combine(Root, "logs");
        public LaunchPlan Plan { get; }

        public Fixture()
        {
            Directory.CreateDirectory(Root);
            var paths = ServerPaths.Create(Path.Combine(Root, "DedicatedServer"), Path.Combine(Root, "data"), Path.Combine(Root, "coop"));
            Plan = new LaunchPlan
            {
                Paths = paths,
                ModuleIds = ["Native", "TAOM", "TAOM_Map"],
                EnginePort = 7368,
                ExtraEnvironment = new Dictionary<string, string>
                {
                    ["MODDERLORDS_CREATE_WORLD_LOG"] = Path.Combine(Logs, "worldcreate.log"),
                    ["EXAMPLE_PASSWORD"] = "secret-value",
                },
            };
        }

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}
