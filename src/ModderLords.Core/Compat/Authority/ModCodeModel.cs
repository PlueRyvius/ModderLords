using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using ModderLords.Core.Modules;

namespace ModderLords.Core.Compat.Authority;

/// <summary>How an entry point gets invoked under Coop — the first half of an authority verdict.</summary>
public enum RootTrigger
{
    /// <summary>A campaign event that fires on every peer running the campaign (ticks, map events, …).</summary>
    Simulation,
    /// <summary>A load/session event: runs once per peer, usually to register menus and dialogs.</summary>
    Session,
    /// <summary>Runs only on the client whose player acted: menu/dialog consequences, ViewModel actions, console commands.</summary>
    PlayerInput,
    /// <summary>A pure question every peer asks and must answer alike: game models, menu/dialog conditions.</summary>
    Query,
    /// <summary>Mission logic overrides, run by each peer's own mission.</summary>
    Mission,
    /// <summary>Screens, views, menu init/tick: local display.</summary>
    Presentation,
    /// <summary>MBSubModuleBase overrides.</summary>
    Lifecycle,
    /// <summary>A Harmony patch; it inherits the trigger of whatever calls its target.</summary>
    Patch,
}

public enum PatchKind { Prefix, Postfix, Transpiler, Finalizer, Unknown }

public sealed record PatchInfo(string TargetType, string TargetMethod, PatchKind Kind, bool Manual);

/// <summary>An entry point into a mod's code. Method ids are "Type::Name" (overloads share an id).</summary>
public sealed record AuthorityRoot(string Method, RootTrigger Trigger, string Detail, PatchInfo? Patch = null);

public sealed class MethodNode
{
    public MethodNode(string id) => Id = id;
    public string Id { get; }
    /// <summary>Callee ids: calls, object creation, delegate targets (ldftn) and state-machine bodies.</summary>
    public HashSet<string> Calls { get; } = new(StringComparer.Ordinal);
    /// <summary>"Type.field" written with stfld/stsfld.</summary>
    public HashSet<string> FieldWrites { get; } = new(StringComparer.Ordinal);
    public HashSet<string> FieldReads { get; } = new(StringComparer.Ordinal);
    /// <summary>Uses reflection invocation, so its real callees are not visible statically.</summary>
    public bool Opaque { get; set; }
}

/// <summary>A mod's call graph and entry points, read from IL metadata only (nothing is loaded or run).</summary>
public sealed class ModCodeModel
{
    public ModCodeModel(string moduleId) => ModuleId = moduleId;
    public string ModuleId { get; }
    public bool NotAnalysable { get; internal set; }
    public List<string> Notes { get; } = new();
    public Dictionary<string, MethodNode> Methods { get; } = new(StringComparer.Ordinal);
    /// <summary>Mod type → its base type's full name.</summary>
    public Dictionary<string, string> BaseTypes { get; } = new(StringComparer.Ordinal);
    /// <summary>Base class or interface full name → mod types that directly derive from or implement it.</summary>
    public Dictionary<string, HashSet<string>> Implementers { get; } = new(StringComparer.Ordinal);
    public List<AuthorityRoot> Roots { get; } = new();

    public static string MethodId(string type, string method) => type + "::" + method;

    /// <summary>The callee plus every mod override or interface implementation it can dispatch to.</summary>
    public IEnumerable<string> Dispatch(string calleeId)
    {
        yield return calleeId;
        var sep = calleeId.IndexOf("::", StringComparison.Ordinal);
        if (sep < 0) yield break;
        var type = calleeId[..sep];
        var name = calleeId[(sep + 2)..];
        var seen = new HashSet<string>(StringComparer.Ordinal) { type };
        var queue = new Queue<string>();
        queue.Enqueue(type);
        while (queue.Count > 0)
        {
            if (!Implementers.TryGetValue(queue.Dequeue(), out var subs)) continue;
            foreach (var sub in subs)
            {
                if (!seen.Add(sub)) continue;
                queue.Enqueue(sub);
                if (Methods.ContainsKey(MethodId(sub, name))) yield return MethodId(sub, name);
                var explicitImpl = MethodId(sub, type + "." + name);
                if (Methods.ContainsKey(explicitImpl)) yield return explicitImpl;
            }
        }
    }

