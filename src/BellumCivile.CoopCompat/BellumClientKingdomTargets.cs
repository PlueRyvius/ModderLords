namespace ModderLords.CompatSync.Coop;

/// <summary>
/// The one place Bellum Civile makes a kingdom, by name. Kept apart from the patch so a test can check it against
/// the installed Bellum without loading the game.
/// </summary>
public static class BellumClientKingdomTargets
{
    /// <summary>Every kingdom Bellum founds (rebel, breakaway, restored, feud and granted realms) goes through this.</summary>
    public const string Maker = "BellumCivile.KingdomCreationSafetyHelper";

    /// <summary>Returns the new kingdom, or null when it refuses. Every caller handles null.</summary>
    public const string MakerMethod = "CreateKingdom";
}
