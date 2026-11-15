using ModderLords.Coop.Testing;
using ModderLords.Core.Testing;
using Xunit;

namespace ModderLords.Core.Tests;

/// <summary>The server's own dump in a test run, and suite files that use a module's steps.</summary>
public sealed class CrashHandlingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ml-crash-" + Guid.NewGuid().ToString("N"));

    public CrashHandlingTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void The_server_writes_its_dump_into_the_run_folder_and_nothing_closes_it_until_done()
    {
        var dump = new ServerDump(_root);
        Assert.Equal(Path.Combine(_root, "crash", "server-%p.dmp"), dump.Environment["DOTNET_DbgMiniDumpName"]);
        Assert.Equal("2", dump.Environment["DOTNET_DbgMiniDumpType"]);
        Assert.False(dump.Writing);

        // The runtime's own lines on the server's console (2026-11-15).
        dump.Observe("[createdump] Writing minidump with heap to file C:\\run\\crash\\server-38720.dmp");
        Assert.True(dump.Writing);
        dump.Observe("[Coop] something else");
        Assert.True(dump.Writing);
        dump.Observe("[createdump] Dump successfully written");
        Assert.False(dump.Writing);
        Assert.True(dump.Written);
    }

    [Fact]
    public void A_suite_file_can_name_a_module_to_use_its_steps()
    {
        var file = Path.Combine(_root, "try.json");
        File.WriteAllText(file, """{ "api": 2, "name": "try", "module": "stash", "steps": [ { "id": "stash.count", "side": "server" } ] }""");
        Assert.Equal("stash", TestSuites.Load(file).Module);

        File.WriteAllText(file, """{ "api": 2, "name": "try", "steps": [ { "id": "game.onMap", "side": "server" } ] }""");
        Assert.Equal("", TestSuites.Load(file).Module);

        File.WriteAllText(file, """{ "api": 2, "name": "try", "module": "nope", "steps": [ { "id": "game.onMap", "side": "server" } ] }""");
        Assert.Contains("no test module 'nope'", Assert.Throws<FormatException>(() => TestSuites.Load(file)).Message);
    }
}
