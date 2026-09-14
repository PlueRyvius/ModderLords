using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

namespace ModderLords.Core.Compat.Authority;

/// <summary>A [HarmonyPatch] attribute's target, with class-level values as the fallback for method-level ones.</summary>
internal readonly record struct PatchTarget(string? Type, string? Method, int MethodType)
{
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

/// <summary>Harmony shapes read from metadata, matched by simple name so no Harmony reference is needed.</summary>
internal static class HarmonyMetadata
{
    private static readonly HashSet<string> MemberLookups = new(StringComparer.Ordinal)
    {
        "Field", "DeclaredField", "Property", "DeclaredProperty", "Method", "DeclaredMethod", "PropertySetter", "PropertyGetter",
        "GetField", "GetProperty", "GetMethod",
    };

    /// <summary><c>AccessTools.X(typeof(T), "name")</c> or <c>typeof(T).GetX("name")</c>.</summary>
    public static bool IsMemberLookup(string type, string name) =>
        MemberLookups.Contains(name) && (type.EndsWith("AccessTools", StringComparison.Ordinal) || type == "System.Type");

    public static PatchKind? KindFromName(string methodName) => methodName switch
    {
        "Prefix" => PatchKind.Prefix,
        "Postfix" => PatchKind.Postfix,
        "Transpiler" => PatchKind.Transpiler,
        "Finalizer" => PatchKind.Finalizer,
        _ => null,
    };

    public static PatchKind? KindFromAttribute(string attributeName) => attributeName switch
    {
        "HarmonyPrefix" or "HarmonyPrefixAttribute" => PatchKind.Prefix,
        "HarmonyPostfix" or "HarmonyPostfixAttribute" => PatchKind.Postfix,
        "HarmonyTranspiler" or "HarmonyTranspilerAttribute" => PatchKind.Transpiler,
        "HarmonyFinalizer" or "HarmonyFinalizerAttribute" => PatchKind.Finalizer,
        _ => null,
    };

    /// <summary>(type, member, lookup) for every member lookup in the instructions.</summary>
    public static List<(string type, string member, string lookup)> LookupPairs(MetadataReader md, IEnumerable<IlInstruction> il)
    {
        var pairs = new List<(string, string, string)>();
        string? lastType = null, lastString = null;
        foreach (var i in il)
        {
            switch (i.OpCode)
            {
                case ILOpCode.Ldtoken:
                    lastType = TokenTypeName(md, i.Operand) ?? lastType;
                    break;
                case ILOpCode.Ldstr:
                    lastString = UserString(md, i.Operand);
                    break;
                case ILOpCode.Call or ILOpCode.Callvirt:
                {
                    var (t, n) = IlReader.MemberName(md, i.Operand);
                    if (IsMemberLookup(t, n) && !string.IsNullOrEmpty(lastType) && lastString is not null)
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

    public static string? TokenTypeName(MetadataReader md, int token)
    {
        var eh = MetadataTokens.EntityHandle(token);
        return eh.Kind is HandleKind.TypeDefinition or HandleKind.TypeReference or HandleKind.TypeSpecification ? IlReader.TypeName(md, eh) : null;
    }

    public static string UserString(MetadataReader md, int token) => md.GetUserString(MetadataTokens.UserStringHandle(token & 0xFFFFFF));

    public static PatchTarget? ReadPatch(MetadataReader md, CustomAttribute ca)
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
