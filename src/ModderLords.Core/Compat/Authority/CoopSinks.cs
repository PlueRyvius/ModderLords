using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModderLords.Core.Launch;

namespace ModderLords.Core.Compat.Authority;

public enum CoopGateKind
{
    /// <summary>The prefix is exactly <c>return ModInformation.IsServer</c>: the original never runs on a client.</summary>
    ClientSkip,
    /// <summary>The prefix branches on IsServer/IsClient (intercepts, publishes a request, or runs a client-only path).</summary>
    Conditional,
    /// <summary>The prefix defers to <c>CallOriginalPolicy</c>: the original runs only on the server or inside an allowed scope.</summary>
    Policy,
    /// <summary>The prefix publishes a Coop message: a client's call becomes Coop's own request (or change) to the server.</summary>
    Publishes,
    /// <summary>On a client the prefix returns false without publishing anything: Coop refuses it there on purpose (cheats).</summary>
    ClientDeny,
    /// <summary>On a client the prefix lets the original run locally and publishes nothing: the change never reaches the server.</summary>
    ClientLocal,
}

/// <summary>One vanilla method whose behaviour on a client Coop changes, and the Coop prefix that does it.</summary>
public sealed record CoopGate(string TargetType, string TargetMethod, CoopGateKind Kind, string PatchMethod);

/// <summary>
/// What Coop's own GameInterface assembly says about authority, read from its IL: which vanilla behaviours and methods
/// it blocks on clients, and which members it replicates. The authority classifier treats these as sinks, so the list
/// follows Coop updates instead of being maintained by hand.
/// </summary>
public sealed class CoopSinkCatalogue
{
    public const int CurrentSchema = 3;
    public int SchemaVersion { get; set; } = CurrentSchema;
    public string SourceSha256 { get; set; } = "";
    public List<CoopGate> Gates { get; set; } = new();
    /// <summary>"Type.Member" registered with AutoSyncRegistry.AddField/AddProperty.</summary>
    public List<string> SyncedMembers { get; set; } = new();
    /// <summary>"Type.Member" named inside Coop transpilers (array/collection writes it intercepts).</summary>
    public List<string> InterceptedMembers { get; set; } = new();
    /// <summary>"Type.Method" yielded by TargetMethods(); Coop patches them, but how is not decided statically.</summary>
    public List<string> TargetMethodsTargets { get; set; } = new();
    public List<string> Notes { get; set; } = new();

    /// <summary>Vanilla behaviour types whose RegisterEvents Coop gates on clients.</summary>
    [JsonIgnore]
    public IReadOnlyList<string> GatedBehaviourTypes =>
        Gates.Where(g => g.TargetMethod == "RegisterEvents").Select(g => g.TargetType).Distinct(StringComparer.Ordinal).OrderBy(t => t, StringComparer.Ordinal).ToList();

    public bool IsBehaviourGated(string typeFullName) => GateFor(typeFullName, "RegisterEvents") is not null;

    public bool IsBlocked(string typeFullName, string method) => GateFor(typeFullName, method) is not null;

    public CoopGate? GateFor(string typeFullName, string method) =>
        Gates.FirstOrDefault(g => g.TargetType == typeFullName && g.TargetMethod == method);

    [JsonIgnore]
    public string Summary =>
        $"{GatedBehaviourTypes.Count} gated behaviour(s), {Gates.Count(g => g.TargetMethod != "RegisterEvents")} blocked method(s), " +
        $"{SyncedMembers.Count} synced member(s), {InterceptedMembers.Count} intercepted member(s), {TargetMethodsTargets.Count} TargetMethods target(s)";
}

public static class CoopSinks
{
    public const string AssemblyFileName = "GameInterface.dll";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    /// <summary>GameInterface.dll of the Coop install: the game's Modules\Coop first, then any Workshop item carrying it.</summary>
    public static string? FindGameInterface(IEnumerable<string> steamLibraries)
    {
        foreach (var lib in steamLibraries)
        {
            var candidates = new List<string> { Path.Combine(lib, "steamapps", "common", "Mount & Blade II Bannerlord", "Modules", "Coop") };
            var workshop = GamePaths.WorkshopRoot(lib);
            if (Directory.Exists(workshop)) candidates.AddRange(Directory.EnumerateDirectories(workshop));
            foreach (var mod in candidates)
            {
                var p = Path.Combine(mod, "bin", "Win64_Shipping_Client", AssemblyFileName);
                if (File.Exists(p)) return p;
            }
        }
        return null;
    }

