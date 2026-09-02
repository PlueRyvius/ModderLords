using System.Text.Json;
using System.Text.Json.Serialization;
using ModularCoop.Core.Launch;
using ModularCoop.Core.Overlay;

namespace ModularCoop.Core.Profiles;

/// <summary>One community mod in a profile. SourcePath pins a specific copy; null = pick the best match by id.</summary>
public sealed class ProfileMod
{
    public string Id { get; set; } = "";
    public ServerRole Role { get; set; } = ServerRole.Run;
    public bool Enabled { get; set; } = true;
    public string? SourcePath { get; set; }
    /// <summary>Version seen when the profile was last synced/launched; used for drift warnings.</summary>
    public string? LastVersion { get; set; }
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
    public bool TraceTick { get; set; }
    public bool TracePublish { get; set; }
    public bool TraceBandits { get; set; }
}

/// <summary>A named mod set + order + server settings. Stored as JSON under %LOCALAPPDATA%\ModularCoop\profiles.</summary>
public sealed class Profile
{
    public int SchemaVersion { get; set; } = 1;
    public string Name { get; set; } = "default";
    public string? DedicatedServerRoot { get; set; }
    public string? GameRoot { get; set; }
    public List<string> CustomModRoots { get; set; } = new();
    public List<ProfileMod> Mods { get; set; } = new();
    public string SaveName { get; set; } = "";
    /// <summary>Load the launcher's own DedicatedServer.ModularCoopCompat module (headless guards) on the server.</summary>
    public bool CompatGuards { get; set; } = true;
    public ServerSettings Server { get; set; } = new();

    [JsonIgnore] public IEnumerable<ProfileMod> EnabledMods => Mods.Where(m => m.Enabled);

    /// <summary>The roles that worked for Andy's mod set; anything unknown defaults to Run.</summary>
    public static readonly Dictionary<string, ServerRole> DefaultRoles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Bannerlord.Harmony"] = ServerRole.DependencyOnly,
        ["Bannerlord.ButterLib"] = ServerRole.DependencyOnly,
        ["Bannerlord.UIExtenderEx"] = ServerRole.DependencyOnly,
        ["Bannerlord.MBOptionScreen"] = ServerRole.DependencyOnly,
    };

    public static ServerRole DefaultRoleFor(string id) => DefaultRoles.TryGetValue(id, out var r) ? r : ServerRole.Run;
}

public static class ProfileStore
{
    public static string RootDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ModularCoop");
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
        return Directory.EnumerateFiles(ProfilesDir, "*.json").Select(Path.GetFileNameWithoutExtension).Where(n => n is not null).Cast<string>().OrderBy(n => n).ToList();
    }

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

    public static void Delete(string name)
    {
        var p = PathFor(name);
        if (File.Exists(p)) File.Delete(p);
    }

    public static string Safe(string name)
    {
        var chars = name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray();
        var s = new string(chars).Trim();
        return s.Length == 0 ? "default" : s;
    }
}
