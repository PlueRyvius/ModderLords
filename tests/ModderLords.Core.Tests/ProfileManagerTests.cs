using ModderLords.Core.Profiles;
using Xunit;

namespace ModderLords.Core.Tests;

/// <summary>Groups, pins, safe delete and the recent list behind the Profiles window (docs/PROFILE-MANAGER.md).</summary>
public sealed class ProfileManagerTests : IDisposable
{
    private readonly string _name = "mbc-pm-" + Guid.NewGuid().ToString("N");
    private readonly string _index = Path.Combine(Path.GetTempPath(), "ml-profile-index-" + Guid.NewGuid().ToString("N") + ".json");
    private readonly List<string> _cleanup = [];

    public ProfileManagerTests() => ProfileIndexStore.PathOverride = _index;

    public void Dispose()
    {
        ProfileIndexStore.PathOverride = null;
        foreach (var n in new[] { _name, _name + " copy" }) ProfileStore.Delete(n, keepCopy: false);
        foreach (var dir in _cleanup) if (Directory.Exists(dir)) Directory.Delete(dir, true);
        if (File.Exists(_index)) File.Delete(_index);
    }

    [Fact]
    public void Deleting_moves_the_profile_aside_so_it_can_be_put_back()
    {
        ProfileStore.Save(new Profile { Name = _name, Mods = [new() { Id = "CoopNightly" }] });
        File.WriteAllText(ProfileStore.SettingsOverridesPath(_name), "{}");
        var movedTo = ProfileStore.Delete(_name);
        _cleanup.Add(movedTo!);

        Assert.False(ProfileStore.Exists(_name));
        Assert.StartsWith(ProfileStore.DeletedDir, movedTo);
        Assert.True(File.Exists(Path.Combine(movedTo!, Path.GetFileName(ProfileStore.PathFor(_name)))));
        Assert.True(File.Exists(Path.Combine(movedTo!, Path.GetFileName(ProfileStore.SettingsOverridesPath(_name)))));
        Assert.Null(ProfileStore.Delete(_name));   // nothing left to move
    }

    [Fact]
    public void A_deleted_profile_is_listed_and_restores_unless_its_name_is_taken_again()
    {
        ProfileStore.Save(new Profile { Name = _name, Mods = [new() { Id = "CoopNightly" }] });
        var folder = ProfileStore.Delete(_name)!;
        _cleanup.Add(folder);
        Assert.Contains(ProfileStore.ListDeleted(), d => d.Name == _name && d.Folder == folder);

        ProfileStore.Save(new Profile { Name = _name });
        Assert.Contains("exists again", Assert.Throws<InvalidOperationException>(() => ProfileStore.Restore(folder)).Message);
        ProfileStore.Delete(_name, keepCopy: false);

        Assert.Equal(_name, ProfileStore.Restore(folder));
        Assert.Equal(["CoopNightly"], ProfileStore.Load(_name)!.Mods.Select(m => m.Id));
        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    public void Moving_to_a_group_or_pinning_changes_only_that()
    {
        ProfileStore.Save(new Profile { Name = _name, Mods = [new() { Id = "CoopNightly" }], SaveName = "world" });
        ProfileStore.UpdatePlacement(_name, group: "Coop hosting");
        ProfileStore.UpdatePlacement(_name, pinned: true);
        var saved = ProfileStore.Load(_name)!;
        Assert.Equal("Coop hosting", saved.Group);
        Assert.True(saved.Pinned);
        Assert.Equal(["CoopNightly"], saved.Mods.Select(m => m.Id));
        Assert.Equal("world", saved.SaveName);
    }

    [Fact]
    public void A_duplicate_keeps_the_group_but_not_the_pin()
    {
        ProfileStore.Save(new Profile { Name = _name, Group = "Testing", Pinned = true, Mods = [new() { Id = "CoopNightly" }] });
        var copy = ProfileStore.Duplicate(_name, _name + " copy");
        Assert.Equal("Testing", copy.Group);
        Assert.False(copy.Pinned);
        Assert.True(ProfileStore.Exists(_name + " copy"));
        Assert.True(ProfileStore.Exists(_name));
    }

    [Fact]
    public void An_old_profile_file_reads_as_ungrouped_and_unpinned()
    {
        var profile = System.Text.Json.JsonSerializer.Deserialize<Profile>("""{ "SchemaVersion": 1, "Name": "old", "Mods": [] }""")!;
        Assert.Equal("", profile.Group);
        Assert.False(profile.Pinned);
    }

    [Fact]
    public void The_recent_list_keeps_the_last_used_first_and_follows_renames_and_deletes()
    {
        foreach (var n in Enumerable.Range(1, ProfileIndexStore.RecentCount + 2)) ProfileIndexStore.Used("p" + n);
        ProfileIndexStore.Used("p3");
        var recent = ProfileIndexStore.Load().Recent;
        Assert.Equal(ProfileIndexStore.RecentCount, recent.Count);
        Assert.Equal("p3", recent[0]);
        Assert.Equal("p8", recent[1]);

        ProfileIndexStore.Renamed("p8", "eight");
        ProfileIndexStore.Forget("p3");
        Assert.Equal(["eight", "p7", "p6", "p5", "p4"], ProfileIndexStore.Load().Recent);
    }

    [Fact]
    public void A_group_can_exist_before_anything_is_in_it()
    {
        ProfileIndexStore.AddGroup("Testing");
        ProfileIndexStore.AddGroup("testing");
        Assert.Equal(["Testing"], ProfileIndexStore.Load().Groups);
        ProfileIndexStore.RemoveGroup("TESTING");
        Assert.Empty(ProfileIndexStore.Load().Groups);
    }
}
