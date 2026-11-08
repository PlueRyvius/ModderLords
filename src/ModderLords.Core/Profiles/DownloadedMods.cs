using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using ModderLords.Core.Modules;
using ModderLords.Core.Saves;

namespace ModderLords.Core.Profiles;

/// <summary>
/// Mods ModderLords downloaded itself: the maintainer's own GitHub releases that a verified profile names when the
/// Workshop does not serve that version yet (docs/PROFILE-MANAGER.md, decision 12).
///
/// Each version gets its own folder, <c>%LOCALAPPDATA%\ModderLords\mods\&lt;Id&gt;\&lt;version&gt;\&lt;Id&gt;</c>,
/// and a profile pins it (<see cref="ProfileMod.SourcePath"/>). Nothing is written to the game's Modules folder or the
/// Workshop's: a developer's own copy in Modules, and a subscriber's Workshop copy, stay exactly as they were.
/// </summary>
public static class DownloadedMods
{
    /// <summary>The largest release accepted. Modular Smithing 2 is about 450 MB.</summary>
    public const long MaxBytes = 2L * 1024 * 1024 * 1024;

    public static string Root => RootOverride ?? Path.Combine(ProfileStore.RootDir, "mods");

    /// <summary>Test seam.</summary>
    internal static string? RootOverride { get; set; }

    public static string FolderFor(string id, string version) => Path.Combine(Root, Safe(id), Safe(version), id);

    private static string Safe(string s) => string.Concat(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    /// <summary>Every downloaded copy of a mod.</summary>
    public static IEnumerable<DiscoveredModule> Copies(string id)
    {
        var dir = Path.Combine(Root, Safe(id));
        if (!Directory.Exists(dir)) yield break;
        foreach (var version in Directory.EnumerateDirectories(dir))
        {
            var folder = Path.Combine(version, id);
            if (Directory.Exists(folder) && ModuleCatalog.TryParse(folder, ModuleSourceKind.Custom, out _) is { } m
                && m.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) yield return m;
        }
    }

    /// <summary>Whether a folder is one of ours, so a profile pins it rather than leaving the choice to the scan.</summary>
    public static bool IsDownloaded(string folder) => folder.Length > 0 &&
        Path.GetFullPath(folder).StartsWith(Path.GetFullPath(Root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Downloads the release, refuses it unless its SHA-256 is the one the verified profile pins, and unpacks it into
    /// its version's folder. The zip must hold the mod's folder (<c>&lt;Id&gt;/SubModule.xml</c>) at the version the
    /// profile names. Returns that folder; one already there at that version is kept and returned without a download.
    /// </summary>
    /// <param name="progress">Bytes so far, and the total when the server says.</param>
    public static async Task<string> InstallAsync(VerifiedMod mod, HttpClient http, IProgress<(long Done, long? Total)>? progress = null,
        CancellationToken ct = default)
    {
        var release = mod.GitHub ?? throw new InvalidOperationException($"{mod.Id} has no GitHub release");
        var target = FolderFor(mod.Id, mod.Version);
        if (Directory.Exists(target) && ModuleCatalog.TryParse(target, ModuleSourceKind.Custom, out _) is { } have
            && SaveHeaderReader.VersionsEqual(have.Version, mod.Version)) return target;

        Directory.CreateDirectory(Root);
        var zip = Path.Combine(Root, $".download-{Guid.NewGuid():N}.zip");
        var staging = Path.Combine(Root, $".staging-{Guid.NewGuid():N}");
        try
        {
            await DownloadAsync(release, zip, http, progress, ct);
            await Task.Run(() => ZipFile.ExtractToDirectory(zip, staging), ct);   // refuses entries that would land outside staging
            var unpacked = Path.Combine(staging, mod.Id);
            if (ModuleCatalog.TryParse(unpacked, ModuleSourceKind.Custom, out _) is not { } found || !found.Id.Equals(mod.Id, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"{release.Asset} does not hold a {mod.Id} folder");
            if (!SaveHeaderReader.VersionsEqual(found.Version, mod.Version))
                throw new InvalidDataException($"{release.Asset} holds {mod.Id} {found.Version}, not {mod.Version}");

            // A half-written folder from an earlier attempt is replaced as a whole, never merged into: a mod's
            // ModuleData and its DLLs must come from the same release.
            if (Directory.Exists(target)) Directory.Delete(target, true);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            Directory.Move(unpacked, target);
            return target;
        }
        finally
        {
            try { if (File.Exists(zip)) File.Delete(zip); } catch (Exception) { }
            try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch (Exception) { }
        }
    }

    private static async Task DownloadAsync(GitHubRelease release, string path, HttpClient http, IProgress<(long, long?)>? progress, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, release.DownloadUrl);
        request.Headers.UserAgent.ParseAdd("ModderLords-mod-download");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"GitHub answered {(int)response.StatusCode} for {release.Asset}");
        var total = response.Content.Headers.ContentLength;
        if (total > MaxBytes) throw new InvalidDataException($"{release.Asset} is larger than {MaxBytes >> 20} MB");

        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using (var source = await response.Content.ReadAsStreamAsync(ct))
        await using (var file = File.Create(path))
        {
            var chunk = new byte[1 << 16];
            long done = 0;
            int read;
            while ((read = await source.ReadAsync(chunk, ct)) > 0)
            {
                done += read;
                if (done > MaxBytes) throw new InvalidDataException($"{release.Asset} is larger than {MaxBytes >> 20} MB");
                sha.AppendData(chunk, 0, read);
                await file.WriteAsync(chunk.AsMemory(0, read), ct);
                progress?.Report((done, total));
            }
        }
        var got = Convert.ToHexString(sha.GetHashAndReset());
        if (!got.Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"{release.Asset} is not the file the verified profile names (its SHA-256 differs), so it was not used");
    }
}
