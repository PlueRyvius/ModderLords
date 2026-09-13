using System.IO.Compression;
using System.Net;
using System.Net.Http;
using ModderLords.Core.Updates;
using Xunit;

namespace ModderLords.Core.Tests;

public class UpdateTests
{
    private static string ReleaseJson(string tag, string assetName, string? digest = "sha256:abc", bool prerelease = false, bool draft = false) =>
        "{\"tag_name\":\"" + tag + "\",\"draft\":" + (draft ? "true" : "false") + ",\"prerelease\":" + (prerelease ? "true" : "false") +
        ",\"html_url\":\"https://example.test/release\",\"body\":\"What changed\"," +
        "\"assets\":[{\"name\":\"" + assetName + "\",\"browser_download_url\":\"https://example.test/" + assetName + "\",\"size\":10" +
        (digest is null ? "" : ",\"digest\":\"" + digest + "\"") + "}]}";

    // ---- the check ------------------------------------------------------------------------------------

    [Theory]
    [InlineData("v1.0.1", "1.0.1")]
    [InlineData("1.2.3", "1.2.3")]
    [InlineData("V2.0.0", "2.0.0")]
    public void ReleaseTagsParse(string tag, string expected) => Assert.Equal(Version.Parse(expected), UpdateChecker.ParseTag(tag));

    [Theory]
    [InlineData("v1.0")]
    [InlineData("v1.0.1-test")]
    [InlineData("v1.0.0.4")]
    [InlineData("banana")]
    [InlineData("")]
    [InlineData(null)]
    public void AnythingButAPlainReleaseTagIsRefused(string? tag) => Assert.Null(UpdateChecker.ParseTag(tag));

    [Fact]
    public void ParseFindsTheReleaseZipAndItsChecksum()
    {
        var r = UpdateChecker.Parse(ReleaseJson("v1.0.1", "ModderLords-1.0.1.zip", "sha256:DEADbeef"));
        Assert.NotNull(r);
        Assert.Equal(new Version(1, 0, 1), r.Version);
        Assert.Equal("ModderLords-1.0.1.zip", r.Asset.Name);
        Assert.Equal("DEADbeef", r.Asset.Sha256);
        Assert.Equal("What changed", r.Notes);
    }

    [Fact]
    public void ReleasesThatBreakTheContractAreIgnored()
    {
        Assert.Null(UpdateChecker.Parse(ReleaseJson("v1.0.1", "ModderLords-1.0.0.zip")));          // asset for another version
        Assert.Null(UpdateChecker.Parse(ReleaseJson("v1.0.1", "Something-else.zip")));
        Assert.Null(UpdateChecker.Parse(ReleaseJson("v1.0.1", "ModderLords-1.0.1.zip", prerelease: true)));
        Assert.Null(UpdateChecker.Parse(ReleaseJson("v1.0.1", "ModderLords-1.0.1.zip", draft: true)));
        Assert.Null(UpdateChecker.Parse("{ not json"));
        Assert.Null(UpdateChecker.Parse("[]"));
        // No checksum is still a release (the installer refuses it, with a way out); it is not silently dropped.
        Assert.Null(UpdateChecker.Parse(ReleaseJson("v1.0.1", "ModderLords-1.0.1.zip", digest: null))!.Asset.Sha256);
    }

    [Fact]
    public void OnlyANewerUnskippedReleaseIsAnUpdate()
    {
        var r = UpdateChecker.Parse(ReleaseJson("v1.0.1", "ModderLords-1.0.1.zip"))!;
        Assert.True(UpdateChecker.IsUpdate(r, new Version(1, 0, 0), null));
        Assert.True(UpdateChecker.IsUpdate(r, new Version(1, 0, 0, 0), null));   // assembly versions carry a fourth part
        Assert.False(UpdateChecker.IsUpdate(r, new Version(1, 0, 1), null));
        Assert.False(UpdateChecker.IsUpdate(r, new Version(1, 1, 0), null));
        Assert.False(UpdateChecker.IsUpdate(r, new Version(1, 0, 0), "1.0.1"));
        Assert.True(UpdateChecker.IsUpdate(r, new Version(1, 0, 0), "1.0.0"));   // skipping an older one does not hide a newer
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(respond(request));
    }

