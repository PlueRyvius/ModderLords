using System.Globalization;
using System.Text.RegularExpressions;

namespace ModderLords.Core.Logs;

/// <summary>
/// Watches for the engine's "Main_map read OK, navmesh CRC=N" line and reports when the world is served on a
/// different map from the one it was built on.
///
/// Measured 2026-09-11: an automatic TAOM launch generated its campaign on TAOM's map (CRC 3101457840) and then
/// served it on the stock map (CRC 1465536726). Both phases reported success; the server reached SERVING, a client
/// joined, and the only evidence that anything was wrong was two different numbers thousands of lines apart.
/// A campaign whose settlements and parties were placed on one heightmap, pathfinding across another, is not a
/// server that should look healthy.
///
/// Deliberately identity-based rather than value-based: nothing here knows which CRC belongs to which mod. It
/// compares the serving map to the creating map, so it works for any map-replacing mod without a table to maintain.
/// </summary>
public sealed class MapIdentityCheck
{
    private static readonly Regex Line = new(@"navmesh CRC=(\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private uint? _created;
    private uint? _served;

    /// <summary>The map the world was generated on, once a creation phase has reported one.</summary>
    public uint? CreatedOn => _created;

    /// <summary>The map the running server actually loaded.</summary>
    public uint? ServedOn => _served;

    /// <summary>Records a CRC seen during world creation. The last one wins: the map is read more than once.</summary>
    public void ObserveCreation(string line)
    {
        if (Crc(line) is { } crc) _created = crc;
    }

    /// <summary>
    /// Records a CRC seen while starting the server. Returns a message when it disagrees with the creation phase,
    /// and only the first time, so a map re-read per scene load cannot fill the console.
    /// </summary>
    public string? ObserveServing(string line)
    {
        if (Crc(line) is not { } crc) return null;
        if (_served is not null) return null;
        _served = crc;
        if (_created is null || _created == crc) return null;
        return $"the world was generated on map {_created} but this server loaded map {crc}. " +
               "The campaign's settlements and parties belong to a different heightmap than the one being " +
               "pathfound, which crashes the engine rather than failing cleanly. Stop the server.";
    }

    private static uint? Crc(string line)
    {
        var m = Line.Match(line);
        return m.Success && uint.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var crc)
            ? crc : null;
    }
}
