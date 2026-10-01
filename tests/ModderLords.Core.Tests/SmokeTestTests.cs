using ModderLords.Core.Launch;
using ModderLords.Core.Smoke;

namespace ModderLords.Core.Tests;

// Lines are taken from a real session (2026-10-01: Coop 0.1.5, Bannerlord 1.4.8, Bellum + Fourberie on the dedicated
// server), so a test failing here means the smoke test would misread a real log.
public sealed class SmokeSignalsTests
{
    [Fact]
    public void ReadsCoopsClientLogLine()
    {
        var line = SmokeSignals.ParseCoopLine("[(53960) 01:52:13 INF GameInterface.Services.GameDebug.Patches.CharacterCreationIntroPatch] Game State is changing to MapState");
        Assert.NotNull(line);
        Assert.Equal(53960, line!.Pid);
        Assert.Equal("INF", line.Level);
        Assert.Equal(ClientStage.OnMap, SmokeSignals.StageOf(line));
    }

    [Theory]
    [InlineData("Attempting connection to 127.0.0.1:4200...", ClientStage.Connecting)]
    [InlineData("Receiving host save transfer 1: 86 chunks, 5,608,932 compressed bytes, 49,249,543 save bytes", ClientStage.ReceivingWorld)]
    [InlineData("Received host save transfer 1: 5,608,932 compressed bytes decompressed to 49,249,543 bytes", ClientStage.WorldReceived)]
    [InlineData("Game State is changing to GameLoadingState", ClientStage.Loading)]
    [InlineData("Game State is changing to CharacterCreationState", ClientStage.CharacterCreation)]
    public void KnowsEachStage(string message, ClientStage stage) =>
        Assert.Equal(stage, SmokeSignals.StageOf(new CoopLogLine(1, "00:00:00", "INF", "Coop", message)));

    [Fact]
    public void ReadsCoopsFrameRateLine()
    {
        var line = SmokeSignals.ParseCoopLine("[(53960) 01:52:29 INF Common.FpsLogger] [Fps] frames=4357 seconds=30.01 avg=145.2 min=14.9 max=200.0");
        Assert.Equal(145.2, SmokeSignals.FpsAverage(line!));
    }

    [Fact]
    public void CoopsAutoSyncErrorsAreNotFatal()
    {
        var line = SmokeSignals.ParseCoopLine("[(53960) 01:52:51 ERR AutoSync.MobileParty_DynamicPatches] Client updated managed _isDisorganized")!;
        Assert.False(SmokeSignals.IsCoopFatal(line));
        Assert.True(SmokeSignals.IsCoopFatal(line with { Level = "FTL" }));
    }

    [Fact]
    public void ReadsTheServersPlayerEvent()
    {
        var players = SmokeSignals.ParsePlayers("Engine     @DS@{\"ev\":\"players\",\"list\":[{\"id\":0,\"name\":\"Phorys\",\"state\":\"on map\",\"addr\":\"127.0.0.1:57921\"}]}");
        Assert.NotNull(players);
        var p = Assert.Single(players!);
        Assert.Equal(("Phorys", "on map"), (p.Name, p.State));
        Assert.Empty(SmokeSignals.ParsePlayers("@DS@{\"ev\":\"players\",\"list\":[]}")!);
        Assert.Null(SmokeSignals.ParsePlayers("@DS@{\"ev\":\"state\",\"x\":1}"));
        Assert.Null(SmokeSignals.ParsePlayers("@DS@{not json"));
    }

    [Fact]
    public void ReadsTheCompatModulesLines()
    {
        var message = SmokeSignals.CompatMessage("01:52:16.120 [ModderLords.Compat] session check: server answered the ping in 42 ms (as Phorys)");
        Assert.Equal(42, SmokeSignals.PingMs(message!));
        Assert.True(SmokeSignals.IsWarning("WARNING Fourberie layer: effects failed and is off: ..."));
        Assert.True(SmokeSignals.IsExpectedWarning("WARNING bellum-civile.state is active only for the explicit Bellum 1.3.1 isolated validation run; production validation remains withheld"));
    }
}

