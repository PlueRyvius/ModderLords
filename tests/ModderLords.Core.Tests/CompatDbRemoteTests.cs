using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using ModderLords.Core.Compat;

namespace ModderLords.Core.Tests;

internal static class CompatRemoteOffForTests
{
    /// <summary>
    /// CompatDb.Current and Reload() read the real data folder. A database downloaded by an installed copy on this
    /// machine must not decide what the tests of the bundled file see, so the process-wide default is switched off;
    /// the tests below build their own instances against a temp folder.
    /// </summary>
    [ModuleInitializer]
    internal static void Init() => Environment.SetEnvironmentVariable(CompatDbRemote.EnvVar, "0");
}

/// <summary>No test here touches the network: every request is answered by <see cref="Handler"/>.</summary>
public sealed class CompatDbRemoteTests : IDisposable
{
    private static readonly DateTime Shipped = new(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly Version Running = new(1, 2, 0);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mbc-compat-remote-" + Guid.NewGuid().ToString("N"));
    private readonly string _data;
    private string Bundled => Path.Combine(_dir, "compat-db.json");
    private string Local => Path.Combine(_dir, "compat-db.local.json");

    public CompatDbRemoteTests()
    {
        _data = Path.Combine(_dir, "data");
        Directory.CreateDirectory(_data);
        CompatDb.WriteFile(Bundled, Records(10, Shipped, CompatVerdict.Works));
    }

    public void Dispose() => Directory.Delete(_dir, true);

    private static List<CompatRecord> Records(int count, DateTime? updatedAt, CompatVerdict verdict) =>
        Enumerable.Range(0, count).Select(i => new CompatRecord { Id = "Mod" + i, Verdict = verdict, UpdatedAt = updatedAt }).ToList();

    /// <summary>What the default branch serves when all is well: every bundled record, re-judged, plus one more.</summary>
    private static string Newer(int count = 11) => CompatDb.Serialize(Records(count, Shipped.AddDays(7), CompatVerdict.Broken));

    private CompatDbRemote Remote(Version? running = null, bool enabled = true) => new(_data, running ?? Running, enabled);
    private IReadOnlyCollection<CompatRecord> BundledRecords => CompatDbRemote.ReadBundled(Bundled);
    private CompatDb Load(CompatDbRemote remote) => CompatDb.Load(Bundled, Local, remote);

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }

