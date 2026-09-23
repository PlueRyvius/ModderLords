using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using ModderLords.App.ViewModels;
using ModderLords.Core.Config;
using ModderLords.Core.Export;
using ModderLords.Core.Launch;
using ModderLords.Core.Modules;
using ModderLords.Core.Profiles;
using ModderLords.Core.Saves;
using ModderLords.Core.Support;
using ModderLords.Coop.Compat;
using ModderLords.Coop.Config;
using ModderLords.Coop.Launch;

namespace ModderLords.App;

public sealed record SupportCategoryEstimate(string Name, string Detail, long Bytes)
{
    public string DisplaySize => Bytes == 0 ? "Not found" : Bytes >= 1024 * 1024
        ? $"up to {Bytes / (1024d * 1024):0.0} MiB"
        : $"up to {Math.Max(1, Bytes / 1024d):0} KiB";
}

public sealed record SupportSaveChoice(string Label, SupportSaveInfo? Save)
{
    public override string ToString() => Label;
}

internal sealed class SupportReportContext
{
    public required Profile Profile { get; init; }
    public required SupportEnvironment Environment { get; init; }
    public required IReadOnlyList<DiscoveredModule> CatalogModules { get; init; }
    public required IReadOnlyDictionary<string, (bool Selected, bool Enabled, string? Role)> ModuleStates { get; init; }
    public required IReadOnlyList<string> LoadOrder { get; init; }
    public required IReadOnlyList<SupportTextSource> Sources { get; init; }
    public required IReadOnlyList<SupportGeneratedText> Generated { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }
    public required IReadOnlyDictionary<string, string> PathTokens { get; init; }
    public required IReadOnlyList<string> KnownSecrets { get; init; }
    public Action? FlushLogs { get; init; }
    public required IReadOnlyList<SupportSaveChoice> SaveChoices { get; init; }
    public SupportSaveInfo? DefaultSaveMetadata { get; init; }
    public required IReadOnlyList<SupportCategoryEstimate> Categories { get; init; }
    public required bool ServerRunning { get; init; }

    public SupportBundleRequest BuildRequest(SupportSaveInfo? selectedSave)
    {
        var modules = CatalogModules.Select(module =>
        {
            ModuleStates.TryGetValue(Normalize(module.FolderPath), out var state);
            return SupportBundleBuilder.DescribeModule(module, state.Selected, state.Enabled, state.Role, PathTokens);
        }).ToList();
        return new SupportBundleRequest
        {
            OutputDirectory = Path.Combine(ProfileStore.RootDir, "support"),
            Environment = Environment,
            ProfileSnapshot = Profile,
            Modules = modules,
            EffectiveLoadOrder = LoadOrder,
            TextSources = Sources,
            GeneratedText = Generated,
            InitialWarnings = Warnings,
            PathTokens = PathTokens,
            KnownSecrets = KnownSecrets,
            FlushLogs = FlushLogs,
            Save = selectedSave ?? DefaultSaveMetadata,
            IncludeSave = selectedSave is not null,
            ServerRunning = ServerRunning,
        };
    }

    private static string Normalize(string path)
    {
        try { return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
        catch { return path; }
    }
}

internal static class SupportReportService
{
    public const string IssueUrl = "https://github.com/PlueRyvius/ModderLords/issues/new?template=bug-report.yml";

