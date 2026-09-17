using System.Text.Json;
using ModderLords.Analysis;
using ModderLords.Core.Compat;
using ModderLords.Core.Modules;
using ModderLords.Core.Profiles;
using ModderLords.Coop.Launch;

namespace ModderLords.Coop.Compat;

public static class OperationPreparation
{
    public const string PlanEnvironmentVariable = "MODDERLORDS_OPERATION_PLAN";
    public const string InputsEnvironmentVariable = "MODDERLORDS_OPERATION_INPUTS";
    public static string StageInputs(AnalysisReport report, string directory)
    {
        var path = Path.Combine(directory, "local-inputs.json");
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        JsonSerializer.Serialize(stream, new { report.Plan.ModuleOrder, report.Gaps,
            Files = report.LocalFiles, Values = report.Fingerprints.Where(f => f.Module == "$environment") }, AnalysisJson.Options);
        return path;
    }
    public static AnalysisRequest Resolve(Profile profile)
    {
        var paths = LaunchSession.ResolvePaths(profile);
        var catalog = LaunchSession.Scan(profile, paths, out var game);
        var selections = LaunchSession.Select(profile, catalog, new List<string>());
        var order = LoadOrder.Compute(catalog.Modules.Where(m => m.IsStock).ToList(), selections.Select(s => s.Module).ToList(), profile.Mods.Select(m => m.Id).ToList(),
            policy: profile.ManualLoadOrder ? LoadOrder.OrderPolicy.Manual : LoadOrder.OrderPolicy.Suggest);
        return OperationAnalysisService.CreateRequest(profile, new(catalog, selections, order), game ?? "", paths.ServerBin);
    }
    public static string Stage(CompatibilityPlan plan, string sessionRoot)
    {
        // Unique directory and create-new semantics: no editable preview can replace a running session's plan.
        var directory = Path.Combine(sessionRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "operations.json");
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        JsonSerializer.Serialize(stream, plan, AnalysisJson.Options);
        return path;
    }
}
