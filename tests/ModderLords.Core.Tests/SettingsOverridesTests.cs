using ModderLords.CompatSync;
using ModderLords.Core.Live;

namespace ModderLords.Core.Tests;

public sealed class SettingsOverridesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mbc-ovr-" + Guid.NewGuid().ToString("N"));
    private string Path_ => Path.Combine(_dir, "p.settings.json");

    public SettingsOverridesTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, true);

    [Fact]
    public void RoundTrip_Set_Remove_Clear()
    {
        var o = new SettingsOverrides();
        o.Set("static:IG:IG.Config", new Dictionary<string, string> { ["MaxRecruit"] = "120", ["Enable"] = "true" });
        o.Set("HealOnKill_v1", new Dictionary<string, string> { ["Percent"] = "25" });
        Assert.Equal(3, o.Count);
        SettingsOverridesStore.SaveTo(Path_, o);
        Assert.False(File.Exists(Path_ + ".tmp"));

        var back = SettingsOverridesStore.LoadFrom(Path_);
        Assert.Equal("120", back.Get("static:IG:IG.Config", "MaxRecruit"));
        Assert.Equal("25", back.Get("HealOnKill_v1", "Percent"));
        Assert.NotNull(back.UpdatedAt);

        back.Remove("static:IG:IG.Config", "MaxRecruit");
        Assert.Equal("true", back.Get("static:IG:IG.Config", "Enable"));
        back.Remove("static:IG:IG.Config", "Enable");
        Assert.Null(back.Get("static:IG:IG.Config", "Enable"));
        Assert.Single(back.Objects);
        back.Clear("HealOnKill_v1");
        Assert.True(back.IsEmpty);
        SettingsOverridesStore.SaveTo(Path_, back);
        Assert.False(File.Exists(Path_), "empty set deletes the file");
        Assert.True(SettingsOverridesStore.LoadFrom(Path_).IsEmpty);
    }

    [Fact]
    public void LiveDirFile_IsReadableByTheModule_AndAckByTheLauncher()
    {
        var o = new SettingsOverrides();
        o.Set("static:IG:IG.Config", new Dictionary<string, string> { ["MaxRecruit"] = "120" });
        SettingsOverridesStore.WriteToLiveDir(_dir, o);

        // What Overrides.Load does with the file.
        var root = MiniJson.ParseObject(File.ReadAllText(Path.Combine(_dir, LiveProtocol.OverridesFileName)));
        var objects = MiniJson.GetObject(root, "Objects")!;
        var values = MiniJson.GetObject(objects, "static:IG:IG.Config")!;
        Assert.Equal("120", MiniJson.GetString(values, "MaxRecruit"));

        // What Overrides.WriteAck produces.
        var ack = new Dictionary<string, object?>
        {
            ["Ok"] = true,
            ["Applied"] = new List<object?> { new Dictionary<string, object?> { ["SettingsId"] = "static:IG:IG.Config", ["Changed"] = 1, ["Report"] = "1 changed", ["Persisted"] = "persisted via ConfigManager.CreateAndUpdateConfigForCurrentGame()" } },
            ["Pending"] = new List<object?> { "HealOnKill_v1" },
        };
        File.WriteAllText(Path.Combine(_dir, LiveProtocol.OverridesAckFileName), MiniJson.Serialize(ack));
        var read = SettingsOverridesStore.ReadAck(_dir)!;
        Assert.True(read.Ok);
        var a = Assert.Single(read.Applied);
        Assert.Equal(1, a.Changed);
        Assert.StartsWith("persisted via", a.Persisted);
        Assert.Equal(["HealOnKill_v1"], read.Pending);

        SettingsOverridesStore.WriteToLiveDir(_dir, new SettingsOverrides());
        Assert.False(File.Exists(Path.Combine(_dir, LiveProtocol.OverridesFileName)));
    }

    [Fact]
    public void Cache_RoundTrip()
    {
        var doc = new LiveSettingsDocument { WrittenAt = DateTime.UtcNow, Objects = { new LiveSettingsObject { SettingsId = "X", DisplayName = "X", Groups = { new LiveSettingsGroup { Name = "General", Properties = { new LiveSettingsProperty { Id = "A", Kind = "int", Value = "1", Editable = true } } } } } } };
        var p = Path.Combine(_dir, "c.json");
        Directory.CreateDirectory(_dir);
        File.WriteAllText(p, System.Text.Json.JsonSerializer.Serialize(doc));
        var back = System.Text.Json.JsonSerializer.Deserialize<LiveSettingsDocument>(File.ReadAllText(p))!;
        Assert.Equal("1", back.Objects[0].Groups[0].Properties[0].Value);
    }
}
