using System.Collections.Immutable;
using System.Xml.Linq;

namespace ModderLords.Analysis;

internal sealed record AssemblyInput(string Module, string Path, ExecutionSide Side, bool IsEntry);
internal sealed record ResolvedInputs(ImmutableArray<AssemblyInput> Assemblies, ImmutableArray<InputFingerprint> Fingerprints, ImmutableArray<CoverageGap> Gaps, ImmutableArray<LocalInputFile> LocalFiles);
internal static class InputResolver
{
    public static ResolvedInputs Resolve(AnalysisRequest request, CancellationToken cancellation)
    {
        var files = new List<AssemblyInput>(); var fingerprints = new List<InputFingerprint>(); var gaps = new List<CoverageGap>();
        var localFiles = new List<LocalInputFile>();
        foreach (var module in request.Modules)
        {
            cancellation.ThrowIfCancellationRequested();
            var manifest = Path.Combine(module.Folder, "SubModule.xml");
            AddFingerprint(manifest, module.Id, "SubModule.xml", null);
            // XML/XSLT/settings affect behavior even when the managed bytes are unchanged. Never read asset payloads as code.
            foreach (var dir in new[] { module.Folder, Path.Combine(module.Folder, "ModuleData") })
                if (Directory.Exists(dir))
                    foreach (var file in Directory.EnumerateFiles(dir, "*", dir == module.Folder ? SearchOption.TopDirectoryOnly : SearchOption.AllDirectories)
                        .Where(p => Path.GetExtension(p).ToLowerInvariant() is ".xml" or ".xslt" or ".json"))
                        if (!file.Equals(manifest, StringComparison.OrdinalIgnoreCase)) AddFingerprint(file, module.Id, Path.GetRelativePath(module.Folder, file), null);
            foreach (var side in Enum.GetValues<ExecutionSide>())
            {
                if (side == ExecutionSide.Server && !module.RunsOnServer) continue;
                if (side == ExecutionSide.Server && module.ServerFolder != null && module.ServerFolder != module.Folder)
                {
                    AddFingerprint(Path.Combine(module.ServerFolder, "SubModule.xml"), module.Id, "SubModule.xml", side);
                    foreach (var dir in new[] { module.ServerFolder, Path.Combine(module.ServerFolder, "ModuleData") })
                        if (Directory.Exists(dir))
                            foreach (var file in Directory.EnumerateFiles(dir, "*", dir == module.ServerFolder ? SearchOption.TopDirectoryOnly : SearchOption.AllDirectories)
                                .Where(p => Path.GetExtension(p).ToLowerInvariant() is ".xml" or ".xslt" or ".json"))
                                AddFingerprint(file, module.Id, Path.GetRelativePath(module.ServerFolder, file), side);
                }
                var bin = Path.Combine(side == ExecutionSide.Server ? module.ServerFolder ?? module.Folder : module.Folder, "bin", side == ExecutionSide.Client ? "Win64_Shipping_Client" : "Win64_Shipping_Server");
                if (!Directory.Exists(bin) && side == ExecutionSide.Server) bin = Path.Combine(module.Folder, "bin", "Win64_Shipping_Client");
                foreach (var name in module.EntryDlls)
                {
                    var path = Path.Combine(bin, name);
                    if (!File.Exists(path)) { gaps.Add(new(module.Id, "missing-entry", $"{side}: {name}")); continue; }
                    files.Add(new(module.Id, path, side, true));
                }
                if (Directory.Exists(bin))
                {
                    foreach (var file in Directory.EnumerateFiles(bin, "*.dll"))
                        if (!files.Any(f => f.Path == file && f.Side == side)) files.Add(new(module.Id, file, side, false));
                    if (module.Id == "CoopModPatch" && Directory.Exists(Path.Combine(bin, "Adapters")))
                        files.AddRange(Directory.EnumerateFiles(Path.Combine(bin, "Adapters"), "*.dll").Select(p => new AssemblyInput(module.Id, p, side, true)));
                }
                if (module.Id == "RTSCameraUniversal")
                {
                    var parts = request.GameVersion.TrimStart('v', 'e', 'a', 'b').Split('.');
                    if (parts.Length < 2 || parts[0] != "1" || !int.TryParse(parts[1], out var minor) || minor < 2 || minor > 4) { gaps.Add(new(module.Id, "payload-unresolved", "Game version has no inspected payload selection rule")); continue; }
                    var variant = minor == 2 ? "v12" : "v13";
                    var payload = Path.Combine(module.Folder, "payloads", variant);
                    if (!Directory.Exists(payload)) payload = Path.Combine(bin, "payloads", variant);
                    if (!Directory.Exists(payload)) gaps.Add(new(module.Id, "payload-missing", variant));
                    else files.AddRange(Directory.EnumerateFiles(payload, "*.dll").Select(p => new AssemblyInput(module.Id, p, side, true)));
                }
            }
        }
        foreach (var file in files.Distinct()) AddFingerprint(file.Path, file.Module, Path.GetFileName(file.Path), file.Side);
        fingerprints.Add(new("$environment", "game-version", AnalysisJson.Hash(request.GameVersion)));
        return new(files.Distinct().ToImmutableArray(), fingerprints.Distinct().ToImmutableArray(), gaps.ToImmutableArray(), localFiles.Distinct().ToImmutableArray());

        void AddFingerprint(string path, string module, string name, ExecutionSide? side)
        {
            try
            {
                var fingerprint = new InputFingerprint(module, name.Replace('\\', '/'), AnalysisJson.FileHash(path), side);
                fingerprints.Add(fingerprint); localFiles.Add(new(fingerprint, System.IO.Path.GetFullPath(path)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { gaps.Add(new(module, "unreadable-input", name + ": " + ex.GetType().Name)); }
        }
    }
}
