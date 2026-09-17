using System.Text.Json;
using System.Text.Json.Serialization;
using ModderLords.Core.Launch;
using ModderLords.Core.Overlay;

namespace ModderLords.Core.Profiles;

/// <summary>Who may see a hosted server in the Steam listing. Stored in the profile, so it lives here rather
/// than with the launch code that consumes it.</summary>
public enum ServerVisibility { Public, FriendsOnly, None }

/// <summary>One community mod in a profile. SourcePath pins a specific copy; null = pick the best match by id.</summary>
public sealed class ProfileMod
{
    public string Id { get; set; } = "";
    public ServerRole Role { get; set; } = ServerRole.Run;
    public bool Enabled { get; set; } = true;
    public string? SourcePath { get; set; }
    public string? DownloadUrl { get; set; }
    /// <summary>Version seen when the profile was last synced/launched; used for drift warnings.</summary>
    public string? LastVersion { get; set; }
    /// <summary>Layer 1: run this mod's behaviours on the server only; clients skip them (needs ModderLords.Compat on both sides).</summary>
    public bool ServerAuthoritative { get; set; }
    /// <summary>
    /// Behaviour type names (from the scan) that stay client-side even when ServerAuthoritative is on; e.g. a mod's
    /// UI behaviour. Everything the scan finds and is not listed here is gated on clients.
    /// </summary>
    public List<string> ClientSideBehaviors { get; set; } = new();
}

public sealed class ServerSettings
{
    /// <summary>UDP port Coop clients join (server-config.json "port").</summary>
    public int JoinPort { get; set; } = 4200;
    public int EnginePort { get; set; } = 7210;
    public string Region { get; set; } = "EU";
    public string Password { get; set; } = "";
    public bool Steam { get; set; } = true;
    public int AutosaveMinutes { get; set; } = 5;
    public bool LogFile { get; set; } = true;
    public ServerVisibility Visibility { get; set; } = ServerVisibility.Public;
    // Diagnostics only, and deliberately NOT persisted: JsonIgnore means these are always off when the
    // app opens, whatever a profile file happens to contain. With one of these on the engine prints
    // hundreds of thousands of lines a second, so a trace switch that survived a restart — silently, in a
    // file nobody reads — would be a footgun. Tick one, launch, diagnose, and it is gone next session.
    [JsonIgnore] public bool TraceTick { get; set; }
    [JsonIgnore] public bool TracePublish { get; set; }
    [JsonIgnore] public bool TraceBandits { get; set; }
}

/// <summary>A named mod set + order + server settings. Stored as JSON under %LOCALAPPDATA%\ModderLords\profiles.</summary>
public sealed class Profile
{
    public int SchemaVersion { get; set; } = 1;
    public string Name { get; set; } = "default";
    public string? DedicatedServerRoot { get; set; }
    public string? GameRoot { get; set; }
    public List<string> CustomModRoots { get; set; } = new();
    public List<ProfileMod> Mods { get; set; } = new();
    public string SaveName { get; set; } = "";
    /// <summary>Load the launcher's own DedicatedServer.ModderLordsCompat module (headless guards) on the server.</summary>
    public bool CompatGuards { get; set; } = true;
    /// <summary>Also load the shared ModderLords.Compat module (settings sync). Players must install it too; it is part of the handshake.</summary>
    public bool SettingsSync { get; set; } = false;
    /// <summary>Only shipped, fingerprint-matched and validated operation contracts may activate automatically.</summary>
    public bool AutomaticCompatibility { get; set; } = true;
    /// <summary>Use shipped host defaults without applying saved manual compatibility overrides.</summary>
    public bool SimpleCompatibility { get; set; } = true;

    public Profile ForServerLaunch()
    {
        var copy = ProfileStore.Snapshot(this);
        if (!copy.SimpleCompatibility) return copy;
        var defaults = Compat.CompatDb.Load(Compat.CompatDb.BundledPath, null);
        copy.CompatGuards = true;
        copy.SettingsSync = false;
        copy.AutomaticCompatibility = true;
        copy.UseModDistanceCache = false;
        foreach (var mod in copy.Mods)
        {
            mod.Role = defaults.DefaultRoleFor(mod.Id);
            mod.ServerAuthoritative = false;
            mod.ClientSideBehaviors.Clear();
        }
        return copy;
    }

