namespace ModderLords.Coop.Launch;

/// <summary>
/// Launch rules for Fourberie (module id Fourberie). ModderLords' co-op support for it lives in the shared
/// ModderLords.Compat module (CompatSync.Coop/Fourberie, docs/FOURBERIE-LAYER-PLAN.md), which the server and every
/// player load only with Settings sync on. Without it the server runs one criminal empire for nobody and every
/// player's crimes vanish at the next join.
/// </summary>
public static class FourberieLaunchPolicy
{
    public const string ModuleId = "Fourberie";

    public static bool IsFourberie(IEnumerable<string> moduleIds) =>
        moduleIds.Any(id => id.Equals(ModuleId, StringComparison.OrdinalIgnoreCase));

    /// <summary>Refusal: Fourberie on a server without Settings sync.</summary>
    public static string? SyncProblem(IEnumerable<string> moduleIds, bool settingsSync) =>
        !settingsSync && IsFourberie(moduleIds)
            ? "Fourberie needs Settings sync on: ModderLords' Fourberie co-op support is in the ModderLords.Compat module, " +
              "which the server and every player load only with Settings sync. Without it nothing a player does in " +
              "Fourberie is kept and the server runs the mod for nobody. Turn it on in the Server tab."
            : null;

    /// <summary>
    /// Warning: the experimental per-mod "Server-only logic" is ticked for Fourberie. The layer does that job per player;
    /// the generic path would gate or rewrite the same code a second time.
    /// </summary>
    public static string? ServerOnlyLogicProblem(IEnumerable<string> serverOnlyModIds) =>
        IsFourberie(serverOnlyModIds)
            ? "Fourberie has its own co-op layer in ModderLords; untick Server-only logic for it."
            : null;
}
