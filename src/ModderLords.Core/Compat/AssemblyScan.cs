using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using ModderLords.Core.Modules;

namespace ModderLords.Core.Compat;

public enum ServerVerdict
{
    /// <summary>No UI or client-only references found; expected to run on the server as-is.</summary>
    ServerSafe,
    /// <summary>References UI entry points or inquiries that the Compat guards handle.</summary>
    Guarded,
    /// <summary>References client-only assemblies (StoryMode, views, Gauntlet layers, desktop UI frameworks) in ways guards cannot cover.</summary>
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
    IReadOnlyList<string> Notes,
    IReadOnlyList<string> CampaignBehaviors,
    IReadOnlyList<string> MissionBehaviors,
    IReadOnlyList<string> SettingsClasses,
    bool UsesMcm,
    /// <summary>Coop assemblies this mod's SUBMODULE dlls reference; non-empty means it binds to Coop as it loads.
    /// Optional so the many places that build a ScanResult by hand do not all have to say "no".</summary>
    IReadOnlyList<string>? CoopAssemblyReferences = null,
    /// <summary>Desktop-framework assemblies (WinForms, WPF, GDI+) the server's runtime does not ship. Optional so
    /// the many places that build a ScanResult by hand do not all have to say "none".</summary>
    IReadOnlyList<string>? DesktopAssemblies = null)
{
    public string Summary => Verdict switch
    {
        ServerVerdict.ServerSafe => "server-safe",
        ServerVerdict.Guarded => "guarded: " + string.Join(", ", GuardedCalls.Take(3)) + (GuardedCalls.Count > 3 ? ", …" : ""),
        ServerVerdict.NeedsReview => "needs review: " + string.Join(", ", Blockers.Take(3)),
        ServerVerdict.DataOnly => "data only",
        _ => "not scanned",
    };

    /// <summary>Everything that makes this mod unsafe to Run headless, most fatal first. Desktop-framework
    /// references lead because they are unconditional: the assembly is simply absent from the server's runtime,
    /// so the method carrying the reference cannot be compiled at all, whatever the code path would have done.</summary>
    public IEnumerable<string> Blockers =>
        (DesktopAssemblies ?? Array.Empty<string>()).Concat(UiAssemblies).Concat(StoryModeAssemblies);

    /// <summary>What the Mod settings tab can expect from this mod: "MCM", "own settings (N values)", "MCM + own …", or "none found".</summary>
    public string SettingsSummary
    {
        get
        {
            var values = SettingsValueCount;
            var own = SettingsClasses.Count > 0 ? $"own settings ({values} value{(values == 1 ? "" : "s")})" : null;
            return UsesMcm && own is not null ? "MCM + " + own : UsesMcm ? "MCM" : own ?? (Verdict == ServerVerdict.DataOnly ? "" : "none found");
        }
    }

    /// <summary>Sum of the "(N values)" suffixes in SettingsClasses.</summary>
    public int SettingsValueCount => SettingsClasses.Sum(s =>
    {
        var i = s.LastIndexOf('(');
        return i > 0 && int.TryParse(s.Substring(i + 1).Split(' ')[0], out var n) ? n : 0;
    });
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

    /// <summary>
    /// Desktop UI frameworks. The dedicated server runs on the runtime bundled at engine\dotnet, which ships only
    /// Microsoft.NETCore.App - there is no Microsoft.WindowsDesktop.App, so none of these assemblies exist in the
    /// process. The real game does ship them (bin\Win64_Shipping_Client\Microsoft.WindowsDesktop.App), which is why
    /// a mod that reaches for one runs perfectly as a client and kills the server.
    ///
    /// This is harsher than a view-assembly reference: those resolve and only misbehave if a headless code path
    /// actually reaches them, so a guard can cover them. These do not resolve at all. A method with a WinForms call
    /// site cannot be JIT-compiled on the server even when the call is in a catch block that never runs - which is
    /// exactly how DismembermentPlus killed the server on 2026-09-19, and why it died without logging an exception:
    /// the handler that would have reported the failure is the thing that is missing.
    /// </summary>
    private static readonly string[] DesktopFrameworkPrefixes =
    [
        "System.Windows.Forms", "Microsoft.VisualBasic.Forms", "WindowsFormsIntegration",
        "PresentationFramework", "PresentationCore", "PresentationUI", "WindowsBase", "System.Windows.Presentation",
        "System.Drawing.Common", "System.Drawing.Design", "Microsoft.Win32.SystemEvents",
    ];

    /// <summary>
    /// Desktop-framework assemblies the launcher supplies to the server itself (see HookSetup), so a reference to
    /// one is reported but is not a blocker. Kept as a separate list rather than dropped from
    /// <see cref="DesktopFrameworkPrefixes"/> so that turning the passthrough off restores the warning by deleting
    /// one entry.
    ///
    /// Note what is NOT here: System.Drawing and System.Drawing.Primitives are already in the server's
    /// Microsoft.NETCore.App, so Color/Point/Rectangle arithmetic has always worked headless and never belonged on
    /// either list. Only the GDI+ half (Bitmap, Graphics, Image) is actually missing.
    /// </summary>
    private static readonly string[] SuppliedDesktopPrefixes =
    [
        "System.Drawing.Common", "Microsoft.Win32.SystemEvents",
    ];

    /// <summary>True when the launcher makes this desktop assembly available to the headless server.</summary>
    public static bool IsSuppliedOnServer(string assemblyName) =>
        SuppliedDesktopPrefixes.Any(p => assemblyName.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Coop's own assemblies. A SUBMODULE assembly that references one of these is resolved as the engine loads the
    /// module, so it has to come after Coop in the order or the resolve fails and the game dies at startup. An
    /// adapter DLL that the mod loads itself, by reflection, once Coop is present does not count -- it is never a
    /// submodule assembly, which is exactly how ModularSmithing2 stays order-independent.
    /// </summary>
    private static readonly string[] CoopAssemblyPrefixes = ["GameInterface", "Coop.Core", "Coop.Steam"];
    private static readonly (string type, string member)[] GuardedMembers =
    [
        ("InformationManager", "ShowInquiry"), ("InformationManager", "ShowTextInquiry"), ("InformationManager", "ShowTooltip"),
        ("MBInformationManager", "ShowMultiSelectionInquiry"),
        ("ScreenManager", "PushScreen"), ("ScreenManager", "CleanAndPushScreen"), ("ScreenManager", "ReplaceTopScreen"), ("ScreenManager", "PopScreen"),
    ];
    /// <summary>References that no guard can fix: constructing UI objects the server never has.</summary>
    private static readonly string[] HardUiTypes = ["GauntletLayer", "GauntletMovie", "ScreenBase", "MapScreen", "MissionView", "SandBoxViewSubModule"];

    // Mirror of the runtime heuristic in ModderLords.CompatSync.StaticSettingsSource (kept in step by hand).
    private static readonly string[] SettingsNameSuffixes = ["Settings", "Setting", "Config", "Configs", "Configuration", "Options"];
    private static readonly string[] SingletonNames = ["Instance", "Current", "Settings", "Config", "Default"];
    private static readonly string[] SettingsExcludedBases = ["ViewModel", "CampaignBehaviorBase", "MissionBehavior", "MissionLogic", "MBSubModuleBase", "ScreenBase", "GameModel", "MissionView"];
    private static readonly string[] SettingsExcludedNameParts = ["Template", "Snapshot", "Dto", "ViewModel", "VM"];
    private static readonly string[] SettingsExcludedNamespaceSegments = ["SaveData", "Saveable", "Serialization"];
    private static readonly string[] SettingsExcludedNamespaceTails = ["Data", "DataTypes"];
    private static readonly string[] SingletonOnlyExcludedSuffixes = ["Manager", "UI", "Screen", "Widget", "Behavior", "Behaviour", "Handler", "Service", "Controller", "Patch", "Patches"];
    private static readonly string[] HolderProps = ["Config", "Settings", "Configuration", "Options", "Current"];

    /// <summary>The submodule DLLs a manifest names, server bin first, each path once.</summary>
    public static IReadOnlyList<string> ModuleDlls(DiscoveredModule mod)
    {
        var dlls = new List<string>();
        foreach (var bin in new[] { mod.ServerBin, mod.ClientBin })
            if (Directory.Exists(bin))
                foreach (var sub in mod.Info.SubModules)
                {
                    var p = Path.Combine(bin, sub.DLLName);
                    if (File.Exists(p) && !dlls.Contains(p, StringComparer.OrdinalIgnoreCase)) dlls.Add(p);
                }
        return dlls;
    }

    public static ScanResult Scan(DiscoveredModule mod)
    {
        var dlls = ModuleDlls(mod);
        if (dlls.Count == 0)
            return new ScanResult(mod.Id, mod.HasCode ? ServerVerdict.Unknown : ServerVerdict.DataOnly, [], [], [], mod.HasCode ? ["submodule DLL not found"] : [], [], [], [], false, []);
        return ScanDlls(mod.Id, dlls, mod.Info.DependentModules.Any(d => d.Id == "StoryMode" && d.IsOptional));
    }

    /// <summary>Scans concrete DLL paths (the module form above resolves them from the manifest; tests pass their own).</summary>
    /// <summary>
    /// Every type name defined in these DLLs. IL metadata only; nothing is loaded or executed.
    ///
    /// Deliberately dumber than <see cref="ScanDlls"/>, which also walks TypeDefinitions but discards the list and
    /// has to be fussy about its own failures because its verdict feeds user-facing advice. This is a yes/no
    /// existence index for one question - "is the class the manifest names actually in here" - so an unreadable DLL
    /// simply contributes nothing. Kept separate on purpose: sharing the loop would couple two callers with opposite
    /// failure tolerance, and ScanDlls' loop feeds the settings-detection logic that is intricate enough already.
    /// </summary>
    public static HashSet<string> TypeNames(IReadOnlyList<string> dlls)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var dll in dlls)
        {
            try
            {
                using var fs = File.OpenRead(dll);
                using var pe = new PEReader(fs);
                if (!pe.HasMetadata) continue;
                var md = pe.GetMetadataReader();
                foreach (var h in md.TypeDefinitions)
                {
                    var td = md.GetTypeDefinition(h);
                    var ns = md.GetString(td.Namespace);
                    var name = md.GetString(td.Name);
                    names.Add(string.IsNullOrEmpty(ns) ? name : ns + "." + name);
                }
            }
            catch { /* a file we cannot read simply defines nothing we can prove */ }
        }
        return names;
    }

    /// <summary>Type names defined in a module's own submodule DLLs.</summary>
    public static HashSet<string> ModuleTypeNames(DiscoveredModule mod) => TypeNames(ModuleDlls(mod));

    public static ScanResult ScanDlls(string moduleId, IReadOnlyList<string> dlls, bool storyModeOptional = false)
    {
        var ui = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var story = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var guarded = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var hard = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var desktop = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var desktopSupplied = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var campaignBehaviors = new SortedSet<string>(StringComparer.Ordinal);
        var missionBehaviors = new SortedSet<string>(StringComparer.Ordinal);
        var settingsClasses = new SortedSet<string>(StringComparer.Ordinal);
        var usesMcm = false;
        var coopRefs = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
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
                    if (DesktopFrameworkPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                        (IsSuppliedOnServer(name) ? desktopSupplied : desktop).Add(name);
                    if (name.StartsWith("MCM", StringComparison.OrdinalIgnoreCase)) usesMcm = true;
                    if (CoopAssemblyPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase))) coopRefs.Add(name);
                }
                foreach (var h in md.TypeReferences)
                {
                    var tr = md.GetTypeReference(h);
                    var tn = md.GetString(tr.Name);
                    if (HardUiTypes.Contains(tn)) hard.Add(tn);
                }
                // Behaviour classes: direct subclasses of the engine's campaign/mission behaviour bases (deeper chains
                // inside the mod are followed one level through the mod's own type definitions).
                var defBase = new Dictionary<string, string>(StringComparer.Ordinal);
                var baseFromMcm = new HashSet<string>(StringComparer.Ordinal);
                var defs = new List<(string full, TypeDefinition td)>();
                foreach (var h in md.TypeDefinitions)
                {
                    try
                    {
                        var td = md.GetTypeDefinition(h);
                        var full = (md.GetString(td.Namespace) + "." + md.GetString(td.Name)).TrimStart('.');
                        defs.Add((full, td));
                        if (td.BaseType.IsNil) continue;
                        string? baseName = null;
                        switch (td.BaseType.Kind)
                        {
                            case HandleKind.TypeReference:
                            {
                                var btr = md.GetTypeReference((TypeReferenceHandle)td.BaseType);
                                baseName = md.GetString(btr.Name);
                                if (btr.ResolutionScope.Kind == HandleKind.AssemblyReference
                                    && md.GetString(md.GetAssemblyReference((AssemblyReferenceHandle)btr.ResolutionScope).Name).StartsWith("MCM", StringComparison.OrdinalIgnoreCase))
                                    baseFromMcm.Add(full);
                                break;
                            }
                            case HandleKind.TypeDefinition: { var bd = md.GetTypeDefinition((TypeDefinitionHandle)td.BaseType); baseName = (md.GetString(bd.Namespace) + "." + md.GetString(bd.Name)).TrimStart('.'); break; }
                            case HandleKind.TypeSpecification:
                            {
                                // Generic base (AttributeGlobalSettings<T> and friends): decode to find the open type's assembly.
                                var spec = md.GetTypeSpecification((TypeSpecificationHandle)td.BaseType).DecodeSignature(new PrimitiveSignatureProvider(md, []), null);
                                if (spec.StartsWith("ref:MCM", StringComparison.OrdinalIgnoreCase)) baseFromMcm.Add(full);
                                continue;
                            }
                            default: continue;
                        }
                        if (!td.Attributes.HasFlag(TypeAttributes.Abstract) || td.Attributes.HasFlag(TypeAttributes.Sealed)) defBase[full] = baseName;
                    }
                    catch (Exception ex) { notes.Add("type scan: " + ex.Message); }
                }
                foreach (var kv in defBase)
                {
                    var b = kv.Value;
                    if (defBase.TryGetValue(b, out var grand)) b = grand;   // one level of mod-internal inheritance
                    if (b == "CampaignBehaviorBase") campaignBehaviors.Add(kv.Key);
                    else if (b is "MissionLogic" or "MissionBehavior" or "MissionNetwork") missionBehaviors.Add(kv.Key);
                }
                var reachable = ReachableThroughHolders(md, defs);
                foreach (var (full, td) in defs)
                {
                    try
                    {
                        var n = SettingsValueCount(md, full, td, defBase, baseFromMcm, defs, reachable);
                        if (n > 0) settingsClasses.Add($"{full} ({n} value{(n == 1 ? "" : "s")})");
                    }
                    catch (Exception ex) { notes.Add("settings scan: " + ex.Message); }
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

        var verdict = desktop.Count > 0 || hard.Count > 0 || story.Count > 0 ? ServerVerdict.NeedsReview
            : ui.Count > 0 || guarded.Count > 0 ? ServerVerdict.Guarded
            : ServerVerdict.ServerSafe;
        if (desktop.Count > 0)
            notes.Add("references desktop frameworks the server's runtime does not ship (" + string.Join(", ", desktop)
                      + "); any method holding one of these call sites fails to compile headless, even in a catch block");
        if (desktopSupplied.Count > 0)
            notes.Add("references " + string.Join(", ", desktopSupplied) + ", which the launcher supplies to the server");
        if (hard.Count > 0) notes.Add("constructs UI objects: " + string.Join(", ", hard));
        if (story.Count > 0 && storyModeOptional)
            notes.Add("StoryMode dependency is declared optional; probably fine when the reference is only in StoryMode-specific code paths");
        return new ScanResult(moduleId, verdict, ui.ToList(), story.ToList(), guarded.ToList(), notes, campaignBehaviors.ToList(), missionBehaviors.ToList(), settingsClasses.ToList(), usesMcm, coopRefs.ToList(), desktop.ToList());
    }

    // ---- settings-shaped classes (metadata only) ----------------------------------------------------------------

    /// <summary>Number of public settable primitive/string/enum members on a type that passes the runtime heuristic's name and exclusion rules; 0 = not a settings class.</summary>
    /// <summary>Types returned by an instance getter named Config/Settings/... on a type that itself has a static singleton (Holder.Instance.Config).</summary>
    private static HashSet<string> ReachableThroughHolders(MetadataReader md, List<(string full, TypeDefinition td)> defs)
    {
        var reachable = new HashSet<string>(StringComparer.Ordinal);
        var provider = new PrimitiveSignatureProvider(md, defs);
        foreach (var (full, td) in defs)
        {
            var isHolder = false;
            var getters = new List<string>();
            foreach (var ph in td.GetProperties())
            {
                var pd = md.GetPropertyDefinition(ph);
                var acc = pd.GetAccessors();
                if (acc.Getter.IsNil) continue;
                MethodSignature<string> sig;
                try { sig = pd.DecodeSignature(provider, null); } catch { continue; }
                var g = md.GetMethodDefinition(acc.Getter);
                if ((g.Attributes & MethodAttributes.MemberAccessMask) != MethodAttributes.Public) continue;
                var pn = md.GetString(pd.Name);
                if (g.Attributes.HasFlag(MethodAttributes.Static)) { if (SingletonNames.Contains(pn) && sig.ReturnType == "def:" + full) isHolder = true; }
                else if (HolderProps.Contains(pn)) getters.Add(sig.ReturnType);
            }
            foreach (var fh in td.GetFields())
            {
                var fd = md.GetFieldDefinition(fh);
                if (!fd.Attributes.HasFlag(FieldAttributes.Static) || (fd.Attributes & FieldAttributes.FieldAccessMask) != FieldAttributes.Public) continue;
                string ft; try { ft = fd.DecodeSignature(provider, null); } catch { continue; }
                if (SingletonNames.Contains(md.GetString(fd.Name)) && ft == "def:" + full) isHolder = true;
            }
            if (!isHolder) continue;
            foreach (var type in getters) if (type.StartsWith("def:", StringComparison.Ordinal)) reachable.Add(type.Substring(4));
        }
        return reachable;
    }

    private static int SettingsValueCount(MetadataReader md, string full, TypeDefinition td, Dictionary<string, string> defBase, HashSet<string> baseFromMcm, List<(string full, TypeDefinition td)> defs, HashSet<string> reachable)
    {
        var attrs = td.Attributes;
        if ((attrs & TypeAttributes.ClassSemanticsMask) == TypeAttributes.Interface) return 0;
        if (!td.GetDeclaringType().IsNil) return 0;                                            // nested
        if (attrs.HasFlag(TypeAttributes.Abstract) && !attrs.HasFlag(TypeAttributes.Sealed)) return 0;
        if (md.GetString(td.Name).Contains('`')) return 0;                                      // generic
        var name = md.GetString(td.Name);
        if (name.StartsWith('<')) return 0;                                                      // compiler-generated
        if (SettingsExcludedNameParts.Any(p => name.Contains(p, StringComparison.Ordinal))) return 0;
        var ns = md.GetString(td.Namespace);
        var segments = ns.Split('.');
        if (segments.Any(s => SettingsExcludedNamespaceSegments.Contains(s, StringComparer.OrdinalIgnoreCase))) return 0;
        if (SettingsExcludedNamespaceTails.Contains(segments[^1], StringComparer.OrdinalIgnoreCase)) return 0;
        if (baseFromMcm.Contains(full)) return 0;
        // Base chain (one level inside the mod, then the external base name).
        if (defBase.TryGetValue(full, out var b))
        {
            if (SettingsExcludedBases.Contains(b) || SettingsExcludedBases.Contains(b[(b.LastIndexOf('.') + 1)..])) return 0;
            if (defBase.TryGetValue(b, out var grand) && (SettingsExcludedBases.Contains(grand) || SettingsExcludedBases.Contains(grand[(grand.LastIndexOf('.') + 1)..]))) return 0;
            if (baseFromMcm.Contains(b)) return 0;
            if (IsEnumBase(md, td)) return 0;
        }
        foreach (var ah in td.GetCustomAttributes())
        {
            var an = AttributeName(md, md.GetCustomAttribute(ah));
            if (an is "SaveableClassAttribute" or "SaveableRootClassAttribute") return 0;
        }

        var nameMatch = SettingsNameSuffixes.Any(s => name.EndsWith(s, StringComparison.OrdinalIgnoreCase));
        if (!nameMatch && SingletonOnlyExcludedSuffixes.Any(s => name.EndsWith(s, StringComparison.Ordinal))) return 0;
        var hasSingleton = false;
        var hasStatic = false;
        var count = 0;
        var provider = new PrimitiveSignatureProvider(md, defs);
        foreach (var ph in td.GetProperties())
        {
            var pd = md.GetPropertyDefinition(ph);
            var acc = pd.GetAccessors();
            var pn = md.GetString(pd.Name);
            MethodSignature<string> sig;
            try { sig = pd.DecodeSignature(provider, null); } catch { continue; }
            if (!acc.Getter.IsNil && SingletonNames.Contains(pn) && md.GetMethodDefinition(acc.Getter).Attributes.HasFlag(MethodAttributes.Static) && sig.ReturnType == "def:" + full) hasSingleton = true;
            if (acc.Getter.IsNil || acc.Setter.IsNil) continue;
            var g = md.GetMethodDefinition(acc.Getter);
            var s = md.GetMethodDefinition(acc.Setter);
            if ((g.Attributes & MethodAttributes.MemberAccessMask) != MethodAttributes.Public || (s.Attributes & MethodAttributes.MemberAccessMask) != MethodAttributes.Public) continue;
            if (sig.ParameterTypes.Length > 0) continue;                                        // indexer
            if (provider.IsSupported(sig.ReturnType)) { count++; if (s.Attributes.HasFlag(MethodAttributes.Static)) hasStatic = true; }
        }
        foreach (var fh in td.GetFields())
        {
            var fd = md.GetFieldDefinition(fh);
            var fa = fd.Attributes;
            if ((fa & FieldAttributes.FieldAccessMask) != FieldAttributes.Public || fa.HasFlag(FieldAttributes.InitOnly) || fa.HasFlag(FieldAttributes.Literal)) continue;
            string ft;
            try { ft = fd.DecodeSignature(provider, null); } catch { continue; }
            var fn = md.GetString(fd.Name);
            if (fa.HasFlag(FieldAttributes.Static) && SingletonNames.Contains(fn) && ft == "def:" + full) hasSingleton = true;
            if (provider.IsSupported(ft)) { count++; if (fa.HasFlag(FieldAttributes.Static)) hasStatic = true; }
        }
        // Same reach rule as the runtime: static members, a singleton of its own type, or Holder.Instance.<Config>.
        if (!(nameMatch || hasSingleton)) return 0;
        if (!(hasStatic || hasSingleton || reachable.Contains(full))) return 0;
        return count;
    }

    private static bool IsEnumBase(MetadataReader md, TypeDefinition td)
    {
        if (td.BaseType.Kind != HandleKind.TypeReference) return false;
        return md.GetString(md.GetTypeReference((TypeReferenceHandle)td.BaseType).Name) == "Enum";
    }

    private static string AttributeName(MetadataReader md, CustomAttribute ca)
    {
        try
        {
            switch (ca.Constructor.Kind)
            {
                case HandleKind.MemberReference:
                {
                    var parent = md.GetMemberReference((MemberReferenceHandle)ca.Constructor).Parent;
                    return parent.Kind == HandleKind.TypeReference ? md.GetString(md.GetTypeReference((TypeReferenceHandle)parent).Name) : "";
                }
                case HandleKind.MethodDefinition:
                    return md.GetString(md.GetTypeDefinition(md.GetMethodDefinition((MethodDefinitionHandle)ca.Constructor).GetDeclaringType()).Name);
            }
        }
        catch { }
        return "";
    }

    /// <summary>Decodes signatures to a few tokens: bool / int / float / string / enum, "def:&lt;full&gt;" for a type defined in the same DLL, or "other".</summary>
    private sealed class PrimitiveSignatureProvider : ISignatureTypeProvider<string, object?>
    {
        private readonly HashSet<string> _enumDefs;

        public PrimitiveSignatureProvider(MetadataReader md, List<(string full, TypeDefinition td)> defs)
        {
            _enumDefs = new HashSet<string>(defs.Where(d => IsEnumBase(md, d.td)).Select(d => d.full), StringComparer.Ordinal);
        }

        public bool IsSupported(string t) => t is "bool" or "int" or "float" or "string" or "enum";

        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode switch
        {
            PrimitiveTypeCode.Boolean => "bool",
            PrimitiveTypeCode.String => "string",
            PrimitiveTypeCode.Int32 or PrimitiveTypeCode.Int64 or PrimitiveTypeCode.Int16 or PrimitiveTypeCode.Byte or PrimitiveTypeCode.UInt32 or PrimitiveTypeCode.UInt64 or PrimitiveTypeCode.UInt16 or PrimitiveTypeCode.SByte => "int",
            PrimitiveTypeCode.Single or PrimitiveTypeCode.Double => "float",
            _ => "other",
        };
        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
        {
            var td = reader.GetTypeDefinition(handle);
            var full = (reader.GetString(td.Namespace) + "." + reader.GetString(td.Name)).TrimStart('.');
            if (_enumDefs.Contains(full)) return "enum";
            return "def:" + full;
        }
        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
        {
            var tr = reader.GetTypeReference(handle);
            var asm = tr.ResolutionScope.Kind == HandleKind.AssemblyReference ? reader.GetString(reader.GetAssemblyReference((AssemblyReferenceHandle)tr.ResolutionScope).Name) : "";
            return "ref:" + asm + ":" + reader.GetString(tr.Name);
        }
        public string GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind) => "other";
        public string GetSZArrayType(string elementType) => "other";
        public string GetArrayType(string elementType, ArrayShape shape) => "other";
        public string GetByReferenceType(string elementType) => "other";
        public string GetPointerType(string elementType) => "other";
        public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) => genericType;
        public string GetGenericMethodParameter(object? genericContext, int index) => "other";
        public string GetGenericTypeParameter(object? genericContext, int index) => "other";
        public string GetFunctionPointerType(MethodSignature<string> signature) => "other";
        public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;
        public string GetPinnedType(string elementType) => elementType;
    }
}
