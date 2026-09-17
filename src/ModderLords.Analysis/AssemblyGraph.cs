using Mono.Cecil;
using Mono.Cecil.Cil;

namespace ModderLords.Analysis;

internal sealed record CallEdge(string Target, string Kind, int Offset, string ReceiverType);
internal sealed record DirectEffect(string Kind, int Offset, string Detail, bool AuthorityGuarded = false);
internal sealed record Registration(string Callback, string Sink, int Offset);
internal sealed record DiBinding(string Service, string Implementation);
internal sealed record MethodNode(string Key, string Identity, string Type, string Name, string? BaseType, string[] Interfaces,
    bool IsRoot, bool IsVirtual, CallEdge[] Calls, DirectEffect[] Effects, Registration[] Registrations);
internal sealed record TypeNode(string Type, string? BaseType, string[] Interfaces);
internal sealed record AssemblyGraph(string Name, string Hash, string Mvid, string[] References, MethodNode[] Methods, DiBinding[] Bindings, TypeNode[] Types);

internal static class GraphReader
{
    public static string MethodKey(MethodReference m)
    {
        if (m is GenericInstanceMethod gm) m = gm.ElementMethod;
        return $"{Scope(m.DeclaringType)}|{m.FullName}|g{m.GenericParameters.Count}";
    }
    private static string Scope(TypeReference t) => t.Scope is AssemblyNameReference a ? a.Name : t.Module.Assembly.Name.Name;
    public static AssemblyGraph Read(string path, string hash, bool metadataOnly = false)
    {
        using var a = AssemblyDefinition.ReadAssembly(path, new ReaderParameters { ReadSymbols = false, ReadingMode = ReadingMode.Deferred });
        var typeNodes = Flatten(a.MainModule.Types).Select(t => new TypeNode(t.FullName, t.BaseType?.FullName, t.Interfaces.Select(i => i.InterfaceType.FullName).ToArray())).ToArray();
        if (metadataOnly) return new(a.Name.Name, hash, a.MainModule.Mvid.ToString("D"), a.MainModule.AssemblyReferences.Select(x => x.Name).ToArray(), [], [], typeNodes);
        var methods = new List<MethodNode>(); var bindings = new List<DiBinding>();
        foreach (var type in Flatten(a.MainModule.Types)) foreach (var method in type.Methods)
        {
            var calls = new List<CallEdge>(); var effects = new List<DirectEffect>(); var registrations = new List<Registration>();
            // Async/iterator entry methods construct a compiler state machine; its effects live in MoveNext.
            foreach (var attribute in method.CustomAttributes.Where(x => x.AttributeType.FullName is
                "System.Runtime.CompilerServices.AsyncStateMachineAttribute" or "System.Runtime.CompilerServices.IteratorStateMachineAttribute"))
            {
                if (attribute.ConstructorArguments.Count == 1 && attribute.ConstructorArguments[0].Value is TypeReference stateType)
                {
                    var state = Flatten(a.MainModule.Types).FirstOrDefault(t => t.FullName == stateType.FullName);
                    var moveNext = state?.Methods.FirstOrDefault(m => m.Name == "MoveNext");
                    if (moveNext != null) calls.Add(new(MethodKey(moveNext), "state-machine", 0, stateType.FullName));
                    else effects.Add(new("unresolved-state-machine", 0, stateType.FullName));
                }
            }
            var ins = method.HasBody ? method.Body.Instructions.ToArray() : [];
            var hasGuard = method.HasBody && method.Body.ExceptionHandlers.Count == 0 && ins.Any(i => i.Operand is MethodReference r && r.DeclaringType.FullName == "Common.ModInformation" && r.Name is "get_IsServer" or "get_IsClient");
            foreach (var i in ins)
            {
                if (i.Operand is MethodReference target)
                {
                    var key = MethodKey(target);
                    calls.Add(new(key, i.OpCode.Name, i.Offset, target.DeclaringType.FullName));
                    foreach (var kind in EffectRules.ForCall(target)) effects.Add(new(kind, i.Offset, target.FullName, hasGuard && kind.Contains("mutation") && GuardAnalysis.IsAuthorityGuarded(ins, i)));
                    if (target.Name.Contains("AddNonSerializedListener") || target.Name.Contains("AddSerializedListener") || target.Name is "AddGameMenuOption" or "AddDialogLine" or "AddPlayerLine" or "Subscribe")
                    {
                        // Pair only straight-line delegate construction; branching/escaped delegates stay unresolved.
                        var prior = ins.TakeWhile(x => x != i).Reverse().TakeWhile(x => x.OpCode.FlowControl is not FlowControl.Branch and not FlowControl.Cond_Branch and not FlowControl.Return).Take(40).ToArray();
                        var callbacks = prior.Where(x => x.OpCode.Code is Code.Ldftn or Code.Ldvirtftn).Select(x => MethodKey((MethodReference)x.Operand)).Distinct().ToArray();
                        foreach (var cb in callbacks) registrations.Add(new(cb, target.FullName, i.Offset));
                        if (callbacks.Length == 0) effects.Add(new("unresolved-callback", i.Offset, target.FullName));
                    }
                    if (target is GenericInstanceMethod generic && target.Name.StartsWith("Register", StringComparison.Ordinal) && target.DeclaringType.Namespace.StartsWith("DryIoc", StringComparison.Ordinal) && generic.GenericArguments.Count == 2)
                        bindings.Add(new(generic.GenericArguments[0].FullName, generic.GenericArguments[1].FullName));
                    if (target.Name == "Resolve" && target is GenericInstanceMethod)
                        effects.Add(new("di-resolution", i.Offset, ((GenericInstanceMethod)target).GenericArguments[0].FullName));
                }
                if (i.OpCode.Code is Code.Stfld or Code.Stsfld)
                {
                    var field = (FieldReference)i.Operand;
                    effects.Add(new(type.Name.EndsWith("VM", StringComparison.Ordinal) || type.BaseType?.Name == "ViewModel" ? "presentation-state-write" : "state-write-unclassified", i.Offset, field.FullName, hasGuard && GuardAnalysis.IsAuthorityGuarded(ins, i)));
                }
                if (i.OpCode.Code == Code.Calli) effects.Add(new("unresolved-indirect-call", i.Offset, "calli"));
            }
            if (method.IsPInvokeImpl) effects.Add(new("native-boundary", 0, method.PInvokeInfo.EntryPoint));
            var engineRoot = type.BaseType?.Name is "CampaignBehaviorBase" or "MissionBehavior" or "MissionLogic" or "MBSubModuleBase" || type.BaseType?.Name.EndsWith("Model", StringComparison.Ordinal) == true;
            var root = method.IsVirtual && engineRoot && !method.IsConstructor || method.Name.StartsWith("Execute", StringComparison.Ordinal)
                || method.CustomAttributes.Any(x => x.AttributeType.Namespace.StartsWith("HarmonyLib", StringComparison.Ordinal))
                || type.CustomAttributes.Any(x => x.AttributeType.Namespace.StartsWith("HarmonyLib", StringComparison.Ordinal))
                || method.Name == ".cctor";
            methods.Add(new(MethodKey(method), $"{a.MainModule.Mvid:D}:{method.MetadataToken.ToInt32():X8}", type.FullName, method.Name,
                type.BaseType?.FullName, type.Interfaces.Select(x => x.InterfaceType.FullName).ToArray(), root, method.IsVirtual, calls.ToArray(), effects.ToArray(), registrations.ToArray()));
        }
        return new(a.Name.Name, hash, a.MainModule.Mvid.ToString("D"), a.MainModule.AssemblyReferences.Select(x => x.Name).ToArray(), methods.ToArray(), bindings.Distinct().ToArray(), typeNodes);
    }
    private static IEnumerable<TypeDefinition> Flatten(IEnumerable<TypeDefinition> types)
    { foreach (var t in types) { yield return t; foreach (var n in Flatten(t.NestedTypes)) yield return n; } }
}