    /// <summary>Base types nearest first, followed through the mod and ending at the first type the mod does not define.</summary>
    public IReadOnlyList<string> BaseChain(string type)
    {
        var chain = new List<string>();
        var current = type;
        for (var i = 0; i < 32 && BaseTypes.TryGetValue(current, out var b); i++)
        {
            chain.Add(b);
            current = b;
        }
        return chain;
    }

    public string Summary => NotAnalysable
        ? "not analysable: " + string.Join("; ", Notes.Take(2))
        : $"{Methods.Count} methods, {Roots.Count} roots (" + string.Join(", ", Enum.GetValues<RootTrigger>()
            .Select(t => (t, n: Roots.Count(r => r.Trigger == t))).Where(x => x.n > 0).Select(x => $"{x.t} {x.n}")) + ")";
}

public static class ModAnalysis
{
    private static readonly HashSet<string> SessionEvents = new(StringComparer.Ordinal)
    {
        "OnSessionLaunchedEvent", "OnAfterSessionLaunchedEvent", "OnNewGameCreatedEvent", "OnNewGameCreatedPartialFollowUpEvent",
        "OnNewGameCreatedPartialFollowUpEndEvent", "OnGameEarlyLoadedEvent", "OnGameLoadedEvent", "OnGameLoadFinishedEvent",
        "OnCharacterCreationIsOverEvent", "OnBeforeSaveEvent", "OnSaveOverEvent", "OnGameOverEvent",
    };

    // Delegate arguments of the menu/dialog builders, in declaration order (null delegates still take their slot).
    private static readonly Dictionary<string, RootTrigger[]> DelegateArgs = new(StringComparer.Ordinal)
    {
        ["AddGameMenuOption"] = [RootTrigger.Query, RootTrigger.PlayerInput],
        ["AddPlayerLine"] = [RootTrigger.Query, RootTrigger.PlayerInput],
        ["AddRepeatablePlayerLine"] = [RootTrigger.Query, RootTrigger.PlayerInput],
        ["AddDialogLine"] = [RootTrigger.Query, RootTrigger.PlayerInput],
        ["AddGameMenu"] = [RootTrigger.Presentation],
        ["AddWaitGameMenu"] = [RootTrigger.Presentation, RootTrigger.Query, RootTrigger.PlayerInput, RootTrigger.Presentation],
    };

    private static readonly HashSet<string> OpaqueCalls = new(StringComparer.Ordinal)
    {
        "System.Reflection.MethodBase::Invoke", "System.Reflection.MethodInfo::Invoke", "System.Reflection.ConstructorInfo::Invoke",
        "System.Activator::CreateInstance", "System.Type::InvokeMember", "System.Delegate::DynamicInvoke",
    };

    public static ModCodeModel Analyse(DiscoveredModule mod) => Analyse(mod.Id, AssemblyScan.ModuleDlls(mod));

    /// <summary>Never throws for malformed assemblies: unreadable ones go to Notes, and a mod with none readable is NotAnalysable.</summary>
    public static ModCodeModel Analyse(string moduleId, IReadOnlyList<string> dlls)
    {
        var model = new ModCodeModel(moduleId);
        var typeMethods = new List<(string type, string method, MethodAttributes attrs)>();
        var readable = 0;
        foreach (var dll in dlls)
        {
            try
            {
                using var fs = File.OpenRead(dll);
                using var pe = new PEReader(fs);
                if (!pe.HasMetadata) { model.Notes.Add(Path.GetFileName(dll) + ": no managed metadata"); continue; }
                var md = pe.GetMetadataReader();
                readable++;
                ReadAssembly(model, pe, md, typeMethods);
            }
            catch (Exception ex) when (ex is BadImageFormatException or IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                model.Notes.Add(Path.GetFileName(dll) + ": " + ex.Message);
            }
        }
        model.NotAnalysable = dlls.Count > 0 && readable == 0;
        AddTypeRoots(model, typeMethods);
        var distinct = model.Roots.Distinct().ToList();
        model.Roots.Clear();
        model.Roots.AddRange(distinct);
        return model;
    }

