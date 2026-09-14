using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace ModderLords.Core.Compat.Authority;

/// <summary>One decoded IL instruction: its opcode and, for token/int operands, the raw 4-byte value.</summary>
public readonly record struct IlInstruction(int Offset, ILOpCode OpCode, int Operand);

/// <summary>
/// Minimal IL walker over method bodies read through System.Reflection.Metadata (nothing is loaded or executed), plus
/// the name helpers the authority scans share. Only what classification needs: opcodes, tokens and string literals.
/// </summary>
public static class IlReader
{
    public static IReadOnlyList<IlInstruction> Read(PEReader pe, MethodDefinition method)
    {
        var list = new List<IlInstruction>();
        if (method.RelativeVirtualAddress == 0) return list;                  // abstract / extern
        var body = pe.GetMethodBody(method.RelativeVirtualAddress);
        var r = body.GetILReader();
        while (r.RemainingBytes > 0)
        {
            var offset = r.Offset;
            int b = r.ReadByte();
            var op = (ILOpCode)(b == 0xFE ? 0xFE00 | r.ReadByte() : b);
            var operand = 0;
            if (op == ILOpCode.Switch)
            {
                var n = r.ReadInt32();
                for (var i = 0; i < n; i++) r.ReadInt32();
            }
            else
            {
                switch (OperandSize(op))
                {
                    case 1: operand = r.ReadSByte(); break;
                    case 2: operand = r.ReadInt16(); break;
                    case 4: operand = r.ReadInt32(); break;
                    case 8: r.ReadInt64(); break;
                }
            }
            list.Add(new IlInstruction(offset, op, operand));
        }
        return list;
    }

    /// <summary>
    /// Compiler-generated nested types that carry a method's body elsewhere (iterators and async state machines,
    /// named <c>&lt;Method&gt;d__N</c>), keyed by owner type and the method they belong to.
    /// </summary>
    public static Dictionary<(TypeDefinitionHandle owner, string method), List<TypeDefinitionHandle>> StateMachineTypes(MetadataReader md)
    {
        var map = new Dictionary<(TypeDefinitionHandle, string), List<TypeDefinitionHandle>>();
        foreach (var h in md.TypeDefinitions)
        {
            var td = md.GetTypeDefinition(h);
            var owner = td.GetDeclaringType();
            if (owner.IsNil) continue;
            var name = md.GetString(td.Name);
            var close = name.IndexOf('>');
            if (!name.StartsWith('<') || close < 2) continue;
            var key = (owner, name[1..close]);
            if (!map.TryGetValue(key, out var list)) map[key] = list = new List<TypeDefinitionHandle>();
            list.Add(h);
        }
        return map;
    }

    private static int OperandSize(ILOpCode op)
    {
        if (op.IsBranch()) return op.GetBranchOperandSize();
        switch (op)
        {
            case ILOpCode.Ldarg_s: case ILOpCode.Ldarga_s: case ILOpCode.Starg_s:
            case ILOpCode.Ldloc_s: case ILOpCode.Ldloca_s: case ILOpCode.Stloc_s:
            case ILOpCode.Ldc_i4_s: case ILOpCode.Unaligned:
                return 1;
            case ILOpCode.Ldarg: case ILOpCode.Ldarga: case ILOpCode.Starg:
            case ILOpCode.Ldloc: case ILOpCode.Ldloca: case ILOpCode.Stloc:
                return 2;
            case ILOpCode.Ldc_i8: case ILOpCode.Ldc_r8:
                return 8;
            case ILOpCode.Ldc_i4: case ILOpCode.Ldc_r4: case ILOpCode.Jmp:
            case ILOpCode.Call: case ILOpCode.Calli: case ILOpCode.Callvirt: case ILOpCode.Newobj:
            case ILOpCode.Ldstr: case ILOpCode.Ldtoken:
            case ILOpCode.Ldfld: case ILOpCode.Ldflda: case ILOpCode.Stfld:
            case ILOpCode.Ldsfld: case ILOpCode.Ldsflda: case ILOpCode.Stsfld:
            case ILOpCode.Box: case ILOpCode.Unbox: case ILOpCode.Unbox_any: case ILOpCode.Castclass: case ILOpCode.Isinst:
            case ILOpCode.Newarr: case ILOpCode.Ldelema: case ILOpCode.Ldelem: case ILOpCode.Stelem:
            case ILOpCode.Mkrefany: case ILOpCode.Refanyval: case ILOpCode.Cpobj: case ILOpCode.Ldobj: case ILOpCode.Stobj:
            case ILOpCode.Initobj: case ILOpCode.Sizeof: case ILOpCode.Constrained: case ILOpCode.Ldftn: case ILOpCode.Ldvirtftn:
                return 4;
            default:
                return 0;
        }
    }

