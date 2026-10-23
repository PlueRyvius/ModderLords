using ModderLords.Core.Compat;

namespace ModderLords.Core.Tests;

/// <summary>The Supported mods tab's list as shipped: every entry is complete enough to show and to match an installed mod.</summary>
public sealed class SupportedModsTests
{
    private static readonly string[] Statuses = ["Works", "Settings", "Partial", "Broken"];

    [Fact]
    public void The_bundled_list_loads_and_every_entry_is_complete()
    {
        var list = SupportedModList.Bundled();
        Assert.NotEmpty(list.General);
        Assert.NotEmpty(list.Mods);
        foreach (var m in list.Mods)
        {
            Assert.False(string.IsNullOrWhiteSpace(m.Name));
            Assert.Contains(m.Status, Statuses);
            Assert.False(string.IsNullOrWhiteSpace(m.Summary), m.Name);
            Assert.True(m.Details.Count + m.Setup.Count + m.Limits.Count > 0, m.Name);
            Assert.NotEmpty(m.ModuleIds);
            Assert.All(m.ModuleIds, id => Assert.Equal(id.Trim(), id));
        }
    }

    [Fact]
    public void No_module_id_belongs_to_two_entries()
    {
        var ids = SupportedModList.Bundled().Mods.SelectMany(m => m.ModuleIds).ToList();
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Missing_fields_read_as_empty()
    {
        var list = SupportedModList.Parse("""{ "mods": [ { "name": "X" } ] }""");
        var m = Assert.Single(list.Mods);
        Assert.Equal("X", m.Name);
        Assert.Empty(m.ModuleIds);
        Assert.False(m.NeedsCompat);
        Assert.Empty(list.General);
    }
}
