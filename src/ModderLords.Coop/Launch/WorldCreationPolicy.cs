namespace ModderLords.Coop.Launch;

/// <summary>
/// Decides whether a launch should generate its campaign with the profile's mods loaded, or bootstrap it from the
/// pre-baked vanilla template — and says which, in words, either way.
///
/// The rule the whole type exists to enforce: <b>this only ever applies to a save that is being created.</b> If the
/// named save is already on disk the server loads it and nothing is generated, whatever the tick box says. Generating
/// is an extra full engine run; doing it over an existing campaign would destroy one.
///
/// <see cref="TaomLaunchPolicy"/> stays the special case above this. TAOM cannot be served from the template at all
/// (the vanilla navmesh loads instead of TAOM's), so a complete TAOM recipe generates whether or not the box is
/// ticked. Every other mod set is the user's call, which is what the box is.
/// </summary>
public static class WorldCreationPolicy
{
    /// <summary>
    /// <paramref name="ShouldCreate"/> is the answer; <paramref name="Message"/> is the line to print, or null when
    /// there is nothing worth saying (an unmodded profile bootstrapping a vanilla world is unremarkable).
    /// </summary>
    public sealed record Decision(bool ShouldCreate, string? Message);

    /// <param name="moduleIds">Enabled community modules, in profile order.</param>
    /// <param name="saveName">The profile's save name. Empty means the caller has not named one yet.</param>
    /// <param name="saveExists">Whether that save is already on disk — the load-versus-create question.</param>
    /// <param name="generateWithActiveMods">The profile's tick box.</param>
    public static Decision Decide(IEnumerable<string> moduleIds, string? saveName, Func<string, bool> saveExists,
                                  bool generateWithActiveMods)
    {
        var ids = moduleIds.ToList();

        // Loading, not creating. The earliest possible exit, so no other rule here can reach an existing campaign.
        if (!string.IsNullOrWhiteSpace(saveName) && saveExists(saveName!))
            return new Decision(false, null);

        // TAOM decides for itself; the caller consults TaomLaunchPolicy, which refuses an incomplete recipe outright.
        if (TaomLaunchPolicy.IsTaom(ids))
            return new Decision(TaomLaunchPolicy.HasCompleteRecipe(ids), null);

        if (ids.Count == 0)
            return new Decision(false, null);

        if (generateWithActiveMods)
            return new Decision(true, $"this campaign will be generated with {ids.Count} active mod(s) loaded before the server starts");

        // The quiet failure this warning exists for: a modded profile that bootstraps from default_new_game.sav gets a
        // world listing Native;SandBoxCore;Sandbox;Coop and nothing else, and nothing else in the launch says so.
        return new Decision(false,
            "WARNING a new world is about to be created from the vanilla template, so it will NOT contain your " +
            $"{ids.Count} active mod(s). Tick \"Generate a new world with the active mods\" on the Server tab to " +
            "have the launcher build the campaign with them loaded instead.");
    }
}
