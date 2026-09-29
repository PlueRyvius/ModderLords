namespace ModderLords.Coop.Launch;

/// <summary>
/// Launch rules for Bannerlord Living Economy (module id BetterEconomy). ModderLords' co-op support for it lives in the
/// shared ModderLords.Compat module (CompatSync.Coop/LivingEconomy, docs/LIVING-ECONOMY-LAYER.md), which the server
/// and every player load only with Settings sync on. Without it every player's game runs its own economy and the
/// server hands players' towns, and their gold, to the AI.
/// </summary>
public static class LivingEconomyLaunchPolicy
{
    public const string ModuleId = "BetterEconomy";

    public static bool IsLivingEconomy(IEnumerable<string> moduleIds) =>
        moduleIds.Any(id => id.Equals(ModuleId, StringComparison.OrdinalIgnoreCase));

    /// <summary>Refusal: Living Economy on a server without Settings sync.</summary>
    public static string? SyncProblem(IEnumerable<string> moduleIds, bool settingsSync) =>
        !settingsSync && IsLivingEconomy(moduleIds)
            ? "Living Economy needs Settings sync on: ModderLords' Living Economy co-op support is in the ModderLords.Compat " +
              "module, which the server and every player load only with Settings sync. Without it each player's game runs its " +
              "own economy and the server lets the AI spend players' gold. Turn it on in the Server tab."
            : null;

    /// <summary>
    /// Warning: the experimental per-mod "Server-only logic" is ticked for Living Economy. The layer already does that
    /// job; the generic path would also rewrite the mod's "is this your town?" checks to "anyone's town", letting any
    /// player manage any other player's fiefs.
    /// </summary>
    public static string? ServerOnlyLogicProblem(IEnumerable<string> serverOnlyModIds) =>
        IsLivingEconomy(serverOnlyModIds)
            ? "Living Economy has its own co-op layer in ModderLords; untick Server-only logic for it. The generic path would " +
              "let any player manage another player's towns."
            : null;
}
