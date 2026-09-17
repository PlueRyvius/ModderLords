using ModderLords.Operations;
using Newtonsoft.Json.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

if (args.Length == 2 && args[0] == "--check-bootstrap")
{
    using var input = File.OpenRead(args[1]);
    using var pe = new PEReader(input);
    var metadata = pe.GetMetadataReader();
    var references = metadata.AssemblyReferences.Select(h => metadata.GetString(metadata.GetAssemblyReference(h).Name)).ToArray();
    if (references.Any(n => n.StartsWith("Coop", StringComparison.Ordinal) || n == "Common" || n.StartsWith("GameInterface", StringComparison.Ordinal)
        || n.StartsWith("ModderLords.", StringComparison.Ordinal)))
        throw new InvalidDataException("Fixture bootstrap has an eager operation or Coop dependency.");
    Console.WriteLine("Fixture bootstrap metadata verified: no eager Coop or operation dependencies.");
    return;
}

if (args.Length != 1) throw new ArgumentException("Provide a new output file for the empty diagnostic fixture plan.");
var plan = new JObject
{
    ["Contracts"] = new JArray(), ["Fingerprints"] = new JArray(), ["ModuleOrder"] = new JArray(),
    ["ContextDigest"] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("original-fixture-session.v1"))),
    ["Purpose"] = "Original fixture protocol only; no third-party contract authorized",
    ["AdmissionAdapter"] = "ModderLords.Operations.CoopJoin.v1"
};
plan["Digest"] = PlanIntegrity.Compute(plan);
if (!PlanIntegrity.Verify(plan, out var reason)) throw new InvalidDataException(reason);
using (var output = new StreamWriter(new FileStream(args[0], FileMode.CreateNew, FileAccess.Write, FileShare.Read))) output.Write(plan.ToString());
Console.WriteLine("Diagnostic fixture plan written; no third-party contract activated.");
