using System;
using System.Collections.Generic;
using System.Net;
using ModderLords.CompatSync;

namespace ModderLords.CompatSync.Coop.Admin;

/// <summary>One ban: a Steam id (Coop's controller id), an IP address, or both. Either one matching refuses the player.</summary>
public sealed class BanEntry
{
    public string SteamId { get; set; } = "";
    public string Ip { get; set; } = "";
    public string Name { get; set; } = "";
    public string Reason { get; set; } = "";
}

/// <summary>
/// The server's ban list, as the launcher writes it (<see cref="AdminWire.BansFile"/>):
/// <c>{"schema":1,"bans":[{"steamId":"...","ip":"...","name":"...","reason":"..."}]}</c>.
/// <para>No game types here: the launcher's tests compile this file to check the two ends read the same file.</para>
/// </summary>
public sealed class BanList
{
    /// <summary>
    /// What Coop registers for a game that started without a Steam id. Every such player shares it, so it never
    /// identifies one person and is never matched.
    /// </summary>
    public const string SharedDefaultId = "DefaultId";

    public static readonly BanList Empty = new BanList(new List<BanEntry>());

    public IReadOnlyList<BanEntry> Entries { get; }

    public BanList(List<BanEntry> entries) => Entries = entries;

    /// <summary>Reads the file's text; throws FormatException when it is not the expected shape.</summary>
    public static BanList Parse(string json)
    {
        var root = MiniJson.ParseObject(json);
        var list = new List<BanEntry>();
        foreach (var item in MiniJson.GetArray(root, "bans") ?? new List<object?>())
        {
            if (item is not Dictionary<string, object?> o) continue;
            var entry = new BanEntry
            {
                SteamId = (MiniJson.GetString(o, "steamId") ?? "").Trim(),
                Ip = (MiniJson.GetString(o, "ip") ?? "").Trim(),
                Name = MiniJson.GetString(o, "name") ?? "",
                Reason = MiniJson.GetString(o, "reason") ?? "",
            };
            if (Usable(entry)) list.Add(entry);
        }
        return new BanList(list);
    }

    /// <summary>The ban that refuses this player, or null. Either value may be null when it is not known yet.</summary>
    public BanEntry? Match(string? steamId, string? ip)
    {
        var id = steamId?.Trim() ?? "";
        var address = NormalizeIp(ip);
        foreach (var e in Entries)
        {
            if (IsPersonalId(e.SteamId) && string.Equals(e.SteamId, id, StringComparison.Ordinal)) return e;
            if (IsRemoteIp(e.Ip) && address.Length > 0 && string.Equals(NormalizeIp(e.Ip), address, StringComparison.OrdinalIgnoreCase)) return e;
        }
        return null;
    }

    /// <summary>
    /// The address as one spelling: an IPv4 address the socket reports in its IPv6-mapped form (::ffff:1.2.3.4) is
    /// written as plain IPv4, so a ban taken from either form matches the other. "" when it is not an address.
    /// </summary>
    public static string NormalizeIp(string? ip)
    {
        if (string.IsNullOrWhiteSpace(ip) || !IPAddress.TryParse(ip!.Trim(), out var a)) return "";
        if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
        return a.ToString();
    }

    /// <summary>A Steam id that names one person: not empty, and not the id every Steam-less game shares.</summary>
    public static bool IsPersonalId(string? steamId) =>
        !string.IsNullOrWhiteSpace(steamId) && !string.Equals(steamId!.Trim(), SharedDefaultId, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// An address worth banning: a parseable IP that is not this machine. A game on the host's own PC connects from
    /// loopback, so banning that address would refuse the host.
    /// </summary>
    public static bool IsRemoteIp(string? ip) =>
        NormalizeIp(ip) is { Length: > 0 } n && !IPAddress.IsLoopback(IPAddress.Parse(n));

    private static bool Usable(BanEntry e) => IsPersonalId(e.SteamId) || IsRemoteIp(e.Ip);
}
