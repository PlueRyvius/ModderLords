using System.Text;
using System.Text.Json;
using ModderLords.Core.Config;
using ModderLords.Coop.Config;
using ModderLords.Core.Launch;
using ModderLords.Coop.Launch;
using ModderLords.Core.Profiles;
using ModderLords.Core.Saves;
using ModderLords.Coop.Saves;
using Xunit;

namespace ModderLords.Core.Tests;

public class ServerConfigTests
{
    [Fact]
    public void Render_then_read_round_trips_and_keeps_unknown_keys()
    {
        var settings = new ServerSettings { JoinPort = 4321, Password = "s3cret \"q\"", Steam = false, AutosaveMinutes = 0, LogFile = true, TraceTick = true };
        var unknown = new Dictionary<string, JsonElement> { ["battleSize"] = JsonDocument.Parse("1000").RootElement.Clone() };
        var text = ServerConfig.Render("My Save", settings, unknown);
        Assert.Contains("// ", text);
        var tmp = Path.GetTempFileName();
        File.WriteAllText(tmp, text, new UTF8Encoding(false));
        try
        {
            var snap = ServerConfig.Read(tmp)!;
            Assert.Equal("My Save", snap.SaveName);
            Assert.Equal(4321, snap.Settings.JoinPort);
            Assert.Equal("s3cret \"q\"", snap.Settings.Password);
            Assert.False(snap.Settings.Steam);
            Assert.Equal(0, snap.Settings.AutosaveMinutes);
            Assert.True(snap.Settings.TraceTick);
            Assert.Equal("1000", snap.Unknown["battleSize"].GetRawText());
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void Reads_the_official_commented_format_with_trailing_commas()
    {
        const string official = """
            {
              // UDP port used by Coop clients; forward this port to the server.
              "port": 4200,
              "saveName": "29 August 26",
              "password": "g",
              "logFile": true,
              "steam": true,
              "traceTick": false,
            }
            """;
        var tmp = Path.GetTempFileName();
        File.WriteAllText(tmp, official);
        try
        {
            var snap = ServerConfig.Read(tmp)!;
            Assert.Equal("29 August 26", snap.SaveName);
            Assert.Equal("g", snap.Settings.Password);
            Assert.Empty(snap.Unknown);
        }
        finally { File.Delete(tmp); }
    }
}

public class SaveHeaderTests
{
    private static string WriteSave(string json)
    {
        var tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".sav");
        var body = Encoding.UTF8.GetBytes(json);
        using var fs = File.Create(tmp);
        fs.Write(BitConverter.GetBytes(body.Length));
        fs.Write(body);
        fs.Write(new byte[64]); // fake payload
        return tmp;
    }

    [Fact]
    public void Parses_modules_versions_and_metadata()
    {
        var path = WriteSave("""{"List":{"Modules":"Native;SandBoxCore;Sandbox;ModularSmithing2;Coop","Module_Native":"v1.4.8.118999","Module_ModularSmithing2":"v0.9.28.0","Module_Coop":"v0.1.4.0","ApplicationVersion":"v1.4.8.118999","CharacterName":"Pacarios","MainHeroLevel":"2","DayLong":"1061.9312"}}""");
        try
        {
            var h = SaveHeaderReader.TryRead(path, out var err)!;
            Assert.Null(err);
            Assert.Equal(["Native", "SandBoxCore", "Sandbox", "ModularSmithing2", "Coop"], h.ModuleIds);
            Assert.Equal("v0.9.28.0", h.ModuleVersions["ModularSmithing2"]);
            Assert.Equal("Pacarios", h.CharacterName);
            Assert.Equal(1061.9312, h.DayLong, 3);
            Assert.Equal(["ModularSmithing2", "Coop"], h.CommunityModuleIds);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Compare_reports_added_removed_and_changed()
    {
        var path = WriteSave("""{"List":{"Modules":"Native;A;B","Module_A":"v1.0.0.0","Module_B":"v2.0.0.0"}}""");
        try
        {
            var h = SaveHeaderReader.TryRead(path, out _)!;
            var diffs = SaveHeaderReader.Compare(h, new Dictionary<string, string> { ["A"] = "v1.0.0", ["C"] = "v3.0.0" });
            Assert.DoesNotContain(diffs, d => d.ModuleId == "A");                 // v1.0.0.0 == v1.0.0
            Assert.Contains(diffs, d => d.ModuleId == "B" && d.Kind == "missing now");
            Assert.Contains(diffs, d => d.ModuleId == "C" && d.Kind == "added");
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("v0.9.28", "v0.9.28.0", true)]
    [InlineData("v1.4.8.118999", "v1.4.8.119303", false)]
    [InlineData("v2.4.2.248", "v2.4.2.248", true)]
    public void Versions_compare_numerically(string a, string b, bool equal) => Assert.Equal(equal, SaveHeaderReader.VersionsEqual(a, b));

    [Fact]
    public void Rejects_garbage()
    {
        var tmp = Path.GetTempFileName();
        File.WriteAllBytes(tmp, new byte[] { 0xFF, 0xFF, 0xFF, 0x7F, 1, 2 });
        try { Assert.Null(SaveHeaderReader.TryRead(tmp, out var err)); Assert.NotNull(err); }
        finally { File.Delete(tmp); }
    }
}

public class ProfileStoreTests
{
    [Fact]
    public void Default_roles_match_the_verified_set()
    {
        Assert.Equal(Overlay.ServerRole.DependencyOnly, Profile.DefaultRoleFor("Bannerlord.Harmony"));
        Assert.Equal(Overlay.ServerRole.Run, Profile.DefaultRoleFor("ModularSmithing2"));
        Assert.Equal("a_b", ProfileStore.Safe("a/b"));
    }
}
