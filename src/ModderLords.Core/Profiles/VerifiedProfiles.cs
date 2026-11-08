using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModderLords.Core.Compat;
using ModderLords.Core.Modules;
using ModderLords.Core.Saves;
using ModderLords.Core.Updates;

namespace ModderLords.Core.Profiles;

/// <summary>verified-profiles.json (docs/PROFILE-MANAGER.md, "The verified-profiles file").</summary>
public sealed class VerifiedProfilesFile
{
    public int SchemaVersion { get; set; } = 1;
    public List<VerifiedProfile> Profiles { get; set; } = [];
}

/// <summary>A mod set we say works, and who says so. The mods are listed in load order.</summary>
public sealed class VerifiedProfile
{
    /// <summary>Stable: badges, reports and the profile file written on first use refer to it.</summary>
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    /// <summary>The game version it was verified on, as Native's manifest has it ("v1.4.8").</summary>
    public string Game { get; set; } = "";
    public List<VerifiedMod> Mods { get; set; } = [];
    public List<Verification> Verifications { get; set; } = [];

    /// <summary>The strongest claim made for it; null while nobody has verified it (a draft).</summary>
    [JsonIgnore]
    public VerificationLevel? Level => Verifications.Count == 0 ? null : Verifications.Max(v => v.Level);
}

public sealed class VerifiedMod
{
    public string Id { get; set; } = "";
    /// <summary>The version every verification of the profile was made with.</summary>
    public string Version { get; set; } = "";
    public ulong? WorkshopId { get; set; }
    /// <summary>
    /// The version the Workshop item serves, when the entry was last updated. Only the maintainer's own mods need it:
    /// when it is not <see cref="Version"/> and the mod has a <see cref="GitHub"/> release, the release is fetched
    /// instead (decision 12).
    /// </summary>
    public string? WorkshopVersion { get; set; }
    public GitHubRelease? GitHub { get; set; }
    /// <summary>The install fingerprint (ModuleFingerprint), when recorded.</summary>
    public string? Fingerprint { get; set; }
}

/// <summary>A release asset on GitHub: a zip holding the mod's folder, pinned by its SHA-256.</summary>
public sealed class GitHubRelease
{
    /// <summary>owner/name.</summary>
    public string Repo { get; set; } = "";
    public string Tag { get; set; } = "";
    public string Asset { get; set; } = "";
    /// <summary>Of the zip, in hex. The download is refused unless it matches: this file vouches for it, not the release.</summary>
    public string Sha256 { get; set; } = "";
    public long? Size { get; set; }

    [JsonIgnore]
    public string DownloadUrl => $"https://github.com/{Repo}/releases/download/{Uri.EscapeDataString(Tag)}/{Uri.EscapeDataString(Asset)}";
}

/// <summary>Who vouches for a profile, weakest first so Max() is the strongest.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<VerificationLevel>))]
public enum VerificationLevel { Green, Silver, Gold }

public sealed class Verification
{
    public VerificationLevel Level { get; set; }
    public string? By { get; set; }
    public string? Date { get; set; }
    /// <summary>What was run: test modules and modes.</summary>
    public string? Tests { get; set; }
    public List<string> Runs { get; set; } = [];
    public string? Notes { get; set; }
    /// <summary>Green: how many players' reports were accepted, and when the last one came.</summary>
    public int? Reports { get; set; }
    public string? LastReport { get; set; }
}

/// <summary>The verified profiles in use: the bundled file, or a newer one fetched from GitHub.</summary>
public sealed record VerifiedProfileList(IReadOnlyList<VerifiedProfile> Profiles, bool FromRemote, IReadOnlyList<string> Problems)
{
    /// <summary>The ones the Profiles window lists: verified ones, and in a dev build drafts too.</summary>
    public IEnumerable<VerifiedProfile> Shown => Profiles.Where(p => p.Level is not null || VerifiedProfiles.ShowDrafts);
}

public static class VerifiedProfiles
{
    public const string BundledFileName = "verified-profiles.json";
    public const int SupportedSchema = 1;

