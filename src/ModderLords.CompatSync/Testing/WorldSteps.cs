using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.CharacterCreationContent;
using TaleWorlds.CampaignSystem.GameState;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;
using TaleWorlds.SaveSystem.Load;

namespace ModderLords.Testing;

/// <summary>
/// Steps that make, load and leave a world from the main menu, for single-player runs (side "game"), where there is no
/// server to load one and no join to wait for. Each does what the main menu does, by code (docs/INGAME-TEST-HARNESS.md,
/// decisions 15 and 22). SandBox is reached by reflection: this module references only the game's core assemblies.
/// </summary>
internal static class WorldSteps
{
    private const string StageViewModel = "TaleWorlds.CampaignSystem.ViewModelCollection.CharacterCreation.CharacterCreationStageBaseVM";

    internal static void Register()
    {
        InGameTests.Register("game.createWorld", StepSide.Client, CreateWorld);
        InGameTests.Register("game.loadSave", StepSide.Client, LoadSave);
        InGameTests.Register("game.toMainMenu", StepSide.Client, ToMainMenu);
        InGameTests.Register("game.save", StepSide.Client, Save);
        InGameTests.Register("game.keepWorld", StepSide.Client, KeepWorld);
    }

    // ---- game.save / game.keepWorld ---------------------------------------------------------------------------

    /// <summary>
    /// Saves the campaign under <c>name</c>, as the escape menu's Save As does, and waits until it is written. With
    /// <c>game.toMainMenu</c> and <c>game.loadSave</c> after it, a single-player suite saves and reloads (decision 16).
    /// </summary>
    private static IEnumerable<StepWait?> Save(StepContext ctx)
    {
        var name = ctx.Arg("name");
        foreach (var wait in SaveAs(ctx, name)) yield return wait;
        ctx.Report("save", name);
    }

