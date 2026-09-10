using ModderLords.Coop.Launch;

namespace ModderLords.Core.Tests;

public sealed class WorldCreationRequestTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "world-request-" + Guid.NewGuid().ToString("N"));
    private ServerPaths Paths => ServerPaths.Create(_root, Path.Combine(_root, "data"), Path.Combine(_root, "coop"));

    [Fact]
    public void Conflicting_arguments_do_not_create_directories()
    {
        Assert.Throws<ArgumentException>(() => WorldCreationRequest.Validate(Paths, "fresh", true));
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public void Existing_save_is_rejected_and_preserved()
    {
        Directory.CreateDirectory(Paths.SavesDir);
        var file = Path.Combine(Paths.SavesDir, "existing.sav");
        File.WriteAllText(file, "precious save");
        Assert.Throws<IOException>(() => WorldCreationRequest.Validate(Paths, "existing", false));
        Assert.Equal("precious save", File.ReadAllText(file));
    }

    [Fact]
    public void New_name_validation_is_read_only()
    {
        WorldCreationRequest.Validate(Paths, "fresh", false);
        Assert.False(Directory.Exists(_root));
    }

    [Theory]
    [InlineData(" fresh")]
    [InlineData("fresh ")]
    [InlineData("fresh.")]
    public void Ambiguous_windows_names_are_rejected_without_changes(string name)
    {
        Assert.Throws<ArgumentException>(() => WorldCreationRequest.Validate(Paths, name, false));
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public void Diagnostic_environment_keeps_both_data_roots_isolated()
    {
        var plan = new LaunchPlan { Paths = Paths, ModuleIds = [] };
        Assert.Equal(Paths.DataDir, plan.Environment()["BANNERLORD_USER_DIR"]);
        Assert.Equal(Paths.CoopDataDir, plan.Environment()["COOP_DATA_DIR"]);
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
}