    private static void ReadAssembly(ModCodeModel model, PEReader pe, MetadataReader md, List<(string, string, MethodAttributes)> typeMethods)
    {
        var stateMachines = IlReader.StateMachineTypes(md);
        foreach (var h in md.TypeDefinitions)
        {
            string typeName;
            TypeDefinition td;
            try { td = md.GetTypeDefinition(h); typeName = IlReader.TypeName(md, h); }
            catch (BadImageFormatException ex) { model.Notes.Add("type skipped: " + ex.Message); continue; }
            if (typeName == "<Module>") continue;
            try
            {
                if (!td.BaseType.IsNil) Link(model, typeName, IlReader.TypeName(md, td.BaseType), isBase: true);
                foreach (var ih in td.GetInterfaceImplementations())
                    Link(model, typeName, IlReader.TypeName(md, md.GetInterfaceImplementation(ih).Interface), isBase: false);

                var classPatch = new PatchTarget(null, null, 0);
                foreach (var ah in td.GetCustomAttributes())
                    if (HarmonyMetadata.ReadPatch(md, md.GetCustomAttribute(ah)) is { } p) classPatch = p.Over(classPatch);
                var hasTargetMethods = td.GetMethods().Any(mh => md.GetString(md.GetMethodDefinition(mh).Name) == "TargetMethods");

                foreach (var mh in td.GetMethods())
                    ReadMethod(model, pe, md, h, typeName, mh, classPatch, hasTargetMethods, stateMachines, typeMethods);
            }
            catch (BadImageFormatException ex) { model.Notes.Add(typeName + ": " + ex.Message); }
        }
    }

    private static void Link(ModCodeModel model, string type, string baseOrInterface, bool isBase)
    {
        if (baseOrInterface.Length == 0) return;
        if (isBase) model.BaseTypes[type] = baseOrInterface;
        if (!model.Implementers.TryGetValue(baseOrInterface, out var set)) model.Implementers[baseOrInterface] = set = new HashSet<string>(StringComparer.Ordinal);
        set.Add(type);
    }

    private static MethodNode Node(ModCodeModel model, string id)
    {
        if (!model.Methods.TryGetValue(id, out var node)) model.Methods[id] = node = new MethodNode(id);
        return node;
    }

