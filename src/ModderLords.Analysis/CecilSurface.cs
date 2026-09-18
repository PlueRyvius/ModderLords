using System.Globalization;
using System.Text;
using Mono.Cecil;
using Mono.Cecil.Cil;
using ModderLords.Operations;

namespace ModderLords.Analysis;

/// <summary>
/// The offline half of <see cref="MethodSurface"/>. A provider mod references TaleWorlds types, so it cannot be
/// loaded for reflection outside the game; Cecil reads it from the file instead. The text this produces must be
/// byte-identical to the reflection reader's — MethodSurfaceParityTests fails the build if it drifts.
/// </summary>
public static class CecilSurface
{
    /// <summary>What the adapter actually depends on, captured offline: one method's surface plus its call sites.</summary>
    public static TargetSurface? Read(AssemblyDefinition assembly, string identity)
    {
        var method = Find(assembly, identity);
        if (method == null) return null;
        return new TargetSurface(identity, MethodSurface.Hash(Canonicalize(method)), CountCallers(assembly, method));
    }

    public static MethodDefinition? Find(AssemblyDefinition assembly, string identity)
    {
        var split = identity.Split(["::"], 2, StringSplitOptions.None);
        if (split.Length != 2) return null;
        var type = assembly.MainModule.GetTypes().FirstOrDefault(t => Name(t) == MethodSurface.NormalizeTypeName(split[0]));
        return type?.Methods.FirstOrDefault(m => m.Name == split[1]);
    }

    /// <summary>
    /// Call sites of the target anywhere in the provider's own assembly. An IL hash catches the body changing; it
    /// cannot catch a NEW caller appearing, which is what would widen an adapter's blast radius without touching a
    /// single byte of the method it patches. Recording the count makes that a refusal rather than a surprise.
    /// </summary>
    public static int CountCallers(AssemblyDefinition assembly, MethodDefinition target) =>
        assembly.MainModule.GetTypes()
            .SelectMany(t => t.Methods)
            .Where(m => m.HasBody)
            .SelectMany(m => m.Body.Instructions)
            .Count(i => i.Operand is MethodReference r && r.Name == target.Name
                        && Name(r.DeclaringType) == Name(target.DeclaringType));

    private static string Name(TypeReference type) => MethodSurface.NormalizeTypeName(type.FullName);

    public static string Canonicalize(MethodDefinition method)
    {
        var text = new StringBuilder();
        text.Append(Name(method.ReturnType)).Append(' ')
            .Append(Name(method.DeclaringType)).Append("::").Append(method.Name).Append('(')
            .Append(string.Join(",", method.Parameters.Select(p => Name(p.ParameterType))))
            .Append(")\n");

        if (!method.HasBody) { text.Append("<no body>\n"); return text.ToString(); }
        foreach (var local in method.Body.Variables.OrderBy(v => v.Index))
            text.Append(".local ").Append(local.Index).Append(' ').Append(Name(local.VariableType)).Append('\n');

        foreach (var instruction in method.Body.Instructions)
        {
            text.Append(instruction.OpCode.Name);
            AppendOperand(text, instruction, method.HasThis);
            text.Append('\n');
        }
        return text.ToString();
    }

    private static void AppendOperand(StringBuilder text, Instruction instruction, bool hasThis)
    {
        var op = instruction.OpCode;
        switch (op.OperandType)
        {
            case Mono.Cecil.Cil.OperandType.InlineNone:
                return;
            case Mono.Cecil.Cil.OperandType.ShortInlineBrTarget:
            case Mono.Cecil.Cil.OperandType.InlineBrTarget:
                // Reflection sees the encoded relative displacement; Cecil resolves it to an instruction. Recompute
                // the displacement so both sides write the same number.
                text.Append(" ->").Append(Displacement(instruction, (Instruction)instruction.Operand).ToString(CultureInfo.InvariantCulture));
                return;
            case Mono.Cecil.Cil.OperandType.InlineSwitch:
                var targets = (Instruction[])instruction.Operand;
                text.Append(' ').Append(targets.Length.ToString(CultureInfo.InvariantCulture));
                foreach (var target in targets)
                    text.Append(" ->").Append(Displacement(instruction, target).ToString(CultureInfo.InvariantCulture));
                return;
            case Mono.Cecil.Cil.OperandType.ShortInlineArg:
            case Mono.Cecil.Cil.OperandType.InlineArg:
            case Mono.Cecil.Cil.OperandType.ShortInlineVar:
            case Mono.Cecil.Cil.OperandType.InlineVar:
                text.Append(' ').Append(Index(instruction.Operand, hasThis).ToString(CultureInfo.InvariantCulture));
                return;
            case Mono.Cecil.Cil.OperandType.ShortInlineI:
                text.Append(' ').Append(Convert.ToInt32(instruction.Operand is sbyte b ? (byte)b : instruction.Operand).ToString(CultureInfo.InvariantCulture));
                return;
            case Mono.Cecil.Cil.OperandType.InlineI:
                text.Append(' ').Append(((int)instruction.Operand).ToString(CultureInfo.InvariantCulture));
                return;
            case Mono.Cecil.Cil.OperandType.InlineI8:
                text.Append(' ').Append(((long)instruction.Operand).ToString(CultureInfo.InvariantCulture));
                return;
            case Mono.Cecil.Cil.OperandType.ShortInlineR:
                text.Append(' ').Append(((float)instruction.Operand).ToString("R", CultureInfo.InvariantCulture));
                return;
            case Mono.Cecil.Cil.OperandType.InlineR:
                text.Append(' ').Append(((double)instruction.Operand).ToString("R", CultureInfo.InvariantCulture));
                return;
            case Mono.Cecil.Cil.OperandType.InlineString:
                text.Append(" \"").Append((string)instruction.Operand).Append('"');
                return;
            case Mono.Cecil.Cil.OperandType.InlineSig:
                // calli only. Both readers write the marker and nothing else: the two see a stand-alone signature
                // very differently, and the call target is on the stack anyway, so surrounding IL still covers it.
                text.Append(" sig:");
                return;
            case Mono.Cecil.Cil.OperandType.InlineType:
            case Mono.Cecil.Cil.OperandType.InlineMethod:
            case Mono.Cecil.Cil.OperandType.InlineField:
            case Mono.Cecil.Cil.OperandType.InlineTok:
                text.Append(' ').Append(Member(instruction.Operand));
                return;
            default:
                text.Append(" ?");
                return;
        }
    }

    /// <summary>Displacement is measured from the END of the branch instruction, the way the IL encodes it.</summary>
    private static int Displacement(Instruction branch, Instruction target) => target.Offset - (branch.Offset + branch.GetSize());

    /// <summary>
    /// Cecil numbers parameters without the implicit `this`; the IL byte reflection reads includes it. Add it back so
    /// an instance method's ldarg indices agree on both sides.
    /// </summary>
    private static int Index(object operand, bool hasThis) => operand switch
    {
        VariableReference variable => variable.Index,
        ParameterReference parameter => parameter.Index + (hasThis ? 1 : 0),
        _ => Convert.ToInt32(operand),
    };

    private static string Member(object operand) => operand switch
    {
        TypeReference type => Name(type),
        MemberReference member => Name(member.DeclaringType) + "::" + member.Name,
        _ => "?",
    };
}
