using System.IO;
using ModderLords.Core.Profiles;
using Xunit;

namespace ModderLords.Core.Tests;

public class DataDirMigrationTests
{
    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "mlmig-" + Path.GetRandomFileName());
        Directory.CreateDirectory(d);
        return d;
    }

    [Fact]
    public void CopiesProfilesAndLocalCompatDbAndLeavesLegacyIntact()
    {
        var legacy = TempDir();
        var fresh = Path.Combine(TempDir(), "new");
        Directory.CreateDirectory(Path.Combine(legacy, "profiles"));
        File.WriteAllText(Path.Combine(legacy, "profiles", "default.json"), "{}");
        Directory.CreateDirectory(Path.Combine(legacy, "cache"));
        File.WriteAllText(Path.Combine(legacy, "cache", "default.settings-cache.json"), "{}");
        File.WriteAllText(Path.Combine(legacy, "compat-db.local.json"), "{}");
        // Deliberately not migrated: junctions are bound to their old paths, logs are noise.
        Directory.CreateDirectory(Path.Combine(legacy, "overlay", "default"));
        Directory.CreateDirectory(Path.Combine(legacy, "logs"));
        File.WriteAllText(Path.Combine(legacy, "logs", "launch.log"), "x");

        var copied = DataDirMigration.RunIfNeeded(fresh, legacy);

        Assert.Equal(3, copied);
        Assert.True(File.Exists(Path.Combine(fresh, "profiles", "default.json")));
        Assert.True(File.Exists(Path.Combine(fresh, "cache", "default.settings-cache.json")));
        Assert.True(File.Exists(Path.Combine(fresh, "compat-db.local.json")));
        Assert.False(Directory.Exists(Path.Combine(fresh, "overlay")));
        Assert.False(Directory.Exists(Path.Combine(fresh, "logs")));
        Assert.True(File.Exists(Path.Combine(legacy, "profiles", "default.json")));
        Assert.True(File.Exists(Path.Combine(fresh, DataDirMigration.MarkerFileName)));
    }

    [Fact]
    public void SecondRunCopiesNothingSoADeletedProfileStaysDeleted()
    {
        var legacy = TempDir();
        var fresh = Path.Combine(TempDir(), "new");
        Directory.CreateDirectory(Path.Combine(legacy, "profiles"));
        File.WriteAllText(Path.Combine(legacy, "profiles", "default.json"), "{}");

        Assert.Equal(1, DataDirMigration.RunIfNeeded(fresh, legacy));
        File.Delete(Path.Combine(fresh, "profiles", "default.json"));

        Assert.Equal(0, DataDirMigration.RunIfNeeded(fresh, legacy));
        Assert.False(File.Exists(Path.Combine(fresh, "profiles", "default.json")));
    }

    [Fact]
    public void DoesNotOverwriteAnExistingModderLordsDataDir()
    {
        var legacy = TempDir();
        var fresh = TempDir();
        Directory.CreateDirectory(Path.Combine(legacy, "profiles"));
        File.WriteAllText(Path.Combine(legacy, "profiles", "default.json"), "{\"legacy\":true}");
        Directory.CreateDirectory(Path.Combine(fresh, "profiles"));
        File.WriteAllText(Path.Combine(fresh, "profiles", "mine.json"), "{}");

        Assert.Equal(0, DataDirMigration.RunIfNeeded(fresh, legacy));
        Assert.False(File.Exists(Path.Combine(fresh, "profiles", "default.json")));
        Assert.True(File.Exists(Path.Combine(fresh, DataDirMigration.MarkerFileName)));
    }

    [Fact]
    public void NoLegacyFolderIsNotAnError()
    {
        var fresh = Path.Combine(TempDir(), "new");
        Assert.Equal(0, DataDirMigration.RunIfNeeded(fresh, Path.Combine(Path.GetTempPath(), "mlmig-absent-" + Path.GetRandomFileName())));
    }
}