    /// <summary>
    /// Skip the confirmation when Launch client brings this PC's LauncherData.xml in line with the server. Set by
    /// ticking "don't ask again" in that dialog; warnings the sync cannot fix are still reported either way.
    /// </summary>
    public bool AutoSyncLauncherData { get; set; }
    /// <summary>
    /// Take the mod order in <see cref="Mods"/> exactly as written, instead of moving mods to satisfy the ordering
    /// their manifests declare.
    ///
    /// The computed order is a suggestion built from metadata, and metadata is sometimes wrong: TAOM_Map ships
    /// <c>&lt;DependedModuleMetadata id="TAOM" order="LoadBeforeThis"/&gt;</c>, which contradicts the load order
    /// TAOM's own authors publish. No amount of dependency logic covers every such case, so the user has to be able
    /// to win. Conflicts are still computed and still reported — this changes who decides, not what is said.
    /// </summary>
    public bool ManualLoadOrder { get; set; }
    /// <summary>
    /// Seconds of no loading progress before the launch console says so; 0 turns the warning off entirely. Null uses
    /// <see cref="Logs.LoadStallDetector.DefaultThreshold"/>. Per profile because how long is "too long" is a fact
    /// about the mods, not about the launcher: TAOM's authors put its load at up to two hours.
    /// </summary>
    public int? StallWarningSeconds { get; set; }
    /// <summary>
    /// Let a map mod's settlement distance cache replace SandBox's, because the engine's path to that file is
    /// hardcoded and a map mod's settlements do not match the vanilla cache. Off by default: it is the one thing
    /// this launcher writes inside the DedicatedServer package, so it stays opt-in, and turning it back off restores
    /// the original. See <see cref="Coop.Saves.DistanceCacheOverride"/> for the crash it fixes.
    /// </summary>
    public bool UseModDistanceCache { get; set; }
    public ServerSettings Server { get; set; } = new();

    /// <summary>
    /// Official modules to load when this profile launches the player's own game. The default is the set the game
    /// ships selected for single player; BirthAndDeath, FastMode and Multiplayer are off, as they are in a stock
    /// LauncherData.xml. Ordering is not taken from here — the sorter places them.
    /// </summary>
    public List<string> ClientOfficialModules { get; set; } = new() { "Native", "SandBoxCore", "Sandbox", "StoryMode", "CustomBattle" };

    [JsonIgnore] public IEnumerable<ProfileMod> EnabledMods => Mods.Where(m => m.Enabled);

    /// <summary>Role for a mod not yet in a profile: from the compat database, falling back to the pre-DB table (frameworks = DependencyOnly).</summary>
    public static ServerRole DefaultRoleFor(string id) => Compat.CompatDb.Current.DefaultRoleFor(id);
}

public static class ProfileStore
{
    public static Profile Snapshot(Profile profile)
    {
        var copy = JsonSerializer.Deserialize<Profile>(JsonSerializer.Serialize(profile, Json), Json)!;
        copy.Server.TraceTick = profile.Server.TraceTick;
        copy.Server.TracePublish = profile.Server.TracePublish;
        copy.Server.TraceBandits = profile.Server.TraceBandits;
        return copy;
    }

    /// <summary>Merge observed versions without reverting edits made while a launch was preparing.</summary>
    public static void MergeLastVersions(Profile destination, Profile launched)
    {
        foreach (var mod in destination.Mods)
        {
            var observed = launched.Mods.FirstOrDefault(m => m.Id.Equals(mod.Id, StringComparison.OrdinalIgnoreCase)
                && string.Equals(m.SourcePath, mod.SourcePath, StringComparison.OrdinalIgnoreCase));
            if (observed is not null) mod.LastVersion = observed.LastVersion;
        }
    }
    public static string RootDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ModderLords");
    public static string ProfilesDir => Path.Combine(RootDir, "profiles");
    public static string OverlayDirFor(string profileName) => Path.Combine(RootDir, "overlay", Safe(profileName));

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string PathFor(string name) => Path.Combine(ProfilesDir, Safe(name) + ".json");

    public static IReadOnlyList<string> List()
    {
        if (!Directory.Exists(ProfilesDir)) return Array.Empty<string>();
        return ListIn(ProfilesDir);
    }

    public static IReadOnlyList<string> ListIn(string directory) => Directory.EnumerateFiles(directory, "*.json")
        .Where(p => !p.EndsWith(".settings.json", StringComparison.OrdinalIgnoreCase))
        .Select(p => Path.GetFileNameWithoutExtension(p)).OrderBy(n => n).ToList();

    public static Profile? Load(string name)
    {
        var p = PathFor(name);
        if (!File.Exists(p)) return null;
        return JsonSerializer.Deserialize<Profile>(File.ReadAllText(p), Json);
    }

    public static void Save(Profile profile)
    {
        Directory.CreateDirectory(ProfilesDir);
        var p = PathFor(profile.Name);
        var tmp = p + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(profile, Json));
        File.Move(tmp, p, overwrite: true);
    }

    /// <summary>Host overrides for this profile's mod settings. Written by the coop side, but the profile folder's
    /// layout is owned here, so deleting a profile can take its sidecars with it without reaching across.</summary>
    public static string SettingsOverridesPath(string profileName) => Path.Combine(ProfilesDir, Safe(profileName) + ".settings.json");

    /// <summary>Cached descriptions of a profile's mod settings, so the editor can open offline.</summary>
    public static string SettingsCachePath(string profileName) => Path.Combine(RootDir, "cache", Safe(profileName) + ".settings-cache.json");

    public static void Delete(string name)
    {
        foreach (var p in new[] { PathFor(name), SettingsOverridesPath(name), SettingsCachePath(name) })
            if (File.Exists(p)) File.Delete(p);
    }

    public static string Safe(string name)
    {
        var chars = name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray();
        var s = new string(chars).Trim();
        return s.Length == 0 ? "default" : s;
    }
}