internal static class EffectRules
{
    public static IEnumerable<string> ForCall(MethodReference m)
    {
        var t = m.DeclaringType.FullName; var n = m.Name;
        if (t.Contains("ScreenManager") || t.Contains("Gauntlet") || t.Contains("MapScreen") || t.Contains("InformationManager")) yield return "presentation";
        if (n.Contains("Inquiry")) yield return "interaction";
        if (n is "AddGameMenu" or "AddGameMenuOption" or "AddDialogLine" or "AddPlayerLine") yield return "interaction-registration";
        if (n is "get_MainHero" or "get_MainParty" or "get_PlayerClan" or "get_MainAgent" or "get_CurrentSettlement") yield return "implicit-player-context";
        if (t == "Common.ModInformation" && n is "get_IsServer" or "get_IsClient") yield return "authority-predicate";
        if (n == "SyncData" || t.StartsWith("TaleWorlds.SaveSystem", StringComparison.Ordinal)) yield return "persistence";
        if (t.StartsWith("TaleWorlds.CampaignSystem.Actions.", StringComparison.Ordinal) || n is "AddToCounts" or "AddSkillXp"
            || (t == "TaleWorlds.CampaignSystem.Hero" && n == "set_Gold") || (t == "TaleWorlds.CampaignSystem.Clan" && n == "set_Influence")) yield return "campaign-mutation";
        else if (t == "TaleWorlds.MountAndBlade.Agent" && n.StartsWith("set_", StringComparison.Ordinal)) yield return "mission-mutation";
        else if (t.StartsWith("TaleWorlds.", StringComparison.Ordinal) && n.StartsWith("set_", StringComparison.Ordinal)) yield return "engine-write-unclassified";
        if (t.StartsWith("System.Collections.Generic.", StringComparison.Ordinal) && n is "Add" or "Remove" or "Clear" or "set_Item") yield return "collection-write-unclassified";
        if (t.StartsWith("System.Reflection", StringComparison.Ordinal) || t.StartsWith("HarmonyLib.AccessTools", StringComparison.Ordinal) || n is "DynamicInvoke" or "CreateInstance") yield return "reflection-boundary";
        if (t.Contains("MBRandom") || t == "System.Random") yield return "randomness";
        if (t.StartsWith("System.IO.", StringComparison.Ordinal)) yield return "external-io";
        if (t.StartsWith("Common.Network", StringComparison.Ordinal) || t.StartsWith("Common.Messaging", StringComparison.Ordinal)) yield return "network-reference-not-coverage";
        if (t == "System.Reflection.Assembly" && n.StartsWith("Load", StringComparison.Ordinal)) yield return "dynamic-loading";
    }
}