    /// <summary>Declaring type full name and member name of a method/field token (MethodDef, MemberRef, MethodSpec, FieldDef).</summary>
    public static (string type, string name) MemberName(MetadataReader md, int token)
    {
        try
        {
            var h = MetadataTokens.EntityHandle(token);
            switch (h.Kind)
            {
                case HandleKind.MethodDefinition:
                {
                    var m = md.GetMethodDefinition((MethodDefinitionHandle)h);
                    return (TypeName(md, m.GetDeclaringType()), md.GetString(m.Name));
                }
                case HandleKind.MemberReference:
                {
                    var m = md.GetMemberReference((MemberReferenceHandle)h);
                    return (TypeName(md, m.Parent), md.GetString(m.Name));
                }
                case HandleKind.MethodSpecification:
                    return MemberName(md, MetadataTokens.GetToken(md.GetMethodSpecification((MethodSpecificationHandle)h).Method));
                case HandleKind.FieldDefinition:
                {
                    var f = md.GetFieldDefinition((FieldDefinitionHandle)h);
                    return (TypeName(md, f.GetDeclaringType()), md.GetString(f.Name));
                }
            }
        }
        catch (BadImageFormatException) { }
        return ("", "");
    }

    /// <summary>Full name (Namespace.Outer+Inner) of a TypeDef/TypeRef/TypeSpec handle; generic instantiations report the open type.</summary>
    public static string TypeName(MetadataReader md, EntityHandle h)
    {
        switch (h.Kind)
        {
            case HandleKind.TypeDefinition:
            {
                var td = md.GetTypeDefinition((TypeDefinitionHandle)h);
                var name = md.GetString(td.Name);
                var outer = td.GetDeclaringType();
                return outer.IsNil ? Join(md.GetString(td.Namespace), name) : TypeName(md, outer) + "+" + name;
            }
            case HandleKind.TypeReference:
            {
                var tr = md.GetTypeReference((TypeReferenceHandle)h);
                var name = md.GetString(tr.Name);
                return tr.ResolutionScope.Kind == HandleKind.TypeReference
                    ? TypeName(md, tr.ResolutionScope) + "+" + name
                    : Join(md.GetString(tr.Namespace), name);
            }
            case HandleKind.TypeSpecification:
            {
                // Generic instantiation: blob is GENERICINST (0x15), CLASS/VALUETYPE, then a TypeDefOrRef coded index.
                var r = md.GetBlobReader(md.GetTypeSpecification((TypeSpecificationHandle)h).Signature);
                if (r.ReadSignatureTypeCode() != SignatureTypeCode.GenericTypeInstance) return "";
                r.ReadSignatureTypeCode();
                return TypeName(md, r.ReadTypeHandle());
            }
            case HandleKind.MethodDefinition:
                return TypeName(md, md.GetMethodDefinition((MethodDefinitionHandle)h).GetDeclaringType());
            default:
                return "";
        }
    }

    public static string TypeName(MetadataReader md, TypeDefinitionHandle h) => TypeName(md, (EntityHandle)h);

    /// <summary>Simple (last-segment) name of a custom attribute's type.</summary>
    public static string AttributeName(MetadataReader md, CustomAttribute ca)
    {
        try
        {
            var full = ca.Constructor.Kind switch
            {
                HandleKind.MemberReference => TypeName(md, md.GetMemberReference((MemberReferenceHandle)ca.Constructor).Parent),
                HandleKind.MethodDefinition => TypeName(md, md.GetMethodDefinition((MethodDefinitionHandle)ca.Constructor).GetDeclaringType()),
                _ => "",
            };
            var i = Math.Max(full.LastIndexOf('.'), full.LastIndexOf('+'));
            return i >= 0 ? full[(i + 1)..] : full;
        }
        catch (BadImageFormatException) { return ""; }
    }

    /// <summary>True when a field token (FieldDef or field MemberRef) is declared as a single-dimension array.</summary>
    public static bool IsArrayField(MetadataReader md, int token)
    {
        try
        {
            var h = MetadataTokens.EntityHandle(token);
            BlobHandle sig;
            switch (h.Kind)
            {
                case HandleKind.FieldDefinition: sig = md.GetFieldDefinition((FieldDefinitionHandle)h).Signature; break;
                case HandleKind.MemberReference: sig = md.GetMemberReference((MemberReferenceHandle)h).Signature; break;
                default: return false;
            }
            var r = md.GetBlobReader(sig);
            if (r.ReadSignatureHeader().Kind != SignatureKind.Field) return false;
            var code = r.ReadSignatureTypeCode();
            while (code is SignatureTypeCode.RequiredModifier or SignatureTypeCode.OptionalModifier)
            {
                r.ReadTypeHandle();
                code = r.ReadSignatureTypeCode();
            }
            return code == SignatureTypeCode.SZArray;
        }
        catch (BadImageFormatException) { return false; }
    }

    /// <summary>True when the method signature's return type is bool.</summary>
    public static bool ReturnsBool(MetadataReader md, MethodDefinition m)
    {
        try
        {
            var r = md.GetBlobReader(m.Signature);
            var header = r.ReadSignatureHeader();
            if (header.IsGeneric) r.ReadCompressedInteger();
            r.ReadCompressedInteger();                                         // parameter count
            return r.ReadSignatureTypeCode() == SignatureTypeCode.Boolean;
        }
        catch (BadImageFormatException) { return false; }
    }

    private static string Join(string ns, string name) => ns.Length == 0 ? name : ns + "." + name;
}
