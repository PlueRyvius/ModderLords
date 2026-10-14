namespace ModderLords.CompatSync.Coop;

/// <summary>
/// What a co-op player's game may do with kingdom decisions on its own. Kept free of game types so the rules are
/// tested without the game; <see cref="ClientKingdomDecisions"/> applies them.
/// </summary>
public static class ClientKingdomDecisionsPolicy
{
    /// <summary>
    /// True when a decision added on a player's game must not go into that game's kingdom.
    /// <para>
    /// Coop's client adds a decision to its own list first and then asks the server for it. That is right for the
    /// player's own proposal. A decision some other clan proposed was made by this game's own copy of a mod's AI (Bellum
    /// Civile's deliberations, for one): the server makes its own, which reaches every player through Coop, so this one
    /// is at best a duplicate. When Coop cannot send it at all (a mod's own decision type) it stays on this game alone.
    /// Either way this game's list no longer matches the server's, and Coop refers to decisions by their position in
    /// that list.
    /// </para>
    /// </summary>
    /// <param name="coopClient">This is a player's game in a co-op session.</param>
    /// <param name="coopApplying">Coop's own scope that lets the original run (it is applying something the server sent).</param>
    /// <param name="proposedByPlayerClan">The decision's proposer is this player's clan.</param>
    public static bool RefuseAdd(bool coopClient, bool coopApplying, bool proposedByPlayerClan) =>
        coopClient && !coopApplying && !proposedByPlayerClan;

    /// <summary>
    /// True when a player's game should not demand that its player settle a decision.
    /// <para>
    /// The game asks a ruler to settle a decision whose time has run out, and until they do the map reopens the Kingdom
    /// screen every frame and its prompt has no Cancel. Under Coop the server settles every decision: Coop's vote round,
    /// and the AI for whoever has not voted by its deadline. A player's game never settles one itself, so the demand can
    /// only be met by the server's result arriving. When this game's list does not match the server's, it never does.
    /// </para>
    /// </summary>
    public static bool WaiveResolution(bool coopClient, bool needsPlayerResolution) => coopClient && needsPlayerResolution;
}
