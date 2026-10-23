namespace BetterEconomyCoopCompat;

// Optional extras for a launcher. Nothing here is needed: without it the module works on its own. A launcher finds
// this class by name ("BetterEconomyCoopCompat.Hooks") and checks Version before using anything.
public static class Hooks
{
    /// <summary>Goes up when anything below changes shape.</summary>
    public const int Version = 1;

    /// <summary>A short line of what the module did this session, for a launcher's log. Empty when it isn't running.</summary>
    public static string Summary() => Layer.Summary();
}
