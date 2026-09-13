namespace ModderLords.Coop.Live;

/// <summary>
/// Folds the compat database's per-mod setting defaults into a profile's host overrides for one launch.
///
/// Why: some mod features have to be off (or set a particular way) under Coop, and the switch already exists as the
/// mod's own MCM setting. TAOM's TroopWeight is the first: it trims party rosters from each peer's own simulation
/// (the server logged <c>Shed 24 bodies from 'Galden's Party'</c>), and TAOM's own co-op compat package forces
/// <c>EnableTroopWeight</c> off on every peer. It is not a campaign behaviour, so a recipe cannot gate it, but it reads
/// one setting — and settings sync already carries the server's values to every client. So the fix is a database
/// entry, applied through the override path hosts already use.
///
/// The profile wins: a property the host has explicitly overridden is never replaced, so a host who wants the feature
/// back can have it. Nothing is written to the profile's own overrides file.
/// </summary>
public static class CompatSettingsDefaults
{
    public sealed record Applied(string ModId, string SettingsId, string PropId, string Value);

    /// <summary>
    /// Returns the overrides to stage: a copy of <paramref name="profileOverrides"/> plus every default the profile does
    /// not already set, and the list of defaults that were added.
    /// </summary>
    public static (SettingsOverrides Staged, IReadOnlyList<Applied> Added) Merge(
        SettingsOverrides profileOverrides,
        IEnumerable<(string ModId, IReadOnlyDictionary<string, Dictionary<string, string>> Defaults)> modDefaults)
    {
        var staged = new SettingsOverrides { UpdatedAt = profileOverrides.UpdatedAt };
        foreach (var o in profileOverrides.Objects) staged.Set(o.Key, o.Value);
        staged.UpdatedAt = profileOverrides.UpdatedAt;

        var added = new List<Applied>();
        foreach (var (modId, defaults) in modDefaults)
        {
            foreach (var settings in defaults)
            {
                foreach (var prop in settings.Value)
                {
                    if (string.IsNullOrWhiteSpace(settings.Key) || string.IsNullOrWhiteSpace(prop.Key)) continue;
                    if (staged.Get(settings.Key, prop.Key) is not null) continue;
                    staged.Set(settings.Key, new Dictionary<string, string> { [prop.Key] = prop.Value });
                    added.Add(new Applied(modId, settings.Key, prop.Key, prop.Value));
                }
            }
        }
        if (added.Count > 0) staged.UpdatedAt = profileOverrides.UpdatedAt;
        return (staged, added);
    }
}
