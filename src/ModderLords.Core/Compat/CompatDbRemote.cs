using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ModderLords.Core.Profiles;
using ModderLords.Core.Updates;

namespace ModderLords.Core.Compat;

public enum CompatDbRemoteOutcome
{
    /// <summary>Switched off; nothing was asked.</summary>
    Disabled,
    /// <summary>Offline, timed out, or GitHub answered with an error. Says nothing about the file.</summary>
    Unreachable,
    /// <summary>304: the cache already holds what is on the default branch.</summary>
    NotModified,
    /// <summary>A newer file passed every check and replaced the cache.</summary>
    Updated,
    /// <summary>A file arrived and was refused; whatever was in use before still is.</summary>
    Rejected,
}

/// <summary>
/// What one refresh did. <see cref="AlreadyReported"/> is set when this exact file was refused on an earlier start
/// too, so the caller can stay quiet about it instead of repeating the same line every time the app opens.
/// </summary>
public sealed record CompatDbRemoteResult(CompatDbRemoteOutcome Outcome, string? Reason = null, bool AlreadyReported = false);

/// <summary>A cache that passed every check, and when it was downloaded.</summary>
public sealed record CompatDbRemoteCache(List<CompatRecord> Records, DateTime FetchedAtUtc);

/// <summary>The sidecar next to the cache. Everything is optional: a hand-edited or half-written one must read as "no cache".</summary>
public sealed class CompatDbRemoteMeta
{
    /// <summary>Launcher version that downloaded the cache (X.Y.Z). A newer launcher ignores the cache until it has fetched its own.</summary>
    public string? FetchedBy { get; set; }
    /// <summary>The ETag GitHub sent with the cached file, sent back as If-None-Match.</summary>
    public string? ETag { get; set; }
    public DateTime? FetchedAtUtc { get; set; }
    /// <summary>SHA-256 of the cache file. Ties the two files together: they are written one after the other, not as one.</summary>
    public string? Sha256 { get; set; }
    /// <summary>Identifies the last download that was refused, so the same bad file is reported once and not on every start.</summary>
    public string? RejectedKey { get; set; }
}

/// <summary>
/// Picks up a newer compat-db.json between releases: fetches the file from the repository's default branch, checks it,
/// and keeps it in the user's data folder, where <see cref="CompatDb.Load(string?, string?, CompatDbRemote?)"/> uses it
/// in place of the bundled file.
///
/// This is a second live channel to every install, next to the updater, and unlike the updater nobody clicks anything.
/// So the rule throughout is "never worse than bundled": every doubt - about the network, the file, the cache or the
/// sidecar - resolves to the database that shipped with the launcher, silently, and nothing here throws.
/// </summary>
public sealed class CompatDbRemote
{
    public const string CacheFileName = "compat-db.remote.json";
    public const string MetaFileName = "compat-db.remote.meta.json";

    /// <summary>Set to 0 (or off / false / no) to neither download the database nor use a downloaded one.</summary>
    public const string EnvVar = "MODDERLORDS_COMPAT_REMOTE";

    /// <summary>The bundled file is about 14 KB. Anything near this size is not the database, whatever it parses as.</summary>
    public const int MaxBytes = 2 * 1024 * 1024;

    /// <summary>For the whole exchange, body included: HttpClient's own timeout stops counting once the headers are in.</summary>
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(10);

    /// <summary>The raw file on the default branch. Carries no query string and nothing about the user or the install.</summary>
    public static string Url => $"https://raw.githubusercontent.com/{UpdateChecker.Repository}/main/src/ModderLords.Core/{CompatDb.BundledFileName}";

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

    public CompatDbRemote(string directory, Version running, bool enabled = true)
    {
        CachePath = Path.Combine(directory, CacheFileName);
        MetaPath = Path.Combine(directory, MetaFileName);
        _running = Normalise(running);
        Enabled = enabled;
    }

    /// <summary>
    /// The cache in the user's data folder, for the running launcher. Every project is versioned from
    /// Directory.Build.props, so Core's own version is the launcher's - in the app and in the CLI alike.
    /// The off switches are read here, each time, so the CLI honours the app's setting without being told.
    /// </summary>
    public static CompatDbRemote Default => new(ProfileStore.RootDir,
        typeof(CompatDbRemote).Assembly.GetName().Version ?? new Version(0, 0, 0),
        EnabledFor(Environment.GetEnvironmentVariable(EnvVar)) && UiStateStore.Load().DownloadCompatDb);

    /// <summary>On unless the variable says otherwise; an unset or unrecognised value changes nothing.</summary>
    public static bool EnabledFor(string? value) =>
        value?.Trim().ToLowerInvariant() is not ("0" or "off" or "false" or "no");

