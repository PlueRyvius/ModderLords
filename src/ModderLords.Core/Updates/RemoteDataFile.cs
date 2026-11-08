using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using ModderLords.Core.Compat;

namespace ModderLords.Core.Updates;

/// <summary>
/// A data file that ships with the launcher and is also picked up, newer, from the repository's default branch between
/// releases: the compat database (<see cref="CompatDbRemote"/>) and the verified profiles
/// (<see cref="Profiles.VerifiedProfilesRemote"/>). The fetched copy is kept in the user's data folder with a sidecar,
/// and used in place of the bundled file once it has passed the same checks a download does.
///
/// This is a live channel to every install, next to the updater, and unlike the updater nobody clicks anything. So
/// the rule throughout is "never worse than bundled": every doubt - about the network, the file, the cache or the
/// sidecar - resolves to the file that shipped with the launcher, silently, and nothing here throws.
/// </summary>
/// <typeparam name="TBundled">What a download is measured against: the bundled file's contents.</typeparam>
/// <typeparam name="TContent">What a file that passed the checks reads as.</typeparam>
public abstract class RemoteDataFile<TBundled, TContent>
{
    /// <summary>For the whole exchange, body included: HttpClient's own timeout stops counting once the headers are in.</summary>
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(10);

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
    };

    private readonly Version _running;

    public string CachePath { get; }
    public string MetaPath { get; }
    public bool Enabled { get; }

    protected RemoteDataFile(string directory, string cacheFileName, string metaFileName, Version running, bool enabled)
    {
        CachePath = Path.Combine(directory, cacheFileName);
        MetaPath = Path.Combine(directory, metaFileName);
        _running = Normalise(running);
        Enabled = enabled;
    }

    /// <summary>The raw file on the default branch. Carries no query string and nothing about the user or the install.</summary>
    protected abstract string SourceUrl { get; }

    /// <summary>Anything near this size is not the file, whatever it parses as.</summary>
    protected abstract int ByteLimit { get; }

    /// <summary>Sent as the User-Agent.</summary>
    protected abstract string Agent { get; }

    /// <summary>
    /// Throws (<see cref="InvalidDataException"/> or whatever the parser throws) unless the bytes are a file this
    /// launcher can read and one that is plausibly a successor of the bundled file.
    /// </summary>
    protected abstract TContent Check(byte[] bytes, TBundled bundled);

    /// <summary>On unless the variable says otherwise; an unset or unrecognised value changes nothing.</summary>
    public static bool EnabledFor(string? value) =>
        value?.Trim().ToLowerInvariant() is not ("0" or "off" or "false" or "no");

    private static Version Normalise(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));

    // ---- reading the cache ---------------------------------------------------------------------------------------

    /// <summary>
    /// The cached contents and when they were fetched, or null when the bundled file must be used. Null is the answer
    /// to everything unexpected: no cache, no sidecar, a sidecar that does not describe this cache file (the two are
    /// written one after the other, so a reader can land between them), a cache fetched by an older launcher, or
    /// contents that would be refused as a download today. Deleting either file is therefore a safe way back to the
    /// bundled one.
    /// </summary>
    protected (TContent Content, DateTime FetchedAtUtc)? ReadCache(TBundled bundled)
    {
        try
        {
            if (!Enabled || !File.Exists(CachePath)) return null;
            if (ReadMeta() is not { Sha256: { } sha, FetchedAtUtc: { } at } meta) return null;
            // After the app updates itself, the file it ships may be newer than a cache the previous version fetched
            // weeks ago. That cache stays ignored until this version has completed a fetch of its own. A cache from a
            // NEWER launcher (the user went back a version) is still the default branch's file, so it is kept - and
            // if its schema has moved on, Check refuses it below.
            if (meta.FetchedBy is null || !Version.TryParse(meta.FetchedBy, out var fetchedBy) || Normalise(fetchedBy) < _running) return null;
            if (new FileInfo(CachePath).Length > ByteLimit) return null;
            var bytes = File.ReadAllBytes(CachePath);
            if (!string.Equals(Hash(bytes), sha, StringComparison.OrdinalIgnoreCase)) return null;
            // The same checks as a download, against the bundled file of the launcher that is running NOW.
            return (Check(bytes, bundled), at);
        }
        catch (Exception) { return null; }
    }

    private CompatDbRemoteMeta? ReadMeta()
    {
        try { return File.Exists(MetaPath) ? JsonSerializer.Deserialize<CompatDbRemoteMeta>(File.ReadAllText(MetaPath), Json) : null; }
        catch (Exception) { return null; }
    }

    protected static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    // ---- fetching ------------------------------------------------------------------------------------------------

    /// <summary>
    /// One polite fetch. Never throws, except for the caller's own cancellation. The cache is replaced only by a file
    /// that passed <see cref="Check"/>; on every other path it is left exactly as it was.
    /// </summary>
    public async Task<CompatDbRemoteResult> RefreshAsync(HttpClient http, TBundled bundled, CancellationToken ct = default)
    {
        if (!Enabled) return new(CompatDbRemoteOutcome.Disabled);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(FetchTimeout);

            var meta = ReadMeta() ?? new CompatDbRemoteMeta();
            // The ETag is only offered while the cache it belongs to is actually in use. Otherwise - the cache was
            // deleted, damaged, or fetched by an older launcher - a 304 would confirm a file that is not there.
            var conditional = meta.ETag is { Length: > 0 } && ReadCache(bundled) is not null;

            using var request = new HttpRequestMessage(HttpMethod.Get, SourceUrl);
            request.Headers.UserAgent.ParseAdd(Agent);
            if (conditional) request.Headers.TryAddWithoutValidation("If-None-Match", meta.ETag);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.StatusCode == HttpStatusCode.NotModified)
                return new(conditional ? CompatDbRemoteOutcome.NotModified : CompatDbRemoteOutcome.Unreachable);
            if (!response.IsSuccessStatusCode) return new(CompatDbRemoteOutcome.Unreachable);

            if (response.Content.Headers.ContentLength > ByteLimit) return Reject(meta, "size", $"larger than {ByteLimit / 1024} KB");
            var bytes = await ReadCappedAsync(response.Content, timeout.Token);
            if (bytes is null) return Reject(meta, "size", $"larger than {ByteLimit / 1024} KB");

            try { Check(bytes, bundled); }
            catch (Exception ex) when (ex is not OperationCanceledException) { return Reject(meta, Hash(bytes), ex.Message); }

            // Cache first, sidecar second. Between the two moves the sidecar still carries the old hash, so a reader
            // sees a pair that does not match and uses the bundled file; it never sees new contents under old facts.
            WriteAtomic(CachePath, bytes);
            WriteAtomic(MetaPath, JsonSerializer.SerializeToUtf8Bytes(new CompatDbRemoteMeta
            {
                FetchedBy = UpdateChecker.Format(_running),
                ETag = response.Headers.ETag?.ToString(),
                FetchedAtUtc = DateTime.UtcNow,
                Sha256 = Hash(bytes),
            }, Json));
            return new(CompatDbRemoteOutcome.Updated);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return new(CompatDbRemoteOutcome.Unreachable); }
    }

    /// <summary>
    /// Remembers which file was refused, in the sidecar only: the cache and the facts about it are carried over
    /// untouched. A file that cannot be remembered is still refused; it is merely reported again next time.
    /// </summary>
    private CompatDbRemoteResult Reject(CompatDbRemoteMeta meta, string key, string reason)
    {
        if (meta.RejectedKey == key) return new(CompatDbRemoteOutcome.Rejected, reason, AlreadyReported: true);
        meta.RejectedKey = key;
        try { WriteAtomic(MetaPath, JsonSerializer.SerializeToUtf8Bytes(meta, Json)); }
        catch (Exception) { }
        return new(CompatDbRemoteOutcome.Rejected, reason);
    }

    /// <summary>The body, or null once it runs past the cap. Content-Length is not trusted to be there or to be true.</summary>
    private async Task<byte[]?> ReadCappedAsync(HttpContent content, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > ByteLimit) return null;
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    /// <summary>Tmp + move, the convention of CompatDb.WriteFile. The tmp never outlives a failed move.</summary>
    private static void WriteAtomic(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        try
        {
            File.WriteAllBytes(tmp, bytes);
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); }
            catch (Exception) { }
        }
    }
}
