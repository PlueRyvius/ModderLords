namespace ModderLords.Core.Modules;

using ModderLords.Core.Overlay;

/// <summary>
/// The answer to "which modules, in which order, from where" — everything downstream of choosing a mod set and
/// nothing about how it will be launched.
///
/// It exists so the two launch paths can share the code that exports a mod list and reconciles the player's
/// LauncherData.xml. Those used to take the dedicated server's prepared launch, which meant the player-facing half
/// of the launcher was derived from a server plan and could not run without one.
/// </summary>
public sealed record ModuleSelectionResult(
    ModuleCatalog Catalog,
    IReadOnlyList<ModSelection> Selections,
    LoadOrder.Result Order)
{
    /// <summary>
    /// Mods the profile marks <see cref="ServerRole.ClientOptional"/>: never among <see cref="Selections"/>, because
    /// the server does not load or list them, but part of what a player may run and of any list handed to players.
    /// </summary>
    public IReadOnlyList<ModSelection> ClientOptional { get; init; } = [];

    /// <summary>Their ids, for anything that must leave a player's own choice alone.</summary>
    public IReadOnlySet<string> ClientOptionalIds =>
        ClientOptional.Select(s => s.Module.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Everything a player's game may load: the selections, then the optional mods.</summary>
    public IEnumerable<ModSelection> ForPlayers => Selections.Concat(ClientOptional);

    /// <summary>Version of each selected mod, for comparing against a save's header or another player's list.</summary>
    public IReadOnlyDictionary<string, string> Versions =>
        Selections.ToDictionary(s => s.Module.Id, s => s.Module.Version, StringComparer.OrdinalIgnoreCase);
}
