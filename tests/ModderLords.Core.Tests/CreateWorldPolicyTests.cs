using ModderLords.Compat;
using Xunit;

namespace ModderLords.Core.Tests;

/// <summary>
/// The compat module's first test coverage. `CreateWorldPolicy` is deliberately free of TaleWorlds types so
/// it can be compiled into this net10 assembly, which means the part of world creation that decides *whether*
/// and *when* is testable without a game.
///
/// These also pin the stdout contract. `tools\spike\Run-Stages.ps1` greps for these exact strings to decide
/// PASS/FAIL, so a rename here without a rename there would silently make every stage look like a failure.
/// </summary>
public class CreateWorldPolicyTests
{
    private static readonly DateTime T0 = new(2026, 9, 8, 21, 0, 0, DateTimeKind.Utc);
    private static CreateWorldPolicy Armed(string name = "spike1", TimeSpan? timeout = null)
    {
        var p = new CreateWorldPolicy(name, timeout);
        p.Arm(T0);
        return p;
    }

    // ---- off by default -----------------------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void With_no_save_name_the_mode_is_off(string? name)
    {
        var p = new CreateWorldPolicy(name);
        Assert.False(p.IsEnabled);
        Assert.Equal(CreateWorldPolicy.Phase.Off, p.Current);
        // Off must stay inert: a normal server launch must not be able to trip any of this.
        p.Arm(T0);
        Assert.False(p.ShouldStart(T0.AddMinutes(5), gameAlreadyRunning: false));
        Assert.False(p.HasTimedOut(T0.AddHours(5)));
    }

    /// <summary>
    /// Measured the hard way: OnApplicationTick fires before the hook that arms this. Without the guard the
    /// first tick compared against DateTime.MinValue, declared a timeout of -2147483648 seconds, and killed
    /// the run six seconds in.
    /// </summary>
    [Fact]
    public void Nothing_happens_before_arming()
    {
        var p = new CreateWorldPolicy("spike1");
        Assert.False(p.IsArmed);
        Assert.False(p.HasTimedOut(T0.AddYears(1)));
        Assert.False(p.ShouldStart(T0, gameAlreadyRunning: false));

        p.Arm(T0);
        Assert.True(p.IsArmed);
        Assert.True(p.ShouldStart(T0, gameAlreadyRunning: false));
    }

    [Fact]
    public void Arming_twice_does_not_restart_the_clock()
    {
        var p = new CreateWorldPolicy("spike1", TimeSpan.FromMinutes(10));
        p.Arm(T0);
        Assert.Null(p.Arm(T0.AddMinutes(9)));
        Assert.True(p.HasTimedOut(T0.AddMinutes(10)));
    }

    [Fact]
    public void A_save_name_arms_it() => Assert.Equal(CreateWorldPolicy.Phase.Armed, Armed().Current);

    [Fact]
    public void The_name_is_trimmed() => Assert.Equal("spike1", new CreateWorldPolicy("  spike1  ").SaveName);

    // ---- when to start ------------------------------------------------------------------------------

    /// <summary>
    /// Measured in spike stage 0b: even with no /coopsave the host loads a save of its own, so there is no
    /// idle state to wait for and any delay is a race we can only lose. Arming happens before the host's
    /// state machine runs, so starting immediately is the whole point.
    /// </summary>
    [Fact]
    public void It_starts_immediately_rather_than_waiting()
    {
        Assert.Equal(TimeSpan.Zero, CreateWorldPolicy.SettleDelay);
        Assert.True(Armed().ShouldStart(T0, false));
    }

    /// <summary>
    /// We add a world, we never race the host into one. If DedicatedServer.Core already started a game there
    /// is nothing to create into, and starting a second one would be worse than doing nothing.
    /// </summary>
    [Fact]
    public void It_refuses_to_start_when_the_host_already_has_a_game()
    {
        var p = Armed();
        Assert.False(p.ShouldStart(T0.AddSeconds(30), gameAlreadyRunning: true));
        Assert.Equal(CreateWorldPolicy.Phase.Failed, p.Current);
        Assert.Contains("already started a game", p.FailureReason);
    }

    [Fact]
    public void It_only_starts_once()
    {
        var p = Armed();
        var at = T0.AddSeconds(30);
        Assert.True(p.ShouldStart(at, false));
        p.Advance(CreateWorldPolicy.Phase.Starting, at);
        Assert.False(p.ShouldStart(at.AddSeconds(1), false));
    }

    // ---- the stdout contract the harness greps -------------------------------------------------------

    /// <summary>Arming is announced by Arm(), not Advance() -- it is how "the env var arrived" is proved.</summary>
    [Fact]
    public void Arming_announces_itself_with_the_save_name()
        => Assert.Equal("worldcreate: phase=armed detail=spike1", new CreateWorldPolicy("spike1").Arm(T0));

    [Fact]
    public void Arming_says_nothing_when_the_mode_is_off()
        => Assert.Null(new CreateWorldPolicy(null).Arm(T0));

