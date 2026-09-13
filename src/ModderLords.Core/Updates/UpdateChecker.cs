using System.Net.Http;
using System.Text.Json;

namespace ModderLords.Core.Updates;

/// <summary>The one download a release offers, with the checksum GitHub publishes for it (null when it has none).</summary>
public sealed record ReleaseAsset(string Name, string DownloadUrl, long Size, string? Sha256);

/// <summary>A published release the running copy could update to.</summary>
public sealed record ReleaseInfo(Version Version, string Tag, string PageUrl, string Notes, ReleaseAsset Asset);

/// <summary>What a check found: the update, if any, and whether GitHub answered at all.</summary>
public sealed record UpdateCheckResult(ReleaseInfo? Release, bool Reached)
{
    public static UpdateCheckResult Unreachable { get; } = new(null, false);
}

/// <summary>
/// Asks GitHub whether a newer ModderLords release exists. Read-only and quiet by design: a check runs on startup, so
/// every failure - offline, rate-limited, a release shaped wrongly - means "no update", never an error in front of the
/// user.
///
/// The contract with the release process is deliberately narrow: a normal (non-prerelease) release tagged vX.Y.Z whose
/// download is named exactly ModderLords-X.Y.Z.zip. Anything else is ignored, which is how test builds stay out of
/// friends' hands - publish them as prereleases.
/// </summary>
public static class UpdateChecker
{
    public const string Repository = "PlueRyvius/ModderLords";

    /// <summary>
    /// Points the check at another feed - an https URL or a local JSON file - so the whole update path can be exercised
    /// without publishing anything. Not documented for users.
    /// </summary>
    public const string FeedOverrideEnvVar = "MODDERLORDS_UPDATE_FEED";

    public static string FeedUrl =>
        Environment.GetEnvironmentVariable(FeedOverrideEnvVar) is { Length: > 0 } custom
            ? custom
            : $"https://api.github.com/repos/{Repository}/releases/latest";

    public static string ReleasesPage => $"https://github.com/{Repository}/releases/latest";

    /// <summary>Major.Minor.Build, the only shape a release version has.</summary>
    public static string Format(Version v) => $"{v.Major}.{v.Minor}.{Math.Max(v.Build, 0)}";

    public static string AssetNameFor(Version v) => $"ModderLords-{Format(v)}.zip";

    private static Version Normalise(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));

    /// <summary>
    /// "v1.2.3" or "1.2.3" to a version; null for anything else. A suffix such as -test or -rc1 marks a build that is
    /// not a release, so it is refused here as well as by GitHub's prerelease flag.
    /// </summary>
    public static Version? ParseTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var t = tag.Trim();
        if (t.StartsWith('v') || t.StartsWith('V')) t = t[1..];
        if (t.Contains('-') || t.Contains('+')) return null;
        if (t.Count(c => c == '.') != 2) return null;
        return Version.TryParse(t, out var v) ? Normalise(v) : null;
    }

    /// <summary>Reads one release object from the GitHub API. Null when it is a draft, a prerelease, or lacks the asset.</summary>
    public static ReleaseInfo? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) return null;
            if (Flag(r, "draft") || Flag(r, "prerelease")) return null;
            var tag = Text(r, "tag_name");
            if (ParseTag(tag) is not { } version) return null;
            if (!r.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return null;

            var wanted = AssetNameFor(version);
            foreach (var a in assets.EnumerateArray())
            {
                if (!string.Equals(Text(a, "name"), wanted, StringComparison.OrdinalIgnoreCase)) continue;
                if (Text(a, "browser_download_url") is not { Length: > 0 } url) return null;
                var digest = Text(a, "digest");
                var sha = digest is not null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest[7..] : null;
                var size = a.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt64() : 0;
                return new ReleaseInfo(version, tag!, Text(r, "html_url") ?? ReleasesPage, Text(r, "body") ?? "",
                    new ReleaseAsset(wanted, url, size, sha));
            }
            return null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>Newer than what is running, and not the version the user chose to skip.</summary>
    public static bool IsUpdate(ReleaseInfo release, Version running, string? skippedVersion) =>
        release.Version > Normalise(running) && ParseTag(skippedVersion) != release.Version;

    /// <summary>
    /// The whole check. Never throws, except for the caller's own cancellation. <see cref="UpdateCheckResult.Reached"/>
    /// separates "you are up to date" from "could not ask": the startup check treats both as silence, but a check the
    /// user asked for must not claim they are up to date while offline.
    /// </summary>
    public static async Task<UpdateCheckResult> CheckAsync(HttpClient http, Version running, string? skippedVersion, CancellationToken ct = default)
    {
        try
        {
            var feed = FeedUrl;
            string json;
            if (File.Exists(feed)) json = await File.ReadAllTextAsync(feed, ct);
            else
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, feed);
                request.Headers.UserAgent.ParseAdd("ModderLords-updater");
                request.Headers.Accept.ParseAdd("application/vnd.github+json");
                using var response = await http.SendAsync(request, ct);
                if (!response.IsSuccessStatusCode) return UpdateCheckResult.Unreachable;
                json = await response.Content.ReadAsStringAsync(ct);
            }
            var release = Parse(json) is { } r && IsUpdate(r, running, skippedVersion) ? r : null;
            return new UpdateCheckResult(release, Reached: true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return UpdateCheckResult.Unreachable; }
    }

    /// <summary>For the check only: a short timeout, so a dead network never holds anything up.</summary>
    public static HttpClient CreateCheckClient() => new() { Timeout = TimeSpan.FromSeconds(10) };

    private static bool Flag(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
