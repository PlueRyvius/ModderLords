using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;

namespace ModderLords.Core.Updates;

/// <summary>
/// Downloads a release, proves it is the file GitHub published, and swaps it into the folder the running copy lives
/// in - with a way back if anything fails part-way.
///
/// The swap relies on one Windows fact: a running exe cannot be overwritten or deleted, but it CAN be renamed. So the
/// running ModderLords.exe becomes ModderLords.exe.old, the new one is copied in beside it, the new copy is started, and
/// the next start deletes the .old. Everything the release replaces is moved into a .previous folder inside the install
/// folder first; that stays on the same drive, where a folder move is a rename rather than a copy that can fail half way.
///
/// Only what the release zip contains is touched. User data lives in %LOCALAPPDATA%\ModderLords and is never involved.
/// </summary>
public sealed class UpdateInstaller
{
    public const string ExeName = "ModderLords.exe";
    public const string BackupFolderName = ".previous";

    public string InstallDir { get; }
    public string UpdatesDir { get; }

    /// <summary>Test seam: called with each item's name just before it is copied in, so a test can fail the swap.</summary>
    internal Action<string>? BeforeCopy { get; set; }

    public UpdateInstaller(string installDir, string updatesDir)
    {
        InstallDir = Path.GetFullPath(installDir);
        UpdatesDir = Path.GetFullPath(updatesDir);
    }

    /// <summary>The folder this copy of the app runs from, with downloads kept beside the user's other data.</summary>
    public static UpdateInstaller ForThisApp() =>
        new(AppContext.BaseDirectory, Path.Combine(Profiles.ProfileStore.RootDir, "updates"));

    public string BackupDir => Path.Combine(InstallDir, BackupFolderName);

    /// <summary>False for a copy unzipped somewhere the user cannot write (Program Files, say): offer the download page instead.</summary>
    public bool CanWriteInstallDir()
    {
        try
        {
            var probe = Path.Combine(InstallDir, $".modderlords-write-test-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch (Exception) { return false; }
    }

    /// <summary>For the download: no short timeout, since the zip is ~60 MB; cancellation is the caller's.</summary>
    public static HttpClient CreateDownloadClient() => new() { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>
    /// Downloads the release zip and checks it against the published SHA-256. Nothing in the install folder has been
    /// touched when this throws. A local file path as the download URL is copied instead (the offline test feed).
    /// </summary>
    public async Task<string> DownloadAndVerifyAsync(ReleaseInfo release, HttpClient http, IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(release.Asset.Sha256))
            throw new InvalidDataException("This release does not publish a checksum for its download, so it cannot be verified. " +
                                           "Download it from the release page instead.");
        Directory.CreateDirectory(UpdatesDir);
        var zip = Path.Combine(UpdatesDir, release.Asset.Name);
        var part = zip + ".part";
        try
        {
            if (File.Exists(release.Asset.DownloadUrl))
            {
                File.Copy(release.Asset.DownloadUrl, part, overwrite: true);
                progress?.Report(1);
            }
            else
            {
                using var response = await http.GetAsync(release.Asset.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength ?? release.Asset.Size;
                await using var source = await response.Content.ReadAsStreamAsync(ct);
                await using var target = File.Create(part);
                var buffer = new byte[81920];
                long done = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, ct)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), ct);
                    done += read;
                    if (total > 0) progress?.Report(Math.Min(1, (double)done / total));
                }
            }

            var actual = await Sha256Of(part, ct);
            if (!actual.Equals(release.Asset.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"The download does not match its published checksum (expected {release.Asset.Sha256}, " +
                                               $"got {actual}). Nothing was changed.");
            File.Move(part, zip, overwrite: true);
            return zip;
        }
        finally
        {
            TryDelete(part);
        }
    }

