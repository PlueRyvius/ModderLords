namespace ModderLords.Core.Launch;

/// <summary>
/// How to start the player's game with an exact module set, bypassing the TaleWorlds launcher.
///
/// VERIFIED 2026-09-06 against Bannerlord v1.4.8.119303: Bannerlord.exe takes the same
/// _MODULES_*A*B*_MODULES_ token the TaleWorlds launcher builds, the ids in it are resolved against BOTH the
/// game's Modules folder and the Steam Workshop content folder, and the token completely replaces whatever
/// Configs\LauncherData.xml says. Unique Workshop IDs resolve directly; ambiguous copies use a private view
/// with physical module links because the engine chooses the first matching ID, ahead of Workshop copies.
/// </summary>
public sealed record ClientLaunchPlan
{
    public required string GameRoot { get; init; }

    /// <summary>Module ids in load order. Order is significant and is passed through exactly as given.</summary>
    public required IReadOnlyList<string> ModuleIds { get; init; }
    public IReadOnlyList<Modules.DiscoveredModule> SelectedModules { get; init; } = [];
    public IReadOnlyList<string> MissingModules { get; init; } = [];
    public bool RequiresIsolatedView { get; init; }
    public ModderLords.Analysis.AnalysisRequest? OperationInputs { get; init; }

    public string WorkingDirectory => GamePaths.ClientBin(GameRoot);
    public string Exe => Path.Combine(WorkingDirectory, ClientLauncher.GameExeName);

    /// <summary>The engine's module list argument, e.g. <c>_MODULES_*Native*SandBoxCore*_MODULES_</c>.</summary>
    public string ModuleToken => "_MODULES_*" + string.Join("*", ModuleIds) + "*_MODULES_";

    public IReadOnlyList<string> Arguments => [ModuleToken];

    public IEnumerable<string> Validate()
    {
        if (!Directory.Exists(GameRoot)) yield return $"Game install not found: {GameRoot}";
        else if (!File.Exists(Exe)) yield return $"Missing game executable: {Exe}";
        if (ModuleIds.Count == 0) yield return "No modules selected.";
        foreach (var id in ModuleIds.Where(id => id.Contains('*')))
            yield return $"Module id contains '*', which separates ids in the launch argument: {id}";
    }

    /// <summary>The command line, for the console and for bug reports. Nothing secret goes on it.</summary>
    public string Describe() => $"\"{Exe}\" {ModuleToken}";
}