    /// <summary>A body that does not announce its length, as a chunked response does not.</summary>
    private sealed class UnsizedContent(byte[] bytes) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }

    private static HttpResponseMessage Ok(string body, string? etag = "\"v1\"") => Ok(new ByteArrayContent(Encoding.UTF8.GetBytes(body)), etag);

    private static HttpResponseMessage Ok(HttpContent content, string? etag = "\"v1\"")
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        if (etag is not null) response.Headers.TryAddWithoutValidation("ETag", etag);
        return response;
    }

    private async Task<CompatDbRemoteResult> Refresh(CompatDbRemote remote, Handler handler)
    {
        using var http = new HttpClient(handler);
        return await remote.RefreshAsync(http, BundledRecords);
    }

    private Task<CompatDbRemoteResult> Refresh(CompatDbRemote remote, Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        Refresh(remote, new Handler(respond));

    /// <summary>A cache in use, downloaded by <paramref name="running"/>, and its exact bytes.</summary>
    private async Task<byte[]> GivenACache(Version? running = null)
    {
        var result = await Refresh(Remote(running), _ => Ok(Newer()));
        Assert.Equal(CompatDbRemoteOutcome.Updated, result.Outcome);
        return File.ReadAllBytes(Remote().CachePath);
    }

    private void AssertNoTmpLeftBehind() => Assert.Empty(Directory.GetFiles(_data, "*.tmp"));

    // ---- a good download -------------------------------------------------------------------------------

    [Fact]
    public async Task A_valid_download_becomes_the_cache_and_Load_prefers_it()
    {
        var remote = Remote();
        var handler = new Handler(_ => Ok(Newer()));
        Assert.Null(Load(remote).DownloadedAtUtc);
        Assert.Equal(CompatVerdict.Works, Load(remote).For("Mod0", null).Verdict);

        var result = await Refresh(remote, handler);

        Assert.Equal(CompatDbRemoteOutcome.Updated, result.Outcome);
        Assert.Equal(Newer(), File.ReadAllText(remote.CachePath));
        AssertNoTmpLeftBehind();

        var db = Load(remote);
        Assert.Empty(db.Problems);
        Assert.NotNull(db.DownloadedAtUtc);
        Assert.Equal(CompatVerdict.Broken, db.For("Mod0", null).Verdict);
        Assert.NotNull(db.Find("Mod10"));
        // The plain two-path Load never looks at a cache.
        Assert.Null(CompatDb.Load(Bundled, Local).DownloadedAtUtc);
    }

    [Fact]
    public async Task The_request_is_anonymous_and_carries_a_user_agent()
    {
        var handler = new Handler(_ => Ok(Newer()));
        await Refresh(Remote(), handler);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("https://raw.githubusercontent.com/PlueRyvius/ModderLords/main/src/ModderLords.Core/compat-db.json", request.RequestUri!.AbsoluteUri);
        Assert.Equal("", request.RequestUri.Query);
        Assert.Equal("ModderLords-compat-db", request.Headers.UserAgent.ToString());
        Assert.False(request.Headers.Contains("If-None-Match"));    // nothing cached yet, so nothing to be conditional about
        Assert.Null(request.Headers.Authorization);
        Assert.False(request.Headers.Contains("Cookie"));
    }

    [Fact]
    public async Task The_users_local_record_still_wins_over_a_downloaded_one()
    {
        await GivenACache();
        CompatDb.WriteFile(Local, [new CompatRecord { Id = "Mod0", Verdict = CompatVerdict.NeedsRecipe }]);

        var db = Load(Remote());

        Assert.Equal(CompatVerdict.NeedsRecipe, db.For("Mod0", null).Verdict);
        Assert.Equal(CompatSource.Local, db.SourceOf("Mod0"));
        Assert.Equal(CompatVerdict.Broken, db.For("Mod1", null).Verdict);
    }

    // ---- an unchanged file -----------------------------------------------------------------------------

    [Fact]
    public async Task An_unchanged_file_costs_a_304_and_keeps_the_cache()
    {
        var before = await GivenACache();
        var metaBefore = File.ReadAllText(Remote().MetaPath);
        var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.NotModified));

        var result = await Refresh(Remote(), handler);

        Assert.Equal(CompatDbRemoteOutcome.NotModified, result.Outcome);
        Assert.Equal("\"v1\"", Assert.Single(handler.Requests).Headers.GetValues("If-None-Match").Single());
        Assert.Equal(before, File.ReadAllBytes(Remote().CachePath));
        Assert.Equal(metaBefore, File.ReadAllText(Remote().MetaPath));
        Assert.NotNull(Load(Remote()).DownloadedAtUtc);
    }

    // ---- never worse than bundled ----------------------------------------------------------------------

    public static TheoryData<string> RefusedBodies => new()
    {
        "{ not json",
        "null",
        "[]",
        "{ \"SchemaVersion\": 99, \"Records\": [ { \"Id\": \"Mod0\" } ] }",
        "{ \"SchemaVersion\": 1, \"Records\": [] }",
        "{ \"SchemaVersion\": 1, \"Records\": null }",
        "{ \"SchemaVersion\": 1, \"Records\": [ { \"Id\": \"  \" } ] }",            // records, but none with an id
        CompatDb.Serialize(Records(4, Shipped.AddDays(7), CompatVerdict.Broken)),   // gutted: under half the bundled ten
        CompatDb.Serialize(Records(11, Shipped.AddDays(-1), CompatVerdict.Broken)), // the default branch is behind this build
        CompatDb.Serialize(Records(11, null, CompatVerdict.Broken)),                // no dates at all cannot claim to be newer
        Newer()[..(Newer().Length / 2)],                                            // cut off mid-transfer
    };

    [Theory]
    [MemberData(nameof(RefusedBodies))]
    public async Task A_refused_download_leaves_the_previous_cache_in_use(string body)
    {
        var before = await GivenACache();

        var result = await Refresh(Remote(), _ => Ok(body, "\"v2\""));

        Assert.Equal(CompatDbRemoteOutcome.Rejected, result.Outcome);
        Assert.False(string.IsNullOrWhiteSpace(result.Reason));
        Assert.Equal(before, File.ReadAllBytes(Remote().CachePath));
        AssertNoTmpLeftBehind();
        var db = Load(Remote());
        Assert.NotNull(db.DownloadedAtUtc);
        Assert.Equal(CompatVerdict.Broken, db.For("Mod0", null).Verdict);
        Assert.Empty(db.Problems);
    }

    [Theory]
    [MemberData(nameof(RefusedBodies))]
    public async Task A_refused_download_with_no_cache_leaves_the_bundled_file_in_use(string body)
    {
        var result = await Refresh(Remote(), _ => Ok(body));

        Assert.Equal(CompatDbRemoteOutcome.Rejected, result.Outcome);
        Assert.False(File.Exists(Remote().CachePath));
        AssertNoTmpLeftBehind();
        var db = Load(Remote());
        Assert.Null(db.DownloadedAtUtc);
        Assert.Equal(CompatVerdict.Works, db.For("Mod0", null).Verdict);
    }

    [Fact]
    public async Task An_oversized_download_is_refused_whether_or_not_it_announces_its_size()
    {
        var before = await GivenACache();
        // Valid JSON all the way through, so only the cap can refuse it.
        var huge = Encoding.UTF8.GetBytes(Newer() + new string(' ', CompatDbRemote.MaxBytes));

        var announced = await Refresh(Remote(), _ => Ok(new ByteArrayContent(huge), "\"big\""));
        var unannounced = await Refresh(Remote(), _ => Ok(new UnsizedContent(huge), "\"big\""));

        Assert.Equal(CompatDbRemoteOutcome.Rejected, announced.Outcome);
        Assert.Equal(CompatDbRemoteOutcome.Rejected, unannounced.Outcome);
        Assert.Equal(before, File.ReadAllBytes(Remote().CachePath));
        AssertNoTmpLeftBehind();
        Assert.NotNull(Load(Remote()).DownloadedAtUtc);
    }

    [Fact]
    public async Task Bytes_that_are_not_text_are_refused()
    {
        var result = await Refresh(Remote(), _ => Ok(new ByteArrayContent([0xFF, 0xFE, 0x00, 0xC3, 0x28])));

        Assert.Equal(CompatDbRemoteOutcome.Rejected, result.Outcome);
        Assert.False(File.Exists(Remote().CachePath));
    }

    [Fact]
    public async Task Offline_or_an_http_error_is_silence_and_changes_nothing()
    {
        var before = await GivenACache();
        var metaBefore = File.ReadAllText(Remote().MetaPath);

        var forbidden = await Refresh(Remote(), _ => new HttpResponseMessage(HttpStatusCode.Forbidden));
        var missing = await Refresh(Remote(), _ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var failing = await Refresh(Remote(), _ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var offline = await Refresh(Remote(), _ => throw new HttpRequestException("offline"));
        var timedOut = await Refresh(Remote(), _ => throw new TaskCanceledException("timed out"));

        Assert.All(new[] { forbidden, missing, failing, offline, timedOut }, r => Assert.Equal(CompatDbRemoteOutcome.Unreachable, r.Outcome));
        Assert.Equal(before, File.ReadAllBytes(Remote().CachePath));
        Assert.Equal(metaBefore, File.ReadAllText(Remote().MetaPath));
        Assert.NotNull(Load(Remote()).DownloadedAtUtc);
    }

    [Fact]
    public async Task The_callers_own_cancellation_is_not_swallowed()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        using var http = new HttpClient(new Handler(_ => Ok(Newer())));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Remote().RefreshAsync(http, BundledRecords, cts.Token));
        Assert.False(File.Exists(Remote().CachePath));
    }

    [Fact]
    public async Task The_same_refused_file_is_reported_once()
    {
        await GivenACache();

        var first = await Refresh(Remote(), _ => Ok("{ not json", "\"bad\""));
        var again = await Refresh(Remote(), _ => Ok("{ not json", "\"bad\""));
        var different = await Refresh(Remote(), _ => Ok("{ still not json", "\"worse\""));

        Assert.False(first.AlreadyReported);
        Assert.True(again.AlreadyReported);
        Assert.Equal(CompatDbRemoteOutcome.Rejected, again.Outcome);
        Assert.False(different.AlreadyReported);
        // Remembering the refusal did not disturb the cache it sits beside.
        Assert.NotNull(Load(Remote()).DownloadedAtUtc);
        AssertNoTmpLeftBehind();
    }

    // ---- a newer launcher beats an old cache -----------------------------------------------------------

    [Fact]
    public async Task A_newer_launcher_ignores_a_cache_the_previous_version_fetched()
    {
        await GivenACache(new Version(1, 2, 0));
        var updated = Remote(new Version(1, 2, 1));

        Assert.Null(updated.TryReadCache(BundledRecords));
        var db = Load(updated);
        Assert.Null(db.DownloadedAtUtc);
        Assert.Equal(CompatVerdict.Works, db.For("Mod0", null).Verdict);

        // It asks for the whole file rather than for "anything newer than what the old version had", and from then
        // on the cache is its own.
        var handler = new Handler(_ => Ok(Newer(12), "\"v2\""));
        Assert.Equal(CompatDbRemoteOutcome.Updated, (await Refresh(updated, handler)).Outcome);
        Assert.False(Assert.Single(handler.Requests).Headers.Contains("If-None-Match"));
        Assert.NotNull(Load(updated).Find("Mod11"));
    }

    [Fact]
    public async Task A_304_cannot_revive_a_cache_that_is_being_ignored()
    {
        await GivenACache(new Version(1, 2, 0));

        var result = await Refresh(Remote(new Version(1, 3, 0)), _ => new HttpResponseMessage(HttpStatusCode.NotModified));

        Assert.Equal(CompatDbRemoteOutcome.Unreachable, result.Outcome);
        Assert.Null(Load(Remote(new Version(1, 3, 0))).DownloadedAtUtc);
    }

    [Fact]
    public async Task The_same_and_an_older_launcher_keep_using_the_cache()
    {
        await GivenACache(new Version(1, 2, 0));

        Assert.NotNull(Load(Remote(new Version(1, 2, 0, 0))).DownloadedAtUtc);  // assembly versions carry a fourth part
        Assert.NotNull(Load(Remote(new Version(1, 1, 9))).DownloadedAtUtc);
    }

    [Fact]
    public async Task A_cache_that_falls_behind_a_newer_bundled_file_is_ignored_whatever_the_version_says()
    {
        // A build of the same version whose bundled file has moved on: a test build from a branch, or a dev build.
        await GivenACache();
        CompatDb.WriteFile(Bundled, Records(10, Shipped.AddDays(30), CompatVerdict.NeedsRecipe));

        var db = Load(Remote());

        Assert.Null(db.DownloadedAtUtc);
        Assert.Equal(CompatVerdict.NeedsRecipe, db.For("Mod0", null).Verdict);
    }

    // ---- a damaged cache -------------------------------------------------------------------------------

    [Fact]
    public async Task Load_falls_back_to_bundled_when_the_cache_is_corrupt()
    {
        await GivenACache();
        File.WriteAllText(Remote().CachePath, "{ not json");

        var db = Load(Remote());

        Assert.Null(db.DownloadedAtUtc);
        Assert.Empty(db.Problems);      // a bad cache is not the user's problem; the bundled file simply applies
        Assert.Equal(CompatVerdict.Works, db.For("Mod0", null).Verdict);
        Assert.Null(db.Find("Mod10"));
    }

    [Fact]
    public async Task A_cache_edited_behind_the_sidecars_back_is_not_trusted()
    {
        await GivenACache();
        // Still a perfectly valid database - just not the one that was downloaded and checked.
        File.WriteAllText(Remote().CachePath, CompatDb.Serialize(Records(11, Shipped.AddDays(9), CompatVerdict.NeedsRecipe)));

        Assert.Null(Load(Remote()).DownloadedAtUtc);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("{}")]
    [InlineData("null")]
    public async Task A_missing_or_unreadable_sidecar_means_no_cache(string? meta)
    {
        await GivenACache();
        File.WriteAllText(Remote().MetaPath, meta);
        Assert.Null(Load(Remote()).DownloadedAtUtc);

        File.Delete(Remote().MetaPath);
        Assert.Null(Load(Remote()).DownloadedAtUtc);
    }

    [Fact]
    public async Task Deleting_the_cache_is_a_safe_way_back_and_the_next_fetch_starts_over()
    {
        await GivenACache();
        File.Delete(Remote().CachePath);

        Assert.Null(Load(Remote()).DownloadedAtUtc);
        Assert.Equal(CompatVerdict.Works, Load(Remote()).For("Mod0", null).Verdict);

        // The sidecar still holds the ETag. Offering it would earn a 304 for a file that is no longer here.
        var handler = new Handler(_ => Ok(Newer()));
        Assert.Equal(CompatDbRemoteOutcome.Updated, (await Refresh(Remote(), handler)).Outcome);
        Assert.False(Assert.Single(handler.Requests).Headers.Contains("If-None-Match"));
        Assert.NotNull(Load(Remote()).DownloadedAtUtc);
    }

    // ---- the off switch --------------------------------------------------------------------------------

    [Fact]
    public async Task Switched_off_it_neither_asks_nor_uses_what_was_downloaded_before()
    {
        await GivenACache();
        var off = Remote(enabled: false);
        var handler = new Handler(_ => Ok(Newer(12)));

        var result = await Refresh(off, handler);

        Assert.Equal(CompatDbRemoteOutcome.Disabled, result.Outcome);
        Assert.Empty(handler.Requests);
        Assert.Null(Load(off).DownloadedAtUtc);
        Assert.Equal(CompatVerdict.Works, Load(off).For("Mod0", null).Verdict);
        // Nothing was thrown away: switching it back on finds the cache where it was.
        Assert.NotNull(Load(Remote()).DownloadedAtUtc);
    }

    [Theory]
    [InlineData("0", false)]
    [InlineData(" OFF ", false)]
    [InlineData("false", false)]
    [InlineData("no", false)]
    [InlineData("1", true)]
    [InlineData("", true)]
    [InlineData(null, true)]
    [InlineData("banana", true)]
    public void The_environment_variable_switches_it_off(string? value, bool enabled) =>
        Assert.Equal(enabled, CompatDbRemote.EnabledFor(value));

    [Fact]
    public void The_process_wide_default_is_off_under_test()
    {
        // Guards the module initializer above: without it, the bundled-file tests read this machine's real cache.
        Assert.False(CompatDbRemote.Default.Enabled);
        Assert.Null(CompatDb.Reload().DownloadedAtUtc);
    }
}
