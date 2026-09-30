using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace ModderLords.Core.Compat;

/// <summary>
/// Where in a mod's code its desktop-framework references sit. A missing assembly only kills the method that names
/// it, and only when that method is compiled, which is when it first runs. So a reference is fatal on the server
/// exactly when it is somewhere the server is certain to reach:
/// <list type="bullet">
/// <item>a type's shape (base type, interface, field, member signature, attribute, generic constraint) - loading or
/// reflecting over the type resolves these, and Harmony and the settings discovery reflect over everything;</item>
/// <item>the body of any method on the submodule class - the engine calls those while the mod loads, which is how
/// DismembermentPlus died on 2026-09-19 (MessageBox.Show in a catch block of OnSubModuleLoad);</item>
/// <item>a static constructor, which runs whenever its type is first touched and so cannot be ruled out.</item>
/// </list>
/// Anything else is a method body the server only compiles if that code path runs. RBM 4.5.2 (2026-09-28) is the
/// worked example: its only WinForms use is two lambdas behind the Custom Battle preset hotkeys, started on their
/// own STA thread. Treating that like DismembermentPlus dropped all of RBM's code, and its settings with it.
/// </summary>
public sealed record DesktopSites(IReadOnlyList<string> LoadSites, IReadOnlyList<string> DeferredSites);

public static class DesktopSiteScan
{
    private const string SubModuleBase = "TaleWorlds.MountAndBlade.MBSubModuleBase";

    private static readonly Dictionary<short, OpCode> OpCodesByValue = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(f => (OpCode)f.GetValue(null)!)
        .ToDictionary(o => o.Value);

    public static DesktopSites Analyze(IReadOnlyList<string> dlls, Func<string, bool> isMissingOnServer)
    {
        var load = new SortedSet<string>(StringComparer.Ordinal);
        var deferred = new SortedSet<string>(StringComparer.Ordinal);

        // Base-type map across every DLL first: the submodule class may derive from a base the mod keeps in another file.
        var bases = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var dll in dlls)
        {
            try
            {
                using var fs = File.OpenRead(dll);
                using var pe = new PEReader(fs);
                if (!pe.HasMetadata) continue;
                var md = pe.GetMetadataReader();
                foreach (var h in md.TypeDefinitions)
                {
                    var td = md.GetTypeDefinition(h);
                    if (BaseName(md, td.BaseType) is { } b) bases[FullName(md, h)] = b;
                }
            }
            // Without the map no submodule class is recognised and every site would read as deferred - the unsafe
            // direction - so an unreadable file is a load-path finding, not a skip.
            catch (Exception ex) { load.Add(Path.GetFileName(dll) + " (could not be read: " + ex.Message + ")"); }
        }

