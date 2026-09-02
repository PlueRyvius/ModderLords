using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using ModularCoop.Core.Modules;

namespace ModularCoop.Core.Compat;

public enum ServerVerdict
{
    /// <summary>No UI or client-only references found; expected to run on the server as-is.</summary>
    ServerSafe,
    /// <summary>References UI entry points or inquiries that the Compat guards handle.</summary>
    Guarded,
    /// <summary>References client-only assemblies (StoryMode, views, Gauntlet layers) in ways guards cannot cover.</summary>
    NeedsReview,
    /// <summary>No managed code (data-only module).</summary>
    DataOnly,
    Unknown,
}

/// <summary>What a mod's assemblies reference, read from IL metadata only (nothing is loaded or executed).</summary>
public sealed record ScanResult(
    string ModuleId,
    ServerVerdict Verdict,
    IReadOnlyList<string> UiAssemblies,
    IReadOnlyList<string> StoryModeAssemblies,
    IReadOnlyList<string> GuardedCalls,
    IReadOnlyList<string> Notes)
{
    public string Summary => Verdict switch
    {
        ServerVerdict.ServerSafe => "server-safe",
        ServerVerdict.Guarded => "guarded: " + string.Join(", ", GuardedCalls.Take(3)) + (GuardedCalls.Count > 3 ? ", …" : ""),
        ServerVerdict.NeedsReview => "needs review: " + string.Join(", ", UiAssemblies.Concat(StoryModeAssemblies).Take(3)),
        ServerVerdict.DataOnly => "data only",
        _ => "not scanned",
    };
}

public static class AssemblyScan
{
    private static readonly string[] UiAssemblyPrefixes =
    [
        "TaleWorlds.GauntletUI", "TaleWorlds.Engine.GauntletUI", "TaleWorlds.MountAndBlade.GauntletUI", "TaleWorlds.ScreenSystem",
        "TaleWorlds.MountAndBlade.View", "SandBox.View", "SandBox.GauntletUI", "TaleWorlds.TwoDimension",
        "TaleWorlds.Core.ViewModelCollection", "TaleWorlds.CampaignSystem.ViewModelCollection", "SandBox.ViewModelCollection", "TaleWorlds.MountAndBlade.ViewModelCollection",
    ];
    private static readonly string[] StoryModePrefixes = ["StoryMode", "CustomBattle"];
    private static readonly (string type, string member)[] GuardedMembers =
    [
        ("InformationManager", "ShowInquiry"), ("InformationManager", "ShowTextInquiry"), ("InformationManager", "ShowTooltip"),
        ("MBInformationManager", "ShowMultiSelectionInquiry"),
        ("ScreenManager", "PushScreen"), ("ScreenManager", "CleanAndPushScreen"), ("ScreenManager", "ReplaceTopScreen"), ("ScreenManager", "PopScreen"),
    ];
    /// <summary>References that no guard can fix: constructing UI objects the server never has.</summary>
    private static readonly string[] HardUiTypes = ["GauntletLayer", "GauntletMovie", "ScreenBase", "MapScreen", "MissionView", "SandBoxViewSubModule"];

    public static ScanResult Scan(DiscoveredModule mod)
    {
        var dlls = new List<string>();
        foreach (var bin in new[] { mod.ServerBin, mod.ClientBin })
            if (Directory.Exists(bin))
                foreach (var sub in mod.Info.SubModules)
                {
                    var p = Path.Combine(bin, sub.DLLName);
                    if (File.Exists(p) && !dlls.Contains(p, StringComparer.OrdinalIgnoreCase)) dlls.Add(p);
                }
        if (dlls.Count == 0)
            return new ScanResult(mod.Id, mod.HasCode ? ServerVerdict.Unknown : ServerVerdict.DataOnly, [], [], [], mod.HasCode ? ["submodule DLL not found"] : []);

        var ui = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var story = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var guarded = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var hard = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var notes = new List<string>();

        foreach (var dll in dlls)
        {
            try
            {
                using var fs = File.OpenRead(dll);
                using var pe = new PEReader(fs);
                if (!pe.HasMetadata) continue;
                var md = pe.GetMetadataReader();
                foreach (var h in md.AssemblyReferences)
                {
                    var name = md.GetString(md.GetAssemblyReference(h).Name);
                    if (UiAssemblyPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase))) ui.Add(name);
                    if (StoryModePrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase))) story.Add(name);
                }
                foreach (var h in md.TypeReferences)
                {
                    var tr = md.GetTypeReference(h);
                    var tn = md.GetString(tr.Name);
                    if (HardUiTypes.Contains(tn)) hard.Add(tn);
                }
                foreach (var h in md.MemberReferences)
                {
                    var mr = md.GetMemberReference(h);
                    if (mr.Parent.Kind != HandleKind.TypeReference) continue;
                    var parent = md.GetTypeReference((TypeReferenceHandle)mr.Parent);
                    var tn = md.GetString(parent.Name);
                    var mn = md.GetString(mr.Name);
                    foreach (var (t, m) in GuardedMembers) if (tn == t && mn == m) guarded.Add(tn + "." + mn);
                }
            }
            catch (Exception ex) { notes.Add(Path.GetFileName(dll) + ": " + ex.Message); }
        }

        var verdict = hard.Count > 0 || story.Count > 0 ? ServerVerdict.NeedsReview
            : ui.Count > 0 || guarded.Count > 0 ? ServerVerdict.Guarded
            : ServerVerdict.ServerSafe;
        if (hard.Count > 0) notes.Add("constructs UI objects: " + string.Join(", ", hard));
        if (story.Count > 0 && mod.Info.DependentModules.Any(d => d.Id == "StoryMode" && d.IsOptional))
            notes.Add("StoryMode dependency is declared optional; probably fine when the reference is only in StoryMode-specific code paths");
        return new ScanResult(mod.Id, verdict, ui.ToList(), story.ToList(), guarded.ToList(), notes);
    }
}