public sealed class SmokeEvaluatorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 1, 51, 0, TimeSpan.Zero);
    private static readonly SmokeRunFacts Done = new() { ClientPid = 53960, SteadyTarget = TimeSpan.FromSeconds(60), SteadyAchieved = TimeSpan.FromSeconds(60) };

    /// <summary>A healthy join, as the real session logged it.</summary>
    private static SmokeObserver HealthyJoin(bool withPing = true)
    {
        var o = new SmokeObserver();
        o.ObserveServer("Milestone  [DedicatedServer] SERVING — coop server up, waiting for clients", T0);
        o.ObserveServer("Engine     @DS@{\"ev\":\"players\",\"list\":[]}", T0);
        o.ClientStarted(T0.AddSeconds(1));
        o.ObserveCoopClient("[(53960) 01:51:41 INF Coop.Core.Client.CoopClient] Attempting connection to 127.0.0.1:4200...", T0.AddSeconds(41));
        o.ObserveServer("Engine     @DS@{\"ev\":\"players\",\"list\":[{\"id\":0,\"name\":\"(joining)\",\"state\":\"handshake\",\"addr\":\"127.0.0.1:57921\"}]}", T0.AddSeconds(42));
        o.ObserveCoopClient("[(53960) 01:51:59 INF Coop.Core.Client.Services.Save.PacketHandlers.GameSaveDataPacketHandler] Receiving host save transfer 1: 86 chunks, 5,608,932 compressed bytes, 49,249,543 save bytes", T0.AddSeconds(59));
        o.ObserveCoopClient("[(53960) 01:52:01 INF Coop.Core.Client.Services.Save.PacketHandlers.GameSaveDataPacketHandler] Received host save transfer 1: 5,608,932 compressed bytes decompressed to 49,249,543 bytes", T0.AddSeconds(61));
        o.ObserveServer("Engine     @DS@{\"ev\":\"players\",\"list\":[{\"id\":0,\"name\":\"Phorys\",\"state\":\"loading\",\"addr\":\"127.0.0.1:57921\"}]}", T0.AddSeconds(62));
        o.ObserveCoopClient("[(53960) 01:52:02 INF GameInterface.Services.GameDebug.Patches.CharacterCreationIntroPatch] Game State is changing to GameLoadingState", T0.AddSeconds(62));
        o.ObserveCoopClient("[(53960) 01:52:13 INF GameInterface.Services.GameDebug.Patches.CharacterCreationIntroPatch] Game State is changing to MapState", T0.AddSeconds(73));
        o.ObserveServer("Engine     @DS@{\"ev\":\"players\",\"list\":[{\"id\":0,\"name\":\"Phorys\",\"state\":\"on map\",\"addr\":\"127.0.0.1:57921\"}]}", T0.AddSeconds(74));
        o.ObserveCompatClient("01:52:14.001 [ModderLords.Compat] session check: campaign ready on this player's game", T0.AddSeconds(74));
        if (withPing)
        {
            o.ObserveServer("Tool       [ModderLords.Compat] session check: answered the ping of Lord Phorys", T0.AddSeconds(76));
            o.ObserveCompatClient("01:52:16.120 [ModderLords.Compat] session check: server answered the ping in 42 ms (as Lord Phorys)", T0.AddSeconds(76));
        }
        o.ObserveCoopClient("[(53960) 01:52:29 INF Common.FpsLogger] [Fps] frames=4357 seconds=30.01 avg=145.2 min=14.9 max=200.0", T0.AddSeconds(89));
        o.ObserveCoopClient("[(53960) 01:52:51 ERR AutoSync.MobileParty_DynamicPatches] Client updated managed _isDisorganized", T0.AddSeconds(110));
        return o;
    }

    private static SmokeVerdict VerdictOf(IReadOnlyList<SmokeCheck> checks, string name) => checks.Single(c => c.Name == name).Verdict;

    [Fact]
    public void AHealthyJoinPasses()
    {
        var checks = SmokeEvaluator.Evaluate(HealthyJoin(), Done);
        Assert.Equal(SmokeVerdict.Pass, SmokeReport.OverallOf(checks));
        Assert.Contains("42 ms", checks.Single(c => c.Name == "ModderLords channel round trip").Detail);
        Assert.Equal(SmokeVerdict.Info, VerdictOf(checks, "Coop errors in the game's log"));
        Assert.Equal(SmokeVerdict.Info, VerdictOf(checks, "Frame rate on the map"));
    }

    [Fact]
    public void AJoinPassesWithoutCoopsClientLog()
    {
        // The first live run: Coop buffers its client log until a normal exit, so it stayed empty. The server's player
        // states and ModderLords' own lines carry the stages (lines from that run, 2026-10-01 03:44).
        var o = new SmokeObserver();
        o.ObserveServer("Milestone  [DedicatedServer] SERVING — coop server up, waiting for clients", T0);
        o.ObserveServer("Engine     @DS@{\"ev\":\"players\",\"list\":[]}", T0);
        o.ClientStarted(T0);
        o.ObserveServer("Engine     @DS@{\"ev\":\"players\",\"list\":[{\"id\":0,\"name\":\"(joining)\",\"state\":\"handshake\",\"addr\":\"127.0.0.1:61022\"}]}", T0.AddSeconds(40));
        o.ObserveServer("Engine     @DS@{\"ev\":\"players\",\"list\":[{\"id\":0,\"name\":\"Phorys\",\"state\":\"loading\",\"addr\":\"127.0.0.1:61022\"}]}", T0.AddSeconds(60));
        o.ObserveServer("Engine     @DS@{\"ev\":\"players\",\"list\":[{\"id\":0,\"name\":\"Phorys\",\"state\":\"on map\",\"addr\":\"127.0.0.1:61022\"}]}", T0.AddSeconds(75));
        o.ObserveCompatClient("03:44:24.108 [ModderLords.Compat] session check: campaign ready on this player's game", T0.AddSeconds(76));
        o.ObserveServer("03:44:29.217 Tool       [ModderLords.Compat] session check: answered the ping of Lord Phorys", T0.AddSeconds(81));
        o.ObserveCompatClient("03:44:28.980 [ModderLords.Compat] session check: server answered the ping in 139 ms (as Lord Phorys)", T0.AddSeconds(81));
        var checks = SmokeEvaluator.Evaluate(o, Done);
        Assert.Equal(SmokeVerdict.Pass, SmokeReport.OverallOf(checks));
        Assert.Equal(SmokeVerdict.Pass, VerdictOf(checks, "Joined the server"));
        Assert.Equal(SmokeVerdict.Pass, VerdictOf(checks, "On the campaign map"));
        Assert.Equal(SmokeVerdict.Info, VerdictOf(checks, "Coop's client log"));
    }

    [Fact]
    public void AModderLordsWarningInTheGameFails()
    {
        // The #151 failure: a layer could not install on the player's game, and only the game's log said so.
        var o = HealthyJoin();
        o.ObserveCompatClient("23:43:49.711 [ModderLords.Compat] WARNING Fourberie layer: effects failed and is off: You can only patch implemented methods/constructors.", T0.AddSeconds(30));
        var checks = SmokeEvaluator.Evaluate(o, Done);
        Assert.Equal(SmokeVerdict.Fail, VerdictOf(checks, "No ModderLords warnings"));
        Assert.Equal(SmokeVerdict.Fail, SmokeReport.OverallOf(checks));
    }

    [Fact]
    public void ValidationModeWarningsAreListedNotCounted()
    {
        var o = HealthyJoin();
        o.ObserveCompatClient("01:51:30.000 [ModderLords.Compat] WARNING bellum-civile.state is active only for the explicit Bellum 1.3.1 isolated validation run; production validation remains withheld", T0.AddSeconds(30));
        var checks = SmokeEvaluator.Evaluate(o, Done);
        Assert.Equal(SmokeVerdict.Pass, VerdictOf(checks, "No ModderLords warnings"));
        Assert.Equal(SmokeVerdict.Info, VerdictOf(checks, "Expected warnings"));
    }

    [Fact]
    public void ANoAnswerToThePingFails()
    {
        var checks = SmokeEvaluator.Evaluate(HealthyJoin(withPing: false), Done);
        Assert.Equal(SmokeVerdict.Fail, VerdictOf(checks, "ModderLords channel round trip"));
    }

    [Fact]
    public void AGameWithoutTheCompatModuleSkipsThePing()
    {
        var o = new SmokeObserver();
        o.ServingNow();
        o.ClientStarted(T0);
        o.ObserveCoopClient("[(1) 01:51:41 INF Coop.Core.Client.CoopClient] Attempting connection to 127.0.0.1:4200...", T0);
        o.ObserveCoopClient("[(1) 01:52:01 INF X] Received host save transfer 1: 5 bytes", T0);
        o.ObserveCoopClient("[(1) 01:52:13 INF X] Game State is changing to MapState", T0);
        o.ObserveServer("@DS@{\"ev\":\"players\",\"list\":[{\"id\":0,\"name\":\"P\",\"state\":\"on map\",\"addr\":\"\"}]}", T0);
        var checks = SmokeEvaluator.Evaluate(o, Done);
        Assert.Equal(SmokeVerdict.Skipped, VerdictOf(checks, "ModderLords channel round trip"));
        Assert.Equal(SmokeVerdict.Pass, SmokeReport.OverallOf(checks));
    }

    [Fact]
    public void StoppingAtCharacterCreationIsAWarningNotAPass()
    {
        var o = new SmokeObserver();
        o.ServingNow();
        o.ClientStarted(T0);
        o.ObserveCoopClient("[(1) 01:51:41 INF Coop.Core.Client.CoopClient] Attempting connection to 127.0.0.1:4200...", T0);
        o.ObserveServer("@DS@{\"ev\":\"players\",\"list\":[{\"id\":0,\"name\":\"(joining)\",\"state\":\"handshake\",\"addr\":\"\"}]}", T0);
        o.ObserveCoopClient("[(1) 01:52:01 INF X] Received host save transfer 1: 5 bytes", T0);
        o.ObserveCoopClient("[(1) 01:52:02 INF X] Game State is changing to CharacterCreationState", T0);
        var checks = SmokeEvaluator.Evaluate(o, Done with { SteadyAchieved = TimeSpan.Zero });
        Assert.Equal(SmokeVerdict.Warn, VerdictOf(checks, "On the campaign map"));
        Assert.Equal(SmokeVerdict.Warn, SmokeReport.OverallOf(checks));
    }

    [Fact]
    public void AGameThatNeverConnectsFails()
    {
        var o = new SmokeObserver();
        o.ServingNow();
        o.ClientStarted(T0);
        var checks = SmokeEvaluator.Evaluate(o, Done with { TimedOutWaitingFor = "connecting", SteadyAchieved = TimeSpan.Zero });
        Assert.Equal(SmokeVerdict.Fail, VerdictOf(checks, "Joined the server"));
        Assert.Equal(SmokeVerdict.Skipped, VerdictOf(checks, "On the campaign map"));
    }

    [Fact]
    public void AGameThatClosesOnTheMapIsACrash()
    {
        var checks = SmokeEvaluator.Evaluate(HealthyJoin(), Done with { ClientExitedEarly = true, ClientExitCode = -1073741819, SteadyAchieved = TimeSpan.FromSeconds(12) });
        Assert.Equal(SmokeVerdict.Fail, VerdictOf(checks, "Stayed connected"));
        Assert.Equal(SmokeVerdict.Fail, VerdictOf(checks, "No crash"));
    }

    [Fact]
    public void ANewCrashReportFails()
    {
        var checks = SmokeEvaluator.Evaluate(HealthyJoin(), Done with { NewCrashFolders = [@"C:\ProgramData\Mount and Blade II Bannerlord\crashes\2026-10-01_01.53.00"] });
        Assert.Equal(SmokeVerdict.Fail, VerdictOf(checks, "No crash"));
    }

    [Fact]
    public void ThePlayersAlreadyOnTheServerAreNotTheTestsPlayer()
    {
        var o = new SmokeObserver();
        o.ServingNow();
        o.ObserveServer("@DS@{\"ev\":\"players\",\"list\":[{\"id\":3,\"name\":\"Friend\",\"state\":\"on map\",\"addr\":\"\"}]}", T0);
        o.ClientStarted(T0);
        o.ObserveServer("@DS@{\"ev\":\"players\",\"list\":[{\"id\":3,\"name\":\"Friend\",\"state\":\"on map\",\"addr\":\"\"},{\"id\":4,\"name\":\"(joining)\",\"state\":\"handshake\",\"addr\":\"\"}]}", T0);
        Assert.Equal(4, o.TestPlayer?.Id);
        Assert.Equal("handshake", o.BestServerState);
    }

    [Fact]
    public void ServerWarningsBeforeTheJoinAreInformation()
    {
        var o = new SmokeObserver();
        o.ObserveServer("[ModderLords.Compat] WARNING something at load", T0);
        o.ServingNow();
        o.ClientStarted(T0);
        var checks = SmokeEvaluator.Evaluate(o, Done with { TimedOutWaitingFor = "connecting" });
        Assert.Equal(SmokeVerdict.Info, VerdictOf(checks, "Server warnings before the join"));
        Assert.Equal(SmokeVerdict.Pass, VerdictOf(checks, "No ModderLords warnings"));
    }
}

