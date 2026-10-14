using System.Text;
using ModderLords.Coop.Launch;
using ModderLords.Coop.Saves;
using Xunit;

namespace ModderLords.Core.Tests;

/// <summary>
/// The launcher's copies of the hosted save. Reported 2026-10-13: the server wrote over the only copy of a 900-day
/// campaign with a world that crashed on every boot, and nothing older existed anywhere.
/// </summary>
public class SaveBackupsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("modderlords-backups").FullName;
    private readonly ServerPaths _paths;
    private static readonly DateTime T0 = new(2026, 10, 13, 19, 0, 0, DateTimeKind.Local);

    public SaveBackupsTests() => _paths = ServerPaths.Create(Path.Combine(_root, "DedicatedServer"), Path.Combine(_root, "CoopData"));

    public void Dispose() => Directory.Delete(_root, recursive: true);

    /// <summary>Writes the hosted save (and, unless told not to, Coop's session file) as the server would leave them.</summary>
    private string WriteSave(string name, string content, DateTime writtenAt, bool session = true)
    {
        Directory.CreateDirectory(_paths.SavesDir);
        var path = SavePreparer.SavePath(_paths, name);
        File.WriteAllText(path, content, Encoding.UTF8);
        File.SetLastWriteTimeUtc(path, writtenAt.ToUniversalTime());
        if (session) File.WriteAllText(SavePreparer.SessionPath(path), "{\"Players\":[\"" + content + "\"]}");
        return path;
    }

    [Fact]
    public void A_backup_holds_the_save_and_its_session_file()
    {
        WriteSave("World", "day 900", T0);

        var b = SaveBackups.Take(_paths, "World", SaveBackups.Kind.Start, T0.AddMinutes(1));

        Assert.NotNull(b);
        Assert.Equal("day 900", File.ReadAllText(b.SavePath));
        Assert.Equal("{\"Players\":[\"day 900\"]}", File.ReadAllText(b.SessionPath!));
        Assert.StartsWith(SaveBackups.DirFor(_paths, "World"), b.SavePath);
        // Never beside the hosted saves, where the Saves tab and the server would take it for a world.
        Assert.DoesNotContain(_paths.SavesDir, b.SavePath);
        Assert.Equal([b.SavePath], SaveBackups.List(_paths, "World").Select(x => x.SavePath));
    }

    [Fact]
    public void An_unchanged_save_is_not_copied_again()
    {
        // The point: a server that crashes on every boot must not rotate the good copies out with the broken one.
        WriteSave("World", "broken", T0);
        Assert.NotNull(SaveBackups.Take(_paths, "World", SaveBackups.Kind.Start, T0.AddMinutes(1)));
        for (var i = 2; i < 20; i++)
            Assert.Null(SaveBackups.Take(_paths, "World", SaveBackups.Kind.Start, T0.AddMinutes(i)));
        Assert.Single(SaveBackups.List(_paths, "World"));

        WriteSave("World", "changed", T0.AddMinutes(30));
        Assert.NotNull(SaveBackups.Take(_paths, "World", SaveBackups.Kind.Running, T0.AddMinutes(31)));
    }

    [Fact]
    public void While_the_server_runs_a_save_written_moments_ago_is_left_alone()
    {
        WriteSave("World", "being written", T0);

        Assert.Null(SaveBackups.Take(_paths, "World", SaveBackups.Kind.Running, T0.AddSeconds(5), serverRunning: true));
        Assert.NotNull(SaveBackups.Take(_paths, "World", SaveBackups.Kind.Running, T0 + SaveBackups.Settled + TimeSpan.FromSeconds(1), serverRunning: true));
    }

    [Fact]
    public void A_long_session_cannot_push_out_the_copy_from_before_it_started()
    {
        WriteSave("World", "before the session", T0);
        var start = SaveBackups.Take(_paths, "World", SaveBackups.Kind.Start, T0.AddMinutes(1))!;

        for (var hour = 1; hour <= 45; hour++)
        {
            WriteSave("World", "hour " + hour, T0.AddHours(hour));
            Assert.NotNull(SaveBackups.Take(_paths, "World", SaveBackups.Kind.Running, T0.AddHours(hour).AddMinutes(1)));
        }

        var all = SaveBackups.List(_paths, "World");
        Assert.Equal(SaveBackups.KeepRunning, all.Count(b => b.Kind == SaveBackups.Kind.Running));
        Assert.Contains(all, b => b.SavePath == start.SavePath);
        Assert.Equal("hour 45", File.ReadAllText(all[0].SavePath));
        // Pruned copies take their session file with them.
        Assert.Equal(all.Count, Directory.GetFiles(SaveBackups.DirFor(_paths, "World"), "*.json").Length);
    }

    [Fact]
    public void Start_copies_are_kept_to_their_own_limit()
    {
        for (var i = 0; i < SaveBackups.KeepStart + 3; i++)
        {
            WriteSave("World", "boot " + i, T0.AddHours(i));
            SaveBackups.Take(_paths, "World", SaveBackups.Kind.Start, T0.AddHours(i).AddMinutes(1));
        }
        var starts = SaveBackups.List(_paths, "World").Where(b => b.Kind == SaveBackups.Kind.Start).ToList();
        Assert.Equal(SaveBackups.KeepStart, starts.Count);
        Assert.Equal("boot " + (SaveBackups.KeepStart + 2), File.ReadAllText(starts[0].SavePath));
    }

    [Fact]
    public void Restoring_makes_a_new_save_and_leaves_the_hosted_one_alone()
    {
        WriteSave("World", "good", T0);
        var good = SaveBackups.Take(_paths, "World", SaveBackups.Kind.Start, T0.AddMinutes(1))!;
        WriteSave("World", "broken", T0.AddHours(2));

        var name = SaveBackups.Restore(_paths, good, T0.AddHours(3));
        var again = SaveBackups.Restore(_paths, good, T0.AddHours(3));

        Assert.Equal("World_restored_20261013-190100", name);
        Assert.Equal(name + "_2", again);
        Assert.Equal("good", File.ReadAllText(SavePreparer.SavePath(_paths, name)));
        Assert.Equal("{\"Players\":[\"good\"]}", File.ReadAllText(SavePreparer.SessionPath(SavePreparer.SavePath(_paths, name))));
        Assert.Equal("broken", File.ReadAllText(SavePreparer.SavePath(_paths, "World")));
    }

    [Fact]
    public void A_save_without_a_session_file_is_still_backed_up_and_says_so()
    {
        WriteSave("World", "no players yet", T0, session: false);
        var b = SaveBackups.Take(_paths, "World", SaveBackups.Kind.Start, T0.AddMinutes(1))!;
        Assert.Null(b.SessionPath);

        var name = SaveBackups.Restore(_paths, b, T0.AddMinutes(2));
        Assert.False(File.Exists(SavePreparer.SessionPath(SavePreparer.SavePath(_paths, name))));
    }

    [Fact]
    public void Files_someone_else_put_in_the_folder_are_neither_listed_nor_deleted()
    {
        var dir = SaveBackups.DirFor(_paths, "World");
        Directory.CreateDirectory(dir);
        var stranger = Path.Combine(dir, "my-own-copy.sav");
        File.WriteAllText(stranger, "mine");

        for (var hour = 0; hour < SaveBackups.KeepRunning + 2; hour++)
        {
            WriteSave("World", "hour " + hour, T0.AddHours(hour));
            SaveBackups.Take(_paths, "World", SaveBackups.Kind.Running, T0.AddHours(hour).AddMinutes(1));
        }

        Assert.DoesNotContain(SaveBackups.List(_paths, "World"), b => b.SavePath == stranger);
        Assert.True(File.Exists(stranger));
    }

    [Fact]
    public void No_save_means_no_backup() =>
        Assert.Null(SaveBackups.Take(_paths, "Missing", SaveBackups.Kind.Start, T0));
}