    [Fact]
    public async Task ACheckThatCannotReachGitHubSaysSoInsteadOfThrowing()
    {
        using var failing = new HttpClient(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)));
        var r = await UpdateChecker.CheckAsync(failing, new Version(1, 0, 0), null);
        Assert.False(r.Reached);
        Assert.Null(r.Release);

        using var throwing = new HttpClient(new FakeHandler(_ => throw new HttpRequestException("offline")));
        Assert.False((await UpdateChecker.CheckAsync(throwing, new Version(1, 0, 0), null)).Reached);
    }

    [Fact]
    public async Task ACheckThatReachesGitHubReportsTheUpdateOrNone()
    {
        using var http = new HttpClient(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(ReleaseJson("v1.0.1", "ModderLords-1.0.1.zip")) }));
        var newer = await UpdateChecker.CheckAsync(http, new Version(1, 0, 0), null);
        Assert.True(newer.Reached);
        Assert.Equal(new Version(1, 0, 1), newer.Release?.Version);
        var same = await UpdateChecker.CheckAsync(http, new Version(1, 0, 1), null);
        Assert.True(same.Reached);
        Assert.Null(same.Release);
    }

    // ---- the install ----------------------------------------------------------------------------------

    private sealed class Sandbox : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "ModderLords-update-tests", Guid.NewGuid().ToString("N"));
        public string Install => Path.Combine(Root, "install");
        public string Updates => Path.Combine(Root, "updates");

        public Sandbox()
        {
            Write(Install, "ModderLords.exe", "old exe");
            Write(Install, @"bin\ModderLords.Hook.dll", "old hook");
            Write(Install, "notes-the-user-left.txt", "mine");
        }

        public static void Write(string root, string relative, string text)
        {
            var path = Path.Combine(root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }

        /// <summary>A release zip shaped like package-release.ps1's output.</summary>
        public string Zip(bool withExe = true)
        {
            var content = Path.Combine(Root, "zip-content-" + Guid.NewGuid().ToString("N"));
            if (withExe) Write(content, "ModderLords.exe", "new exe");
            Write(content, @"bin\ModderLords.Hook.dll", "new hook");
            Write(content, @"data\compat-db.json", "{}");
            var zip = Path.Combine(Root, $"ModderLords-9.9.9-{Guid.NewGuid():N}.zip");
            ZipFile.CreateFromDirectory(content, zip);
            return zip;
        }

        public string Read(string relative) => File.ReadAllText(Path.Combine(Install, relative));
        public void Dispose() { try { Directory.Delete(Root, true); } catch { } }
    }

    [Fact]
    public async Task ADownloadThatDoesNotMatchItsChecksumIsRefused()
    {
        using var box = new Sandbox();
        var zip = box.Zip();
        var installer = new UpdateInstaller(box.Install, box.Updates);
        var real = await UpdateInstaller.Sha256Of(zip);
        using var http = new HttpClient();

        var wrong = new ReleaseInfo(new Version(9, 9, 9), "v9.9.9", "", "", new ReleaseAsset("ModderLords-9.9.9.zip", zip, 0, new string('0', 64)));
        await Assert.ThrowsAsync<InvalidDataException>(() => installer.DownloadAndVerifyAsync(wrong, http));
        Assert.Empty(Directory.EnumerateFiles(box.Updates));

        var none = wrong with { Asset = wrong.Asset with { Sha256 = null } };
        await Assert.ThrowsAsync<InvalidDataException>(() => installer.DownloadAndVerifyAsync(none, http));

        var right = wrong with { Asset = wrong.Asset with { Sha256 = real.ToUpperInvariant() } };
        Assert.True(File.Exists(await installer.DownloadAndVerifyAsync(right, http)));
        Assert.Equal("old exe", box.Read("ModderLords.exe"));   // verifying touches nothing installed
    }

    [Fact]
    public void InstallSwapsTheReleaseInAndKeepsWhatItDoesNotShip()
    {
        using var box = new Sandbox();
        var installer = new UpdateInstaller(box.Install, box.Updates);
        var exe = installer.Install(box.Zip());

        Assert.Equal(Path.Combine(box.Install, "ModderLords.exe"), exe);
        Assert.Equal("new exe", box.Read("ModderLords.exe"));
        Assert.Equal("old exe", box.Read("ModderLords.exe.old"));      // the running copy, renamed not deleted
        Assert.Equal("new hook", box.Read(@"bin\ModderLords.Hook.dll"));
        Assert.True(File.Exists(Path.Combine(box.Install, @"data\compat-db.json")));
        Assert.Equal("mine", box.Read("notes-the-user-left.txt"));
        Assert.Equal("old hook", File.ReadAllText(Path.Combine(installer.BackupDir, @"bin\ModderLords.Hook.dll")));

        Assert.Equal(1, installer.CleanUpAfterUpdate());
        Assert.False(File.Exists(Path.Combine(box.Install, "ModderLords.exe.old")));
    }

    [Fact]
    public void AFailedSwapPutsTheInstallBackExactlyAsItWas()
    {
        using var box = new Sandbox();
        var installer = new UpdateInstaller(box.Install, box.Updates);
        var copies = 0;
        // Fail on the second item copied in, so the rollback has written files, moved files and a renamed exe to undo.
        installer.BeforeCopy = _ => { if (++copies == 2) throw new IOException("disk full"); };

        Assert.Throws<IOException>(() => installer.Install(box.Zip()));

        Assert.Equal("old exe", box.Read("ModderLords.exe"));
        Assert.False(File.Exists(Path.Combine(box.Install, "ModderLords.exe.old")));
        Assert.Equal("old hook", box.Read(@"bin\ModderLords.Hook.dll"));
        Assert.False(Directory.Exists(Path.Combine(box.Install, "data")));
        Assert.Equal("mine", box.Read("notes-the-user-left.txt"));
    }

    [Fact]
    public void AZipWithoutTheExeIsNotARelease()
    {
        using var box = new Sandbox();
        var installer = new UpdateInstaller(box.Install, box.Updates);
        Assert.Throws<InvalidDataException>(() => installer.Install(box.Zip(withExe: false)));
        Assert.Equal("old exe", box.Read("ModderLords.exe"));
        Assert.Equal("old hook", box.Read(@"bin\ModderLords.Hook.dll"));
    }
}
