using System.Text.Json;
using System.Text.Json.Serialization;
using ModderLords.Core.Profiles;
using ModderLords.Core.Saves;
using ModderLords.Core.Smoke;

namespace ModderLords.Core.Compat;

/// <summary>
/// Watches one server run for the moment that proves a set-up works: a player on the campaign map. The dedicated
/// server only lists a player as "on map" after it has started, served the world and taken that player through the
/// join, so that one line stands for both halves - the server launched and a game joined it.
/// </summary>
public sealed class JoinWatch
{
    public bool Joined { get; private set; }

    /// <summary>True exactly once per run: for the line that shows the first player on the map.</summary>
    public bool Observe(string line)
    {
        if (Joined) return false;
        if (SmokeSignals.ParsePlayers(line) is not { } players || !players.Any(p => p.State == SmokeSignals.OnMapState)) return false;
        return Joined = true;
    }
}

/// <summary>
/// One mod, at one version, hosted with one set of settings while a player was on the map. Deliberately nothing
/// about the session itself: no player, no address, no profile name. Submit… may quote the date and nothing else.
/// </summary>
public sealed record CompatProof(string Id, string? ModVersion, string? CoopVersion, DateTime ProvenAtUtc, ModSettings Settings)
{
    /// <summary>Whether this is proof of what is on screen now: the same mod version, Coop version and settings.</summary>
    public bool Covers(string? modVersion, string? coopVersion, ModSettings settings) =>
        SaveHeaderReader.VersionsEqual(ModVersion, modVersion) && string.Equals(CoopVersion, coopVersion, StringComparison.Ordinal)
        && Settings.Same(settings);
}

/// <summary>The proofs this PC has gathered, one per mod (the latest), in compat-proof.json next to the profiles.</summary>
public static class CompatProofStore
{
    public const string FileName = "compat-proof.json";
    public static string DefaultPath => Path.Combine(ProfileStore.RootDir, FileName);

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>A missing or unreadable file is no proof, never an error: the worst case is hosting once more.</summary>
    public static List<CompatProof> Load(string path)
    {
        if (!File.Exists(path)) return [];
        try
        {
            return (JsonSerializer.Deserialize<List<CompatProof>>(File.ReadAllText(path), Json) ?? [])
                .Where(p => p is { Id.Length: > 0, Settings: not null }).ToList();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException) { return []; }
    }

    public static CompatProof? Find(string path, string id) =>
        Load(path).FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Adds these, each replacing an earlier proof for the same mod.</summary>
    public static void Add(string path, IReadOnlyCollection<CompatProof> proofs)
    {
        if (proofs.Count == 0) return;
        var all = Load(path);
        all.RemoveAll(old => proofs.Any(p => p.Id.Equals(old.Id, StringComparison.OrdinalIgnoreCase)));
        all.AddRange(proofs);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(all.OrderBy(p => p.Id, StringComparer.OrdinalIgnoreCase).ToList(), Json));
        File.Move(tmp, path, overwrite: true);
    }
}
