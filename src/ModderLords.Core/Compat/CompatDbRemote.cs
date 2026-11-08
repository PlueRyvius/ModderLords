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
/// in place of the bundled file. The fetching, the cache and the "never worse than bundled" rule are
/// <see cref="RemoteDataFile{TBundled, TContent}"/>'s; this adds what makes a database plausible.
/// </summary>
public sealed class CompatDbRemote : RemoteDataFile<IReadOnlyCollection<CompatRecord>, List<CompatRecord>>
{
    public const string CacheFileName = "compat-db.remote.json";
    public const string MetaFileName = "compat-db.remote.meta.json";

    /// <summary>Set to 0 (or off / false / no) to neither download the database nor use a downloaded one.</summary>
    public const string EnvVar = "MODDERLORDS_COMPAT_REMOTE";

    /// <summary>The bundled file is about 14 KB. Anything near this size is not the database, whatever it parses as.</summary>
    public const int MaxBytes = 2 * 1024 * 1024;

    /// <summary>The raw file on the default branch. Carries no query string and nothing about the user or the install.</summary>
    public static string Url => $"https://raw.githubusercontent.com/{UpdateChecker.Repository}/main/src/ModderLords.Core/{CompatDb.BundledFileName}";

    public CompatDbRemote(string directory, Version running, bool enabled = true)
        : base(directory, CacheFileName, MetaFileName, running, enabled) { }

    protected override string SourceUrl => Url;
    protected override int ByteLimit => MaxBytes;
    protected override string Agent => "ModderLords-compat-db";
    protected override List<CompatRecord> Check(byte[] bytes, IReadOnlyCollection<CompatRecord> bundled) => Validate(bytes, bundled);

    /// <summary>
    /// The cache in the user's data folder, for the running launcher. Every project is versioned from
    /// Directory.Build.props, so Core's own version is the launcher's - in the app and in the CLI alike.
    /// The off switches are read here, each time, so the CLI honours the app's setting without being told.
    /// </summary>
    public static CompatDbRemote Default => new(ProfileStore.RootDir,
        typeof(CompatDbRemote).Assembly.GetName().Version ?? new Version(0, 0, 0),
        EnabledFor(Environment.GetEnvironmentVariable(EnvVar)) && UiStateStore.Load().DownloadCompatDb);

    /// <summary>The cached database, or null when the bundled one must be used (see <see cref="RemoteDataFile{TBundled, TContent}.ReadCache"/>).</summary>
    public CompatDbRemoteCache? TryReadCache(IReadOnlyCollection<CompatRecord> bundled) =>
        ReadCache(bundled) is { } c ? new CompatDbRemoteCache(c.Content, c.FetchedAtUtc) : null;

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
}
