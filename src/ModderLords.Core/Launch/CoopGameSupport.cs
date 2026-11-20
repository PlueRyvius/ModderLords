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

    // ---- matching a Coop build to the installed game -------------------------------------------------------------
    //
    // Coop builds for one game version at a time: the Workshop's Coop 0.1.7 is for v1.4.8, the v1.5.7 nightlies are
    // CoopNightly. A PC's game is on one of them (Steam's beta branches switch it), so the server package and the Coop
    // module ModderLords uses follow the game installed here, whichever the profile last named.

    /// <summary>"v1.5.7" for "v1.5.7", "v1.5.7.0" or "e1.5.7.125802": the first three numbers. Null when there are none.</summary>
    public static string? ShortVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return null;
        var parts = version.Trim().TrimStart('v', 'V', 'e', 'E').Split('.');
        if (parts.Length < 3 || !parts.Take(3).All(p => p.Length > 0 && p.All(char.IsDigit))) return null;
        return "v" + string.Join(".", parts.Take(3).Select(p => int.Parse(p, System.Globalization.CultureInfo.InvariantCulture)));
    }

    /// <summary>Whether two versions name the same game release (first three numbers); false when either is unknown.</summary>
    public static bool SameGame(string? a, string? b) => ShortVersion(a) is { } x && x == ShortVersion(b);

    /// <summary>The game version a Coop module was built for: the version of Native its manifest depends on.</summary>
    public static string? BuiltFor(DiscoveredModule coop) =>
        ShortVersion(coop.Info?.DependentModules
            .FirstOrDefault(d => d.Id.Equals("Native", StringComparison.OrdinalIgnoreCase))?.Version.ToString());

    /// <summary>The game version a Coop server package runs: its engine's own Native module. Null when unreadable.</summary>
    public static string? ServerGameVersion(string serverRoot)
    {
        try
        {
            var doc = new XmlDocument();
            doc.Load(Path.Combine(serverRoot, "engine", "Modules", "Native", "SubModule.xml"));
            return ShortVersion((doc.SelectSingleNode("/Module/Version") as XmlElement)?.GetAttribute("value"));
        }
        catch (Exception ex) when (ex is IOException or XmlException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>The server folder to host from, and a line saying why when it is not simply the one the profile named.</summary>
    public sealed record ServerChoice(string? Root, string? Note);

    /// <summary>
    /// The Coop server package for the game installed here (<paramref name="gameVersion"/>). The profile's folder
    /// (<paramref name="configured"/>) wins when it runs that game, or when nothing says otherwise; when it runs
    /// another game and a package in <paramref name="found"/> runs this one, that package hosts instead, and the
    /// note says so. Without a configured folder, the first package for this game, else the first found.
    /// </summary>
    public static ServerChoice PickServer(string? configured, IEnumerable<string> found, string? gameVersion, Func<string, string?> versionOf)
    {
        var all = found.Where(f => configured is null || !SamePath(f, configured)).ToList();
        var game = ShortVersion(gameVersion);
        if (game is null) return new(configured ?? all.FirstOrDefault(), null);
        var configuredRuns = configured is null ? null : versionOf(configured);
        if (configured is not null && (configuredRuns is null || SameGame(configuredRuns, game))) return new(configured, null);
        var match = all.FirstOrDefault(f => SameGame(versionOf(f), game));
        if (match is not null)
            return new(match, configured is null
                ? $"Coop server for this game ({game}): {match}"
                : $"the profile's Coop server runs game {configuredRuns} ({configured}), but this PC's game is {game}; hosting from the Coop server for {game} instead ({match})");
        var fallback = configured ?? all.FirstOrDefault();
        var runs = fallback is null ? null : versionOf(fallback);
        return new(fallback, fallback is null || runs is null ? null
            : $"WARNING: no Coop server for this PC's game ({game}) was found; the one used runs game {runs} ({fallback}). Install the Coop build for {game} and set its folder in Folders.");
    }

    /// <summary>
    /// The Coop module the player's game should load. <paramref name="chosen"/> (the profile's) stays unless it was
    /// built for another game and one of <paramref name="installed"/> was built for this one. Null means keep it.
    /// </summary>
    public static DiscoveredModule? BetterCoopFor(DiscoveredModule chosen, IEnumerable<DiscoveredModule> installed, string? gameVersion)
    {
        var game = ShortVersion(gameVersion);
        if (game is null || BuiltFor(chosen) is not { } built || SameGame(built, game)) return null;
        return installed.FirstOrDefault(m => !ReferenceEquals(m, chosen) && IsCoop(m.Id) && SameGame(BuiltFor(m), game));
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
            StringComparison.OrdinalIgnoreCase);
}
