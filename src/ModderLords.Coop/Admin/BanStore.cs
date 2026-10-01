using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ModderLords.Coop.Admin;

/// <summary>One ban as the launcher keeps it. The server reads <see cref="SteamId"/>, <see cref="Ip"/>, Name and Reason.</summary>
public sealed record BanRecord
{
    public string SteamId { get; init; } = "";
    public string Ip { get; init; } = "";
    public string Name { get; init; } = "";
    public string Reason { get; init; } = "";
    public DateTimeOffset At { get; init; }
}

/// <summary>
/// The server's ban list: <c>&lt;server user folder&gt;\ModderLords\bans.json</c>. The launcher owns the file; the
/// server's compat module rereads it whenever it changes and refuses anyone it lists (its BanList). Kept in the
/// server's user folder, not the profile, so a ban holds whichever profile hosts next.
/// </summary>
public sealed class BanStore
{
    /// <summary>Must match the module's AdminWire.BansFile (a test checks).</summary>
    public const string RelativePath = "ModderLords\\bans.json";
    public const string SharedDefaultId = "DefaultId";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public string Path { get; }

    public BanStore(string serverDataDir) => Path = System.IO.Path.Combine(serverDataDir, RelativePath);

    public IReadOnlyList<BanRecord> Load()
    {
        if (!File.Exists(Path)) return [];
        var file = JsonSerializer.Deserialize<BanFile>(File.ReadAllText(Path), Json);
        return file?.Bans ?? [];
    }

    /// <summary>
    /// Adds a ban for this player, replacing one that names the same Steam id. Throws when there is nothing to ban
    /// by: the Steam id every Steam-less game shares, and this PC's own address, identify no one in particular.
    /// </summary>
    public BanRecord Add(string steamId, string ip, string name, string reason)
    {
        var record = new BanRecord
        {
            SteamId = IsPersonalId(steamId) ? steamId.Trim() : "",
            Ip = IsRemoteIp(ip) ? NormalizeIp(ip) : "",
            Name = name.Trim(),
            Reason = reason.Trim(),
            At = DateTimeOffset.Now,
        };
        if (record.SteamId.Length == 0 && record.Ip.Length == 0)
            throw new InvalidOperationException("This player has no Steam id of their own and no outside address, so there is nothing to ban them by.");
        var list = Load().Where(b => !(record.SteamId.Length > 0 && b.SteamId == record.SteamId)).ToList();
        list.Add(record);
        Save(list);
        return record;
    }

    /// <summary>Removes the bans that match this record's Steam id and address.</summary>
    public void Remove(BanRecord ban) =>
        Save(Load().Where(b => !(b.SteamId == ban.SteamId && b.Ip == ban.Ip)).ToList());

    /// <summary>Written whole to a temporary file and moved into place, so the server never reads half a list.</summary>
    private void Save(List<BanRecord> bans)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var temp = Path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(new BanFile { Bans = bans }, Json));
        File.Move(temp, Path, overwrite: true);
    }

    public static bool IsPersonalId(string? id) =>
        !string.IsNullOrWhiteSpace(id) && !string.Equals(id.Trim(), SharedDefaultId, StringComparison.OrdinalIgnoreCase);

    public static bool IsRemoteIp(string? ip) =>
        NormalizeIp(ip) is { Length: > 0 } n && !IPAddress.IsLoopback(IPAddress.Parse(n));

    public static string NormalizeIp(string? ip)
    {
        if (string.IsNullOrWhiteSpace(ip) || !IPAddress.TryParse(ip.Trim(), out var a)) return "";
        if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
        return a.ToString();
    }

    private sealed class BanFile
    {
        public int Schema { get; set; } = 1;
        public List<BanRecord> Bans { get; set; } = [];
    }
}