    /// <summary>
    /// Saves the campaign and asks the launcher to keep it under test-worlds when the run ends (decision 27): as
    /// <c>name</c>, or with <c>upgradeOf</c> a module id, as that module's upgrade world
    /// <c>upgrade-&lt;ModId&gt;-&lt;its loaded version&gt;</c>. Reports <c>keptWorld</c> and <c>keptSave</c>, which the
    /// launcher reads; a later failure in the run does not stop the copy, since this step passed.
    /// </summary>
    private static IEnumerable<StepWait?> KeepWorld(StepContext ctx)
    {
        string world;
        if (ctx.HasArg("upgradeOf"))
        {
            var id = ctx.Arg("upgradeOf");
            var module = TaleWorlds.ModuleManager.ModuleHelper.GetModules().FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));
            ctx.Assert(module != null, $"module {id} is not loaded, so there is no version to keep an upgrade world of");
            // The game's version string carries a change set ("v0.9.49.0"); the mod's own SubModule.xml says v0.9.49.
            world = "upgrade-" + module!.Id + "-" + System.Text.RegularExpressions.Regex.Replace(module.Version.ToString(), @"^(\D*\d+\.\d+\.\d+)\.0$", "$1");
        }
        else world = ctx.Arg("name");
        ctx.Assert(world.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) < 0, $"\"{world}\" is not a file name");
        var save = "mltest-keep-" + world;
        foreach (var wait in SaveAs(ctx, save)) yield return wait;
        ctx.Report("keptSave", save);
        ctx.Report("keptWorld", world);
        ctx.Report("value", world);
    }

    private static IEnumerable<StepWait?> SaveAs(StepContext ctx, string name)
    {
        ctx.Assert(Campaign.Current != null, "there is no campaign to save");
        Campaign.Current!.TimeControlMode = CampaignTimeControlMode.Stop;
        Campaign.Current.SaveHandler.SaveAs(name);
        yield return null;
        yield return ctx.Until(() => !Campaign.Current.SaveHandler.IsSaving && MBSaveLoad.IsSaveGameFileExists(name), $"the save {name} to be written", TimeSpan.FromMinutes(3));
    }

    private static string ActiveStateName => GameStateManager.Current?.ActiveState?.GetType().Name ?? "";

    private static bool OnMap => Campaign.Current != null && GameStateManager.Current?.ActiveState is MapState;

    /// <summary>The main menu, held still: other mods' start-up work has finished before a game is started from it.</summary>
    private static StepWait MainMenu(StepContext ctx) =>
        ctx.Settled(() => Campaign.Current == null ? ActiveStateName : "a campaign", "the main menu", "InitialState", TimeSpan.FromSeconds(3), TimeSpan.FromMinutes(5));

    private static Type SandBoxType(string name) =>
        AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(name, false)).FirstOrDefault(t => t != null)
        ?? throw new StepFailedException($"{name} is not loaded: is the SandBox module in this launch?");

    // ---- game.createWorld --------------------------------------------------------------------------------------

    /// <summary>
    /// A new Sandbox campaign, as the main menu's Sandbox button starts one, then character creation by pressing Next
    /// on each page the way a player would: the page's own view-model decides whether it can advance, and when it
    /// can't, the first choice is taken (and an empty name filled). Pages a mod adds are handled the same way when
    /// they are built like the game's own. Saves under <c>name</c> once on the map. Reports each page it passed.
    /// </summary>
    private static IEnumerable<StepWait?> CreateWorld(StepContext ctx)
    {
        var name = ctx.Arg("name");
        var overwrite = ctx.Arg("overwrite", "false") == "true";
        ctx.Assert(overwrite || !MBSaveLoad.IsSaveGameFileExists(name), $"a save named {name} already exists (give overwrite: true to replace it)");
        yield return MainMenu(ctx);

        var manager = SandBoxType("SandBox.SandBoxGameManager");
        var creatorType = manager.GetNestedType("CampaignCreatorDelegate")
            ?? throw new StepFailedException("SandBoxGameManager.CampaignCreatorDelegate is missing: this game version starts a campaign differently");
        var creator = Delegate.CreateDelegate(creatorType, typeof(WorldSteps).GetMethod(nameof(NewCampaign), BindingFlags.NonPublic | BindingFlags.Static)!);
        MBGameManager.StartNewGame((MBGameManager)Activator.CreateInstance(manager, creator));
        ctx.Log("started a new Sandbox campaign");

        var pages = new List<string>();
        var driver = new CreationDriver(ctx, pages);
        var nextLook = DateTime.MinValue;
        while (!OnMap)
        {
            yield return null;
            // Twice a second: the screens build their view-models over a few frames after each change.
            if (DateTime.UtcNow < nextLook) continue;
            nextLook = DateTime.UtcNow.AddSeconds(0.5);
            var state = GameStateManager.Current?.ActiveState;
            if (state is VideoPlaybackState video)
            {
                video.OnVideoFinished();
                ctx.Log("skipped the campaign intro video");
            }
            else if (state is CharacterCreationState creation)
            {
                driver.Step(creation);
            }
        }
        ctx.Dump("creation-pages", string.Join(Environment.NewLine, pages));
        ctx.Report("pages", pages.Count);
        ctx.Log($"character creation done in {pages.Count} page(s); on the campaign map");

        yield return ctx.Settled(() => OnMap, "the campaign map to settle", true, TimeSpan.FromSeconds(3));
        Campaign.Current.TimeControlMode = CampaignTimeControlMode.Stop;
        Campaign.Current.SaveHandler.SaveAs(name);
        yield return ctx.Until(() => !Campaign.Current.SaveHandler.IsSaving && MBSaveLoad.IsSaveGameFileExists(name), $"the save {name} to be written", TimeSpan.FromMinutes(3));
        ctx.Report("save", name);
        ctx.Report("heroName", Hero.MainHero?.Name?.ToString());
        ctx.Report("culture", Hero.MainHero?.Culture?.StringId);
    }

    private static Campaign NewCampaign() => new Campaign(CampaignGameMode.Campaign);

    /// <summary>Presses Next on the current character-creation page, choosing a default first when the page needs one.</summary>
    private sealed class CreationDriver
    {
        private readonly StepContext _ctx;
        private readonly List<string> _pages;
        private string _page = "";
        private int _looks;
        private bool _defaultsTried;

        public CreationDriver(StepContext ctx, List<string> pages) { _ctx = ctx; _pages = pages; }

        public void Step(CharacterCreationState state)
        {
            var manager = state.CharacterCreationManager;
            var index = manager.GetIndexOfCurrentStage();
            var stage = manager.CurrentStage?.GetType().Name ?? "no stage";
            var page = $"{index + 1}/{manager.GetTotalStagesCount()} {stage}" + (manager.CurrentStage is CharacterCreationNarrativeStage && manager.CurrentMenu != null ? " menu " + manager.CurrentMenu.StringId : "");
            if (page != _page) { _page = page; _looks = 0; _defaultsTried = false; }
            _looks++;

            // The page's view listens to its stage (CharacterCreationScreen sets stage.Listener), so it is the current one.
            var view = manager.CurrentStage?.Listener;
            if (view == null)
            {
                // The screen builds the page's view a few frames after the stage starts.
                if (_looks > 20) throw new StepFailedException($"character creation page {page} has no view to press Next on (a page with its own screen?); make this world by hand and record it");
                return;
            }
            // Next calls the page's view-model, which does the page's work and then the view's NextStage. A page with
            // no stage view-model (the face editor's) is advanced through its view alone, as its own Next does.
            var vm = FindStageViewModel(view);
            if (vm == null)
            {
                if (_looks < 3) return;
                _pages.Add($"{page} ({view.GetType().Name}, through its view)");
                Invoke<object>(view, "NextStage");
                return;
            }
            var vmType = vm.GetType();
            if (Invoke<bool>(vm, "CanAdvanceToNextStage"))
            {
                _pages.Add($"{page} ({vmType.Name}{(_defaultsTried ? ", default chosen" : "")})");
                Invoke<object>(vm, "OnNextStage");
                return;
            }
            if (!_defaultsTried)
            {
                _defaultsTried = true;
                var chose = ChooseDefaults(vm);
                _ctx.Log($"page {page}: chose {(chose.Length > 0 ? chose : "nothing (no choice found)")}");
                return;
            }
            if (_looks > 6)
                throw new StepFailedException($"character creation page {page} ({vmType.Name}) won't advance after choosing defaults; make this world by hand and record it");
        }

        /// <summary>
        /// The first entry of each list the page offers, through the page's own selection method (OnCultureSelection,
        /// OnOptionSelected: a public method taking one list entry), and a fixed name for an empty name field.
        /// </summary>
        private static string ChooseDefaults(object vm)
        {
            var chosen = new List<string>();
            var type = vm.GetType();
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                var parameters = method.GetParameters();
                if (parameters.Length != 1 || !method.Name.StartsWith("On", StringComparison.Ordinal) || method.Name.IndexOf("Select", StringComparison.Ordinal) < 0) continue;
                var itemType = parameters[0].ParameterType;
                foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (property.GetIndexParameters().Length > 0 || !typeof(IEnumerable).IsAssignableFrom(property.PropertyType) || property.PropertyType == typeof(string)) continue;
                    if (!(property.GetValue(vm) is IEnumerable list)) continue;
                    var first = list.Cast<object?>().FirstOrDefault(item => item != null && itemType.IsInstanceOfType(item));
                    if (first == null) continue;
                    method.Invoke(vm, new[] { first });
                    chosen.Add($"{method.Name}(first of {property.Name})");
                    break;
                }
            }
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (property.PropertyType != typeof(string) || !property.CanWrite || !property.Name.EndsWith("Name", StringComparison.Ordinal)) continue;
                if (!string.IsNullOrWhiteSpace(property.GetValue(vm) as string)) continue;
                var value = property.Name.IndexOf("Clan", StringComparison.Ordinal) >= 0 ? "Testers" : "Tester";
                property.SetValue(vm, value);
                chosen.Add($"{property.Name}={value}");
            }
            return string.Join(", ", chosen);
        }

        /// <summary>
        /// The page's view-model: a field (or a field of a field) of the page's view holding a stage view-model. The
        /// game's own pages and pages built on its base class are found this way; the view's type is not assumed.
        /// </summary>
        private static object? FindStageViewModel(object? view)
        {
            if (view == null) return null;
            var seen = new HashSet<object>(ReferenceComparer.Instance);
            var queue = new Queue<(object Value, int Depth)>();
            queue.Enqueue((view, 0));
            while (queue.Count > 0)
            {
                var (value, depth) = queue.Dequeue();
                if (!seen.Add(value)) continue;
                if (IsStageViewModel(value.GetType())) return value;
                if (depth >= 3) continue;
                for (var t = value.GetType(); t != null && t != typeof(object); t = t.BaseType)
                    foreach (var field in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    {
                        if (field.FieldType.IsValueType || field.FieldType == typeof(string) || field.FieldType.IsArray) continue;
                        object? child;
                        try { child = field.GetValue(value); } catch { continue; }
                        if (child != null) queue.Enqueue((child, depth + 1));
                    }
            }
            return null;
        }

        private static bool IsStageViewModel(Type? type)
        {
            for (; type != null; type = type.BaseType)
                if (type.FullName == StageViewModel) return true;
            return false;
        }
    }

    // ---- game.loadSave ----------------------------------------------------------------------------------------

    /// <summary>
    /// Loads the save <c>name</c> as the Load Game screen does, without its pop-ups: module differences the screen
    /// would warn about are logged instead (and reported as <c>moduleWarnings</c>), then the game waits for the map.
    /// </summary>
    private static IEnumerable<StepWait?> LoadSave(StepContext ctx)
    {
        var name = ctx.Arg("name");
        yield return MainMenu(ctx);
        var info = MBSaveLoad.GetSaveFileWithName(name);
        ctx.Assert(info != null, $"there is no save named {name} in this game's saves");

        var warnings = new List<string>();
        try
        {
            var check = SandBoxType("SandBox.SandBoxSaveHelper").GetMethod("CheckMetaDataCompatibilityErrors", BindingFlags.Public | BindingFlags.Static);
            if (check?.Invoke(null, new object[] { info!.MetaData }) is IEnumerable results)
                foreach (var r in results) warnings.Add(Describe(r));
        }
        catch (Exception ex) { ctx.Log("could not check the save's modules: " + ex.GetBaseException().Message); }
        foreach (var w in warnings) ctx.Log("module difference: " + w);
        ctx.Report("moduleWarnings", warnings.Count);

        var loaded = MBSaveLoad.LoadSaveGameData(name);
        ctx.Assert(loaded != null && loaded.Successful,
            $"the save {name} did not load: " + (loaded?.Errors == null ? "no reason given" : string.Join("; ", loaded.Errors.Select(e => e.ToString()))));
        MBGameManager.StartNewGame((MBGameManager)Activator.CreateInstance(SandBoxType("SandBox.SandBoxGameManager"), loaded));
        ctx.Log($"loading {name}");
        yield return ctx.Until(() => OnMap, "the campaign map", TimeSpan.FromMinutes(10));
        ctx.Report("save", name);
    }

    /// <summary>A ModuleCheckResult as text, by its public properties (its shape differs between game versions).</summary>
    private static string Describe(object result)
    {
        var text = new StringBuilder();
        foreach (var p in result.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (p.GetIndexParameters().Length > 0) continue;
            if (text.Length > 0) text.Append(", ");
            text.Append(p.Name).Append('=').Append(p.GetValue(result));
        }
        foreach (var f in result.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            if (text.Length > 0) text.Append(", ");
            text.Append(f.Name).Append('=').Append(f.GetValue(result));
        }
        return text.Length > 0 ? text.ToString() : result.ToString() ?? "";
    }

    // ---- game.toMainMenu --------------------------------------------------------------------------------------

    /// <summary>Leaves the campaign for the main menu without saving, as the escape menu's Exit to main menu does.</summary>
    private static IEnumerable<StepWait?> ToMainMenu(StepContext ctx)
    {
        if (Campaign.Current != null) MBGameManager.EndGame();
        yield return MainMenu(ctx);
    }

    // ---- reflection -------------------------------------------------------------------------------------------

    private static T Invoke<T>(object target, string method)
    {
        var m = target.GetType().GetMethod(method, BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null)
            ?? throw new StepFailedException($"{target.GetType().Name} has no {method}()");
        return (T)m.Invoke(target, null)!;
    }

    private sealed class ReferenceComparer : IEqualityComparer<object>
    {
        public static readonly ReferenceComparer Instance = new ReferenceComparer();
        public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);
        public int GetHashCode(object obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
    }
}