    public static SupportReportContext Capture(MainViewModel main)
    {
        main.CollectProfileFromRows(); // current UI state, deliberately not saved
        var profile = ProfileStore.Snapshot(main.Profile);
        var catalog = main.ScannedCatalog?.Catalog;
        var modules = catalog?.Modules.ToList() ?? [];
        var gameRoot = main.ScannedCatalog?.GameRoot;
        ServerPaths? serverPaths = null;
        if (main.IsHost)
        {
            try { serverPaths = LaunchSession.ResolvePaths(profile); }
            catch { }
        }

        var tokens = PathTokens(profile, gameRoot, serverPaths);
        var sources = FindSources(serverPaths);
        var generated = new List<SupportGeneratedText>();
        var warnings = new List<string>();
        try
        {
            var description = main.IsHost
                ? LaunchSession.Prepare(profile, applySideEffects: false, scanned: main.ScannedCatalog,
                    experimentalCompat: main.ExperimentalCompat).Plan.Describe()
                : ClientLaunchSession.Prepare(profile, main.ScannedCatalog).Plan.Describe();
            generated.Add(new SupportGeneratedText("launch-plan.txt", description, 100, true));
        }
        catch (Exception ex)
        {
            generated.Add(new SupportGeneratedText("launch-plan.txt", "Could not prepare the current launch plan: " + ex, 100, true));
        }

        var states = new Dictionary<string, (bool Selected, bool Enabled, string? Role)>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in main.Mods.Where(r => !r.IsMissing))
            states[Normalize(row.Folder)] = (row.Enabled, row.Enabled, row.IsGameModule ? null : row.Role.ToString());
        // Server-stock modules have no rows but can still be part of the effective server order.
        foreach (var module in modules.Where(m => m.IsStock && main.LoadOrderPreview.Contains(m.Id, StringComparer.OrdinalIgnoreCase)))
            states[Normalize(module.FolderPath)] = (true, true, "server stock");

        var saves = new List<SupportSaveInfo>();
        if (serverPaths is not null)
        {
            try { saves.AddRange(SaveHeaderReader.ReadAll(serverPaths.SavesDir).Select(SupportSaveInfo.From)); }
            catch { }
        }
        var choices = new List<SupportSaveChoice> { new("None (recommended)", null) };
        foreach (var save in saves.OrderByDescending(s => s.Name.Equals(profile.SaveName, StringComparison.OrdinalIgnoreCase)).ThenByDescending(s => s.LastWriteUtc))
        {
            var selected = save.Name.Equals(profile.SaveName, StringComparison.OrdinalIgnoreCase) ? " — selected by this profile" : "";
            choices.Add(new SupportSaveChoice($"{save.Name}{selected} ({FormatBytes(save.Length)}, {save.LastWriteUtc.ToLocalTime():g})", save));
        }
        var defaultSave = saves.FirstOrDefault(s => s.Name.Equals(profile.SaveName, StringComparison.OrdinalIgnoreCase));

        var secrets = new List<string> { profile.Server.Password };
        if (Environment.UserName.Length >= 4) secrets.Add(Environment.UserName);
        if (Environment.MachineName.Length >= 4) secrets.Add(Environment.MachineName);
        AddStructuredConfiguration(serverPaths, generated, warnings, new SupportRedactor(tokens, secrets));

        var categories = sources.GroupBy(s => FriendlyCategory(s.Category))
            .Select(g => new SupportCategoryEstimate(g.Key, DetailFor(g.Key), SupportBundleBuilder.EstimateSourceBytes(g)))
            .OrderBy(c => c.Name).ToList();
        categories.Insert(0, new SupportCategoryEstimate("Configuration and environment",
            "Sanitized profile, versions, launch plan, load order and settings", 64 * 1024));
        categories.Insert(1, new SupportCategoryEstimate("Installed mods",
            "IDs, versions, dependencies, selected copies and manifest/DLL fingerprints; no mod binaries", Math.Max(32 * 1024, modules.Count * 2048L)));

        var version = Assembly.GetExecutingAssembly().GetName().Version;
        var native = modules.FirstOrDefault(m => m.Id.Equals("Native", StringComparison.OrdinalIgnoreCase));
        var coop = modules.FirstOrDefault(m => ClientManifest.CoopClientModuleIds.Contains(m.Id));
        var environment = new SupportEnvironment(
            main.Mode.ToString(),
            version is null ? "unknown" : $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}",
            native?.Version,
            coop?.Version,
            Environment.OSVersion.VersionString,
            RuntimeInformation.ProcessArchitecture.ToString(),
            RuntimeInformation.FrameworkDescription);