    [Theory]
    [InlineData(CreateWorldPolicy.Phase.Starting, "worldcreate: phase=starting")]
    [InlineData(CreateWorldPolicy.Phase.CampaignCreated, "worldcreate: phase=campaign-created")]
    [InlineData(CreateWorldPolicy.Phase.MapReady, "worldcreate: phase=map-ready")]
    [InlineData(CreateWorldPolicy.Phase.Saving, "worldcreate: phase=saving")]
    [InlineData(CreateWorldPolicy.Phase.Saved, "worldcreate: phase=saved")]
    internal void Each_phase_announces_the_exact_string_the_harness_greps(CreateWorldPolicy.Phase phase, string expected)
        => Assert.Equal(expected, new CreateWorldPolicy("spike1").Advance(phase, T0));

    [Fact]
    public void A_repeated_phase_says_nothing()
    {
        var p = Armed();
        Assert.NotNull(p.Advance(CreateWorldPolicy.Phase.Starting, T0));
        Assert.Null(p.Advance(CreateWorldPolicy.Phase.Starting, T0.AddSeconds(1)));
    }

    [Fact]
    public void Detail_is_appended_when_given()
        => Assert.Equal("worldcreate: phase=starting detail=SandBoxGameManager",
            new CreateWorldPolicy("spike1").Advance(CreateWorldPolicy.Phase.Starting, T0, "SandBoxGameManager"));

    [Fact]
    public void Failure_names_the_phase_it_failed_in()
    {
        var p = Armed();
        p.Advance(CreateWorldPolicy.Phase.Starting, T0);
        var line = p.Fail(T0.AddSeconds(5), "no campaign creator ctor");
        Assert.Equal("worldcreate: fail phase=starting reason=no campaign creator ctor", line);
        Assert.Equal(CreateWorldPolicy.Phase.Failed, p.Current);
    }

    [Fact]
    public void Success_reports_the_name_path_and_size()
        => Assert.Equal(@"worldcreate: ok name=spike1 path=C:\saves\spike1.sav bytes=4083467",
            Armed().Succeeded(@"C:\saves\spike1.sav", 4083467));

    // ---- timeout ------------------------------------------------------------------------------------

    [Fact]
    public void It_gives_up_after_the_budget()
    {
        var p = Armed(timeout: TimeSpan.FromMinutes(15));
        Assert.False(p.HasTimedOut(T0.AddMinutes(14)));
        Assert.True(p.HasTimedOut(T0.AddMinutes(15)));
        Assert.Contains("timed out after", p.TimeoutMessage(T0.AddMinutes(15)));
    }

    [Fact]
    public void A_finished_run_never_times_out()
    {
        var p = Armed(timeout: TimeSpan.FromMinutes(1));
        p.Advance(CreateWorldPolicy.Phase.Saved, T0.AddSeconds(10));
        Assert.False(p.HasTimedOut(T0.AddHours(3)));
    }

    /// <summary>A heavy mod can legitimately load for a long time, so the default has to be generous.</summary>
    [Fact]
    public void The_default_budget_is_generous()
        => Assert.True(CreateWorldPolicy.DefaultTimeout >= TimeSpan.FromMinutes(15));

    // ---- environment --------------------------------------------------------------------------------

    [Fact]
    public void It_reads_the_mode_from_the_environment()
    {
        var env = new Dictionary<string, string?>
        {
            [CreateWorldPolicy.EnvSaveName] = "spike1",
            [CreateWorldPolicy.EnvTimeout] = "60",
        };
        var p = CreateWorldPolicy.FromEnvironment(k => env.GetValueOrDefault(k));
        Assert.True(p.IsEnabled);
        Assert.Equal("spike1", p.SaveName);
        p.Arm(T0);
        Assert.True(p.HasTimedOut(T0.AddSeconds(60)));
    }

    [Fact]
    public void An_unset_environment_leaves_it_off()
        => Assert.False(CreateWorldPolicy.FromEnvironment(_ => null).IsEnabled);

    [Theory]
    [InlineData("later")]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("")]
    public void An_unusable_timeout_falls_back_to_the_default_rather_than_zero(string raw)
        => Assert.Null(CreateWorldPolicy.ParseTimeout(raw));

    // ---- exit codes ---------------------------------------------------------------------------------

    [Fact]
    public void A_saved_world_exits_with_the_created_code()
    {
        var p = Armed();
        p.Advance(CreateWorldPolicy.Phase.Saved, T0.AddMinutes(1));
        Assert.Equal(CreateWorldPolicy.ExitCreated, p.ExitCode);
    }

    [Fact]
    public void Anything_else_exits_with_the_failed_code()
    {
        var p = Armed();
        Assert.Equal(CreateWorldPolicy.ExitFailed, p.ExitCode);          // still mid-flight
        p.Fail(T0.AddMinutes(1), "whatever");
        Assert.Equal(CreateWorldPolicy.ExitFailed, p.ExitCode);
    }

    [Fact]
    public void The_two_exit_codes_are_distinct_and_not_engine_codes()
    {
        // 0/2/3/4 already mean something to the server; a deliberate stop must never read as one of those.
        Assert.NotEqual(CreateWorldPolicy.ExitCreated, CreateWorldPolicy.ExitFailed);
        Assert.DoesNotContain(CreateWorldPolicy.ExitCreated, new[] { 0, 2, 3, 4 });
        Assert.DoesNotContain(CreateWorldPolicy.ExitFailed, new[] { 0, 2, 3, 4 });
    }
}