    /// <summary>The catalogue for a GameInterface.dll, from a cache keyed by the file's SHA-256 when one is current.</summary>
    public static CoopSinkCatalogue Load(string path, string? cacheDir = null)
    {
        var sha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
        var dir = cacheDir ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ModderLords", "cache");
        var file = Path.Combine(dir, $"coop-sinks-{sha[..16]}.json");
        try
        {
            if (File.Exists(file) && JsonSerializer.Deserialize<CoopSinkCatalogue>(File.ReadAllText(file), Json) is { } cached
                && cached.SchemaVersion == CoopSinkCatalogue.CurrentSchema && cached.SourceSha256 == sha)
                return cached;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }

        var cat = ScanDll(path);
        cat.SourceSha256 = sha;
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(file, JsonSerializer.Serialize(cat, Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { cat.Notes.Add("cache not written: " + ex.Message); }
        return cat;
    }

    /// <summary>Reads the catalogue from IL metadata only. Never throws for a malformed assembly; problems go to Notes.</summary>
    public static CoopSinkCatalogue ScanDll(string path)
    {
        var cat = new CoopSinkCatalogue();
        var gates = new HashSet<CoopGate>();
        var synced = new SortedSet<string>(StringComparer.Ordinal);
        var intercepted = new SortedSet<string>(StringComparer.Ordinal);
        var listed = new SortedSet<string>(StringComparer.Ordinal);
        try
        {
            using var fs = File.OpenRead(path);
            using var pe = new PEReader(fs);
            if (!pe.HasMetadata) { cat.Notes.Add(Path.GetFileName(path) + ": no managed metadata"); return cat; }
            var md = pe.GetMetadataReader();
            var stateMachines = IlReader.StateMachineTypes(md);
            var delegates = new DelegateIndex(md);

            foreach (var h in md.TypeDefinitions)
            {
                try
                {
                    var td = md.GetTypeDefinition(h);
                    var typeName = IlReader.TypeName(md, h);
                    var classPatch = new PatchTarget(null, null, 0);
                    foreach (var ah in td.GetCustomAttributes())
                        if (HarmonyMetadata.ReadPatch(md, md.GetCustomAttribute(ah)) is { } p) classPatch = p.Over(classPatch);

                    foreach (var mh in td.GetMethods())
                    {
                        var m = md.GetMethodDefinition(mh);
                        var name = md.GetString(m.Name);
                        var methodPatch = new PatchTarget(null, null, 0);
                        var kind = HarmonyMetadata.KindFromName(name);
                        foreach (var ah in m.GetCustomAttributes())
                        {
                            var ca = md.GetCustomAttribute(ah);
                            if (HarmonyMetadata.KindFromAttribute(IlReader.AttributeName(md, ca)) is { } k) kind = k;
                            else if (HarmonyMetadata.ReadPatch(md, ca) is { } p) methodPatch = p.Over(methodPatch);
                        }

                        var own = IlReader.Read(pe, m);
                        var body = new List<IlInstruction>(own);
                        if (stateMachines.TryGetValue((h, name), out var nested))
                            foreach (var nh in nested)
                                foreach (var nmh in md.GetTypeDefinition(nh).GetMethods())
                                    body.AddRange(IlReader.Read(pe, md.GetMethodDefinition(nmh)));

                        var pairs = HarmonyMetadata.LookupPairs(md, body);
                        if (name == "TargetMethods")
                            foreach (var (t, member, _) in pairs) listed.Add(t + "." + member);
                        else if (kind == PatchKind.Transpiler)
                            foreach (var (t, member, _) in pairs.Where(x => !x.lookup.Contains("Method"))) intercepted.Add(t + "." + member);
                        if (CallsAny(md, body, "AutoSyncRegistry", "AddField", "AddProperty"))
                            foreach (var (t, member, _) in pairs.Where(x => !x.lookup.Contains("Method"))) synced.Add(t + "." + member);

                        if (kind != PatchKind.Prefix || !IlReader.ReturnsBool(md, m)) continue;
                        var target = methodPatch.Over(classPatch);
                        if (target.Type is null || target.ResolvedMethod is null) continue;
                        if (Classify(md, own) is { } gateKind)
                            gates.Add(new CoopGate(target.Type, target.ResolvedMethod, gateKind, typeName + "." + name));
                        else if (delegates.Classify(pe, own) is { } delegated)
                            gates.Add(new CoopGate(target.Type, target.ResolvedMethod, delegated.kind, typeName + "." + name + " -> " + delegated.via));
                    }
                }
                catch (BadImageFormatException ex) { cat.Notes.Add("type skipped: " + ex.Message); }
            }
        }
        catch (Exception ex) when (ex is BadImageFormatException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            cat.Notes.Add(Path.GetFileName(path) + ": " + ex.Message);
        }

        cat.Gates = gates.OrderBy(g => g.TargetType, StringComparer.Ordinal).ThenBy(g => g.TargetMethod, StringComparer.Ordinal).ThenBy(g => g.PatchMethod, StringComparer.Ordinal).ToList();
        cat.SyncedMembers = synced.ToList();
        cat.InterceptedMembers = intercepted.ToList();
        cat.TargetMethodsTargets = listed.ToList();
        return cat;
    }

    /// <summary>
    /// Coop's own bool methods, by "Type::Name", with interfaces resolved to their implementations. Some prefixes only
    /// forward (<c>return kingdomInterface.RemoveDecisionPrefix(__instance, d)</c>), so the authority check that makes
    /// them a gate is in the method they call. One level is followed, and only into bool methods: a forwarded prefix
    /// returns its callee's answer, while a helper that merely logs under IsServer must not make its caller a gate.
    /// </summary>
    private sealed class DelegateIndex
    {
        private readonly MetadataReader _md;
        private readonly Dictionary<string, List<MethodDefinitionHandle>> _methods = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<string>> _implementors = new(StringComparer.Ordinal);

        public DelegateIndex(MetadataReader md)
        {
            _md = md;
            foreach (var h in md.TypeDefinitions)
            {
                try
                {
                    var td = md.GetTypeDefinition(h);
                    var type = IlReader.TypeName(md, h);
                    foreach (var mh in td.GetMethods())
                    {
                        var key = type + "::" + md.GetString(md.GetMethodDefinition(mh).Name);
                        if (!_methods.TryGetValue(key, out var list)) _methods[key] = list = new();
                        list.Add(mh);
                    }
                    foreach (var ih in td.GetInterfaceImplementations())
                    {
                        var iface = IlReader.TypeName(md, md.GetInterfaceImplementation(ih).Interface);
                        if (!_implementors.TryGetValue(iface, out var types)) _implementors[iface] = types = new();
                        types.Add(type);
                    }
                }
                catch (BadImageFormatException) { }
            }
        }

        /// <summary>The gate kind of the first bool method this prefix calls that consults authority, and which one it was.</summary>
        public (CoopGateKind kind, string via)? Classify(PEReader pe, IReadOnlyList<IlInstruction> prefix)
        {
            foreach (var i in prefix)
            {
                if (i.OpCode is not (ILOpCode.Call or ILOpCode.Callvirt)) continue;
                var (type, name) = IlReader.MemberName(_md, i.Operand);
                if (type.Length == 0) continue;
                var owners = _implementors.TryGetValue(type, out var impls) ? impls.Prepend(type) : new[] { type };
                foreach (var owner in owners)
                {
                    if (!_methods.TryGetValue(owner + "::" + name, out var candidates)) continue;
                    foreach (var mh in candidates)
                    {
                        var m = _md.GetMethodDefinition(mh);
                        if (!IlReader.ReturnsBool(_md, m)) continue;
                        if (CoopSinks.Classify(_md, IlReader.Read(pe, m)) is { } kind) return (kind, owner + "." + name);
                    }
                }
            }
            return null;
        }
    }

    /// <summary>Null when the prefix does not consult Coop's authority at all (e.g. a plain <c>return true</c>).</summary>
    private static CoopGateKind? Classify(MetadataReader md, IReadOnlyList<IlInstruction> il)
    {
        bool server = false, client = false, policy = false, otherCalls = false, logic = false, publish = false;
        var clientCheck = -1;
        for (var k = 0; k < il.Count; k++)
        {
            var i = il[k];
            switch (i.OpCode)
            {
                case ILOpCode.Call or ILOpCode.Callvirt:
                {
                    var (t, n) = IlReader.MemberName(md, i.Operand);
                    if (t.EndsWith("ModInformation", StringComparison.Ordinal) && n == "get_IsServer") server = true;
                    else if (t.EndsWith("ModInformation", StringComparison.Ordinal) && n == "get_IsClient") { client = true; if (clientCheck < 0) clientCheck = k; }
                    else if (t.EndsWith("MessageBroker", StringComparison.Ordinal) && n == "Publish") { publish = true; otherCalls = true; }
                    else if (t.EndsWith("CallOriginalPolicy", StringComparison.Ordinal)) policy = true;
                    else otherCalls = true;
                    break;
                }
                case ILOpCode.Ldarg or ILOpCode.Ldarg_s or ILOpCode.Ldarg_0 or ILOpCode.Ldarg_1 or ILOpCode.Ldarg_2 or ILOpCode.Ldarg_3
                    or ILOpCode.Ldarga or ILOpCode.Ldarga_s or ILOpCode.Ceq or ILOpCode.Ldc_i4_0 or ILOpCode.Ldc_i4_1:
                    logic = true;
                    break;
            }
        }
        if (!server && !client && !policy) return null;
        if (server && !client && !policy && !otherCalls && !logic) return CoopGateKind.ClientSkip;
        // What the client branch does decides it: a prefix can publish on the server (to broadcast) and still refuse on a
        // client (Coop's Kingdom.RemoveDecision does nothing there).
        if (clientCheck >= 0 && ClientBranch(md, il, clientCheck) is { allowed: { } allowed } branch)
            return branch.publishes ? CoopGateKind.Publishes : allowed ? CoopGateKind.ClientLocal : CoopGateKind.ClientDeny;
        if (publish) return CoopGateKind.Publishes;
        return server || client ? CoopGateKind.Conditional : CoopGateKind.Policy;
    }

    /// <summary>
    /// For <c>if (ModInformation.IsClient) { … return X; }</c>: X as a bool, read from the constant the fall-through block
    /// loads before leaving (a direct <c>ret</c> or a jump to the shared return), and whether that block publishes a Coop
    /// message. Null for any other shape.
    /// </summary>
    private static (bool? allowed, bool publishes)? ClientBranch(MetadataReader md, IReadOnlyList<IlInstruction> il, int clientCheck)
    {
        // Debug builds store the condition in a local first: call get_IsClient; stloc.0; ldloc.0; brfalse.
        var b = clientCheck + 1;
        while (b < il.Count && il[b].OpCode is ILOpCode.Nop or ILOpCode.Stloc_0 or ILOpCode.Stloc_1 or ILOpCode.Stloc_2 or ILOpCode.Stloc_3
                   or ILOpCode.Stloc_s or ILOpCode.Ldloc_0 or ILOpCode.Ldloc_1 or ILOpCode.Ldloc_2 or ILOpCode.Ldloc_3 or ILOpCode.Ldloc_s)
            b++;
        if (b >= il.Count || il[b].OpCode is not (ILOpCode.Brfalse or ILOpCode.Brfalse_s)) return null;
        var branch = il[b];
        var end = (b + 1 < il.Count ? il[b + 1].Offset : branch.Offset) + branch.Operand;
        bool? last = null;
        var publishes = false;
        for (var k = b + 1; k < il.Count && il[k].Offset < end; k++)
        {
            switch (il[k].OpCode)
            {
                case ILOpCode.Ldc_i4_0: last = false; break;
                case ILOpCode.Ldc_i4_1: last = true; break;
                case ILOpCode.Call or ILOpCode.Callvirt:
                    var (t, n) = IlReader.MemberName(md, il[k].Operand);
                    if (t.EndsWith("MessageBroker", StringComparison.Ordinal) && n == "Publish") publishes = true;
                    break;
                case ILOpCode.Ret or ILOpCode.Br or ILOpCode.Br_s or ILOpCode.Leave or ILOpCode.Leave_s: return (last, publishes);
            }
        }
        return null;
    }

    private static bool CallsAny(MetadataReader md, List<IlInstruction> il, string typeSuffix, params string[] names)
    {
        foreach (var i in il)
        {
            if (i.OpCode is not (ILOpCode.Call or ILOpCode.Callvirt)) continue;
            var (t, n) = IlReader.MemberName(md, i.Operand);
            if (t.EndsWith(typeSuffix, StringComparison.Ordinal) && names.Contains(n)) return true;
        }
        return false;
    }
}