    private static void ReadMethod(ModCodeModel model, PEReader pe, MetadataReader md, TypeDefinitionHandle owner, string typeName,
        MethodDefinitionHandle mh, PatchTarget classPatch, bool hasTargetMethods,
        Dictionary<(TypeDefinitionHandle, string), List<TypeDefinitionHandle>> stateMachines, List<(string, string, MethodAttributes)> typeMethods)
    {
        var m = md.GetMethodDefinition(mh);
        var name = md.GetString(m.Name);
        var id = ModCodeModel.MethodId(typeName, name);
        var node = Node(model, id);
        typeMethods.Add((typeName, name, m.Attributes));

        // Attribute-declared patches and console commands.
        var methodPatch = new PatchTarget(null, null, 0);
        var kind = HarmonyMetadata.KindFromName(name);
        foreach (var ah in m.GetCustomAttributes())
        {
            var ca = md.GetCustomAttribute(ah);
            var an = IlReader.AttributeName(md, ca);
            if (HarmonyMetadata.KindFromAttribute(an) is { } k) kind = k;
            else if (an is "CommandLineArgumentFunction" or "CommandLineArgumentFunctionAttribute") model.Roots.Add(new AuthorityRoot(id, RootTrigger.PlayerInput, "console command"));
            else if (HarmonyMetadata.ReadPatch(md, ca) is { } p) methodPatch = p.Over(methodPatch);
        }
        if (kind is { } pk)
        {
            var target = methodPatch.Over(classPatch);
            if (target.Type is not null && target.ResolvedMethod is not null)
                model.Roots.Add(new AuthorityRoot(id, RootTrigger.Patch, $"{pk} {target.Type}.{target.ResolvedMethod}", new PatchInfo(target.Type, target.ResolvedMethod, pk, false)));
            else if (hasTargetMethods)
                model.Roots.Add(new AuthorityRoot(id, RootTrigger.Patch, pk + " (TargetMethods)", new PatchInfo("?", "?", pk, false)));
        }

        if (stateMachines.TryGetValue((owner, name), out var nested))
            foreach (var nh in nested)
                foreach (var nmh in md.GetTypeDefinition(nh).GetMethods())
                    node.Calls.Add(ModCodeModel.MethodId(IlReader.TypeName(md, nh), md.GetString(md.GetMethodDefinition(nmh).Name)));

        IReadOnlyList<IlInstruction> il;
        try { il = IlReader.Read(pe, m); }
        catch (BadImageFormatException ex) { model.Notes.Add(id + ": " + ex.Message); return; }

        string? lastEvent = null, lastFtn = null, lastType = null, lastString = null;
        var delegateArgs = new List<string?>();                               // ldftn targets / ldnull since the last call
        (string type, string method)? original = null, pendingLookup = null;  // manual Harmony.Patch(original, prefix, postfix, …)
        var slot = 0;
        var manual = new List<(int slot, string type, string method)>();

        foreach (var i in il)
        {
            switch (i.OpCode)
            {
                case ILOpCode.Ldtoken:
                    lastType = HarmonyMetadata.TokenTypeName(md, i.Operand) ?? lastType;
                    break;
                case ILOpCode.Ldstr:
                    lastString = HarmonyMetadata.UserString(md, i.Operand);
                    break;
                case ILOpCode.Ldnull:
                    delegateArgs.Add(null);
                    if (original is not null) slot++;
                    break;
                case ILOpCode.Ldftn or ILOpCode.Ldvirtftn:
                {
                    var (t, n) = IlReader.MemberName(md, i.Operand);
                    var target = ModCodeModel.MethodId(t, n);
                    node.Calls.Add(target);
                    lastFtn = target;
                    delegateArgs.Add(target);
                    break;
                }
                case ILOpCode.Stfld or ILOpCode.Stsfld:
                {
                    var (t, n) = IlReader.MemberName(md, i.Operand);
                    node.FieldWrites.Add(t + "." + n);
                    break;
                }
                case ILOpCode.Ldfld or ILOpCode.Ldsfld or ILOpCode.Ldflda or ILOpCode.Ldsflda:
                {
                    var (t, n) = IlReader.MemberName(md, i.Operand);
                    node.FieldReads.Add(t + "." + n);
                    break;
                }
                case ILOpCode.Newobj:
                {
                    var (t, n) = IlReader.MemberName(md, i.Operand);
                    node.Calls.Add(ModCodeModel.MethodId(t, n));
                    if (t.EndsWith("HarmonyMethod", StringComparison.Ordinal) && original is not null)
                    {
                        var patchMethod = pendingLookup ?? (lastType is not null && lastString is not null ? (lastType, lastString) : null);
                        if (patchMethod is { } pm) manual.Add((slot, pm.type, pm.method));
                        pendingLookup = null;
                        slot++;
                    }
                    break;
                }
                case ILOpCode.Call or ILOpCode.Callvirt:
                {
                    var (t, n) = IlReader.MemberName(md, i.Operand);
                    var callee = ModCodeModel.MethodId(t, n);
                    node.Calls.Add(callee);
                    if (OpaqueCalls.Contains(callee)) node.Opaque = true;

                    if (t.EndsWith("CampaignEvents", StringComparison.Ordinal) && n.StartsWith("get_", StringComparison.Ordinal))
                        lastEvent = n[4..];
                    else if (n == "AddNonSerializedListener" && lastFtn is not null && lastEvent is not null)
                    {
                        model.Roots.Add(new AuthorityRoot(lastFtn, SessionEvents.Contains(lastEvent) ? RootTrigger.Session : RootTrigger.Simulation, lastEvent));
                        lastEvent = null;
                    }
                    else if (DelegateArgs.TryGetValue(n, out var roles))
                    {
                        for (var j = 0; j < delegateArgs.Count && j < roles.Length; j++)
                            if (delegateArgs[j] is { } target) model.Roots.Add(new AuthorityRoot(target, roles[j], n));
                    }
                    else if (HarmonyMetadata.IsMemberLookup(t, n) && lastType is not null && lastString is not null)
                    {
                        var member = n switch { "PropertyGetter" => "get_" + lastString, "PropertySetter" => "set_" + lastString, _ => lastString };
                        if (original is null) original = (lastType, member);
                        else pendingLookup = (lastType, member);
                        lastString = null;
                    }
                    else if (n == "Patch" && t.EndsWith("Harmony", StringComparison.Ordinal) && original is { } o)
                    {
                        foreach (var (s, pt, pn) in manual)
                        {
                            var pk2 = s switch
                            {
                                0 => PatchKind.Prefix,
                                1 => PatchKind.Postfix,
                                2 => PatchKind.Transpiler,
                                3 => PatchKind.Finalizer,
                                _ => HarmonyMetadata.KindFromName(pn) ?? PatchKind.Unknown,
                            };
                            model.Roots.Add(new AuthorityRoot(ModCodeModel.MethodId(pt, pn), RootTrigger.Patch, $"{pk2} {o.type}.{o.method} (manual)",
                                new PatchInfo(o.type, o.method, pk2, true)));
                        }
                        original = null;
                        pendingLookup = null;
                        slot = 0;
                        manual.Clear();
                    }
                    if (n != "GetTypeFromHandle") delegateArgs.Clear();   // builder arguments are pushed without intervening calls
                    break;
                }
            }
        }
    }

