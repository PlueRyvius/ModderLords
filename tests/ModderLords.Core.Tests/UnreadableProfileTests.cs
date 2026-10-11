using ModderLords.Core.Profiles;

namespace ModderLords.Core.Tests;

/// <summary>
/// A profile this version cannot read must not stop the app. 1.2.8 added a role; a 1.2.7 copy that met it in the
/// first profile threw from the main window's constructor and so never reached its own update check.
/// </summary>
public class UnreadableProfileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ModderLords-profile-tests", Guid.NewGuid().ToString("N"));
    public UnreadableProfileTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, true);

    private string Write(string name, string json)
    {
        var path = Path.Combine(_dir, name + ".json");
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void ARoleFromANewerVersionIsReportedNotThrown()
    {
        var json = """{ "Name": "Newer", "Mods": [ { "Id": "A", "Role": "Run" }, { "Id": "B", "Role": "RoleFromTheFuture" } ] }""";
        var path = Write("Newer", json);

        var profile = ProfileStore.TryLoadFile(path, "Newer", out var problem);

        Assert.Null(profile);
        Assert.NotNull(problem);
        Assert.Contains("“Newer”", problem);
        Assert.Contains("update ModderLords", problem);
        Assert.Contains("$.Mods[1].Role", problem);
        Assert.DoesNotContain("System.", problem);
        Assert.Equal(json, File.ReadAllText(path)); // Left exactly as it was, for the version that can read it.
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("{ \"Name\": ")]
    [InlineData("not json at all")]
    public void ADamagedFileIsReportedNotThrown(string content)
    {
        var profile = ProfileStore.TryLoadFile(Write("Damaged", content), "Damaged", out var problem);
        Assert.Null(profile);
        Assert.NotNull(problem);
    }

    [Fact]
    public void AMissingProfileIsNotAProblem()
    {
        var profile = ProfileStore.TryLoadFile(Path.Combine(_dir, "nobody.json"), "nobody", out var problem);
        Assert.Null(profile);
        Assert.Null(problem);
    }

    [Fact]
    public void AReadableProfileStillLoads()
    {
        var path = Write("Fine", """{ "Name": "Fine", "Mods": [ { "Id": "A", "Role": "ClientOptional" } ] }""");
        var profile = ProfileStore.TryLoadFile(path, "Fine", out var problem);
        Assert.Null(problem);
        Assert.Equal("A", Assert.Single(profile!.Mods).Id);
    }
}