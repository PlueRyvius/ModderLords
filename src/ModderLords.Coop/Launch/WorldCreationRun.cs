using ModderLords.Coop.Config;
using ModderLords.Coop.Saves;
using ModderLords.Core.Profiles;

namespace ModderLords.Coop.Launch;

/// <summary>
/// The creation pass a launch runs before hosting, when its campaign has to be generated with the profile's mods
/// loaded (TAOM always; any other mod set when the profile asks for it): the decision, then one engine run that
/// generates the save and exits. Shared by the Host tab and the in-game test harness's Coop runs, so both make a
/// world the same way. Failure throws <see cref="InvalidOperationException"/> with the reason.
/// </summary>
public static class WorldCreationRun
{
    /// <summary>The exit code of a creation pass that generated and saved the world.</summary>
    public const int CreatedExitCode = 11;

    /// <summary>What the caller shows while the pass runs. Every member is optional.</summary>
    public sealed class Hooks
    {
        /// <summary>The creation process started (the Host tab keeps it to stop, and starts its progress).</summary>
        public Action<EngineProcess>? Started { get; init; }
        /// <summary>Every line the creation process prints, on the output thread.</summary>
        public Action<EngineLine>? Line { get; init; }
        /// <summary>Exit code, whether the watchdog stopped it, whether the save was written.</summary>
        public Action<int, bool, bool>? Finished { get; init; }
    }

    /// <summary>
    /// Whether launching <paramref name="profile"/> must first generate its campaign. A named save that exists is a
    /// campaign to load, and nothing is generated over it. An empty save name gets <paramref name="newSaveName"/>
    /// (written into <paramref name="profile"/>; the caller decides whether to keep it). False also when TAOM's recipe
    /// is incomplete: Prepare then says what is missing.
    /// </summary>
    public static bool Needed(Profile profile, Func<string, bool> saveExists, Func<bool, string> newSaveName, Action<string> say)
    {
        var ids = profile.EnabledMods.Select(m => m.Id).ToList();
        var isTaom = TaomLaunchPolicy.IsTaom(ids);
        if (!isTaom && !profile.GenerateWorldWithActiveMods) return false;
        var named = !string.IsNullOrWhiteSpace(profile.SaveName);
        if (!named) profile.SaveName = newSaveName(isTaom);
        if (saveExists(profile.SaveName))
        {
            if (profile.GenerateWorldWithActiveMods || !named) say($"loading the existing save '{profile.SaveName}'; no world is being generated");
            return false;
        }
        if (!named) say($"no save was selected; creating '{profile.SaveName}' automatically");
        return !isTaom || TaomLaunchPolicy.HasCompleteRecipe(ids);
    }

    /// <summary>
    /// Runs the creation pass for <paramref name="prepared"/> (a plan prepared with world creation allowed) and waits
    /// for <paramref name="saveName"/> to be written. Diagnostics go to <paramref name="logDir"/>. On return the save
    /// exists; prepare again (without creation) to serve it.
    /// </summary>
    public static async Task RunAsync(LaunchSession.Prepared prepared, string saveName, ServerSettings server, string logDir,
        Action<string> say, Hooks? hooks = null, CancellationToken cancel = default)
    {
        // The creation phase must start with no configured save. Otherwise a stale server-config.json can make the
        // official host begin loading an older campaign before the creation hook gets control.
        ServerConfig.Write(prepared.Paths, "", server);
        var creationPlan = LaunchSession.CreateWorldPlan(prepared, saveName);
        using var diagnostics = CreationDiagnostics.Start(logDir, creationPlan, saveName);
        say($"world creation diagnostics -> {diagnostics.ManifestPath}");
        // A creation pass that is not creating anything looks exactly like a healthy server, because that is what it
        // becomes: on 2026-09-19 a profile with Server guards off set the creation variables, loaded no module that
        // reads them, and quietly served a template world for the full 15-minute budget before the watchdog killed it
        // under a connected player. These two lines are the engine saying "I am serving", which a creation pass never
        // legitimately does - a definitive signal rather than a timing guess, since heavy mods can take many minutes to
        // reach the arming hook.
        var servingInsteadOfCreating = false;
        using var creation = EngineProcess.Start(creationPlan);
        diagnostics.Attach(creation.ProcessId);
        hooks?.Started?.Invoke(creation);
        creation.LineReceived += line =>
        {
            diagnostics.Record(line);
            hooks?.Line?.Invoke(line);
            if (!servingInsteadOfCreating &&
                (line.Text.Contains("[DedicatedServer] pulse:", StringComparison.OrdinalIgnoreCase) ||
                 line.Text.Contains("player entered the campaign", StringComparison.OrdinalIgnoreCase)))
                servingInsteadOfCreating = true;
        };
        // The compat module normally exits at the same deadline. Keep a launcher-side watchdog as well so a native
        // hang cannot leave a half-started creation process behind indefinitely.
        var exit = creation.Exited;
        var watchdog = Task.Delay(TimeSpan.FromSeconds(LaunchSession.DefaultCreateWorldTimeoutSeconds + 30), cancel);
        // Polled rather than awaited on the flag: the flag is set from the output thread, and the point is to stop
        // within seconds of the engine revealing itself, not to wait out the 15-minute budget first.
        var served = Task.Run(async () =>
        {
            while (!servingInsteadOfCreating && !exit.IsCompleted && !cancel.IsCancellationRequested)
                await Task.Delay(250);
            return servingInsteadOfCreating;
        });
        var completed = await Task.WhenAny(exit, watchdog, served);
        if (completed == served && servingInsteadOfCreating && !exit.IsCompleted)
        {
            diagnostics.RecordEvent("creation-process-began-serving; no world was being created; stopping");
            say("the creation pass started serving instead of generating a world: " +
                $"nothing in the module list acted on the creation request. Check that {LaunchSession.CompatModuleId} is loaded.");
            await creation.StopAsync(TimeSpan.FromSeconds(20));
            throw new InvalidOperationException(
                "World creation did not start: the engine began hosting instead. The compat module " +
                $"({LaunchSession.CompatModuleId}) is what performs world creation, so it has to be loaded for this to work.");
        }
        var timedOut = completed != exit;
        var code = timedOut ? await creation.StopAsync(TimeSpan.FromSeconds(20)) : await exit;
        if (timedOut)
        {
            diagnostics.RecordEvent(cancel.IsCancellationRequested
                ? "cancelled; process-stop-requested"
                : $"launcher-watchdog timeout seconds={LaunchSession.DefaultCreateWorldTimeoutSeconds + 30}; process-stop-requested");
            say(cancel.IsCancellationRequested
                ? "world creation was cancelled; the creation process was stopped"
                : "world creation exceeded its 15-minute limit; the creation process was stopped");
        }
        var saveExists = SavePreparer.Exists(prepared.Paths, saveName);
        diagnostics.Complete(code, timedOut, saveExists);
        hooks?.Finished?.Invoke(code, timedOut, saveExists);
        cancel.ThrowIfCancellationRequested();
        if (code != CreatedExitCode || !saveExists)
            throw new InvalidOperationException($"World creation stopped with exit code {code}; no usable save was written.");
    }
}
