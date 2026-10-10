namespace ModderLords.Core.Logs;

/// <summary>What the console should do with one server line: hide it, and any summary lines to show before it.</summary>
public readonly record struct NoiseVerdict(bool Folded, IReadOnlyList<string> Summaries)
{
    public static readonly NoiseVerdict Show = new(false, []);
}

/// <summary>
/// Folds two bursts Coop prints while the world loads into one counted line each. Both were traced to their cause in
/// Coop 0.1.6 and say nothing about the session; anything not matched here is left exactly as it was.
///
/// 1. <c>[Coop] ["ObjectManager"] Failed to get id for object of type "...Clan", "x"</c>, hundreds of times in a
///    fraction of a second. Loading a save rebuilds every clan's list of war parties and supporter notables
///    (WarPartyComponent.OnInitialize, Hero.PreAfterLoad). Coop announces each addition to clients, and to do that it
///    looks the clan's network id up, but it registers the loaded world's objects only afterwards
///    (InitialServerState). The lookup fails, the announcement is dropped, and no client is connected to miss it.
///    Folded ONLY until the server says it is serving: the same line later means a clan list changed and the players
///    were not told, which is a real fault and stays visible.
///
/// 2. <c>Error: The 'coop_...' attribute is not declared.</c> with its <c>Node:</c> and <c>Line:</c> lines. Coop's
///    own NPC file uses attributes the game's schema does not list; the game reports each and loads the file anyway.
///    Only attributes named <c>coop_</c> are folded, so a schema error in any other mod's file still shows.
///
/// The launch log keeps every line. One instance per launch.
/// </summary>
public sealed class StartupNoiseFolder
{
    private const string ClanIdMiss = "Failed to get id for object of type \"TaleWorlds.CampaignSystem.Clan\"";

    private bool _serving, _reported, _inSchemaError;
    private int _clanIds, _schema;

    public int ClanIdLines => _clanIds;
    public int SchemaLines => _schema;

    public NoiseVerdict Observe(string text)
    {
        if (_serving) return NoiseVerdict.Show;

        if (text.Contains("SERVING", StringComparison.Ordinal) || text.Contains("\"phase\":\"serving\"", StringComparison.Ordinal))
        {
            _serving = true;
            return new NoiseVerdict(false, Report());
        }

        if (text.Contains("[Coop]", StringComparison.Ordinal) && text.Contains(ClanIdMiss, StringComparison.Ordinal))
        {
            _inSchemaError = false;
            _clanIds++;
            return new NoiseVerdict(true, []);
        }

        var t = text.AsSpan().Trim();
        if (text.Contains("Error: The 'coop_", StringComparison.Ordinal) && text.Contains("' attribute is not declared", StringComparison.Ordinal))
        {
            _inSchemaError = true;
            _schema++;
            return new NoiseVerdict(true, []);
        }
        // The two detail lines the engine prints under such an error; only directly after one.
        if (_inSchemaError && (t.StartsWith("Node: coop_", StringComparison.Ordinal) || t.StartsWith("Line: ", StringComparison.Ordinal)))
            return new NoiseVerdict(true, []);

        _inSchemaError = false;
        return NoiseVerdict.Show;
    }

    /// <summary>The summaries, once. Call when the server exits, for a launch that never got as far as serving.</summary>
    public IReadOnlyList<string> Report()
    {
        if (_reported) return [];
        _reported = true;
        var lines = new List<string>();
        if (_clanIds > 0)
            lines.Add($"[ModderLords] folded {_clanIds:N0} Coop lines from the save load (\"Failed to get id for object of type ...Clan\"): "
                + "Coop announces each clan's parties before it has registered the loaded world, and nobody is connected yet. "
                + "The same line after this point is NOT folded and means players were not told of a change. Every line is in the launch log.");
        if (_schema > 0)
            lines.Add($"[ModderLords] folded {_schema:N0} schema complaints about Coop's own 'coop_...' attributes: the game reports them and loads the file anyway. Every line is in the launch log.");
        return lines;
    }
}
