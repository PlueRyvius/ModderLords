namespace ModderLords.Core.Overlay;

/// <summary>
/// How the launcher tells the server which mods are <see cref="ServerRole.ClientOptional"/>, and how that is read back.
///
/// <para>Coop's join check wants a player's community mods to match the server's exactly, in both directions, so a
/// player running a mod the server does not list is refused ("Server does not support module ..."). These mods are
/// not on the server by design, so their ids go to it in one environment variable and are left out of what the
/// check sees. Nothing else changes: every other mod is still matched id for id and version for version.</para>
///
/// Kept free of game types so both the launcher and the server module can use it and tests can check it.
/// </summary>
public static class ClientOptionalMods
{
    public const string Variable = "MODDERLORDS_CLIENT_OPTIONAL_MODS";

    /// <summary>No module id contains one, and it survives being an environment value on Windows.</summary>
    private const char Separator = '|';

    /// <summary>The ids worth sending: trimmed, no blanks, no repeats (ids compare without case, as Coop compares them with).</summary>
    public static IReadOnlyList<string> Ids(IEnumerable<string> ids) =>
        ids.Select(id => id.Trim()).Where(id => id.Length > 0 && !id.Contains(Separator))
           .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    public static string Format(IEnumerable<string> ids) => string.Join(Separator, Ids(ids));

    public static IReadOnlySet<string> Parse(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : Ids(value.Split(Separator)).ToHashSet(StringComparer.OrdinalIgnoreCase);
}
