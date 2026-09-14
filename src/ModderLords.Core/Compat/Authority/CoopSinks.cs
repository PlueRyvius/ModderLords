using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
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
    public const int CurrentSchema = 1;
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

    private static readonly HashSet<string> MemberLookups = new(StringComparer.Ordinal)
    {
        "Field", "DeclaredField", "Property", "DeclaredProperty", "Method", "DeclaredMethod", "PropertySetter", "PropertyGetter",
        "GetField", "GetProperty", "GetMethod",
    };

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

            // Iterator/async bodies live in nested <Method>d__N types; attribute them to the method that owns them.
            var stateMachines = new Dictionary<(TypeDefinitionHandle, string), List<TypeDefinitionHandle>>();
            foreach (var h in md.TypeDefinitions)
            {
                var td = md.GetTypeDefinition(h);
                var owner = td.GetDeclaringType();
                if (owner.IsNil) continue;
                var name = md.GetString(td.Name);
                var close = name.IndexOf('>');
                if (!name.StartsWith('<') || close < 2) continue;
                var key = (owner, name[1..close]);
                if (!stateMachines.TryGetValue(key, out var list)) stateMachines[key] = list = new List<TypeDefinitionHandle>();
                list.Add(h);
            }

            foreach (var h in md.TypeDefinitions)
            {
                try
                {
                    var td = md.GetTypeDefinition(h);
                    var typeName = IlReader.TypeName(md, h);
                    var classPatch = new PatchTarget(null, null, 0);
                    foreach (var ah in td.GetCustomAttributes())
                        if (ReadPatch(md, md.GetCustomAttribute(ah)) is { } p) classPatch = p.Over(classPatch);

                    foreach (var mh in td.GetMethods())
                    {
                        var m = md.GetMethodDefinition(mh);
                        var name = md.GetString(m.Name);
                        var methodPatch = new PatchTarget(null, null, 0);
                        var prefix = name == "Prefix";
                        var transpiler = name == "Transpiler";
                        foreach (var ah in m.GetCustomAttributes())
                        {
                            var ca = md.GetCustomAttribute(ah);
                            var an = IlReader.AttributeName(md, ca);
                            if (an is "HarmonyPrefix" or "HarmonyPrefixAttribute") prefix = true;
                            else if (an is "HarmonyTranspiler" or "HarmonyTranspilerAttribute") transpiler = true;
                            else if (ReadPatch(md, ca) is { } p) methodPatch = p.Over(methodPatch);
                        }

                        var own = IlReader.Read(pe, m);
                        var body = new List<IlInstruction>(own);
                        if (stateMachines.TryGetValue((h, name), out var nested))
                            foreach (var nh in nested)
                                foreach (var nmh in md.GetTypeDefinition(nh).GetMethods())
                                    body.AddRange(IlReader.Read(pe, md.GetMethodDefinition(nmh)));

                        var pairs = LookupPairs(md, body);
                        if (name == "TargetMethods")
                            foreach (var (t, member, _) in pairs) listed.Add(t + "." + member);
                        else if (transpiler)
                            foreach (var (t, member, lookup) in pairs.Where(x => !x.lookup.Contains("Method"))) intercepted.Add(t + "." + member);
                        if (CallsAny(md, body, "AutoSyncRegistry", "AddField", "AddProperty"))
                            foreach (var (t, member, _) in pairs.Where(x => !x.lookup.Contains("Method"))) synced.Add(t + "." + member);

                        if (!prefix || !IlReader.ReturnsBool(md, m)) continue;
                        var target = methodPatch.Over(classPatch);
                        if (target.Type is null || target.ResolvedMethod is null) continue;
                        if (Classify(md, own) is { } kind)
                            gates.Add(new CoopGate(target.Type, target.ResolvedMethod, kind, typeName + "." + name));
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

    /// <summary>Null when the prefix does not consult Coop's authority at all (e.g. a plain <c>return true</c>).</summary>
    private static CoopGateKind? Classify(MetadataReader md, IReadOnlyList<IlInstruction> il)
    {
        bool server = false, client = false, policy = false, otherCalls = false, logic = false;
        foreach (var i in il)
        {
            switch (i.OpCode)
            {
                case ILOpCode.Call or ILOpCode.Callvirt:
                {
                    var (t, n) = IlReader.MemberName(md, i.Operand);
                    if (t.EndsWith("ModInformation", StringComparison.Ordinal) && n == "get_IsServer") server = true;
                    else if (t.EndsWith("ModInformation", StringComparison.Ordinal) && n == "get_IsClient") client = true;
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
        return server || client ? CoopGateKind.Conditional : CoopGateKind.Policy;
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

    /// <summary>(type, member, lookup) for every <c>AccessTools.X(typeof(T), "name")</c> / <c>typeof(T).GetX("name")</c> in the body.</summary>
    private static List<(string type, string member, string lookup)> LookupPairs(MetadataReader md, List<IlInstruction> il)
    {
        var pairs = new List<(string, string, string)>();
        string? lastType = null, lastString = null;
        foreach (var i in il)
        {
            switch (i.OpCode)
            {
                case ILOpCode.Ldtoken:
                {
                    var eh = MetadataTokens.EntityHandle(i.Operand);
                    if (eh.Kind is HandleKind.TypeDefinition or HandleKind.TypeReference or HandleKind.TypeSpecification) lastType = IlReader.TypeName(md, eh);
                    break;
                }
                case ILOpCode.Ldstr:
                    lastString = md.GetUserString(MetadataTokens.UserStringHandle(i.Operand & 0xFFFFFF));
                    break;
                case ILOpCode.Call or ILOpCode.Callvirt:
                {
                    var (t, n) = IlReader.MemberName(md, i.Operand);
                    if (MemberLookups.Contains(n) && (t.EndsWith("AccessTools", StringComparison.Ordinal) || t == "System.Type")
                        && !string.IsNullOrEmpty(lastType) && lastString is not null)
                    {
                        pairs.Add((lastType, lastString, n));
                        lastString = null;
                    }
                    break;
                }
            }
        }
        return pairs;
    }

    private static PatchTarget? ReadPatch(MetadataReader md, CustomAttribute ca)
    {
        if (IlReader.AttributeName(md, ca) is not ("HarmonyPatch" or "HarmonyPatchAttribute")) return null;
        CustomAttributeValue<string> value;
        try { value = ca.DecodeValue(AttributeTypeProvider.Instance); }
        catch (Exception ex) when (ex is BadImageFormatException or NotSupportedException or InvalidOperationException) { return new PatchTarget(null, null, 0); }
        string? type = null, method = null;
        var methodType = 0;
        foreach (var a in value.FixedArguments)
        {
            if (a.Type == "System.Type" && a.Value is string s) type ??= StripAssembly(s);
            else if (a.Type == nameof(PrimitiveTypeCode.String) && a.Value is string m) method ??= m;
            else if (a.Type.EndsWith("MethodType", StringComparison.Ordinal) && a.Value is int k) methodType = k;
        }
        return new PatchTarget(type, method, methodType);
    }

    /// <summary>"Ns.Type, Assembly, Version=…" → "Ns.Type" (commas inside generic brackets are kept).</summary>
    private static string StripAssembly(string serialized)
    {
        var depth = 0;
        for (var i = 0; i < serialized.Length; i++)
        {
            switch (serialized[i])
            {
                case '[': depth++; break;
                case ']': depth--; break;
                case ',' when depth == 0: return serialized[..i].Trim();
            }
        }
        return serialized.Trim();
    }

    private readonly record struct PatchTarget(string? Type, string? Method, int MethodType)
    {
        /// <summary>This attribute's values, falling back to the enclosing (class-level) ones.</summary>
        public PatchTarget Over(PatchTarget fallback) =>
            new(Type ?? fallback.Type, Method ?? fallback.Method, MethodType != 0 ? MethodType : fallback.MethodType);

        // HarmonyLib.MethodType: 0 Normal, 1 Getter, 2 Setter, 3 Constructor.
        public string? ResolvedMethod => MethodType switch
        {
            1 when Method is not null => "get_" + Method,
            2 when Method is not null => "set_" + Method,
            3 => ".ctor",
            _ => Method,
        };
    }

    private sealed class AttributeTypeProvider : ICustomAttributeTypeProvider<string>
    {
        public static readonly AttributeTypeProvider Instance = new();
        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode.ToString();
        public string GetSystemType() => "System.Type";
        public string GetSZArrayType(string elementType) => elementType + "[]";
        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => IlReader.TypeName(reader, handle);
        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) => IlReader.TypeName(reader, handle);
        public string GetTypeFromSerializedName(string name) => name;
        public PrimitiveTypeCode GetUnderlyingEnumType(string type) => PrimitiveTypeCode.Int32;
        public bool IsSystemType(string type) => type == "System.Type";
    }
}