public sealed class LogTailTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("smoke-tail").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void ReadsOnlyWhatIsAddedAfterItStarts()
    {
        var path = Path.Combine(_dir, "a.log");
        File.WriteAllText(path, "old line\n");
        var tail = new LogTail(path);
        File.AppendAllText(path, "new line\npartial");
        Assert.Equal(["new line"], tail.ReadNew());
        File.AppendAllText(path, " finished\n");
        Assert.Equal(["partial finished"], tail.ReadNew());
    }

    [Fact]
    public void StartsOverWhenTheGameReplacesTheFile()
    {
        var path = Path.Combine(_dir, "b.log");
        File.WriteAllText(path, "2026-10-01 01:00:00 log started (pid 1)\nlots of old lines that make the file long\n");
        var tail = new LogTail(path);
        // A new launch rewrites the log; it is already longer than the old one by the first read.
        File.WriteAllText(path, "2026-10-01 02:00:00 log started (pid 2)\nfirst\nsecond\nthird line that makes it longer than before\n");
        Assert.Equal(["2026-10-01 02:00:00 log started (pid 2)", "first", "second", "third line that makes it longer than before"], tail.ReadNew());
    }

    [Fact]
    public void AMissingFileIsNotAnError()
    {
        var path = Path.Combine(_dir, "c.log");
        var tail = new LogTail(path);
        Assert.Empty(tail.ReadNew());
        File.WriteAllText(path, "appeared\n");
        Assert.Equal(["appeared"], tail.ReadNew());
    }

    [Fact]
    public void ReadsAFileAnotherProcessHoldsOpenForWriting()
    {
        var path = Path.Combine(_dir, "d.log");
        using var writer = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        var tail = new LogTail(path);
        writer.Write("held\n"u8);
        writer.Flush();
        Assert.Equal(["held"], tail.ReadNew());
    }
}

