using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Bannerlord.ModuleManager;
using ModderLords.Core.Modules;
using ModderLords.Core.Profiles;
using ModderLords.Core.Support;

namespace ModderLords.Core.Tests;

public sealed class SupportBundleTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("modderlords-support-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    [Fact]
    public void Redactor_removes_secrets_identifiers_and_personal_paths_without_eating_timestamps()
    {
        var redactor = new SupportRedactor(
            new Dictionary<string, string> { [@"C:\Users\Alice"] = "%USERPROFILE%", [@"D:\Games\Bannerlord"] = "%GAME_ROOT%" },
            ["known-secret-value"]);
        var input = "12:34:56 password=opensesame token: abc123 Authorization: Bearer eyJ-private-token\n" +
                    "https://discord.com/api/webhooks/123/secret 76561198012345678 203.0.113.42 " +
                    "2001:0db8:85a3:0000:0000:8a2e:0370:7334 fe80::1 known-secret-value " +
                    @"C:\Users\Alice\Documents D:\Games\Bannerlord\Modules " +
                    "{\"path\":\"C:\\\\Users\\\\Alice\\\\Documents\"}";

        var text = redactor.Redact(input);

        Assert.Contains("12:34:56", text);
        Assert.Contains("%USERPROFILE%", text);
        Assert.Contains("%GAME_ROOT%", text);
        Assert.DoesNotContain("opensesame", text);
        Assert.DoesNotContain("abc123", text);
        Assert.DoesNotContain("eyJ-private-token", text);
        Assert.DoesNotContain("76561198012345678", text);
        Assert.DoesNotContain("203.0.113.42", text);
        Assert.DoesNotContain("2001:0db8", text);
        Assert.DoesNotContain("fe80::1", text);
        Assert.DoesNotContain("known-secret-value", text);
        Assert.DoesNotContain("api/webhooks", text);
    }

    [Fact]
    public async Task Bundle_contains_redacted_head_and_tail_plus_a_complete_manifest()
    {
        var log = Path.Combine(_root, "launch.log");
        var head = "BEGIN password=hunter2 C:\\Users\\Alice\\game\n";
        var tail = "\nEND token=last-secret 198.51.100.9";
        await File.WriteAllTextAsync(log, head + new string('x', SupportBundleBuilder.HeadBytes + SupportBundleBuilder.TailBytes) + tail);
        var request = Request(
            sources: [new SupportTextSource("launch", log, 100)],
            profile: new Profile
            {
                Name = "PrivateProfile8765", SaveName = "CharacterSecret8765", GameRoot = @"C:\Users\Alice\game",
                Server = new ServerSettings { Password = "profile-secret" },
                Mods = [new ProfileMod { Id = "Example", SourcePath = @"C:\Users\Alice\game\Modules\Example" }],
            },
            paths: new Dictionary<string, string> { [@"C:\Users\Alice"] = "%USERPROFILE%" },
            secrets: ["profile-secret"]) with
        {
            Save = new SupportSaveInfo(Path.Combine(_root, "CharacterSecret8765.sav"), "CharacterSecret8765", "v1.2.3",
                ["Native"], new Dictionary<string, string> { ["Native"] = "v1.2.3" }, 12, DateTime.UtcNow, 1234),
        };

        var result = await SupportBundleBuilder.CreateAsync(request);

        Assert.True(File.Exists(result.DiagnosticPath));
        Assert.True(result.DiagnosticBytes < SupportBundleBuilder.DefaultDiagnosticMaxBytes);
        using var zip = ZipFile.OpenRead(result.DiagnosticPath);
        Assert.Contains(zip.Entries, e => e.FullName == "report.json");
        Assert.Contains(zip.Entries, e => e.FullName == "README.txt");
        Assert.Contains(zip.Entries, e => e.FullName == "profile.json");
        Assert.Contains(zip.Entries, e => e.FullName == "save-metadata.json");
        var allText = string.Join("\n", zip.Entries.Where(e => e.Length < 10 * 1024 * 1024).Select(Read));
        Assert.Contains("BEGIN", allText);
        Assert.Contains("END", allText);
        Assert.Contains("bytes omitted by support-bundle truncation", allText);
        Assert.Contains("%USERPROFILE%", allText);
        Assert.DoesNotContain("hunter2", allText);
        Assert.DoesNotContain("last-secret", allText);
        Assert.DoesNotContain("profile-secret", allText);
        Assert.DoesNotContain("198.51.100.9", allText);
        Assert.DoesNotContain("PrivateProfile8765", allText);
        Assert.DoesNotContain("CharacterSecret8765", allText);
        Assert.Contains(result.Entries, e => e.ArchiveName.Contains("launch.log") && e.Truncated && e.Redacted);
    }

    [Fact]
    public async Task Low_priority_evidence_is_dropped_before_required_metadata_when_zip_budget_is_tiny()
    {
        string Noise(int seed)
        {
            var bytes = new byte[96 * 1024];
            new Random(seed).NextBytes(bytes);
            return Convert.ToBase64String(bytes);
        }
        var request = Request(generated:
        [
            new SupportGeneratedText("high.txt", Noise(1), 90),
            new SupportGeneratedText("low.txt", Noise(2), 1),
        ]) with { DiagnosticMaxBytes = 90 * 1024 };

        var result = await SupportBundleBuilder.CreateAsync(request);

        Assert.True(result.DiagnosticBytes <= 90 * 1024);
        using var zip = ZipFile.OpenRead(result.DiagnosticPath);
        Assert.Contains(zip.Entries, e => e.FullName == "report.json");
        Assert.Contains(zip.Entries, e => e.FullName == "profile.json");
        Assert.DoesNotContain(zip.Entries, e => e.FullName == "low.txt");
        Assert.Contains(result.Entries, e => e.ArchiveName == "low.txt" && e.Status.Contains("size budget"));
    }

    [Fact]
    public async Task Missing_files_and_directories_are_reported_but_do_not_break_the_bundle_or_recurse()
    {
        var directory = Path.Combine(_root, "module");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "private.log"), "password=must-not-appear");
        var lockedFile = Path.Combine(_root, "locked.log");
        await File.WriteAllTextAsync(lockedFile, "token=also-must-not-appear");
        using var locked = new FileStream(lockedFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var request = Request(sources:
        [
            new SupportTextSource("missing", Path.Combine(_root, "absent.log")),
            new SupportTextSource("module directory", directory),
            new SupportTextSource("locked", lockedFile),
        ]) with { FlushLogs = () => throw new IOException("flush locked") };

        var result = await SupportBundleBuilder.CreateAsync(request);
        using var zip = ZipFile.OpenRead(result.DiagnosticPath);
        var allText = string.Join("\n", zip.Entries.Select(Read));

        Assert.Contains(result.Warnings, w => w.Contains("absent.log"));
        Assert.Contains(result.Warnings, w => w.Contains("module"));
        Assert.Contains(result.Warnings, w => w.Contains("locked.log"));
        Assert.Contains(result.Warnings, w => w.Contains("could not be flushed"));
        Assert.DoesNotContain("must-not-appear", allText);
        Assert.DoesNotContain("also-must-not-appear", allText);
    }

    [Fact]
    public async Task Save_is_separate_opt_in_and_is_refused_while_server_runs_or_when_too_large()
    {
        var saveFile = Path.Combine(_root, "campaign.sav");
        await File.WriteAllBytesAsync(saveFile, Encoding.UTF8.GetBytes(new string('s', 16 * 1024)));
        var save = new SupportSaveInfo(saveFile, "campaign", "v1.4.8", ["Native"],
            new Dictionary<string, string>(), 42, File.GetLastWriteTimeUtc(saveFile), new FileInfo(saveFile).Length);

        var running = await SupportBundleBuilder.CreateAsync(Request() with { IncludeSave = true, Save = save, ServerRunning = true });
        Assert.Null(running.SavePath);
        Assert.Contains(running.Warnings, w => w.Contains("server is running"));

        var stopped = await SupportBundleBuilder.CreateAsync(Request() with { IncludeSave = true, Save = save });
        Assert.NotNull(stopped.SavePath);
        Assert.True(File.Exists(stopped.SavePath));
        using (var zip = ZipFile.OpenRead(stopped.SavePath!))
            Assert.Equal("campaign.sav", Assert.Single(zip.Entries).FullName);

        var tooSmall = await SupportBundleBuilder.CreateAsync(Request() with { IncludeSave = true, Save = save, SaveMaxBytes = 1 });
        Assert.Null(tooSmall.SavePath);
        Assert.Contains(tooSmall.Warnings, w => w.Contains("over GitHub's 25 MB"));
    }

    [Fact]
    public async Task Module_inventory_fingerprints_selected_files_without_copying_mod_binaries()
    {
        var moduleRoot = Path.Combine(_root, "Modules", "Example");
        var bin = Path.Combine(moduleRoot, "bin", "Win64_Shipping_Client");
        Directory.CreateDirectory(bin);
        await File.WriteAllTextAsync(Path.Combine(moduleRoot, "SubModule.xml"),
            "<Module><Name value='Example'/><Id value='Example'/><Version value='v1.2.3'/>" +
            "<DependedModules><DependedModule Id='Needed'/></DependedModules><SubModules><SubModule>" +
            "<Name value='Example'/><DLLName value='Example.dll'/><SubModuleClassType value='Example.Sub'/></SubModule></SubModules></Module>");
        await File.WriteAllTextAsync(Path.Combine(bin, "Example.dll"), "fixture binary");
        var module = ModuleCatalog.TryParse(moduleRoot, ModuleSourceKind.GameModules, out var problem);
        Assert.Null(problem);
        var info = SupportBundleBuilder.DescribeModule(module!, true, true, "Run",
            new Dictionary<string, string> { [moduleRoot] = "%MOD_ROOT_1%" });
        var staleRoot = Path.Combine(_root, "OldModules", "Example");
        Directory.CreateDirectory(staleRoot);
        await File.WriteAllTextAsync(Path.Combine(staleRoot, "SubModule.xml"),
            "<Module><Name value='Example'/><Id value='Example'/><Version value='v0.9.0'/><SubModules/></Module>");
        var stale = ModuleCatalog.TryParse(staleRoot, ModuleSourceKind.Custom, out var staleProblem);
        Assert.Null(staleProblem);
        var staleInfo = SupportBundleBuilder.DescribeModule(stale!, false, false, null,
            new Dictionary<string, string> { [staleRoot] = "%MOD_ROOT_2%" });
        var result = await SupportBundleBuilder.CreateAsync(Request(modules: [info, staleInfo]));

        using var zip = ZipFile.OpenRead(result.DiagnosticPath);
        Assert.DoesNotContain(zip.Entries, e => e.FullName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(zip.Entries, e => e.FullName.StartsWith("manifests/") && e.FullName.EndsWith("-SubModule.xml"));
        var modulesJson = Read(zip.GetEntry("modules.json")!);
        Assert.Contains("Example.dll", modulesJson);
        Assert.Contains("Needed", modulesJson);
        Assert.Contains("%MOD_ROOT_1%", modulesJson);
        Assert.Contains("%MOD_ROOT_2%", modulesJson);
        using var modulesDocument = JsonDocument.Parse(modulesJson);
        var copies = modulesDocument.RootElement.GetProperty("InstalledCopies").EnumerateArray().ToList();
        Assert.Equal(2, copies.Count);
        Assert.Contains(copies, copy => copy.GetProperty("Version").GetString() == "v1.2.3" && copy.GetProperty("Selected").GetBoolean());
        Assert.Contains(copies, copy => copy.GetProperty("Version").GetString() == "v0.9.0" && !copy.GetProperty("Selected").GetBoolean());
        Assert.Contains("<Module>", info.ManifestText);
        Assert.Null(staleInfo.ManifestText);
        Assert.NotNull(info.ManifestSha256);
        Assert.Single(info.DllFingerprints);
    }

    [Fact]
    public async Task Local_retention_keeps_only_five_report_groups()
    {
        for (var i = 0; i < 7; i++) await SupportBundleBuilder.CreateAsync(Request());
        Assert.Equal(5, Directory.EnumerateFiles(_root, "ModderLords-support-*.zip").Count());
    }

    private SupportBundleRequest Request(
        IReadOnlyList<SupportTextSource>? sources = null,
        IReadOnlyList<SupportGeneratedText>? generated = null,
        Profile? profile = null,
        IReadOnlyList<SupportModuleInfo>? modules = null,
        IReadOnlyDictionary<string, string>? paths = null,
        IReadOnlyList<string>? secrets = null) => new()
        {
            OutputDirectory = _root,
            Environment = new SupportEnvironment("Player", "1.2.3", "v1.4.8", "v0.1.5", "Windows", "X64", ".NET"),
            ProfileSnapshot = profile ?? new Profile { Name = "support-test" },
            Modules = modules ?? [],
            EffectiveLoadOrder = ["Native"],
            TextSources = sources ?? [],
            GeneratedText = generated ?? [],
            PathTokens = paths ?? new Dictionary<string, string>(),
            KnownSecrets = secrets ?? [],
        };

    private static string Read(ZipArchiveEntry entry)
    {
        using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
