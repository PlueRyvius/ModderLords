using ModderLords.CompatSync;
using ModderLords.Core.Live;
using ModderLords.Core.Logs;

namespace ModderLords.Core.Tests;

/// <summary>The launcher writes requests with System.Text.Json and the engine module reads them with MiniJson (and vice versa for acks and settings.json).</summary>
public sealed class LiveSettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mbc-live-" + Guid.NewGuid().ToString("N"));

    public LiveSettingsTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, true);

    [Fact]
    public void Request_WrittenByLauncher_ReadByModule()
    {
        var token = LiveProtocol.WriteRequest(_dir, new LiveApplyRequest
        {
            SettingsId = "ImprovedGarrisons_v1",
            Values = { ["MaxGarrison"] = "250", ["Enabled"] = "true", ["Name"] = "quote \" and \\ backslash é" },
        });
        var path = Path.Combine(_dir, LiveProtocol.RequestFileName(token));
        Assert.True(File.Exists(path));
        Assert.False(File.Exists(path + ".tmp"));

        // This is exactly what LiveSettings.ProcessRequests does with the file.
        var req = MiniJson.ParseObject(File.ReadAllText(path));
        Assert.Equal("ImprovedGarrisons_v1", MiniJson.GetString(req, "SettingsId"));
        var values = MiniJson.GetObject(req, "Values")!;
        Assert.Equal("250", MiniJson.GetString(values, "MaxGarrison"));
        Assert.Equal("true", MiniJson.GetString(values, "Enabled"));
        Assert.Equal("quote \" and \\ backslash é", MiniJson.GetString(values, "Name"));
    }

    [Fact]
    public void Ack_WrittenByModule_ReadByLauncher()
    {
        var token = LiveProtocol.NewToken();
        var ack = new Dictionary<string, object?> { ["Ok"] = true, ["SettingsId"] = "X", ["Changed"] = 2, ["Report"] = "2 changed, unsupported: Colour", ["Persisted"] = "persisted" };
        File.WriteAllText(Path.Combine(_dir, LiveProtocol.AckFileName(token)), MiniJson.Serialize(ack));

        var read = LiveProtocol.TakeAck(_dir, token)!;
        Assert.True(read.Ok);
        Assert.Equal(2, read.Changed);
        Assert.Equal("2 changed, unsupported: Colour", read.Report);
        Assert.Equal("persisted", read.Persisted);
        Assert.False(File.Exists(Path.Combine(_dir, LiveProtocol.AckFileName(token))), "ack is consumed");
        Assert.Null(LiveProtocol.TakeAck(_dir, token));
    }

    [Fact]
    public void SettingsDocument_WrittenByModule_ReadByLauncher()
    {
        // Shape produced by McmBridge.Describe + LiveSettings.WriteDescriptionIfChanged.
        var prop = new Dictionary<string, object?> { ["Id"] = "MaxGarrison", ["DisplayName"] = "Max garrison", ["Hint"] = "Cap", ["Kind"] = "int", ["Value"] = "200", ["Editable"] = true, ["Min"] = 0.0, ["Max"] = 1000.0 };
        var en = new Dictionary<string, object?> { ["Id"] = "Mode", ["DisplayName"] = "Mode", ["Kind"] = "enum", ["Value"] = "Fast", ["Editable"] = true, ["Choices"] = new List<string> { "Slow", "Fast" } };
        var color = new Dictionary<string, object?> { ["Id"] = "Tint", ["DisplayName"] = "Tint", ["Kind"] = "Color", ["Value"] = "Color(1,0,0)", ["Editable"] = false, ["RequireRestart"] = true };
        var doc = new Dictionary<string, object?>
        {
            ["SchemaVersion"] = 1,
            ["WrittenAt"] = new DateTime(2026, 9, 2, 12, 0, 0, DateTimeKind.Utc).ToString("o"),
            ["Objects"] = new List<object?>
            {
                new Dictionary<string, object?>
                {
                    ["SettingsId"] = "IG", ["DisplayName"] = "Improved Garrisons", ["Folder"] = "ImprovedGarrisons",
                    ["Groups"] = new List<object?>
                    {
                        new Dictionary<string, object?> { ["Name"] = "General", ["Properties"] = new List<object?> { prop, en } },
                        new Dictionary<string, object?> { ["Name"] = "General / Looks", ["Properties"] = new List<object?> { color } },
                    },
                },
            },
        };
        File.WriteAllText(Path.Combine(_dir, LiveProtocol.SettingsFileName), MiniJson.Serialize(doc));

        var read = LiveProtocol.ReadSettings(_dir)!;
        Assert.Equal(1, read.SchemaVersion);
        Assert.Equal(new DateTime(2026, 9, 2, 12, 0, 0, DateTimeKind.Utc), read.WrittenAt.ToUniversalTime());
        var obj = Assert.Single(read.Objects);
        Assert.Equal("Improved Garrisons", obj.DisplayName);
        Assert.Equal(3, obj.PropertyCount);
        var p = obj.Groups[0].Properties[0];
        Assert.Equal("int", p.Kind); Assert.True(p.Editable); Assert.Equal(0, p.Min); Assert.Equal(1000, p.Max); Assert.Equal("200", p.Value);
        var e = obj.Groups[0].Properties[1];
        Assert.Equal(new[] { "Slow", "Fast" }, e.Choices);
        var c = obj.Groups[1].Properties[0];
        Assert.False(c.Editable); Assert.True(c.RequireRestart); Assert.Null(c.Choices);
    }

    [Fact]
    public void Tokens_SortInCreationOrder_AsFileNames()
    {
        var a = LiveProtocol.NewToken();
        Thread.Sleep(2);
        var b = LiveProtocol.NewToken();
        Assert.Equal(19, a.Length);
        Assert.True(string.CompareOrdinal(LiveProtocol.RequestFileName(a), LiveProtocol.RequestFileName(b)) < 0);
        // What the module does: Directory.GetFiles + Array.Sort(Ordinal) yields a before b.
        File.WriteAllText(Path.Combine(_dir, LiveProtocol.RequestFileName(b)), "{}");
        File.WriteAllText(Path.Combine(_dir, LiveProtocol.RequestFileName(a)), "{}");
        var files = Directory.GetFiles(_dir, LiveProtocol.RequestPrefix + "*.json");
        Array.Sort(files, StringComparer.Ordinal);
        Assert.EndsWith(LiveProtocol.RequestFileName(a), files[0]);
    }

    [Fact]
    public void Reset_ClearsStaleFiles()
    {
        File.WriteAllText(Path.Combine(_dir, LiveProtocol.SettingsFileName), "{}");
        LiveProtocol.Reset(_dir);
        Assert.True(Directory.Exists(_dir));
        Assert.Empty(Directory.GetFiles(_dir));
        Assert.Null(LiveProtocol.ReadSettings(_dir));
    }

    [Fact]
    public async Task Client_RaisesChanged_WhenSettingsRewritten()
    {
        using var client = new LiveSettingsClient(_dir);
        var got = new TaskCompletionSource<LiveSettingsDocument>();
        client.Changed += d => { if (d is not null) got.TrySetResult(d); };
        var doc = new Dictionary<string, object?> { ["SchemaVersion"] = 1, ["WrittenAt"] = DateTime.UtcNow.ToString("o"), ["Objects"] = new List<object?>() };
        var path = Path.Combine(_dir, LiveProtocol.SettingsFileName);
        File.WriteAllText(path + ".tmp", MiniJson.Serialize(doc));
        File.Move(path + ".tmp", path);
        var done = await Task.WhenAny(got.Task, Task.Delay(5000));
        Assert.Same(got.Task, done);
    }

    [Fact]
    public async Task Client_ApplyAsync_ReturnsAck_OrNullOnTimeout()
    {
        using var client = new LiveSettingsClient(_dir);
        var none = await client.ApplyAsync("X", new Dictionary<string, string> { ["A"] = "1" }, TimeSpan.FromMilliseconds(600));
        Assert.Null(none);
        var pending = Directory.GetFiles(_dir, LiveProtocol.RequestPrefix + "*.json");
        Assert.Single(pending);

        // Simulate the module answering.
        var task = client.ApplyAsync("X", new Dictionary<string, string> { ["A"] = "2" }, TimeSpan.FromSeconds(5));
        await Task.Delay(100);
        var token = Directory.GetFiles(_dir, LiveProtocol.RequestPrefix + "*.json").Select(Path.GetFileNameWithoutExtension).Select(n => n![LiveProtocol.RequestPrefix.Length..]).OrderBy(t => t).Last();
        File.WriteAllText(Path.Combine(_dir, LiveProtocol.AckFileName(token)), MiniJson.Serialize(new Dictionary<string, object?> { ["Ok"] = true, ["Changed"] = 1, ["Report"] = "1 changed" }));
        var ack = await task;
        Assert.NotNull(ack);
        Assert.Equal(1, ack!.Changed);
    }

    [Fact]
    public void MiniJson_RoundTripsEscapesAndNumbers()
    {
        var src = new Dictionary<string, object?> { ["s"] = "a\"b\\c\nd\te", ["n"] = 12L, ["f"] = 1.5, ["neg"] = -3L, ["b"] = false, ["z"] = null, ["arr"] = new List<object?> { 1L, "x", null } };
        var back = MiniJson.ParseObject(MiniJson.Serialize(src));
        Assert.Equal("a\"b\\c\nd\te", back["s"]);
        Assert.Equal(12L, back["n"]);
        Assert.Equal(1.5, back["f"]);
        Assert.Equal(-3L, back["neg"]);
        Assert.Equal(false, back["b"]);
        Assert.Null(back["z"]);
        Assert.Equal(3, ((List<object?>)back["arr"]!).Count);
        Assert.Throws<FormatException>(() => MiniJson.Parse("{ \"a\": }"));
        Assert.Throws<FormatException>(() => MiniJson.Parse("[1] x"));
    }

    [Theory]
    [InlineData("[ModderLords.Compat] live apply IG: 1 changed", LogCategory.Tool)]
    [InlineData("[ModderLords.Compat] WARNING settings sync disabled", LogCategory.Tool)]
    public void Classifier_TreatsCompatModuleLinesAsTool(string line, LogCategory expected)
        => Assert.Equal(expected, LogClassifier.Classify(line).Category);
}