    public static async Task<string> Sha256Of(string path, CancellationToken ct = default)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).ToLowerInvariant();
    }

    /// <summary>
    /// Extracts a verified zip and swaps it into the install folder. Returns the path of the new exe to start. On any
    /// failure the install folder is put back as it was and the exception is rethrown.
    /// </summary>
    public string Install(string zipPath)
    {
        var staging = Path.Combine(UpdatesDir, "staging");
        DeleteDirectory(staging);
        ZipFile.ExtractToDirectory(zipPath, staging);
        if (!File.Exists(Path.Combine(staging, ExeName)))
        {
            DeleteDirectory(staging);
            throw new InvalidDataException($"{Path.GetFileName(zipPath)} does not contain {ExeName}, so it is not a ModderLords release. Nothing was changed.");
        }

        DeleteDirectory(BackupDir);
        Directory.CreateDirectory(BackupDir);
        var exe = Path.Combine(InstallDir, ExeName);
        var renamedExe = OldExeName();
        var backedUp = new List<string>();   // moved into .previous
        var copied = new List<string>();     // written into the install folder
        var exeRenamed = false;
        try
        {
            foreach (var source in Directory.EnumerateFileSystemEntries(staging))
            {
                var name = Path.GetFileName(source);
                var target = Path.Combine(InstallDir, name);
                if (name.Equals(ExeName, StringComparison.OrdinalIgnoreCase))
                {
                    if (!File.Exists(target)) continue;
                    File.Copy(target, Path.Combine(BackupDir, name), overwrite: true);
                    File.Move(target, renamedExe);
                    exeRenamed = true;
                }
                else if (Directory.Exists(target)) { Directory.Move(target, Path.Combine(BackupDir, name)); backedUp.Add(name); }
                else if (File.Exists(target)) { File.Move(target, Path.Combine(BackupDir, name)); backedUp.Add(name); }
            }

            foreach (var source in Directory.EnumerateFileSystemEntries(staging))
            {
                var name = Path.GetFileName(source);
                BeforeCopy?.Invoke(name);
                var target = Path.Combine(InstallDir, name);
                copied.Add(name);
                if (Directory.Exists(source)) CopyDirectory(source, target);
                else File.Copy(source, target, overwrite: true);
            }
        }
        catch
        {
            // Back to exactly what was there: what we wrote goes, what we moved aside comes back, the exe gets its name.
            foreach (var name in copied)
            {
                var path = Path.Combine(InstallDir, name);
                if (Directory.Exists(path)) DeleteDirectory(path); else TryDelete(path);
            }
            foreach (var name in backedUp)
            {
                var from = Path.Combine(BackupDir, name);
                var to = Path.Combine(InstallDir, name);
                if (Directory.Exists(from)) Directory.Move(from, to); else if (File.Exists(from)) File.Move(from, to);
            }
            if (exeRenamed && File.Exists(renamedExe))
            {
                TryDelete(exe);
                File.Move(renamedExe, exe);
            }
            DeleteDirectory(staging);
            throw;
        }

        DeleteDirectory(staging);
        TryDelete(zipPath);
        return exe;
    }

    /// <summary>
    /// Run at startup: the renamed old exe can only be deleted once it has stopped running, which is now. The .previous
    /// backup stays until the next update replaces it, so one step back is always possible by hand.
    /// </summary>
    public int CleanUpAfterUpdate()
    {
        var removed = 0;
        if (!Directory.Exists(InstallDir)) return 0;
        foreach (var old in Directory.EnumerateFiles(InstallDir, ExeName + ".old*"))
            if (TryDelete(old)) removed++;
        return removed;
    }

    /// <summary>ModderLords.exe.old, or a numbered name when an earlier .old is still locked by a process that has not exited.</summary>
    private string OldExeName()
    {
        var candidate = Path.Combine(InstallDir, ExeName + ".old");
        if (!File.Exists(candidate) || TryDelete(candidate)) return candidate;
        return Path.Combine(InstallDir, $"{ExeName}.old-{DateTime.UtcNow:yyyyMMddHHmmss}");
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
        foreach (var dir in Directory.EnumerateDirectories(source))
            CopyDirectory(dir, Path.Combine(target, Path.GetFileName(dir)));
    }

    private static bool TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); return !File.Exists(path); }
        catch (Exception) { return false; }
    }

    private static void DeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (Exception) { }
    }
}
