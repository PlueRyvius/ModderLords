using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using System.Text;

namespace ModderLords.Operations;

/// <summary>
/// A stable hash of what an adapter actually depends on: the signature and IL body of one method, not the file that
/// happens to carry it. A provider can ship an unrelated fix without invalidating every contract; a change to the
/// patched method invalidates it immediately, which is the case worth catching.
///
/// Raw IL cannot be hashed directly. Operand tokens are metadata row indices, and those renumber whenever anything
/// else in the assembly changes, so a raw hash would move on every rebuild — exactly the false alarm this replaces.
/// Every token is therefore resolved to a name before it is hashed.
///
/// This is the reflection reader, used at runtime where the assembly is loaded. ModderLords.Analysis carries a Cecil
/// reader that must produce byte-identical text offline; MethodSurfaceParityTests holds the two to each other.
/// Any change to the canonical format is a change to both readers, or shipped contracts stop matching.
/// </summary>
public static class MethodSurface
{
    private static readonly Dictionary<short, OpCode> Opcodes = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f.FieldType == typeof(OpCode))
        .Select(f => (OpCode)f.GetValue(null)!)
        .ToDictionary(o => o.Value, o => o);

    /// <summary>SHA-256, uppercase hex, over the canonical text. Shared by both readers so the format has one owner.</summary>
    public static string Hash(string canonical)
    {
        using (var sha = SHA256.Create())
            return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(canonical))).Replace("-", "");
    }

    public static string Of(MethodBase method) => Hash(Canonicalize(method));

    /// <summary>
    /// Nested types and generic instantiations are spelled differently by reflection and by Cecil. Rather than teach
    /// each side the other's spelling, both reduce a type to its open, '+'-separated full name: precise enough to
    /// catch a changed call target, free of the formatting differences that would otherwise cause false mismatches.
    /// </summary>
    public static string NormalizeTypeName(string? fullName)
    {
        if (string.IsNullOrEmpty(fullName)) return "?";
        var name = fullName!.Replace('/', '+');
        // Only the generic ARGUMENTS go: reflection spells them "List`1[[System.Int32, mscorlib...]]" and Cecil
        // "List`1<System.Int32>", and neither spelling is worth teaching to the other side. Array and pointer
        // suffixes stay, because "byte" and "byte[]" are different parameters and must not collide.
        var cut = name.IndexOf('<');
        var generic = name.IndexOf("[[", StringComparison.Ordinal);
        if (generic >= 0 && (cut < 0 || generic < cut)) cut = generic;
        if (cut > 0) name = name.Substring(0, cut);
        return name;
    }

    /// <summary>
    /// A type built from the enclosing method's own generic parameters has no FullName at all, so the raw property
    /// would reduce Dictionary&lt;TKey,TValue&gt; to "?" while Cecil names it. Fall back to namespace + name.
    /// </summary>
    private static string TypeName(Type? type)
    {
        if (type == null) return "?";
        // A generic parameter is named by itself on both sides ("TPeer"), never qualified by the type that declares it.
        if (type.IsGenericParameter) return type.Name;
        if (type.FullName != null) return NormalizeTypeName(type.FullName);
        // A by-ref, pointer or array built over a generic parameter also has no FullName. Name the element and put
        // the suffix back, the way Cecil writes it ("TValidation&").
        if (type.IsByRef) return TypeName(type.GetElementType()) + "&";
        if (type.IsPointer) return TypeName(type.GetElementType()) + "*";
        if (type.IsArray) return TypeName(type.GetElementType()) + "[" + new string(',', type.GetArrayRank() - 1) + "]";
        // Rebuild the nesting the way Cecil spells it, so a nested type inside an open generic still gets its
        // outer types' names rather than collapsing to just its own.
        var name = type.Name;
        for (var outer = type.DeclaringType; outer != null; outer = outer.DeclaringType) name = outer.Name + "+" + name;
        return NormalizeTypeName(string.IsNullOrEmpty(type.Namespace) ? name : type.Namespace + "." + name);
    }

    public static string Canonicalize(MethodBase method)
    {
        if (method == null) throw new ArgumentNullException(nameof(method));
        var text = new StringBuilder();
        var returns = method is MethodInfo info ? TypeName(info.ReturnType) : "System.Void";
        text.Append(returns).Append(' ')
            .Append(TypeName(method.DeclaringType)).Append("::").Append(method.Name).Append('(')
            .Append(string.Join(",", method.GetParameters().Select(p => TypeName(p.ParameterType)).ToArray()))
            .Append(")\n");

        var body = method.GetMethodBody();
        if (body == null) { text.Append("<no body>\n"); return text.ToString(); }
        foreach (var local in body.LocalVariables.OrderBy(l => l.LocalIndex))
            text.Append(".local ").Append(local.LocalIndex).Append(' ').Append(TypeName(local.LocalType)).Append('\n');

        var il = body.GetILAsByteArray();
        if (il == null) { text.Append("<no il>\n"); return text.ToString(); }
        var typeArgs = method.DeclaringType != null && method.DeclaringType.IsGenericType
            ? method.DeclaringType.GetGenericArguments() : null;
        var methodArgs = method.IsGenericMethod ? method.GetGenericArguments() : null;
        var module = method.Module;

        var at = 0;
        while (at < il.Length)
        {
            short code = il[at++];
            if (code == 0xFE && at < il.Length) code = (short)(0xFE00 | il[at++]);
            if (!Opcodes.TryGetValue(code, out var op)) { text.Append("<unknown ").Append(code.ToString("X4", CultureInfo.InvariantCulture)).Append(">\n"); break; }
            text.Append(op.Name);
            at = AppendOperand(text, op, il, at, module, typeArgs, methodArgs);
            text.Append('\n');
        }
        return text.ToString();
    }

    private static int AppendOperand(StringBuilder text, OpCode op, byte[] il, int at, Module module, Type[]? typeArgs, Type[]? methodArgs)
    {
        switch (op.OperandType)
        {
            case OperandType.InlineNone:
                return at;
            case OperandType.ShortInlineI:
            case OperandType.ShortInlineVar:
                text.Append(' ').Append(il[at].ToString(CultureInfo.InvariantCulture));
                return at + 1;
            case OperandType.InlineVar:
                text.Append(' ').Append(BitConverter.ToUInt16(il, at).ToString(CultureInfo.InvariantCulture));
                return at + 2;
            case OperandType.ShortInlineBrTarget:
                // Relative, so inserting code elsewhere in the method does not shift every branch in the hash.
                text.Append(" ->").Append(((sbyte)il[at]).ToString(CultureInfo.InvariantCulture));
                return at + 1;
            case OperandType.InlineBrTarget:
                text.Append(" ->").Append(BitConverter.ToInt32(il, at).ToString(CultureInfo.InvariantCulture));
                return at + 4;
            case OperandType.ShortInlineR:
                text.Append(' ').Append(BitConverter.ToSingle(il, at).ToString("R", CultureInfo.InvariantCulture));
                return at + 4;
            case OperandType.InlineR:
                text.Append(' ').Append(BitConverter.ToDouble(il, at).ToString("R", CultureInfo.InvariantCulture));
                return at + 8;
            case OperandType.InlineI:
                text.Append(' ').Append(BitConverter.ToInt32(il, at).ToString(CultureInfo.InvariantCulture));
                return at + 4;
            case OperandType.InlineI8:
                text.Append(' ').Append(BitConverter.ToInt64(il, at).ToString(CultureInfo.InvariantCulture));
                return at + 8;
            case OperandType.InlineString:
                text.Append(" \"").Append(Safe(() => module.ResolveString(BitConverter.ToInt32(il, at)))).Append('"');
                return at + 4;
            case OperandType.InlineSwitch:
                var count = BitConverter.ToInt32(il, at);
                at += 4;
                text.Append(' ').Append(count.ToString(CultureInfo.InvariantCulture));
                for (var i = 0; i < count && at + 4 <= il.Length; i++, at += 4)
                    text.Append(" ->").Append(BitConverter.ToInt32(il, at).ToString(CultureInfo.InvariantCulture));
                return at;
            case OperandType.InlineSig:
                // calli only. See CecilSurface: the marker alone, so both readers agree.
                text.Append(" sig:");
                return at + 4;
            case OperandType.InlineType:
            case OperandType.InlineMethod:
            case OperandType.InlineField:
            case OperandType.InlineTok:
                text.Append(' ').Append(Member(module, BitConverter.ToInt32(il, at), typeArgs, methodArgs));
                return at + 4;
            default:
                text.Append(" ?");
                return at + 4;
        }
    }

    private static string Member(Module module, int token, Type[]? typeArgs, Type[]? methodArgs) => Safe(() =>
    {
        var member = module.ResolveMember(token, typeArgs, methodArgs);
        if (member is Type type) return TypeName(type);
        return TypeName(member?.DeclaringType) + "::" + (member?.Name ?? "?");
    });

    /// <summary>
    /// A token that will not resolve must not throw here. It is still recorded, because an unresolved token is itself
    /// a difference worth hashing; refusing the launch is the caller's decision, not this reader's.
    /// </summary>
    private static string Safe(Func<string?> read)
    {
        try { return read() ?? "?"; }
        catch (Exception ex) { return "<unresolved:" + ex.GetType().Name + ">"; }
    }
}
