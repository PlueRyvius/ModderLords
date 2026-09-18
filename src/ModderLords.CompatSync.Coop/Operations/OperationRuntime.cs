using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using ModderLords.Operations;
using Newtonsoft.Json.Linq;

namespace ModderLords.CompatSync.Coop.Operations;

public static class OperationRuntime
{
    public const int ProtocolVersion = 1;
    public static SessionActivation Activation { get; private set; } = new SessionActivation();
    private static string[]? runtimeModuleOrder;
    private static JArray? runtimeFingerprints;
    private static int assembliesChanged;
    private static void AssemblyLoaded(object sender, AssemblyLoadEventArgs args) => Interlocked.Exchange(ref assembliesChanged, 1);
    public static bool CheckReadiness()
    {
        if (Failure.Length > 0) return false;
        try
        {
        if (runtimeModuleOrder != null && !ModuleOrderAttestation.Validate(runtimeModuleOrder, TaleWorlds.ModuleManager.ModuleHelper.GetActiveModules().Select(m => m.Id), out var orderReason))
        { Failure = orderReason; return false; }
        if (runtimeFingerprints == null || Interlocked.Exchange(ref assembliesChanged, 0) == 0) return true;
            var loaded = AppDomain.CurrentDomain.GetAssemblies().Where(a => !a.IsDynamic).Select(a => new KeyValuePair<string, string>(a.GetName().Name, a.Location));
            if (!LoadedAssemblyAttestation.Validate(runtimeFingerprints, Common.ModInformation.IsServer ? "Server" : "Client", loaded, HashFile, out var reason))
            { Failure = reason; Log.Warn("Operation readiness lost: " + reason); return false; }
            return true;
        }
        catch (Exception ex) { Failure = "Loaded inputs cannot be verified: " + ex.GetBaseException().Message; return false; }
    }
    private static readonly List<ICompatibilityAdapter> Adapters = new List<ICompatibilityAdapter>();
    internal static CoopJoinBarrier? JoinBarrier { get; private set; }
    internal static void EnsureJoinBarrier()
    {
        if (JoinBarrier != null) return;
        var barrier = new CoopJoinBarrier(); barrier.Install(); JoinBarrier = barrier;
    }
    public static string PlanJson { get; private set; } = "";
    public static bool SessionActive => Activation.Digest != null;
    public static string Failure { get; private set; } = "";
    public static void InitializeFromEnvironment()
    {
        var path = Environment.GetEnvironmentVariable("MODDERLORDS_OPERATION_PLAN");
        if (!string.IsNullOrEmpty(path) || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MODDERLORDS_OPERATION_INPUTS"))) EnsureJoinBarrier();
        if (string.IsNullOrEmpty(path)) return;
        if (!TryActivate(File.ReadAllText(path), out var reason)) throw new InvalidOperationException("Operation plan activation refused: " + reason);
    }
    public static bool TryActivate(string json, out string reason)
    {
        reason = "";
        if (json == null || json.Length > 1024 * 1024) { reason = "Plan exceeds size limit"; return false; }
        try
        {
            var plan = PlanIntegrity.Parse(json);
            if (!PlanIntegrity.Verify(plan, out reason)) throw new InvalidOperationException(reason);
            var digest = (string)plan["Digest"]!;
            if (Activation.Digest != null)
            {
                if (Activation.Digest != digest) throw new InvalidOperationException("Session plan is immutable");
                if (!CheckReadiness()) throw new InvalidOperationException(Failure);
                return true;
            }
            if (Activation.CampaignStarted) throw new InvalidOperationException("Plan arrived after campaign initialization");
            var catalog = ReadCatalog();
            var active = ((JArray?)plan["Contracts"] ?? new JArray()).OfType<JObject>().Where(c => (string?)c["Decision"] == "Activate").ToArray();
            if (active.Length > 0)
            {
                var inputsPath = Environment.GetEnvironmentVariable("MODDERLORDS_OPERATION_INPUTS");
                if (string.IsNullOrEmpty(inputsPath)) throw new InvalidOperationException("Required local launch attestation is missing");
                var inputs = PlanIntegrity.Parse(File.ReadAllText(inputsPath));
                if (!InputAttestation.Validate(plan, inputs, Common.ModInformation.IsServer ? "Server" : "Client", HashFile, out reason))
                    throw new InvalidOperationException(reason);
                var loaded = AppDomain.CurrentDomain.GetAssemblies().Where(a => !a.IsDynamic)
                    .Select(a => new KeyValuePair<string, string>(a.GetName().Name, a.Location));
                if (!LoadedAssemblyAttestation.Validate((JArray)plan["Fingerprints"]!, Common.ModInformation.IsServer ? "Server" : "Client", loaded, HashFile, out reason))
                    throw new InvalidOperationException(reason);
            }
            var pending = new List<ICompatibilityAdapter>();
            foreach (var item in active)
            {
                var id = (string?)item["Contract"]?["Id"];
                var contract = catalog.OfType<JObject>().SingleOrDefault(c => (string?)c["Id"] == id) ?? throw new InvalidOperationException("Unknown compiled contract");
                if (!JToken.DeepEquals(contract, item["Contract"])) throw new InvalidOperationException(id + " differs from the locally shipped contract");
                if ((bool?)contract["OfflineValidated"] != true || (bool?)contract["RuntimeValidated"] != true) throw new InvalidOperationException(id + " has not completed required validation");
                foreach (var required in (JArray)contract["Requires"]!)
                {
                    var side = (string?)required["Side"];
                    if (side != null && side != (Common.ModInformation.IsServer ? "Server" : "Client")) continue;
                    var name = Path.GetFileNameWithoutExtension((string)required["Name"]!);
                    var assembly = AppDomain.CurrentDomain.GetAssemblies().SingleOrDefault(a => !a.IsDynamic && a.GetName().Name == name);
                    if (assembly == null) throw new InvalidOperationException("Required assembly is not loaded: " + name);
                    // A strict requirement is pinned to the file, because the adapter reaches across it by reflection
                    // and any change can move something it depends on. A provider mod pinned by target surfaces is
                    // not: refusing there on an unrelated update would only teach people to ignore the refusal.
                    if ((bool?)required["Strict"] != false && HashFile(assembly.Location) != (string?)required["Sha256"])
                        throw new InvalidOperationException("Required assembly fingerprint mismatch: " + name);
                }
                var adapterId = (string?)contract["AdapterId"];
                if (adapterId != null && TargetSurfaceCheck.Problem(contract) is { } surfaceProblem) throw new InvalidOperationException(surfaceProblem);
                ICompatibilityAdapter adapter = adapterId == "clans-resource-adder.v1" ? new ClansResourceAdderAdapter() : throw new InvalidOperationException("Unknown compiled adapter");
                if (!adapter.ValidateTargets(out reason)) throw new InvalidOperationException(reason);
                pending.Add(adapter);
            }
            EnsureJoinBarrier();
            if (active.Length > 0)
            {
                runtimeModuleOrder = ((JArray)plan["ModuleOrder"]!).Values<string>().ToArray()!;
                runtimeFingerprints = (JArray)plan["Fingerprints"]!.DeepClone();
                AppDomain.CurrentDomain.AssemblyLoad += AssemblyLoaded;
                Interlocked.Exchange(ref assembliesChanged, 1);
                if (!CheckReadiness()) throw new InvalidOperationException(Failure);
            }
            try { foreach (var adapter in pending) { adapter.Install(); Adapters.Add(adapter); } }
            catch { foreach (var a in Adapters) a.Dispose(); Adapters.Clear(); throw; }
            if (!Activation.Freeze(digest)) throw new InvalidOperationException("Activation was too late");
            PlanJson = json; Log.Info("operation plan frozen: " + digest + "; adapters=" + Adapters.Count); return true;
        }
        catch (Exception ex) { reason = Failure = ex.GetBaseException().Message; Log.Warn("operation activation refused: " + reason); return false; }
    }
    public static void BeforeCampaign()
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MODDERLORDS_OPERATION_PLAN")) && !SessionActive)
            throw new InvalidOperationException("Required operation plan is not active: " + Failure);
        if (!CheckReadiness()) throw new InvalidOperationException("Operation readiness failed before campaign: " + Failure);
        Activation.MarkCampaignStarted();
    }
    public static void EndSession()
    {
        foreach (var adapter in Adapters) adapter.Dispose(); Adapters.Clear();
        JoinBarrier?.Dispose(); JoinBarrier = null;
        AppDomain.CurrentDomain.AssemblyLoad -= AssemblyLoaded; runtimeFingerprints = null; runtimeModuleOrder = null; assembliesChanged = 0;
        Activation = new SessionActivation(); PlanJson = ""; Failure = "";
    }
    public static string Report() => Failure.Length > 0 ? "operation failure: " + Failure : string.Join("; ", Adapters.Select(a => a.Id + ": " + a.Readiness + " — " + a.Detail));
    private static JArray ReadCatalog()
    {
        using (var stream = typeof(OperationRuntime).Assembly.GetManifestResourceStream("ModderLords.OperationContracts")!)
        using (var reader = new StreamReader(stream)) return JArray.Parse(reader.ReadToEnd());
    }
    private static string HashFile(string path)
    { using (var sha = SHA256.Create()) using (var stream = File.OpenRead(path)) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", ""); }
}