internal static class GuardAnalysis
{
    // Recognize only a direct, known Coop predicate consumed by a conditional branch.
    // A write is guarded only when ALL paths to it pass through the authorized successor.
    public static bool IsAuthorityGuarded(Instruction[] code, Instruction effect)
    {
        for (var i = 0; i + 1 < code.Length; i++)
        {
            if (code[i].Operand is not MethodReference m || m.DeclaringType.FullName != "Common.ModInformation" || m.Name is not ("get_IsServer" or "get_IsClient")) continue;
            var branch = code[i + 1];
            if (branch.OpCode.Code is not (Code.Brtrue or Code.Brtrue_S or Code.Brfalse or Code.Brfalse_S) || branch.Operand is not Instruction target || i + 2 >= code.Length) continue;
            var positive = branch.OpCode.Code is Code.Brtrue or Code.Brtrue_S;
            var allowed = (m.Name == "get_IsServer") == positive ? target : code[i + 2];
            var forbidden = allowed == target ? code[i + 2] : target;
            if (!Reachable(code[0], effect, branch) && !Reachable(forbidden, effect, null)) return true;
        }
        return false;
    }
    private static bool Reachable(Instruction start, Instruction end, Instruction? excluded)
    {
        var todo = new Stack<Instruction>(); var seen = new HashSet<Instruction>(); todo.Push(start);
        while (todo.TryPop(out var i))
        {
            if (i == excluded || !seen.Add(i)) continue;
            if (i == end) return true;
            if (i.Operand is Instruction jump) todo.Push(jump);
            if (i.Operand is Instruction[] jumps) foreach (var j in jumps) todo.Push(j);
            if (i.OpCode.FlowControl is not (FlowControl.Branch or FlowControl.Return or FlowControl.Throw) && i.Next != null) todo.Push(i.Next);
        }
        return false;
    }
}
