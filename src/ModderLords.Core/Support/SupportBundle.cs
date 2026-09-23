using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ModderLords.Core.Modules;
using ModderLords.Core.Profiles;
using ModderLords.Core.Saves;

namespace ModderLords.Core.Support;

public sealed record SupportEnvironment(
    string Mode,
    string ModderLordsVersion,
    string? BannerlordVersion,
    string? CoopVersion,
    string WindowsVersion,
    string ProcessArchitecture,
    string RuntimeVersion);

public sealed record SupportTextSource(string Category, string Path, int Priority = 50);

public sealed record SupportGeneratedText(string ArchiveName, string Text, int Priority = 90, bool Required = false);

public sealed record SupportFileFingerprint(string Name, long Length, string Sha256);

public sealed record SupportModuleInfo(
    string Id,
    string Version,
    string Source,
    string Folder,
    bool Selected,
    bool Enabled,
    string? Role,
    IReadOnlyList<string> Dependencies,
    IReadOnlyList<string> LoadAfter,
    IReadOnlyList<string> DllNames,
    string? ManifestSha256,
    [property: JsonIgnore] string? ManifestText,
    IReadOnlyList<SupportFileFingerprint> DllFingerprints);

public sealed record SupportSaveInfo(
    string Path,
    string Name,
    string ApplicationVersion,
    IReadOnlyList<string> ModuleIds,
    IReadOnlyDictionary<string, string> ModuleVersions,
    double CampaignDay,
    DateTime LastWriteUtc,
    long Length)
{
    public static SupportSaveInfo From(SaveHeader header) => new(
        header.Path, header.Name, header.ApplicationVersion, header.ModuleIds,
        header.ModuleVersions, header.DayLong, header.LastWriteUtc, header.Length);
}

public sealed record SupportBundleRequest
{
    public required string OutputDirectory { get; init; }
    public required SupportEnvironment Environment { get; init; }
    public required Profile ProfileSnapshot { get; init; }
    public required IReadOnlyList<SupportModuleInfo> Modules { get; init; }
    public required IReadOnlyList<string> EffectiveLoadOrder { get; init; }
    public IReadOnlyList<SupportTextSource> TextSources { get; init; } = [];
    public IReadOnlyList<SupportGeneratedText> GeneratedText { get; init; } = [];
    public IReadOnlyList<string> InitialWarnings { get; init; } = [];
    public IReadOnlyDictionary<string, string> PathTokens { get; init; } = new Dictionary<string, string>();
    public IReadOnlyList<string> KnownSecrets { get; init; } = [];
    public SupportSaveInfo? Save { get; init; }
    public bool IncludeSave { get; init; }
    public bool ServerRunning { get; init; }
    public Action? FlushLogs { get; init; }
    public long DiagnosticMaxBytes { get; init; } = SupportBundleBuilder.DefaultDiagnosticMaxBytes;
    public long SaveMaxBytes { get; init; } = SupportBundleBuilder.DefaultSaveMaxBytes;
}

public sealed record SupportBundleEntryReport(
    string ArchiveName,
    string Category,
    string Source,
    long OriginalBytes,
    long IncludedBytes,
    string? Sha256,
    bool Redacted,
    bool Truncated,
    string Status);

public sealed record SupportBundleResult(
    string ReportId,
    string DiagnosticPath,
    string? SavePath,
    long DiagnosticBytes,
    long? SaveBytes,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<SupportBundleEntryReport> Entries);

/// <summary>
/// Creates a bounded, inspectable support archive from an explicit list of files. It never walks module folders,
/// follows junctions, uploads anything, or copies a mod binary. Callers resolve product-specific paths and pass
/// only the regular files they want considered.
/// </summary>
public static class SupportBundleBuilder
{
    public const long DefaultDiagnosticMaxBytes = 20L * 1024 * 1024;
    public const long DefaultSaveMaxBytes = 24L * 1024 * 1024;
    public const long LocalBundleBudget = 250L * 1024 * 1024;
    public const int KeepReports = 5;
    public const int HeadBytes = 256 * 1024;
    public const int TailBytes = 2 * 1024 * 1024;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private sealed record PreparedEntry(
        string ArchiveName,
        string Category,
        string Source,
        byte[] Bytes,
        long OriginalBytes,
        int Priority,
        bool Required,
        bool Redacted,
        bool Truncated);