    private static Version Normalise(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));

    // ---- reading the cache ---------------------------------------------------------------------------------------

    /// <summary>
    /// The cached database, or null when the bundled one must be used. Null is the answer to everything unexpected:
    /// no cache, no sidecar, a sidecar that does not describe this cache file (the two are written one after the
    /// other, so a reader can land between them), a cache fetched by an older launcher, or contents that would be
    /// refused as a download today. Deleting either file is therefore a safe way back to the bundled database.
    /// </summary>
    public CompatDbRemoteCache? TryReadCache(IReadOnlyCollection<CompatRecord> bundled)
    {
        try
        {
            if (!Enabled || !File.Exists(CachePath)) return null;
            if (ReadMeta() is not { Sha256: { } sha, FetchedAtUtc: { } at } meta) return null;
            // After the app updates itself, the file it ships may be newer than a cache the previous version fetched
            // weeks ago. That cache stays ignored until this version has completed a fetch of its own. A cache from a
            // NEWER launcher (the user went back a version) is still the default branch's file, so it is kept - and
            // if its schema has moved on, Parse refuses it below.
            if (meta.FetchedBy is null || !Version.TryParse(meta.FetchedBy, out var fetchedBy) || Normalise(fetchedBy) < _running) return null;
            if (new FileInfo(CachePath).Length > MaxBytes) return null;
            var bytes = File.ReadAllBytes(CachePath);
            if (!string.Equals(Hash(bytes), sha, StringComparison.OrdinalIgnoreCase)) return null;
            // The same checks as a download, against the bundled file of the launcher that is running NOW.
            return new CompatDbRemoteCache(Validate(bytes, bundled), at);
        }
        catch (Exception) { return null; }
    }

    private CompatDbRemoteMeta? ReadMeta()
    {
        try { return File.Exists(MetaPath) ? JsonSerializer.Deserialize<CompatDbRemoteMeta>(File.ReadAllText(MetaPath), Json) : null; }
        catch (Exception) { return null; }
    }

    /// <summary>The bundled records a download is measured against. Empty when the file is missing or unreadable.</summary>
    public static IReadOnlyCollection<CompatRecord> ReadBundled(string? bundledPath) =>
        CompatDb.Load(bundledPath, null).Records.ToList();

    // ---- validation ----------------------------------------------------------------------------------------------

    /// <summary>
    /// Throws <see cref="InvalidDataException"/> (or whatever the parser throws) unless the bytes are a database this
    /// launcher can read and one that is plausibly a successor of the bundled file.
    ///
    /// Two checks go beyond "it parses", because a file can parse and still be a step backwards:
    /// - fewer than half the bundled records: a truncated or gutted file, which would silently turn curated mods
    ///   back into unknown ones;
    /// - a newest record older than the bundled newest: the default branch is behind this build. That is the case
    ///   for a test build cut from a branch whose records are not merged yet, and for a dev build with new records.
    /// </summary>
    internal static List<CompatRecord> Validate(byte[] bytes, IReadOnlyCollection<CompatRecord> bundled)
    {
        if (bytes.Length > MaxBytes) throw new InvalidDataException($"larger than {MaxBytes / 1024} KB");
        // Strict decoding, and the byte order mark dropped by hand: File.ReadAllText does that for the bundled file,
        // a byte array gets no such help.
        var json = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes).TrimStart('﻿');
        CompatDbFile file;
        try { file = CompatDb.Parse(json); }
        catch (NullReferenceException) { throw new InvalidDataException("no record list"); }   // "Records": null
        var records = file.Records.Where(r => r.Id.Length > 0).ToList();
        if (records.Count == 0) throw new InvalidDataException("no records");

        var bundledCount = bundled.Count(r => !string.IsNullOrWhiteSpace(r.Id));
        if (records.Count * 2 < bundledCount)
            throw new InvalidDataException($"{records.Count} records, where the bundled database has {bundledCount}");
        if (Newest(bundled) is { } shipped && !(Newest(records) >= shipped))
            throw new InvalidDataException("older than the bundled database");
        return records;
    }

    private static DateTime? Newest(IEnumerable<CompatRecord> records) =>
        records.Where(r => r.UpdatedAt is not null).Select(r => (DateTime?)r.UpdatedAt!.Value.ToUniversalTime()).Max();

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    // ---- fetching ------------------------------------------------------------------------------------------------

    /// <summary>
    /// One polite fetch. Never throws, except for the caller's own cancellation. The cache is replaced only by a file
    /// that passed <see cref="Validate"/>; on every other path it is left exactly as it was.
    /// </summary>
    public async Task<CompatDbRemoteResult> RefreshAsync(HttpClient http, IReadOnlyCollection<CompatRecord> bundled, CancellationToken ct = default)
    {
        if (!Enabled) return new(CompatDbRemoteOutcome.Disabled);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(FetchTimeout);

            var meta = ReadMeta() ?? new CompatDbRemoteMeta();
            // The ETag is only offered while the cache it belongs to is actually in use. Otherwise - the cache was
            // deleted, damaged, or fetched by an older launcher - a 304 would confirm a file that is not there.
            var conditional = meta.ETag is { Length: > 0 } && TryReadCache(bundled) is not null;

            using var request = new HttpRequestMessage(HttpMethod.Get, Url);
            request.Headers.UserAgent.ParseAdd("ModderLords-compat-db");
            if (conditional) request.Headers.TryAddWithoutValidation("If-None-Match", meta.ETag);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.StatusCode == HttpStatusCode.NotModified)
                return new(conditional ? CompatDbRemoteOutcome.NotModified : CompatDbRemoteOutcome.Unreachable);
            if (!response.IsSuccessStatusCode) return new(CompatDbRemoteOutcome.Unreachable);

            if (response.Content.Headers.ContentLength > MaxBytes) return Reject(meta, "size", $"larger than {MaxBytes / 1024} KB");
            var bytes = await ReadCappedAsync(response.Content, timeout.Token);
            if (bytes is null) return Reject(meta, "size", $"larger than {MaxBytes / 1024} KB");

            try { Validate(bytes, bundled); }
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
    private static async Task<byte[]?> ReadCappedAsync(HttpContent content, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > MaxBytes) return null;
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
