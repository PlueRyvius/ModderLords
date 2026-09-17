using System.Collections.Immutable;
using ModderLords.Analysis;
using ModderLords.Core.Modules;
using ModderLords.Core.Overlay;
using ModderLords.Core.Profiles;

namespace ModderLords.Core.Compat;

public static class OperationAnalysisService
{
    public static AnalysisRequest CreateRequest(Profile profile, ModuleSelectionResult selection, string gameRoot, string? serverBin = null)
    {
        var result = ImmutableArray.CreateBuilder<AnalysisModule>();
        foreach (var id in selection.Order.ModuleIds)
        {
            if (id == "ModularSmithing2") continue;
            var chosen = selection.Selections.FirstOrDefault(s => s.Module.Id == id);
            var stock = selection.Catalog.Modules.FirstOrDefault(m => m.IsStock && m.Id == id);
            var mod = chosen?.Module ?? stock;
            if (mod == null || mod.IsOfficial || mod.Id == "DedicatedServer.Windows") continue;
            var client = mod.IsStock ? selection.Catalog.Modules.FirstOrDefault(m => !m.IsStock && m.Id == id) ?? mod : mod;
            var pm = profile.Mods.FirstOrDefault(m => m.Id == id);
            var legacy = pm?.ServerAuthoritative == true ? AssemblyScan.Scan(client).CampaignBehaviors.Concat(AssemblyScan.Scan(client).MissionBehaviors)
                .Except(pm.ClientSideBehaviors).ToImmutableArray() : ImmutableArray<string>.Empty;
            result.Add(new(id, client.Version, client.FolderPath, client.Info.SubModules.Select(s => s.DLLName).Where(s => !string.IsNullOrEmpty(s)).Distinct().ToImmutableArray(),
                chosen?.Role != ServerRole.DependencyOnly, legacy, stock?.FolderPath));
        }
        var clientDirs = result.Select(m => Path.Combine(m.Folder, "bin", "Win64_Shipping_Client"))
            .Concat(Directory.Exists(Path.Combine(gameRoot, "Modules")) ? Directory.EnumerateDirectories(Path.Combine(gameRoot, "Modules")).Select(p => Path.Combine(p, "bin", "Win64_Shipping_Client")) : [])
            .Append(Path.Combine(gameRoot, "bin", "Win64_Shipping_Client")).Distinct().ToImmutableArray();
        var serverDirs = result.Select(m => Path.Combine(m.ServerFolder ?? m.Folder, "bin", "Win64_Shipping_Server"))
            .Concat(clientDirs).Prepend(serverBin ?? "").Where(Directory.Exists).Distinct().ToImmutableArray();
        var gameVersion = selection.Catalog.Modules.FirstOrDefault(m => m.Id == "Native")?.Version ?? "unknown";
        return new(result.ToImmutable(), clientDirs, serverDirs, gameVersion, profile.AutomaticCompatibility);
    }
    public static AnalysisReport Analyze(AnalysisRequest request, CancellationToken cancellation = default, string? cacheDirectory = null)
        => new OperationAnalyzer(cacheDirectory ?? Path.Combine(ProfileStore.RootDir, "analysis-cache")).Analyze(request, cancellation);
}