    /// <summary>
    /// The only GitHub accounts whose releases a verified profile may download (decision 12): the maintainer's own
    /// mods. Everything else comes from the Workshop, which ModderLords only asks Steam to subscribe to.
    /// </summary>
    public static readonly IReadOnlyList<string> GitHubOwners = ["PlueRyvius"];

    /// <summary>A profile nobody has verified yet is listed only in a dev build, marked as a draft, so it can be tried.</summary>
#if DEBUG
    public static bool ShowDrafts { get; set; } = true;
#else
    public static bool ShowDrafts { get; set; }
#endif

    /// <summary>In the release's data\ folder beside the compat database; next to the exe in a dev build.</summary>
    public static string BundledPath
    {
        get
        {
            var inData = Path.Combine(AppContext.BaseDirectory, CompatDb.DataFolder, BundledFileName);
            return File.Exists(inData) ? inData : Path.Combine(AppContext.BaseDirectory, BundledFileName);
        }
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static VerifiedProfileList? _current;
    public static VerifiedProfileList Current => Override ?? (_current ??= Load(BundledPath, VerifiedProfilesRemote.Default));
    public static VerifiedProfileList Reload() => Override ?? (_current = Load(BundledPath, VerifiedProfilesRemote.Default));

    /// <summary>Test seam: the list Current and Reload return instead of the files.</summary>
    internal static VerifiedProfileList? Override { get; set; }

    /// <summary>The fetched copy when it passed its checks, else the bundled file. Never throws.</summary>
    public static VerifiedProfileList Load(string? bundledPath, VerifiedProfilesRemote? remote)
    {
        var problems = new List<string>();
        VerifiedProfilesFile? bundled = null;
        try { if (bundledPath is not null && File.Exists(bundledPath)) bundled = Parse(File.ReadAllText(bundledPath)); }
        catch (Exception ex) { problems.Add($"{BundledFileName}: {ex.Message}"); }
        if (remote?.TryReadCache(bundled) is { } fetched) return new(fetched.Profiles, true, problems);
        return new(bundled?.Profiles ?? [], false, problems);
    }

    /// <summary>
    /// Reads the file and refuses one this launcher cannot trust in full: a newer schema, a profile without an id or
    /// name, two with one id, a mod without an id or version, or a GitHub release that is not one of
    /// <see cref="GitHubOwners"/>' or is not pinned by its hash.
    /// </summary>
    public static VerifiedProfilesFile Parse(string json)
    {
        var file = JsonSerializer.Deserialize<VerifiedProfilesFile>(json, Json) ?? throw new InvalidDataException("empty");
        if (file.SchemaVersion > SupportedSchema) throw new InvalidDataException($"schema {file.SchemaVersion} is newer than this ModderLords reads");
        file.Profiles ??= [];
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in file.Profiles)
        {
            if (string.IsNullOrWhiteSpace(p.Id) || string.IsNullOrWhiteSpace(p.Name)) throw new InvalidDataException("a profile has no id or name");
            if (!ids.Add(p.Id)) throw new InvalidDataException($"two profiles are called {p.Id}");
            p.Mods ??= [];
            p.Verifications ??= [];
            foreach (var m in p.Mods)
            {
                if (string.IsNullOrWhiteSpace(m.Id) || string.IsNullOrWhiteSpace(m.Version))
                    throw new InvalidDataException($"{p.Id}: a mod has no id or version");
                if (m.GitHub is { } gh) CheckRelease(p.Id, m, gh);
            }
        }
        return file;
    }

    private static void CheckRelease(string profile, VerifiedMod mod, GitHubRelease gh)
    {
        var parts = gh.Repo.Split('/');
        if (parts.Length != 2 || !GitHubOwners.Contains(parts[0], StringComparer.OrdinalIgnoreCase) || parts[1].Length == 0)
            throw new InvalidDataException($"{profile}: {mod.Id} downloads from {gh.Repo}, which is not an allowed GitHub account");
        if (gh.Tag.Length == 0 || !gh.Asset.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || gh.Asset.Contains('/') || gh.Asset.Contains('\\'))
            throw new InvalidDataException($"{profile}: {mod.Id} names no release tag or zip");
        if (gh.Sha256.Length != 64 || !gh.Sha256.All(Uri.IsHexDigit))
            throw new InvalidDataException($"{profile}: {mod.Id}'s release is not pinned by a SHA-256");
    }

