using System.Xml;
using ModderLords.Core.Modules;
using ModderLords.Core.Testing;

namespace ModderLords.Core.Launch;

/// <summary>
/// Whether the player's Coop module can run on the installed game. Coop is built for one game version: when the game
/// updates first (Bannerlord v1.5.7, 2026-11-12, with Coop 0.1.7 built for v1.4.8), a player's game crashes as soon as
/// Coop calls something the game no longer has, and a host sees only that nobody can join. The dedicated server is
/// not affected (Coop ships its own server package), so the warning names the players' games.
///
/// Reads metadata only (<see cref="ApiCheck"/>): Coop's DLLs that load, against the game's assemblies.
/// </summary>
public static class CoopGameSupport
{
    /// <summary>The Coop module ids a profile can carry.</summary>
    public static readonly IReadOnlyList<string> CoopIds = ["Coop", "CoopNightly"];

    public static bool IsCoop(string id) => CoopIds.Any(c => c.Equals(id, StringComparison.OrdinalIgnoreCase));

    /// <summary>The game's version from bin\Win64_Shipping_Client\Version.xml ("v1.5.7"); null when it cannot be read.</summary>
    public static string? GameVersion(string gameRoot)
    {
        try
        {
            var doc = new XmlDocument();
            doc.Load(Path.Combine(gameRoot, "bin", "Win64_Shipping_Client", "Version.xml"));
            return (doc.SelectSingleNode("/Version/Singleplayer") as XmlElement)?.GetAttribute("Value") is { Length: > 0 } v ? v : null;
        }
        catch (Exception ex) when (ex is IOException or XmlException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// A "WARNING: ..." line when <paramref name="coop"/> (the players' Coop module) calls into the game what the game
    /// at <paramref name="gameRoot"/> no longer has; null when it fits, or when there is nothing to check.
    /// <paramref name="officials"/> are the game's own modules, whose DLLs count as the game. <paramref name="forHost"/>
    /// words it for a server launch (players cannot join) rather than this PC's own game (it will crash).
    /// </summary>
    public static string? Warning(DiscoveredModule coop, string gameRoot, IEnumerable<DiscoveredModule> officials, bool forHost = false)
    {
        var dlls = ApiCheck.LoadedDlls(coop.ClientBin, coop.Info?.SubModules.Select(s => s.DLLName) ?? []);
        if (dlls.Count == 0) return null;
        var game = ApiIndex.OfCached(ApiIndex.DllsIn([Path.Combine(gameRoot, "bin", "Win64_Shipping_Client"), .. officials.Select(m => m.ClientBin)]));
        if (game.TypeCount == 0) return null;
        var broken = ApiCheck.CheckAll(dlls, game);
        if (broken.Count == 0) return null;
        var gameVersion = GameVersion(gameRoot) ?? "this version";
        var calls = broken.Sum(b => b.Missing.Count);
        var first = broken.SelectMany(b => b.Missing).First();
        return $"WARNING: Coop {coop.Version} does not support the installed game ({gameVersion}): it makes {calls} call(s) into the game that "
               + $"{gameVersion} no longer has ({string.Join(", ", broken.Select(b => Path.GetFileName(b.Dll)))}; first: {first}). "
               + (forHost
                   ? $"A player's game on {gameVersion} crashes when it loads Coop, so nobody on it can join; the server itself runs. "
                   : "This game will crash when it loads Coop. ")
               + "Wait for a Coop update for this game version, or keep the game on the version Coop supports (Steam: Bannerlord, Properties, Betas).";
    }
}