    private static void AddTypeRoots(ModCodeModel model, List<(string type, string method, MethodAttributes attrs)> methods)
    {
        var families = new Dictionary<string, RootTrigger?>(StringComparer.Ordinal);
        foreach (var (type, method, attrs) in methods)
        {
            if (!families.TryGetValue(type, out var family)) families[type] = family = Family(model.BaseChain(type));
            if (family is null || method.StartsWith('.')) continue;
            if (family == RootTrigger.PlayerInput)
            {
                if (method.StartsWith("Execute", StringComparison.Ordinal))
                    model.Roots.Add(new AuthorityRoot(ModCodeModel.MethodId(type, method), RootTrigger.PlayerInput, "ViewModel action"));
            }
            else if (attrs.HasFlag(MethodAttributes.Virtual) && !attrs.HasFlag(MethodAttributes.NewSlot))
                model.Roots.Add(new AuthorityRoot(ModCodeModel.MethodId(type, method), family.Value, "override"));
        }
    }

    /// <summary>The engine family a type belongs to, judged by the simple names along its base chain (nearest first).</summary>
    private static RootTrigger? Family(IReadOnlyList<string> chain)
    {
        foreach (var full in chain)
        {
            var simple = full[(Math.Max(full.LastIndexOf('.'), full.LastIndexOf('+')) + 1)..];
            if (simple == "MBSubModuleBase") return RootTrigger.Lifecycle;
            if (simple == "ViewModel") return RootTrigger.PlayerInput;
            if (simple is "MissionView" or "ScreenBase" or "GauntletLayer" || simple.EndsWith("UIHandler", StringComparison.Ordinal)) return RootTrigger.Presentation;
            if (simple is "MissionLogic" or "MissionBehavior" or "MissionNetwork") return RootTrigger.Mission;
            if (simple.EndsWith("Model", StringComparison.Ordinal)) return RootTrigger.Query;
        }
        return null;
    }
}