    /// <summary>The newest date any verification or report carries, for "is this file behind the bundled one".</summary>
    internal static string? Newest(IEnumerable<VerifiedProfile> profiles) => profiles
        .SelectMany(p => p.Verifications).SelectMany(v => new[] { v.Date, v.LastReport })
        .Where(d => d is { Length: > 0 }).Max(StringComparer.Ordinal);

    // ---- comparing with what is installed (decision 8) ---------------------------------------------------------

    /// <summary>How one mod of a verified profile stands on this PC, and where a missing version would come from.</summary>
    public static IReadOnlyList<VerifiedModCheck> Compare(VerifiedProfile profile, Func<string, IEnumerable<DiscoveredModule>> installed) =>
        profile.Mods.Select(m =>
        {
            var copies = installed(m.Id).ToList();
            var match = copies.FirstOrDefault(c => SaveHeaderReader.VersionsEqual(c.Version, m.Version));
            if (match is not null) return new VerifiedModCheck(m, VerifiedModState.Match, match.Version, match, VerifiedFetch.None);
            var state = copies.Count == 0 ? VerifiedModState.Missing : VerifiedModState.Different;
            var have = copies.Count == 0 ? null : string.Join(", ", copies.Select(c => c.Version).Distinct(StringComparer.OrdinalIgnoreCase));
            return new VerifiedModCheck(m, state, have, null, FetchFor(m));
        }).ToList();

    // ---- using one (decision 9) ---------------------------------------------------------------------------------

    /// <summary>
    /// The profile file a verified profile is used through: its mods in its order, each expecting the verified
    /// version, and a downloaded copy pinned (the scan does not look in ModderLords' own mods folder). Everything
    /// that is about this PC rather than the mod set - game, server and mod folders, server settings - comes from
    /// <paramref name="setup"/>: the file being refreshed, or the open profile for a first use.
    /// </summary>
    /// <param name="ownCopy">A copy of the user's own: no VerifiedId, so its mods are theirs to change.</param>
    public static Profile ToProfile(VerifiedProfile entry, string name, Profile setup, IReadOnlyList<VerifiedModCheck> checks, bool ownCopy = false)
    {
        var refresh = setup.VerifiedId == entry.Id;
        var p = ProfileStore.Snapshot(setup);
        p.Name = name;
        p.VerifiedId = ownCopy ? null : entry.Id;
        if (!refresh) { p.Group = ""; p.Pinned = false; p.SaveName = ""; p.PendingLauncherApply = false; p.ManualLoadOrder = false; }
        p.Mods = entry.Mods.Select(m =>
        {
            var copy = checks.FirstOrDefault(c => c.Mod == m)?.Copy;
            var old = setup.Mods.FirstOrDefault(o => o.Id.Equals(m.Id, StringComparison.OrdinalIgnoreCase));
            return new ProfileMod
            {
                Id = m.Id,
                // A role the user set on this file before (Host mode) survives a refresh; a first use takes the default.
                Role = refresh && old is not null ? old.Role : Profile.DefaultRoleFor(m.Id),
                LastVersion = m.Version,
                SourcePath = copy is not null && DownloadedMods.IsDownloaded(copy.FolderPath) ? copy.FolderPath : null,
                DownloadUrl = m.WorkshopId is { } w ? $"https://steamcommunity.com/sharedfiles/filedetails/?id={w}" : null,
            };
        }).ToList();
        return p;
    }

    /// <summary>The name a first use gets: the entry's own, unless a profile of the user's already has it.</summary>
    public static string FileNameFor(VerifiedProfile entry)
    {
        if (ProfileStore.List().FirstOrDefault(n => ProfileStore.TryLoad(n, out _)?.VerifiedId == entry.Id) is { } existing) return existing;
        if (!ProfileStore.Exists(entry.Name)) return entry.Name;
        for (var n = 1; ; n++)
        {
            var name = n == 1 ? $"{entry.Name} (verified)" : $"{entry.Name} (verified {n})";
            if (!ProfileStore.Exists(name)) return name;
        }
    }