        foreach (var dll in dlls)
        {
            try
            {
                using var fs = File.OpenRead(dll);
                using var pe = new PEReader(fs);
                if (!pe.HasMetadata) continue;
                var md = pe.GetMetadataReader();
                var missing = new HashSet<TypeReferenceHandle>();
                foreach (var h in md.TypeReferences)
                    if (RootAssembly(md, h) is { } asm && isMissingOnServer(asm)) missing.Add(h);
                if (missing.Count == 0) continue;
                new FileScan(pe, md, missing, bases, load, deferred).Run();
            }
            catch (Exception ex)
            {
                // Could not place the references, so assume the worst: this is the certain-crash answer.
                load.Add(Path.GetFileName(dll) + " (could not be read: " + ex.Message + ")");
            }
        }
        return new DesktopSites(load.ToList(), deferred.ToList());
    }

    private static string? RootAssembly(MetadataReader md, TypeReferenceHandle h)
    {
        for (var depth = 0; depth < 16; depth++)
        {
            var scope = md.GetTypeReference(h).ResolutionScope;
            if (scope.Kind == HandleKind.AssemblyReference) return md.GetString(md.GetAssemblyReference((AssemblyReferenceHandle)scope).Name);
            if (scope.Kind != HandleKind.TypeReference) return null;
            h = (TypeReferenceHandle)scope;
        }
        return null;
    }

    private static string FullName(MetadataReader md, TypeDefinitionHandle h)
    {
        var td = md.GetTypeDefinition(h);
        var name = md.GetString(td.Name);
        var decl = td.GetDeclaringType();
        if (!decl.IsNil) return FullName(md, decl) + "/" + name;
        var ns = md.GetString(td.Namespace);
        return ns.Length == 0 ? name : ns + "." + name;
    }

    private static string? BaseName(MetadataReader md, EntityHandle h)
    {
        if (h.IsNil) return null;   // <Module> and interfaces
        switch (h.Kind)
        {
            case HandleKind.TypeDefinition: return FullName(md, (TypeDefinitionHandle)h);
            case HandleKind.TypeReference:
            {
                var tr = md.GetTypeReference((TypeReferenceHandle)h);
                var ns = md.GetString(tr.Namespace);
                return ns.Length == 0 ? md.GetString(tr.Name) : ns + "." + md.GetString(tr.Name);
            }
            default: return null;   // generic base: never MBSubModuleBase itself, and the chain stops here
        }
    }

    private static bool IsSubModuleClass(string full, Dictionary<string, string> bases)
    {
        for (var depth = 0; depth < 16 && bases.TryGetValue(full, out var b); depth++)
        {
            if (b == SubModuleBase) return true;
            full = b;
        }
        return false;
    }

    private sealed class FileScan(PEReader pe, MetadataReader md, HashSet<TypeReferenceHandle> missing, Dictionary<string, string> bases,
        SortedSet<string> load, SortedSet<string> deferred) : ISignatureTypeProvider<bool, object?>
    {
        public void Run()
        {
            foreach (var h in md.TypeDefinitions)
            {
                var td = md.GetTypeDefinition(h);
                var type = FullName(md, h);
                var subModule = IsSubModuleClass(type, bases);

                // Shape: resolved when the type loads or is reflected over.
                if (Taints(td.BaseType)) load.Add(type + " (base type)");
                foreach (var ih in td.GetInterfaceImplementations())
                    if (Taints(md.GetInterfaceImplementation(ih).Interface)) load.Add(type + " (interface)");
                if (AttributesTaint(td.GetCustomAttributes())) load.Add(type + " (attribute)");
                if (GenericParamsTaint(td.GetGenericParameters())) load.Add(type + " (generic constraint)");
                foreach (var fh in td.GetFields())
                {
                    var fd = md.GetFieldDefinition(fh);
                    if (fd.DecodeSignature(this, null) || AttributesTaint(fd.GetCustomAttributes()))
                        load.Add(type + "." + md.GetString(fd.Name) + " (field)");
                }
                foreach (var ph in td.GetProperties())
                {
                    var pd = md.GetPropertyDefinition(ph);
                    if (SigTaints(pd.DecodeSignature(this, null)) || AttributesTaint(pd.GetCustomAttributes()))
                        load.Add(type + "." + md.GetString(pd.Name) + " (property)");
                }
                foreach (var eh in td.GetEvents())
                {
                    var ed = md.GetEventDefinition(eh);
                    if (Taints(ed.Type)) load.Add(type + "." + md.GetString(ed.Name) + " (event)");
                }

                foreach (var mh in td.GetMethods())
                {
                    var mdef = md.GetMethodDefinition(mh);
                    var name = type + "::" + md.GetString(mdef.Name);
                    if (SigTaints(mdef.DecodeSignature(this, null)) || AttributesTaint(mdef.GetCustomAttributes())
                        || GenericParamsTaint(mdef.GetGenericParameters())
                        || mdef.GetParameters().Any(p => AttributesTaint(md.GetParameter(p).GetCustomAttributes())))
                    {
                        load.Add(name + " (signature)");
                        continue;
                    }
                    var callees = new List<MethodDefinitionHandle>();
                    var tainted = mdef.RelativeVirtualAddress != 0 && BodyTaints(pe.GetMethodBody(mdef.RelativeVirtualAddress), callees);
                    _methods[mh] = (name, tainted, callees);
                    if (subModule) _roots.Add((mh, "submodule class, runs as the mod loads"));
                    else if (md.GetString(mdef.Name) == ".cctor") _roots.Add((mh, "static constructor"));
                }
            }
            if (AttributesTaint(md.GetCustomAttributes(EntityHandle.AssemblyDefinition))) load.Add("assembly attribute");

            // A method the load path calls directly is compiled when it is called, so it is as fatal as the root.
            // Delegates (ldftn) are not followed: the target compiles when the delegate is invoked, which is RBM's case
            // - its dialog lambdas run on a thread started only by a Custom Battle hotkey.
            var reached = new Dictionary<MethodDefinitionHandle, string>();
            var queue = new Queue<MethodDefinitionHandle>();
            foreach (var (h, why) in _roots) if (reached.TryAdd(h, why)) queue.Enqueue(h);
            while (queue.Count > 0)
            {
                var h = queue.Dequeue();
                if (!_methods.TryGetValue(h, out var m)) continue;
                foreach (var c in m.Callees)
                    if (reached.TryAdd(c, "called from " + (_roots.Any(r => r.Handle == h) ? m.Name : reached[h].Replace("called from ", "")))) queue.Enqueue(c);
            }
            foreach (var (h, m) in _methods)
            {
                if (!m.Tainted) continue;
                if (reached.TryGetValue(h, out var why)) load.Add(m.Name + " (" + why + ")");
                else deferred.Add(m.Name);
            }
        }

        private readonly Dictionary<MethodDefinitionHandle, (string Name, bool Tainted, List<MethodDefinitionHandle> Callees)> _methods = new();
        private readonly List<(MethodDefinitionHandle Handle, string Why)> _roots = new();

        /// <summary>True when the body names a missing type; collects the own-assembly methods it calls directly.</summary>
        private bool BodyTaints(MethodBodyBlock body, List<MethodDefinitionHandle> callees)
        {
            var tainted = !body.LocalSignature.IsNil && md.GetStandaloneSignature(body.LocalSignature).DecodeLocalSignature(this, null).Any(t => t);
            foreach (var r in body.ExceptionRegions)
                if (r.Kind == ExceptionRegionKind.Catch && Taints(r.CatchType)) tainted = true;

            var il = body.GetILReader();
            while (il.RemainingBytes > 0)
            {
                short value = il.ReadByte();
                if (value == 0xFE) value = unchecked((short)(0xFE00 | il.ReadByte()));
                if (!OpCodesByValue.TryGetValue(value, out var op)) return true;   // unreadable IL: assume the worst
                var direct = op.Value == OpCodes.Call.Value || op.Value == OpCodes.Callvirt.Value || op.Value == OpCodes.Newobj.Value;
                switch (op.OperandType)
                {
                    case OperandType.InlineNone: break;
                    case OperandType.ShortInlineBrTarget: case OperandType.ShortInlineI: case OperandType.ShortInlineVar: il.Offset += 1; break;
                    case OperandType.InlineVar: il.Offset += 2; break;
                    case OperandType.InlineI: case OperandType.InlineBrTarget: case OperandType.ShortInlineR: case OperandType.InlineString: il.Offset += 4; break;
                    case OperandType.InlineI8: case OperandType.InlineR: il.Offset += 8; break;
                    case OperandType.InlineSwitch: il.Offset += 4 * il.ReadInt32(); break;
                    case OperandType.InlineField: case OperandType.InlineMethod: case OperandType.InlineSig: case OperandType.InlineTok: case OperandType.InlineType:
                    {
                        var h = MetadataTokens.EntityHandle(il.ReadInt32());
                        if (Taints(h)) tainted = true;
                        if (direct && h.Kind == HandleKind.MethodDefinition) callees.Add((MethodDefinitionHandle)h);
                        break;
                    }
                    default: return true;
                }
            }
            return tainted;
        }

        private bool Taints(EntityHandle h)
        {
            if (h.IsNil) return false;
            switch (h.Kind)
            {
                case HandleKind.TypeReference: return missing.Contains((TypeReferenceHandle)h);
                case HandleKind.TypeSpecification: return md.GetTypeSpecification((TypeSpecificationHandle)h).DecodeSignature(this, null);
                case HandleKind.MemberReference:
                {
                    var mr = md.GetMemberReference((MemberReferenceHandle)h);
                    if (Taints(mr.Parent)) return true;
                    return mr.GetKind() == MemberReferenceKind.Method ? SigTaints(mr.DecodeMethodSignature(this, null)) : mr.DecodeFieldSignature(this, null);
                }
                case HandleKind.MethodSpecification:
                {
                    var ms = md.GetMethodSpecification((MethodSpecificationHandle)h);
                    return Taints(ms.Method) || ms.DecodeSignature(this, null).Any(t => t);
                }
                case HandleKind.StandaloneSignature:
                {
                    var ss = md.GetStandaloneSignature((StandaloneSignatureHandle)h);
                    return ss.GetKind() == StandaloneSignatureKind.Method ? SigTaints(ss.DecodeMethodSignature(this, null)) : ss.DecodeLocalSignature(this, null).Any(t => t);
                }
                // Own methods and fields: their shape is checked as a type of this assembly, not per call site.
                default: return false;
            }
        }

        private bool AttributesTaint(CustomAttributeHandleCollection attrs)
        {
            foreach (var ah in attrs)
            {
                var ctor = md.GetCustomAttribute(ah).Constructor;
                if (ctor.Kind == HandleKind.MemberReference && Taints(ctor)) return true;
            }
            return false;
        }

        private bool GenericParamsTaint(GenericParameterHandleCollection gps)
        {
            foreach (var gh in gps)
                foreach (var ch in md.GetGenericParameter(gh).GetConstraints())
                    if (Taints(md.GetGenericParameterConstraint(ch).Type)) return true;
            return false;
        }

        private static bool SigTaints(MethodSignature<bool> sig) => sig.ReturnType || sig.ParameterTypes.Any(t => t);

        // ISignatureTypeProvider: "does this type mention a missing assembly anywhere".
        public bool GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) => missing.Contains(handle);
        public bool GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => false;
        public bool GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind) =>
            reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);
        public bool GetGenericInstantiation(bool genericType, ImmutableArray<bool> typeArguments) => genericType || typeArguments.Any(t => t);
        public bool GetArrayType(bool elementType, ArrayShape shape) => elementType;
        public bool GetSZArrayType(bool elementType) => elementType;
        public bool GetByReferenceType(bool elementType) => elementType;
        public bool GetPointerType(bool elementType) => elementType;
        public bool GetPinnedType(bool elementType) => elementType;
        public bool GetModifiedType(bool modifier, bool unmodifiedType, bool isRequired) => modifier || unmodifiedType;
        public bool GetFunctionPointerType(MethodSignature<bool> signature) => SigTaints(signature);
        public bool GetGenericMethodParameter(object? genericContext, int index) => false;
        public bool GetGenericTypeParameter(object? genericContext, int index) => false;
        public bool GetPrimitiveType(PrimitiveTypeCode typeCode) => false;
    }
}
