using System.Collections.Immutable;
using System.Text.Json;
using Mono.Cecil;
using ModderLords.Analysis;

// Maintainer capture of reviewed provider identities. Does NOT approve runtime adapters: OfflineValidated and
// RuntimeValidated are never set here, so a captured contract still has to be validated before it can activate.
//
// Replaces the earlier Python capture. Contracts now pin the METHODS an adapter patches (TargetSurfaces), and only
// Cecil can read those from a provider assembly that references TaleWorlds types and cannot be loaded outside the
// game. Run it after reviewing the installed source or IL, and commit the resulting diff for review.
//
// Modules that are not installed on this machine are left exactly as they are, so a partial install updates only
// what it can actually see rather than silently dropping the rest.

var game = new DirectoryInfo(@"D:\Program Files (x86)\Steam\steamapps\common\Mount & Blade II Bannerlord\Modules");
var workshop = new DirectoryInfo(@"D:\Program Files (x86)\Steam\steamapps\workshop\content\261550");
var folders = new Dictionary<string, string>
{
    ["TAOM"] = Path.Combine(game.FullName, "TAOM"),
    ["TAOM.CoopCompat"] = Path.Combine(game.FullName, "TAOM.CoopCompat"),
    ["CoopModPatch"] = Path.Combine(workshop.FullName, "3786391685"),
    ["ImprovedGarrisons"] = Path.Combine(workshop.FullName, "2859265386"),
    ["MyLittleWarband"] = Path.Combine(workshop.FullName, "3627538517"),
    ["Europe1100"] = Path.Combine(workshop.FullName, "2968204274"),
    ["CoopNightly"] = Path.Combine(workshop.FullName, "3770450698"),
};

// Coop's own assemblies stay pinned to the file: adapters reach into them by reflection over a wide surface, so
// there is no small set of methods that would stand for the dependency.
var strictModules = new HashSet<string> { "CoopNightly" };

var destination = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
    "src", "ModderLords.Analysis", "provider-contracts.json");
destination = Path.GetFullPath(destination);
var contracts = JsonSerializer.Deserialize<ImmutableArray<OperationContract>>(File.ReadAllText(destination), AnalysisJson.Options);

var updated = new List<OperationContract>();
foreach (var contract in contracts)
{
    if (!Directory.Exists(folders.GetValueOrDefault(contract.Module, "")))
    {
        Console.WriteLine($"skip   {contract.Id}: {contract.Module} is not installed here");
        updated.Add(contract);
        continue;
    }

    var requires = contract.Requires
        .Select(r => Find(r.Module, r.Name, r.Side) is { } path
            ? r with { Sha256 = AnalysisJson.FileHash(path), Strict = strictModules.Contains(r.Module) }
            : r)
        .ToImmutableArray();

    // The provider's own assembly is the one carrying the patched methods.
    var carrier = contract.Requires.FirstOrDefault(r => r.Module == contract.Module);
    var surfaces = ImmutableArray<TargetSurface>.Empty;
    if (carrier != null && Find(carrier.Module, carrier.Name, carrier.Side) is { } carrierPath)
    {
        using var assembly = AssemblyDefinition.ReadAssembly(carrierPath);
        var read = contract.Targets.Select(t => (Target: t, Surface: CecilSurface.Read(assembly, t))).ToArray();
        foreach (var (target, surface) in read.Where(r => r.Surface == null))
            Console.WriteLine($"WARN   {contract.Id}: {target} was not found in {carrier.Name}");
        surfaces = read.Where(r => r.Surface != null).Select(r => r.Surface!).ToImmutableArray();
        foreach (var surface in surfaces)
            Console.WriteLine($"       {surface.Method}  body={surface.BodyHash[..16]}…  callers={surface.Callers}");
    }

    Console.WriteLine($"update {contract.Id}: {surfaces.Length}/{contract.Targets.Length} surfaces");
    updated.Add(contract with { Requires = requires, TargetSurfaces = surfaces });
}

File.WriteAllText(destination, JsonSerializer.Serialize(updated.ToImmutableArray(), AnalysisJson.Options) + "\n");
Console.WriteLine($"\nwrote {destination}");

string? Find(string module, string name, ExecutionSide? side)
{
    if (!folders.TryGetValue(module, out var folder)) return null;
    var wanted = side == ExecutionSide.Server ? "Win64_Shipping_Server" : "Win64_Shipping_Client";
    var path = Path.Combine(folder, "bin", wanted, name);
    if (File.Exists(path)) return path;
    path = Path.Combine(folder, "bin", "Win64_Shipping_Client", name);
    return File.Exists(path) ? path : null;
}