    /// <summary>Whether the mods a profile enables are still exactly the entry's, in its order.</summary>
    public static bool SameMods(VerifiedProfile entry, Profile profile) =>
        profile.EnabledMods.Select(m => m.Id).SequenceEqual(entry.Mods.Select(m => m.Id), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Where the verified version is to be had: the maintainer's GitHub release when the Workshop does not serve it
    /// (its version is not recorded, or differs), else the Workshop item.
    /// </summary>
    public static VerifiedFetch FetchFor(VerifiedMod m) =>
        m.GitHub is not null && !SaveHeaderReader.VersionsEqual(m.WorkshopVersion, m.Version) ? VerifiedFetch.GitHub
        : m.WorkshopId is not null ? VerifiedFetch.Workshop
        : VerifiedFetch.None;
}

public enum VerifiedModState { Match, Different, Missing }
public enum VerifiedFetch { None, Workshop, GitHub }

/// <param name="Installed">The installed version that matches, or else every version installed; null when none is.</param>
/// <param name="Copy">The installed copy at the verified version.</param>
public sealed record VerifiedModCheck(VerifiedMod Mod, VerifiedModState State, string? Installed, DiscoveredModule? Copy, VerifiedFetch Fetch);

/// <summary>Picks up a newer verified-profiles.json between releases, as <see cref="CompatDbRemote"/> does the compat database.</summary>
public sealed class VerifiedProfilesRemote : RemoteDataFile<VerifiedProfilesFile?, VerifiedProfilesFile>
{
    public const string CacheFileName = "verified-profiles.remote.json";
    public const string MetaFileName = "verified-profiles.remote.meta.json";
    public const int MaxBytes = 1024 * 1024;

    public static string Url => $"https://raw.githubusercontent.com/{UpdateChecker.Repository}/main/src/ModderLords.Core/{VerifiedProfiles.BundledFileName}";

    public VerifiedProfilesRemote(string directory, Version running, bool enabled = true)
        : base(directory, CacheFileName, MetaFileName, running, enabled) { }

    protected override string SourceUrl => Url;
    protected override int ByteLimit => MaxBytes;
    protected override string Agent => "ModderLords-verified-profiles";
    protected override VerifiedProfilesFile Check(byte[] bytes, VerifiedProfilesFile? bundled) => Validate(bytes, bundled);

    /// <summary>The same switches as the compat database: one choice, "use data newer than this release".</summary>
    public static VerifiedProfilesRemote Default => new(ProfileStore.RootDir,
        typeof(VerifiedProfilesRemote).Assembly.GetName().Version ?? new Version(0, 0, 0),
        EnabledFor(Environment.GetEnvironmentVariable(CompatDbRemote.EnvVar)) && UiStateStore.Load().DownloadCompatDb);

    public VerifiedProfilesFile? TryReadCache(VerifiedProfilesFile? bundled) => ReadCache(bundled)?.Content;

    /// <summary>
    /// <see cref="VerifiedProfiles.Parse"/>, plus the two ways a file can parse and still be a step back: fewer than
    /// half the bundled profiles, or nothing as recent as the bundled file's newest verification.
    /// </summary>
    internal static VerifiedProfilesFile Validate(byte[] bytes, VerifiedProfilesFile? bundled)
    {
        if (bytes.Length > MaxBytes) throw new InvalidDataException($"larger than {MaxBytes / 1024} KB");
        var file = VerifiedProfiles.Parse(new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes).TrimStart('﻿'));
        if (bundled is null) return file;
        if (file.Profiles.Count * 2 < bundled.Profiles.Count)
            throw new InvalidDataException($"{file.Profiles.Count} profiles, where the bundled file has {bundled.Profiles.Count}");
        if (VerifiedProfiles.Newest(bundled.Profiles) is { } shipped && string.CompareOrdinal(VerifiedProfiles.Newest(file.Profiles) ?? "", shipped) < 0)
            throw new InvalidDataException("older than the bundled file");
        return file;
    }
}