public sealed class EngineConfigGuardTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("smoke-config").FullName;

    public void Dispose()
    {
        foreach (var f in Directory.GetFiles(_dir)) File.SetAttributes(f, FileAttributes.Normal);
        Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void PutsBackWhatCoopsAutoJoinChanged()
    {
        var path = Path.Combine(_dir, "engine_config.txt");
        File.WriteAllText(path, "safely_exited  = 0\nbrightness = 0.5\n");
        var guard = new EngineConfigGuard(path);
        // What Coop's /autoconnect does: rewrite, then mark read-only.
        File.WriteAllText(path, "safely_exited  = 1\nbrightness = 0.5\n");
        File.SetAttributes(path, FileAttributes.ReadOnly);

        guard.Restore();
        Assert.Equal("safely_exited  = 0\nbrightness = 0.5\n", File.ReadAllText(path));
        Assert.False(File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly));
    }

    [Fact]
    public void ANewReadOnlyFileIsMadeWritable()
    {
        var path = Path.Combine(_dir, "engine_config.txt");
        var guard = new EngineConfigGuard(path);
        File.WriteAllText(path, "safely_exited  = 1\n");
        File.SetAttributes(path, FileAttributes.ReadOnly);
        guard.Restore();
        Assert.False(File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly));
    }
}

public sealed class ClientLaunchPlanArgumentsTests
{
    [Fact]
    public void ExtraArgumentsFollowTheModuleToken()
    {
        var plan = new ClientLaunchPlan { GameRoot = @"C:\Games\Bannerlord", ModuleIds = ["Native", "CoopNightly"] };
        Assert.Equal(["_MODULES_*Native*CoopNightly*_MODULES_"], plan.Arguments);
        var smoke = plan with { ExtraArguments = ["/autoconnect", "127.0.0.1:4200"] };
        Assert.Equal(["_MODULES_*Native*CoopNightly*_MODULES_", "/autoconnect", "127.0.0.1:4200"], smoke.Arguments);
        Assert.EndsWith("_MODULES_*Native*CoopNightly*_MODULES_ /autoconnect 127.0.0.1:4200", smoke.Describe());
    }
}
