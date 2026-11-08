using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using ModderLords.Core.Modules;
using ModderLords.Core.Profiles;
using Xunit;

namespace ModderLords.Core.Tests;

/// <summary>verified-profiles.json, the version comparison and the GitHub downloads (docs/PROFILE-MANAGER.md, decisions 6–12).</summary>
public sealed class VerifiedProfilesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ml-verified-" + Guid.NewGuid().ToString("N"));

    public VerifiedProfilesTests() => DownloadedMods.RootOverride = Path.Combine(_root, "mods");

    public void Dispose()
    {
        DownloadedMods.RootOverride = null;
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private static string Repo([System.Runtime.CompilerServices.CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", ".."));

    private static string Entry(string gitHub) => $$"""
        { "schemaVersion": 1, "profiles": [ { "id": "p", "name": "P", "mods": [
            { "id": "Mod", "version": "v1.0.0", "workshopId": 1, "gitHub": {{gitHub}} } ],
          "verifications": [ { "level": "gold", "date": "2026-11-08" } ] } ] }
        """;

    private const string GoodRelease = """{ "repo": "PlueRyvius/Mod", "tag": "v1.0.0", "asset": "Mod.zip", "sha256": "c60a7c6622298b6851fedfb8523200111b9604b88829e9f84e43b3180dc5ed36" }""";

    [Fact]
    public void The_bundled_file_reads_and_names_only_allowed_releases()
    {
        var file = VerifiedProfiles.Parse(File.ReadAllText(Path.Combine(Repo(), "src", "ModderLords.Core", VerifiedProfiles.BundledFileName)));
        Assert.Contains(file.Profiles, p => p.Id == "core");
        Assert.All(file.Profiles.SelectMany(p => p.Mods), m => Assert.False(string.IsNullOrEmpty(m.Version)));
    }

    [Theory]
    [InlineData("""{ "repo": "SomeoneElse/Mod", "tag": "v1.0.0", "asset": "Mod.zip", "sha256": "c60a7c6622298b6851fedfb8523200111b9604b88829e9f84e43b3180dc5ed36" }""", "not an allowed GitHub account")]
    [InlineData("""{ "repo": "PlueRyvius/Mod", "tag": "v1.0.0", "asset": "Mod.zip", "sha256": "" }""", "SHA-256")]
    [InlineData("""{ "repo": "PlueRyvius/Mod", "tag": "v1.0.0", "asset": "../Mod.exe", "sha256": "c60a7c6622298b6851fedfb8523200111b9604b88829e9f84e43b3180dc5ed36" }""", "no release tag or zip")]
    public void A_release_that_is_not_the_maintainers_or_not_pinned_is_refused(string release, string reason) =>
        Assert.Contains(reason, Assert.Throws<InvalidDataException>(() => VerifiedProfiles.Parse(Entry(release))).Message);

    [Fact]
    public void A_profile_shows_its_strongest_level_and_a_draft_none()
    {
        var p = VerifiedProfiles.Parse(Entry(GoodRelease)).Profiles[0];
        p.Verifications.Add(new Verification { Level = VerificationLevel.Green });
        Assert.Equal(VerificationLevel.Gold, p.Level);
        Assert.Null(new VerifiedProfile().Level);
    }

    [Fact]
    public void Each_mod_is_compared_with_the_verified_version_and_fetched_from_github_only_when_the_workshop_lacks_it()
    {
        var entry = new VerifiedProfile
        {
            Id = "p", Name = "P",
            Mods =
            [
                new() { Id = "Same", Version = "v1.0.0", WorkshopId = 1 },
                new() { Id = "Older", Version = "v2.0.0", WorkshopId = 2, GitHub = new() { Repo = "PlueRyvius/Older" } },
                new() { Id = "Gone", Version = "v1.0.0", WorkshopId = 3, WorkshopVersion = "v1.0.0", GitHub = new() { Repo = "PlueRyvius/Gone" } },
            ],
        };
        var installed = new Dictionary<string, string> { ["Same"] = "v1.0", ["Older"] = "v1.9.0" };
        var checks = VerifiedProfiles.Compare(entry, id => installed.TryGetValue(id, out var v) ? [Module(id, v)] : []);

        Assert.Equal(VerifiedModState.Match, checks[0].State);
        Assert.Equal((VerifiedModState.Different, "v1.9.0", VerifiedFetch.GitHub), (checks[1].State, checks[1].Installed, checks[1].Fetch));
        Assert.Equal((VerifiedModState.Missing, VerifiedFetch.Workshop), (checks[2].State, checks[2].Fetch));   // the Workshop serves v1.0.0
    }

    [Fact]
    public void A_fetched_file_is_refused_when_it_is_older_or_much_smaller_than_the_bundled_one()
    {
        var bundled = VerifiedProfiles.Parse(Entry(GoodRelease));
        bundled.Profiles.Add(new VerifiedProfile { Id = "q", Name = "Q" });
        bundled.Profiles.Add(new VerifiedProfile { Id = "r", Name = "R" });
        Assert.Contains("profiles", Assert.Throws<InvalidDataException>(() => VerifiedProfilesRemote.Validate(Encoding.UTF8.GetBytes(Entry(GoodRelease)), bundled)).Message);

        var older = Entry(GoodRelease).Replace("2026-11-08", "2026-10-01");
        Assert.Contains("older", Assert.Throws<InvalidDataException>(() => VerifiedProfilesRemote.Validate(Encoding.UTF8.GetBytes(older), VerifiedProfiles.Parse(Entry(GoodRelease)))).Message);
        Assert.Single(VerifiedProfilesRemote.Validate(Encoding.UTF8.GetBytes(Entry(GoodRelease)), VerifiedProfiles.Parse(Entry(GoodRelease))).Profiles);
    }

    [Fact]
    public async Task A_release_is_installed_into_its_own_version_folder_only_when_its_hash_and_version_match()
    {
        var zip = Zip("Mod", "v1.0.0");
        var mod = new VerifiedMod { Id = "Mod", Version = "v1.0.0", GitHub = new() { Repo = "PlueRyvius/Mod", Tag = "v1.0.0", Asset = "Mod.zip", Sha256 = Sha(zip) } };
        using var http = new HttpClient(new Serve(zip));

        var folder = await DownloadedMods.InstallAsync(mod, http);
        Assert.Equal(DownloadedMods.FolderFor("Mod", "v1.0.0"), folder);
        Assert.True(DownloadedMods.IsDownloaded(folder));
        Assert.Equal("v1.0.0", Assert.Single(DownloadedMods.Copies("Mod")).Version);
        Assert.Empty(Directory.EnumerateFileSystemEntries(DownloadedMods.Root, ".*"));   // no download or staging left behind

        var tampered = new VerifiedMod { Id = "Mod", Version = "v1.1.0", GitHub = new() { Repo = "PlueRyvius/Mod", Tag = "v1.1.0", Asset = "Mod.zip", Sha256 = new string('0', 64) } };
        Assert.Contains("SHA-256", (await Assert.ThrowsAsync<InvalidDataException>(() => DownloadedMods.InstallAsync(tampered, http))).Message);

        var wrongVersion = new VerifiedMod { Id = "Mod", Version = "v1.1.0", GitHub = new() { Repo = "PlueRyvius/Mod", Tag = "v1.1.0", Asset = "Mod.zip", Sha256 = Sha(zip) } };
        Assert.Contains("not v1.1.0", (await Assert.ThrowsAsync<InvalidDataException>(() => DownloadedMods.InstallAsync(wrongVersion, http))).Message);
        Assert.False(Directory.Exists(DownloadedMods.FolderFor("Mod", "v1.1.0")));
    }

    [Fact]
    public void Using_a_verified_profile_pins_downloads_and_keeps_this_pcs_folders()
    {
        var download = Directory.CreateDirectory(DownloadedMods.FolderFor("Mine", "v2.0.0")).FullName;
        var entry = new VerifiedProfile
        {
            Id = "p", Name = "P",
            Mods = [new() { Id = "Theirs", Version = "v1.0.0", WorkshopId = 5 }, new() { Id = "Mine", Version = "v2.0.0" }],
        };
        var checks = VerifiedProfiles.Compare(entry, id => id == "Mine" ? [Module("Mine", "v2.0.0", download)] : [Module(id, "v1.0.0")]);
        var setup = new Profile { Name = "open", GameRoot = @"D:\Game", CustomModRoots = [@"E:\Mods"], Group = "Mine", Pinned = true, SaveName = "my world" };

        var p = VerifiedProfiles.ToProfile(entry, "P", setup, checks);
        Assert.Equal(("p", @"D:\Game", "", false, ""), (p.VerifiedId, p.GameRoot, p.Group, p.Pinned, p.SaveName));
        Assert.Equal([@"E:\Mods"], p.CustomModRoots);
        Assert.Equal(["Theirs", "Mine"], p.Mods.Select(m => m.Id));
        Assert.Null(p.Mods[0].SourcePath);
        Assert.Equal(download, p.Mods[1].SourcePath);
        Assert.Equal("v2.0.0", p.Mods[1].LastVersion);
        Assert.Contains("id=5", p.Mods[0].DownloadUrl);
        Assert.True(VerifiedProfiles.SameMods(entry, p));

        p.Mods[0].Enabled = false;
        Assert.False(VerifiedProfiles.SameMods(entry, p));
        Assert.Null(VerifiedProfiles.ToProfile(entry, "copy", setup, checks, ownCopy: true).VerifiedId);
    }

    private static DiscoveredModule Module(string id, string version, string folder = "") =>
        new(id, version, folder, ModuleSourceKind.Custom, new Bannerlord.ModuleManager.ModuleInfoExtended { Id = id, Name = id });

    private static byte[] Zip(string id, string version)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var w = new StreamWriter(zip.CreateEntry($"{id}/SubModule.xml").Open()))
                w.Write($"<Module><Name value='{id}'/><Id value='{id}'/><Version value='{version}'/><SubModules/></Module>");
            using (var w = new StreamWriter(zip.CreateEntry($"{id}/ModuleData/a.xml").Open())) w.Write("<a/>");
        }
        return buffer.ToArray();
    }

    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private sealed class Serve(byte[] body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
    }
}