        return new SupportReportContext
        {
            Profile = profile,
            Environment = environment,
            CatalogModules = modules,
            ModuleStates = states,
            LoadOrder = main.LoadOrderPreview.ToList(),
            Sources = sources,
            Generated = generated,
            Warnings = warnings,
            PathTokens = tokens,
            KnownSecrets = secrets,
            FlushLogs = main.Host is { } host ? host.FlushSupportLogs : null,
            SaveChoices = choices,
            DefaultSaveMetadata = defaultSave,
            Categories = categories,
            ServerRunning = main.Host?.IsRunning == true,
        };
    }

    private static List<SupportTextSource> FindSources(ServerPaths? paths)
    {
        var result = new List<SupportTextSource>();
        var launcherLogs = Path.Combine(ProfileStore.RootDir, "logs");
        AddRecent(result, launcherLogs, "launch-*.log", 3, "ModderLords launch logs", 100);
        AddRecent(result, launcherLogs, "hook-*.log", 3, "resolver hook logs", 95);
        AddRecent(result, launcherLogs, "client-launch-*.log", 3, "client launch logs", 95);
        AddRecent(result, launcherLogs, "creation-*.log", 4, "world creation diagnostics", 95);
        AddRecent(result, launcherLogs, "creation-*.json", 2, "world creation diagnostics", 95);
        AddIfFile(result, Path.Combine(launcherLogs, "app-errors.log"), "application errors", 100);

        if (paths is not null)
        {
            AddRecent(result, paths.LogsDir, "coop-server-*.log", 2, "Coop server logs", 90);
            AddRecent(result, paths.LogsDir, "worldcreate-*.log", 2, "world creation diagnostics", 90);
        }

        var bannerlordRoot = Path.GetDirectoryName(ServerPaths.CrashesDir);
        if (bannerlordRoot is not null)
            AddRecent(result, Path.Combine(bannerlordRoot, "logs"), "rgl_log_errors_*.txt", 2, "engine error logs", 90);

        try
        {
            var crash = new DirectoryInfo(ServerPaths.CrashesDir).EnumerateDirectories()
                .OrderByDescending(d => d.LastWriteTimeUtc).FirstOrDefault();
            if (crash is not null)
                foreach (var file in crash.EnumerateFiles("*", SearchOption.TopDirectoryOnly)
                             .Where(f => new[] { ".txt", ".log", ".xml", ".json" }.Contains(f.Extension, StringComparer.OrdinalIgnoreCase))
                             .OrderBy(f => f.Name).Take(12))
                    result.Add(new SupportTextSource("engine crash metadata", file.FullName, 85));
        }
        catch { }

        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var modLogs = Path.Combine(docs, "Mount and Blade II Bannerlord", "Configs", "ModLogs");
        AddRecent(result, modLogs, "ModderLords.Compat-trace-*.jsonl", 2, "compatibility traces", 80);
        AddRecent(result, modLogs, "ModderLords*.log", 3, "compatibility traces", 80);
        AddIfFile(result, Path.Combine(AppContext.BaseDirectory, "compat", LaunchSession.SyncModuleId, RecipeSet.FileName), "compatibility recipes", 90);
        return result.DistinctBy(s => Normalize(s.Path), StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void AddStructuredConfiguration(ServerPaths? paths, List<SupportGeneratedText> generated,
        List<string> warnings, SupportRedactor redactor)
    {
        if (paths is null) return;
        if (File.Exists(paths.ServerConfigPath))
        {
            try
            {
                var snapshot = ServerConfig.Read(paths.ServerConfigPath);
                if (snapshot is not null)
                {
                    var value = new
                    {
                        Save = string.IsNullOrWhiteSpace(snapshot.SaveName) ? "not configured" : "configured",
                        snapshot.Settings.JoinPort,
                        snapshot.Settings.AutosaveMinutes,
                        snapshot.Settings.LogFile,
                        snapshot.Settings.Steam,
                        snapshot.Settings.TraceTick,
                        snapshot.Settings.TracePublish,
                        snapshot.Settings.TraceBandits,
                        Password = string.IsNullOrEmpty(snapshot.Settings.Password) ? "" : "[REDACTED]",
                        Unknown = snapshot.Unknown.ToDictionary(kv => kv.Key, kv => redactor.Redact(kv.Value.GetRawText())),
                    };
                    generated.Add(new SupportGeneratedText("configuration/server-config.json",
                        JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }), 100));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                warnings.Add("server-config.json could not be parsed: " + redactor.Redact(ex.Message));
            }
        }

        if (File.Exists(paths.ModConfigPath))
        {
            try
            {
                var settings = CommentedJson.Leaves(File.ReadAllText(paths.ModConfigPath)).Select(leaf => new
                {
                    leaf.Path,
                    Kind = leaf.Kind.ToString(),
                    Value = SensitiveSetting(leaf.Path) ? "[REDACTED]" : redactor.Redact(leaf.RawValue),
                }).ToList();
                generated.Add(new SupportGeneratedText("configuration/gameplay-config.json",
                    JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }), 95));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or FormatException)
            {
                warnings.Add("mod-config.json could not be parsed: " + redactor.Redact(ex.Message));
            }
        }
    }

    private static bool SensitiveSetting(string path) =>
        new[] { "password", "passwd", "token", "secret", "credential", "authorization", "cookie", "webhook", "apiKey", "accessKey" }
            .Any(key => path.Contains(key, StringComparison.OrdinalIgnoreCase));

    private static IReadOnlyDictionary<string, string> PathTokens(Profile profile, string? gameRoot, ServerPaths? paths)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Add(string? path, string token)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try { result[Path.GetFullPath(path).TrimEnd('\\', '/')] = token; } catch { }
        }
        Add(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "%USERPROFILE%");
        Add(ProfileStore.RootDir, "%MODDERLORDS_DATA%");
        Add(gameRoot, "%GAME_ROOT%");
        Add(paths?.DedicatedServerRoot, "%SERVER_ROOT%");
        Add(paths?.DataDir, "%SERVER_DATA%");
        Add(paths?.CoopDataDir, "%COOP_DATA%");
        for (var i = 0; i < profile.CustomModRoots.Count; i++) Add(profile.CustomModRoots[i], $"%MOD_ROOT_{i + 1}%");
        return result;
    }

    private static void AddRecent(List<SupportTextSource> result, string directory, string pattern, int count, string category, int priority)
    {
        try
        {
            result.AddRange(new DirectoryInfo(directory).EnumerateFiles(pattern, SearchOption.TopDirectoryOnly)
                .OrderByDescending(f => f.LastWriteTimeUtc).Take(count)
                .Select(f => new SupportTextSource(category, f.FullName, priority)));
        }
        catch { }
    }

    private static void AddIfFile(List<SupportTextSource> result, string path, string category, int priority)
    {
        try { if (File.Exists(path)) result.Add(new SupportTextSource(category, path, priority)); } catch { }
    }

    private static string FriendlyCategory(string value) => value switch
    {
        "ModderLords launch logs" or "resolver hook logs" or "client launch logs" or "application errors" => "ModderLords logs",
        "Coop server logs" or "engine error logs" => "Server and engine logs",
        "world creation diagnostics" => "World creation diagnostics",
        "engine crash metadata" => "Crash metadata",
        "compatibility traces" or "compatibility recipes" => "Compatibility diagnostics",
        "configuration" => "Configuration files",
        _ => value,
    };

    private static string DetailFor(string category) => category switch
    {
        "ModderLords logs" => "Recent launcher, resolver, client-launch and application-error logs",
        "Server and engine logs" => "Recent Coop server output and Bannerlord error logs",
        "Crash metadata" => "Text/JSON/XML from the newest crash folder; never minidumps",
        "Configuration files" => "Server and gameplay configuration, passed through secret redaction",
        _ => "Recent relevant text diagnostics",
    };

    private static string Normalize(string path)
    {
        try { return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
        catch { return path; }
    }

    private static string FormatBytes(long bytes) => bytes >= 1024 * 1024
        ? $"{bytes / (1024d * 1024):0.0} MiB"
        : $"{Math.Max(1, bytes / 1024d):0} KiB";
}

public interface ISupportDestinationLauncher
{
    void SelectFile(string path);
    void OpenIssueForm(string url);
}

public sealed class WindowsSupportDestinationLauncher : ISupportDestinationLauncher
{
    public void SelectFile(string path) => Process.Start(new ProcessStartInfo
    {
        FileName = "explorer.exe",
        Arguments = $"/select,\"{path}\"",
        UseShellExecute = true,
    });

    public void OpenIssueForm(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
}

public static class SupportDestination
{
    public static void Open(SupportBundleResult result, ISupportDestinationLauncher launcher)
    {
        launcher.OpenIssueForm(SupportReportService.IssueUrl);
        launcher.SelectFile(result.DiagnosticPath);
    }
}
