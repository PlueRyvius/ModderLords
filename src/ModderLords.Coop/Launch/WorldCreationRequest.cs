using ModderLords.Coop.Saves;

namespace ModderLords.Coop.Launch;

/// <summary>Read-only preflight, before any launch preparation can write files.</summary>
public static class WorldCreationRequest
{
    public static void Validate(ServerPaths paths, string name, bool hasSaveArgument)
    {
        if (hasSaveArgument)
            throw new ArgumentException("--create-world and --save are mutually exclusive");
        SavePreparer.ValidateSaveName(name);
        if (name != name.Trim() || name.EndsWith('.'))
            throw new ArgumentException("Save name must not have surrounding whitespace or a trailing dot.");
        if (File.Exists(SavePreparer.SavePath(paths, name)))
            throw new IOException($"Save '{name}' already exists; world creation never overwrites a save.");
    }
}