    public static async Task<SupportBundleResult> CreateAsync(SupportBundleRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.DiagnosticMaxBytes <= 0) throw new ArgumentOutOfRangeException(nameof(request.DiagnosticMaxBytes));
        Directory.CreateDirectory(request.OutputDirectory);

        var reportId = $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}"[..24];
        var warnings = new List<string>(request.InitialWarnings);
        try { request.FlushLogs?.Invoke(); }
        catch (Exception ex) { warnings.Add("The active launch log could not be flushed before capture: " + ex.Message); }
        var reports = new List<SupportBundleEntryReport>();
        string? savePath = null;
        long? saveBytes = null;

        if (request.IncludeSave)
        {
            if (request.ServerRunning)
                warnings.Add("The selected save was not included because the server is running. Stop it before taking a consistent save snapshot.");
            else if (request.Save is null || !File.Exists(request.Save.Path))
                warnings.Add("The selected save was not included because it could not be found.");
            else
                (savePath, saveBytes) = await CreateSaveArchiveAsync(request, reportId, warnings, cancellationToken).ConfigureAwait(false);
        }

        var redactor = new SupportRedactor(request.PathTokens, request.KnownSecrets);
        var prepared = new List<PreparedEntry>();

        AddStructured(prepared, "environment.json", "environment", request.Environment, redactor, required: true);
        AddStructured(prepared, "profile.json", "profile", SanitizedProfile(request.ProfileSnapshot, redactor), redactor, required: true);
        AddStructured(prepared, "modules.json", "modules", new
        {
            EffectiveLoadOrder = request.EffectiveLoadOrder,
            InstalledCopies = request.Modules,
        }, redactor, required: true);
        if (request.Save is { } save)
            AddStructured(prepared, "save-metadata.json", "save metadata", new
            {
                save.ApplicationVersion,
                save.ModuleIds,
                save.ModuleVersions,
                save.CampaignDay,
                save.LastWriteUtc,
                save.Length,
            }, redactor, required: true);

        foreach (var (module, index) in request.Modules.Where(m => m.Selected && m.ManifestText is not null).Select((m, i) => (m, i)))
            prepared.Add(new PreparedEntry(SafeArchivePath($"manifests/{index + 1:D3}-{module.Id}-SubModule.xml"),
                "selected manifests", module.Folder, Encoding.UTF8.GetBytes(redactor.Redact(module.ManifestText!)),
                Encoding.UTF8.GetByteCount(module.ManifestText!), 75, false, true,
                module.ManifestText!.Contains("manifest truncated by support bundle", StringComparison.Ordinal)));

        foreach (var generated in request.GeneratedText)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var redacted = redactor.Redact(generated.Text);
            prepared.Add(new PreparedEntry(SafeArchivePath(generated.ArchiveName), "generated", "generated by ModderLords",
                Encoding.UTF8.GetBytes(redacted), Encoding.UTF8.GetByteCount(generated.Text), generated.Priority,
                generated.Required, true, false));
        }

        foreach (var source in request.TextSources.OrderByDescending(s => s.Priority))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var loaded = await ReadTextSnapshotAsync(source.Path, cancellationToken).ConfigureAwait(false);
            if (loaded.Error is { } error)
            {
                var src = redactor.Redact(source.Path);
                reports.Add(new SupportBundleEntryReport("", source.Category, src, loaded.OriginalBytes, 0, null,
                    false, false, "omitted: " + error));
                warnings.Add($"{source.Category}: {Path.GetFileName(source.Path)} was skipped ({error}).");
                continue;
            }

            var redacted = redactor.Redact(loaded.Text!);
            var archiveName = UniqueArchiveName(prepared, source.Category, Path.GetFileName(source.Path));
            prepared.Add(new PreparedEntry(archiveName, source.Category, redactor.Redact(source.Path),
                Encoding.UTF8.GetBytes(redacted), loaded.OriginalBytes, source.Priority, false, true, loaded.Truncated));
            if (loaded.Changed)
                warnings.Add($"{source.Category}: {Path.GetFileName(source.Path)} changed while it was being read; the bundle contains a best-effort snapshot.");
        }

        var included = prepared.ToList();
        var diagnosticPath = Path.Combine(request.OutputDirectory, $"ModderLords-support-{reportId}.zip");
        var tempPath = diagnosticPath + ".tmp";
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var evidenceReports = reports.Concat(included.Select(ToReport)).ToList();
                var entryReports = evidenceReports.Concat([
                    new SupportBundleEntryReport("report.json", "manifest", "generated by ModderLords", 0, 0, null,
                        false, false, "included; self-hash omitted"),
                    new SupportBundleEntryReport("README.txt", "manifest", "generated by ModderLords", 0, 0, null,
                        false, false, "included; self-hash omitted"),
                ]).ToList();
                var reportObject = new
                {
                    SchemaVersion = 1,
                    ReportId = reportId,
                    CreatedUtc = DateTime.UtcNow,
                    request.Environment,
                    Warnings = warnings,
                    Entries = entryReports,
                };
                var reportJson = JsonSerializer.Serialize(reportObject, Json);
                var readme = BuildReadme(reportId, request.Environment, entryReports, warnings, savePath, saveBytes);
                var final = included.Concat([
                    RequiredText("report.json", "manifest", reportJson),
                    RequiredText("README.txt", "manifest", readme),
                ]).ToList();

                await WriteZipAsync(tempPath, final, cancellationToken).ConfigureAwait(false);
                var length = new FileInfo(tempPath).Length;
                if (length <= request.DiagnosticMaxBytes)
                {
                    File.Move(tempPath, diagnosticPath, overwrite: true);
                    reports = entryReports;
                    break;
                }

                var drop = included.Where(e => !e.Required)
                    .OrderBy(e => e.Priority).ThenByDescending(e => e.Bytes.Length).FirstOrDefault();
                if (drop is null)
                    throw new InvalidOperationException($"The required support metadata alone exceeds {request.DiagnosticMaxBytes / (1024 * 1024)} MiB.");
                included.Remove(drop);
                reports.Add(ToReport(drop) with { IncludedBytes = 0, Sha256 = null, Status = "omitted: archive size budget" });
                warnings.Add($"{drop.ArchiveName} was omitted to keep the diagnostics archive attachable to GitHub.");
            }
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
        }

        CleanupOldBundles(request.OutputDirectory, reportId);
        return new SupportBundleResult(reportId, diagnosticPath, savePath, new FileInfo(diagnosticPath).Length,
            saveBytes, warnings, reports);
    }

    public static SupportModuleInfo DescribeModule(DiscoveredModule module, bool selected, bool enabled, string? role,
        IReadOnlyDictionary<string, string> pathTokens)
    {
        var redactor = new SupportRedactor(pathTokens, []);
        var manifest = Path.Combine(module.FolderPath, "SubModule.xml");
        var dllNames = module.Info.SubModules.Select(s => s.DLLName).Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n).ToList();
        var dlls = new List<SupportFileFingerprint>();
        foreach (var bin in new[] { module.ClientBin, module.ServerBin })
        foreach (var name in dllNames)
        {
            var path = Path.Combine(bin, name);
            if (!File.Exists(path)) continue;
            try
            {
                var fi = new FileInfo(path);
                dlls.Add(new SupportFileFingerprint($"{(bin == module.ClientBin ? "client" : "server")}/{name}", fi.Length, HashFile(path)));
            }
            catch { }
        }

        var dependencies = module.Info.DependentModules.Select(d => d.Id)
            .Concat(module.Info.DependentModuleMetadatas.Where(d => !d.IsIncompatible).Select(d => d.Id))
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(id => id).ToList();
        var loadAfter = module.Info.ModulesToLoadAfterThis.Select(d => d.Id)
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(id => id).ToList();
        string? manifestHash = null;
        string? manifestText = null;
        try { if (File.Exists(manifest)) manifestHash = HashFile(manifest); } catch { }
        if (selected)
        {
            try
            {
                using var reader = new StreamReader(new FileStream(manifest, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                var buffer = new char[64 * 1024];
                var count = reader.ReadBlock(buffer, 0, buffer.Length);
                manifestText = redactor.Redact(new string(buffer, 0, count));
                if (!reader.EndOfStream) manifestText += "\n<!-- manifest truncated by support bundle -->";
            }
            catch { }
        }
        return new SupportModuleInfo(module.Id, module.Version, module.Source.ToString(), redactor.Redact(module.FolderPath),
            selected, enabled, role, dependencies, loadAfter, dllNames, manifestHash, manifestText, dlls);
    }

    public static long EstimateSourceBytes(IEnumerable<SupportTextSource> sources)
    {
        long total = 0;
        foreach (var source in sources)
        {
            try { total += Math.Min(new FileInfo(source.Path).Length, HeadBytes + TailBytes); }
            catch { }
        }
        return total;
    }

    private static object SanitizedProfile(Profile profile, SupportRedactor redactor) => new
    {
        profile.SchemaVersion,
        Name = "[current profile]",
        DedicatedServerRoot = profile.DedicatedServerRoot is null ? null : redactor.Redact(profile.DedicatedServerRoot),
        GameRoot = profile.GameRoot is null ? null : redactor.Redact(profile.GameRoot),
        CustomModRoots = profile.CustomModRoots.Select(redactor.Redact).ToList(),
        Mods = profile.Mods.Select(m => new
        {
            m.Id, m.Role, m.Enabled,
            SourcePath = m.SourcePath is null ? null : redactor.Redact(m.SourcePath),
            m.LastVersion, m.ServerAuthoritative, m.ClientSideBehaviors,
        }).ToList(),
        Save = string.IsNullOrWhiteSpace(profile.SaveName) ? "not configured" : "configured",
        profile.CompatGuards,
        profile.SettingsSync,
        profile.AutomaticCompatibility,
        profile.ManualLoadOrder,
        profile.StallWarningSeconds,
        profile.UseModDistanceCache,
        profile.GenerateWorldWithActiveMods,
        Server = new
        {
            profile.Server.JoinPort,
            profile.Server.EnginePort,
            profile.Server.Region,
            Password = string.IsNullOrEmpty(profile.Server.Password) ? "" : "[REDACTED]",
            profile.Server.Steam,
            profile.Server.AutosaveMinutes,
            profile.Server.LogFile,
            profile.Server.Visibility,
            profile.Server.TraceTick,
            profile.Server.TracePublish,
            profile.Server.TraceBandits,
        },
        profile.ClientOfficialModules,
    };

    private static void AddStructured(List<PreparedEntry> entries, string name, string category, object value,
        SupportRedactor redactor, bool required)
    {
        var raw = JsonSerializer.Serialize(value, Json);
        var text = redactor.Redact(raw);
        entries.Add(new PreparedEntry(name, category, "generated by ModderLords", Encoding.UTF8.GetBytes(text),
            Encoding.UTF8.GetByteCount(raw), 100, required, true, false));
    }

    private static PreparedEntry RequiredText(string name, string category, string text) =>
        new(name, category, "generated by ModderLords", Encoding.UTF8.GetBytes(text), Encoding.UTF8.GetByteCount(text),
            1000, true, false, false);

    private static SupportBundleEntryReport ToReport(PreparedEntry entry) => new(
        entry.ArchiveName, entry.Category, entry.Source, entry.OriginalBytes, entry.Bytes.Length,
        Convert.ToHexString(SHA256.HashData(entry.Bytes)).ToLowerInvariant(), entry.Redacted, entry.Truncated, "included");

    private static string BuildReadme(string id, SupportEnvironment environment,
        IReadOnlyList<SupportBundleEntryReport> entries, IReadOnlyList<string> warnings, string? savePath, long? saveBytes)
    {
        var sb = new StringBuilder();
        sb.AppendLine("ModderLords support bundle");
        sb.AppendLine($"Report ID: {id}");
        sb.AppendLine($"Created: {DateTime.UtcNow:O}");
        sb.AppendLine($"Mode: {environment.Mode}");
        sb.AppendLine($"ModderLords: {environment.ModderLordsVersion}");
        sb.AppendLine($"Bannerlord: {environment.BannerlordVersion ?? "unknown"}");
        sb.AppendLine($"Bannerlord Coop: {environment.CoopVersion ?? "unknown"}");
        sb.AppendLine();
        sb.AppendLine("Privacy: passwords and common identifiers were redacted, but arbitrary mod logs can contain user-written text. Review this ZIP before posting it publicly.");
        sb.AppendLine("No files were uploaded automatically.");
        sb.AppendLine();
        if (savePath is not null) sb.AppendLine($"Optional save archive: {Path.GetFileName(savePath)} ({saveBytes:N0} bytes)");
        if (warnings.Count > 0)
        {
            sb.AppendLine("Warnings:");
            foreach (var warning in warnings) sb.AppendLine("- " + warning);
            sb.AppendLine();
        }
        sb.AppendLine("Entries:");
        foreach (var entry in entries.OrderBy(e => e.ArchiveName))
            sb.AppendLine($"- {(entry.ArchiveName.Length == 0 ? Path.GetFileName(entry.Source) : entry.ArchiveName)}: {entry.Status}; " +
                          $"source={entry.Source}; original={entry.OriginalBytes:N0}; included={entry.IncludedBytes:N0}; " +
                          $"redacted={entry.Redacted}; truncated={entry.Truncated}");
        return sb.ToString();
    }

    private static async Task<(string? Path, long? Bytes)> CreateSaveArchiveAsync(SupportBundleRequest request, string reportId,
        List<string> warnings, CancellationToken cancellationToken)
    {
        var save = request.Save!;
        var output = Path.Combine(request.OutputDirectory, $"ModderLords-save-{reportId}.zip");
        var temp = output + ".tmp";
        try
        {
            var before = new FileInfo(save.Path);
            var beforeLength = before.Length;
            var beforeWrite = before.LastWriteTimeUtc;
            await using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            using (var zip = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: false))
            {
                var entry = zip.CreateEntry(Path.GetFileName(save.Path), CompressionLevel.Optimal);
                await using var destination = entry.Open();
                await using var source = new FileStream(save.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
                await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
            }
            var after = new FileInfo(save.Path);
            if (after.Length != beforeLength || after.LastWriteTimeUtc != beforeWrite)
            {
                warnings.Add("The selected save changed while it was being copied, so its archive was discarded.");
                return (null, null);
            }
            var bytes = new FileInfo(temp).Length;
            if (bytes > request.SaveMaxBytes)
            {
                warnings.Add($"The selected save compressed to {bytes / (1024d * 1024):0.0} MiB, over GitHub's 25 MB archive limit, so it was not attached. Ask the maintainer for a private transfer method if the save is required.");
                return (null, null);
            }
            File.Move(temp, output, overwrite: true);
            return (output, bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warnings.Add("The selected save could not be packaged: " + ex.Message);
            return (null, null);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }

    private static async Task WriteZipAsync(string path, IReadOnlyList<PreparedEntry> entries, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
        foreach (var item in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = archive.CreateEntry(item.ArchiveName, CompressionLevel.Optimal);
            entry.LastWriteTime = DateTimeOffset.Now;
            await using var destination = entry.Open();
            await destination.WriteAsync(item.Bytes, cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed record TextSnapshot(string? Text, long OriginalBytes, bool Truncated, bool Changed, string? Error);

    private static async Task<TextSnapshot> ReadTextSnapshotAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if (attributes.HasFlag(FileAttributes.Directory) || attributes.HasFlag(FileAttributes.ReparsePoint))
                return new(null, 0, false, false, "not a regular file");
            var before = new FileInfo(path);
            var beforeLength = before.Length;
            var beforeWrite = before.LastWriteTimeUtc;
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 81920, useAsync: true);
            var original = stream.Length;
            if (original <= HeadBytes + TailBytes)
            {
                using var memory = new MemoryStream((int)Math.Min(original, int.MaxValue));
                await stream.CopyToAsync(memory, cancellationToken).ConfigureAwait(false);
                return new(Encoding.UTF8.GetString(memory.ToArray()), original, false,
                    Changed(path, beforeLength, beforeWrite), null);
            }

            var head = new byte[HeadBytes];
            await ReadFullyAsync(stream, head, cancellationToken).ConfigureAwait(false);
            stream.Seek(-TailBytes, SeekOrigin.End);
            var tail = new byte[TailBytes];
            await ReadFullyAsync(stream, tail, cancellationToken).ConfigureAwait(false);
            var marker = Encoding.UTF8.GetBytes($"\r\n--- {original - HeadBytes - TailBytes:N0} bytes omitted by support-bundle truncation ---\r\n");
            var bytes = new byte[head.Length + marker.Length + tail.Length];
            Buffer.BlockCopy(head, 0, bytes, 0, head.Length);
            Buffer.BlockCopy(marker, 0, bytes, head.Length, marker.Length);
            Buffer.BlockCopy(tail, 0, bytes, head.Length + marker.Length, tail.Length);
            return new(Encoding.UTF8.GetString(bytes), original, true,
                Changed(path, beforeLength, beforeWrite), null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new(null, 0, false, false, ex.Message);
        }
    }

    private static bool Changed(string path, long length, DateTime lastWriteUtc)
    {
        try
        {
            var after = new FileInfo(path);
            return after.Length != length || after.LastWriteTimeUtc != lastWriteUtc;
        }
        catch { return true; }
    }

    private static async Task ReadFullyAsync(Stream stream, byte[] bytes, CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < bytes.Length)
        {
            var n = await stream.ReadAsync(bytes.AsMemory(read), cancellationToken).ConfigureAwait(false);
            if (n == 0) break;
            read += n;
        }
    }

    private static string UniqueArchiveName(IEnumerable<PreparedEntry> existing, string category, string fileName)
    {
        var folder = SafeSegment(category);
        var name = SafeSegment(fileName);
        var candidate = $"logs/{folder}/{name}";
        var used = existing.Select(e => e.ArchiveName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var i = 2; used.Contains(candidate); i++)
            candidate = $"logs/{folder}/{Path.GetFileNameWithoutExtension(name)}-{i}{Path.GetExtension(name)}";
        return candidate;
    }

    private static string SafeArchivePath(string value) => string.Join('/', value.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).Select(SafeSegment));

    private static string SafeSegment(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var clean = new string(value.Select(c => invalid.Contains(c) || c is '/' or '\\' ? '_' : c).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(clean) ? "file" : clean;
    }

    private static string HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static void CleanupOldBundles(string directory, string currentReportId)
    {
        try
        {
            var files = new DirectoryInfo(directory).EnumerateFiles("ModderLords-*.zip", SearchOption.TopDirectoryOnly)
                .Where(f => f.Name.StartsWith("ModderLords-support-", StringComparison.OrdinalIgnoreCase)
                         || f.Name.StartsWith("ModderLords-save-", StringComparison.OrdinalIgnoreCase))
                .Select(f => new { File = f, Id = ReportIdFrom(f.Name) })
                .Where(x => x.Id is not null).ToList();
            var groups = files.GroupBy(x => x.Id!, StringComparer.OrdinalIgnoreCase)
                .Select(g => new { Id = g.Key, Files = g.Select(x => x.File).ToList(), Newest = g.Max(x => x.File.LastWriteTimeUtc), Size = g.Sum(x => x.File.Length) })
                .OrderByDescending(g => g.Newest).ToList();
            long retained = 0;
            var kept = 0;
            foreach (var group in groups)
            {
                var keep = group.Id.Equals(currentReportId, StringComparison.OrdinalIgnoreCase)
                           || (kept < KeepReports && retained + group.Size <= LocalBundleBudget);
                if (keep) { retained += group.Size; kept++; continue; }
                foreach (var file in group.Files) try { file.Delete(); } catch { }
            }
        }
        catch { }
    }

    private static string? ReportIdFrom(string name)
    {
        foreach (var prefix in new[] { "ModderLords-support-", "ModderLords-save-" })
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                return name[prefix.Length..^4];
        return null;
    }
}

/// <summary>Best-effort redaction for human-readable support material. Structured configuration is also projected
/// into secret-free DTOs before reaching this pass; the regexes are the second line of defence for arbitrary logs.</summary>
public sealed class SupportRedactor
{
    private static readonly Regex SecretAssignment = new(
        @"(?ix)(?<key>password|passwd|pwd|token|secret|credential|api[_-]?key|access[_-]?key|authorization|cookie|webhook)(?<separator>\s*[\""']?\s*[:=]\s*[\""']?)(?<value>[^\s,;}\""']+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex WebhookUrl = new(@"(?i)https?://[^\s/]+/api/webhooks/[^\s\""']+", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex AuthorizationHeader = new(@"(?im)(?<key>authorization\s*[:=]\s*)(?:bearer\s+)?[^\r\n,;}]+", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SteamId = new(@"\b7656119\d{10}\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Ipv4 = new(@"(?<![\d.])(?:25[0-5]|2[0-4]\d|1?\d?\d)(?:\.(?:25[0-5]|2[0-4]\d|1?\d?\d)){3}(?![\d.])", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    // At least three colon-separated groups, so ordinary log timestamps such as 12:34:56 survive.
    private static readonly Regex ColonAddress = new(@"(?i)(?<![\w:])[0-9a-f:]{3,}(?![\w:])", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private readonly IReadOnlyList<KeyValuePair<string, string>> _paths;
    private readonly IReadOnlyList<string> _secrets;

    public SupportRedactor(IReadOnlyDictionary<string, string> pathTokens, IReadOnlyList<string> knownSecrets)
    {
        _paths = pathTokens.Where(kv => !string.IsNullOrWhiteSpace(kv.Key))
            .OrderByDescending(kv => kv.Key.Length).ToList();
        // Replacing a one-character password as plain text would corrupt almost every word in the bundle. Short
        // values are still removed by the structured projection and key/value regex; exact-value replacement is
        // reserved for strings distinctive enough to match safely.
        _secrets = knownSecrets.Where(s => !string.IsNullOrWhiteSpace(s) && s.Length >= 4)
            .Distinct(StringComparer.Ordinal).OrderByDescending(s => s.Length).ToList();
    }

    public string Redact(string text)
    {
        foreach (var secret in _secrets) text = text.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
        foreach (var pair in _paths)
        {
            var path = pair.Key.TrimEnd('\\', '/');
            text = text.Replace(path, pair.Value, StringComparison.OrdinalIgnoreCase);
            // JSON string escaping doubles Windows separators; structured diagnostics pass through this redactor
            // after serialization as an additional defence.
            text = text.Replace(path.Replace("\\", "\\\\"), pair.Value, StringComparison.OrdinalIgnoreCase);
        }
        text = WebhookUrl.Replace(text, "[REDACTED-WEBHOOK]");
        text = AuthorizationHeader.Replace(text, m => m.Groups["key"].Value + "[REDACTED]");
        text = SecretAssignment.Replace(text, m => m.Groups["key"].Value + m.Groups["separator"].Value + "[REDACTED]");
        text = SteamId.Replace(text, "[REDACTED-STEAM-ID]");
        text = Ipv4.Replace(text, "[REDACTED-IP]");
        text = ColonAddress.Replace(text, m =>
            IPAddress.TryParse(m.Value, out var address) && address.AddressFamily == AddressFamily.InterNetworkV6
                ? "[REDACTED-IP]"
                : m.Value);
        return text;
    }
}
