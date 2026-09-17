using System.Collections.Immutable;
using Mono.Cecil;
using Mono.Cecil.Cil;
using ModderLords.Analysis;

namespace ModderLords.Core.Tests;

public sealed class OperationAnalysisTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "operation-analysis-tests", Guid.NewGuid().ToString("N"));
    private readonly string bin;
    public OperationAnalysisTests() { bin = Path.Combine(root, "bin", "Win64_Shipping_Client"); Directory.CreateDirectory(bin); File.WriteAllText(Path.Combine(root, "SubModule.xml"), "<Module/>"); }
    private AssemblyDefinition Assembly(string name) => AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition(name, new Version(1, 0)), name, ModuleKind.Dll);
    private static TypeDefinition Type(AssemblyDefinition a, string ns, string name, TypeReference? parent = null)
    { var t = new TypeDefinition(ns, name, TypeAttributes.Public | TypeAttributes.Class, parent ?? a.MainModule.TypeSystem.Object); a.MainModule.Types.Add(t); return t; }
    private static MethodDefinition Method(TypeDefinition type, string name)
    { var m = new MethodDefinition(name, MethodAttributes.Public | MethodAttributes.Static, type.Module.TypeSystem.Void); type.Methods.Add(m); return m; }
    private static MethodReference External(AssemblyDefinition a, string assembly, string ns, string type, string name, TypeReference? returns = null)
    {
        var reference = a.MainModule.AssemblyReferences.FirstOrDefault(r => r.Name == assembly);
        if (reference == null) { reference = new AssemblyNameReference(assembly, new Version(1, 0)); a.MainModule.AssemblyReferences.Add(reference); }
        return new MethodReference(name, returns ?? a.MainModule.TypeSystem.Void, new TypeReference(ns, type, a.MainModule, reference));
    }
    private void Write(AssemblyDefinition a) { a.Write(Path.Combine(bin, a.Name.Name + ".dll")); }
    private AnalysisReport Analyze(params string[] entries) => new OperationAnalyzer(Path.Combine(root, "cache")).Analyze(new([new("fixture", "1", root, entries.ToImmutableArray(), false)], [bin], [], "v1.4.8"));

    [Fact] public void MixedCallbackTraversesCrossAssemblyAndKeepsPlayerContextWitness()
    {
        using var helper = Assembly("Helper"); var service = Type(helper, "Fixture", "Service"); var commit = Method(service, "Commit");
        commit.Body.Instructions.Add(Instruction.Create(OpCodes.Call, External(helper, "Engine", "TaleWorlds.CampaignSystem", "Clan", "get_PlayerClan", helper.MainModule.TypeSystem.Object)));
        commit.Body.Instructions.Add(Instruction.Create(OpCodes.Pop));
        commit.Body.Instructions.Add(Instruction.Create(OpCodes.Call, External(helper, "Engine", "TaleWorlds.CampaignSystem.Actions", "AwardAction", "Apply")));
        commit.Body.Instructions.Add(Instruction.Create(OpCodes.Ret)); Write(helper);
        using var main = Assembly("Fixture"); var entry = Method(Type(main, "Fixture", "Behavior"), "ExecuteMixed");
        entry.Body.Instructions.Add(Instruction.Create(OpCodes.Call, main.MainModule.ImportReference(commit)));
        entry.Body.Instructions.Add(Instruction.Create(OpCodes.Call, External(main, "Engine", "TaleWorlds.Library", "InformationManager", "ShowInquiry")));
        entry.Body.Instructions.Add(Instruction.Create(OpCodes.Ret)); Write(main);
        var report = Analyze("Fixture.dll"); var operation = Assert.Single(report.Operations.Where(o => o.EntryPoint.Contains("ExecuteMixed")));
        Assert.Equal(AuthorityDomain.Mixed, operation.Authority); Assert.Contains("implicit-player-context", operation.Effects);
        Assert.Contains(operation.Evidence, e => e.Effect == "campaign-mutation" && e.CallChain.Length == 2);
        Assert.DoesNotContain(report.Plan.Contracts, c => c.Decision == ActivationDecision.Activate);
    }
    [Fact] public void RegisterEventsFindsPrivateDelegateAndDoesNotTreatBehaviorAsOneSide()
    {
        using var main = Assembly("Fixture"); var type = Type(main, "Fixture", "Behavior"); var callback = Method(type, "PrivateTick");
        callback.Body.Instructions.Add(Instruction.Create(OpCodes.Call, External(main, "Engine", "TaleWorlds.CampaignSystem.Actions", "AwardAction", "Apply")));
        callback.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        var register = Method(type, "ExecuteRegistration");
        register.Body.Instructions.Add(Instruction.Create(OpCodes.Ldnull));
        register.Body.Instructions.Add(Instruction.Create(OpCodes.Ldftn, callback));
        var ctor = main.MainModule.ImportReference(typeof(Action).GetConstructor([typeof(object), typeof(IntPtr)])!);
        register.Body.Instructions.Add(Instruction.Create(OpCodes.Newobj, ctor));
        var listener = External(main, "Engine", "TaleWorlds.CampaignSystem", "Event", "AddNonSerializedListener");
        listener.Parameters.Add(new ParameterDefinition(main.MainModule.ImportReference(typeof(Action))));
        register.Body.Instructions.Add(Instruction.Create(OpCodes.Call, listener));
        register.Body.Instructions.Add(Instruction.Create(OpCodes.Ret)); Write(main);
        var report = Analyze("Fixture.dll");
        Assert.Contains(report.Operations, o => o.EntryPoint.Contains("PrivateTick") && o.Effects.Contains("campaign-mutation"));
    }
    [Theory] [InlineData(true)] [InlineData(false)]
    public void PredicateMustDominateAndExcludeUnauthorizedPath(bool guarded)
    {
        using var main = Assembly("Fixture"); var method = Method(Type(main, "Fixture", "Behavior"), "ExecuteAward");
        var end = Instruction.Create(OpCodes.Ret);
        var mutation = Instruction.Create(OpCodes.Call, External(main, "Engine", "TaleWorlds.CampaignSystem.Actions", "AwardAction", "Apply"));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Call, External(main, "Common", "Common", "ModInformation", "get_IsServer", main.MainModule.TypeSystem.Boolean)));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Brfalse, guarded ? end : mutation));
        method.Body.Instructions.Add(mutation); method.Body.Instructions.Add(end); Write(main);
        var effect = Analyze("Fixture.dll").Operations.Single(o => o.EntryPoint.Contains("ExecuteAward")).Evidence.Single(e => e.Effect == "campaign-mutation");
        Assert.Equal(guarded, effect.Detail.Contains("dominated"));
    }
    [Fact] public void PersistenceAndReflectionAreNotReplicationOrPurityProofs()
    {
        using var main = Assembly("Fixture"); var method = Method(Type(main, "Fixture", "Behavior"), "ExecuteSave");
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Call, External(main, "Engine", "TaleWorlds.CampaignSystem", "IDataStore", "SyncData")));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Call, External(main, "mscorlib", "System.Reflection", "FieldInfo", "SetValue")));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret)); Write(main);
        var op = Analyze("Fixture.dll").Operations.Single(); Assert.Equal(CapabilityState.Incomplete, op.Status.Replication); Assert.Contains("reflection-boundary", op.Effects);
        Assert.NotEqual(AuthorityDomain.SharedCalculation, op.Authority);
    }
    [Fact] public void UnreadableAndMissingAssembliesStayExplicitlyUnknown()
    {
        File.WriteAllText(Path.Combine(bin, "Broken.dll"), "not a managed binary");
        var report = Analyze("Broken.dll", "Missing.dll");
        Assert.Contains(report.Gaps, g => g.Code == "unreadable-assembly"); Assert.Contains(report.Gaps, g => g.Code == "missing-entry");
        Assert.Empty(report.Operations); Assert.DoesNotContain(report.Plan.Contracts, c => c.Decision == ActivationDecision.Activate);
    }
    [Fact] public void PayloadSelectionUsesGameMinorAndFingerprintsSelectedBytes()
    {
        using var a = Assembly("Payload"); var method = Method(Type(a, "Fixture", "Behavior"), "Execute"); method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        var dir = Path.Combine(root, "payloads", "v13"); Directory.CreateDirectory(dir); a.Write(Path.Combine(dir, "Payload.dll"));
        var request = new AnalysisRequest([new("RTSCameraUniversal", "1", root, [], false)], [], [], "v1.4.8");
        var report = new OperationAnalyzer(Path.Combine(root, "cache")).Analyze(request);
        Assert.Contains(report.Fingerprints, f => f.Name == "Payload.dll"); Assert.Single(report.Operations);
        var missing = new OperationAnalyzer(Path.Combine(root, "cache")).Analyze(request with { GameVersion = "v1.2.9" });
        Assert.Contains(missing.Gaps, g => g.Code == "payload-missing");
    }
    [Fact] public void InheritedLifecycleAndVirtualImplementationAreTraversed()
    {
        using var main = Assembly("Fixture");
        var engine = External(main, "Engine", "TaleWorlds.CampaignSystem", "CampaignBehaviorBase", "RegisterEvents").DeclaringType;
        var parent = Type(main, "Fixture", "BaseBehavior", engine);
        var child = Type(main, "Fixture", "DerivedBehavior", parent);
        var register = Method(child, "RegisterEvents"); register.Attributes = MethodAttributes.Public | MethodAttributes.Virtual;
        var service = Type(main, "Fixture", "Service");
        var declared = Method(service, "Tick"); declared.Attributes = MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.NewSlot;
        declared.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        var concrete = Type(main, "Fixture", "ConcreteService", service);
        var tick = Method(concrete, "Tick"); tick.Attributes = MethodAttributes.Public | MethodAttributes.Virtual;
        tick.Body.Instructions.Add(Instruction.Create(OpCodes.Call, External(main, "Engine", "TaleWorlds.CampaignSystem.Actions", "AwardAction", "Apply")));
        tick.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        register.Body.Instructions.Add(Instruction.Create(OpCodes.Ldnull));
        register.Body.Instructions.Add(Instruction.Create(OpCodes.Callvirt, declared));
        register.Body.Instructions.Add(Instruction.Create(OpCodes.Ret)); Write(main);
        Assert.Contains(Analyze("Fixture.dll").Operations, o => o.EntryPoint.Contains("DerivedBehavior::RegisterEvents") && o.Effects.Contains("campaign-mutation"));
    }
    [Fact] public void ReadOnlyModelWithoutExternalCallsCanBeSharedCalculation()
    {
        using var main = Assembly("Fixture");
        var parent = External(main, "Engine", "TaleWorlds.CampaignSystem.GameComponents", "DefaultCostModel", "Cost").DeclaringType;
        var method = Method(Type(main, "Fixture", "CostModel", parent), "Cost");
        method.Attributes = MethodAttributes.Public | MethodAttributes.Virtual;
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret)); Write(main);
        Assert.Equal(AuthorityDomain.SharedCalculation, Assert.Single(Analyze("Fixture.dll").Operations).Authority);
    }
    [Theory] [InlineData(true)] [InlineData(false)]
    public void ConstantDiRegistrationTraversesImplementationButUnknownRegistrationStaysUnresolved(bool recognized)
    {
        using var main = Assembly("Fixture");
        var service = Type(main, "Fixture", "IService");
        var implementation = Type(main, "Fixture", "Service");
        var work = Method(implementation, "Work"); work.Attributes = MethodAttributes.Public | MethodAttributes.Virtual;
        work.Body.Instructions.Add(Instruction.Create(OpCodes.Call, External(main, "Engine", "TaleWorlds.CampaignSystem.Actions", "AwardAction", "Apply")));
        work.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        var setup = Method(Type(main, "Fixture", "Setup"), "ExecuteSetup");
        var registration = External(main, "Container", recognized ? "DryIoc" : "UnknownContainer", "Extensions", "Register");
        registration.GenericParameters.Add(new GenericParameter("TService", registration));
        registration.GenericParameters.Add(new GenericParameter("TImplementation", registration));
        var concreteRegistration = new GenericInstanceMethod(registration);
        concreteRegistration.GenericArguments.Add(service); concreteRegistration.GenericArguments.Add(implementation);
        setup.Body.Instructions.Add(Instruction.Create(OpCodes.Call, concreteRegistration));
        var resolve = External(main, "Container", "DryIoc", "Extensions", "Resolve");
        resolve.GenericParameters.Add(new GenericParameter("TService", resolve));
        var concreteResolve = new GenericInstanceMethod(resolve); concreteResolve.GenericArguments.Add(service);
        setup.Body.Instructions.Add(Instruction.Create(OpCodes.Call, concreteResolve));
        setup.Body.Instructions.Add(Instruction.Create(OpCodes.Ret)); Write(main);
        var operation = Analyze("Fixture.dll").Operations.Single(o => o.EntryPoint.Contains("ExecuteSetup"));
        Assert.Equal(recognized, operation.Effects.Contains("campaign-mutation"));
        Assert.Equal(!recognized, operation.Effects.Contains("unresolved-di"));
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
    [Fact] public void CompilerStateMachineEffectsReachTheOriginalEntry()
    {
        using var main = Assembly("Fixture");
        var machine = Type(main, "Fixture", "GeneratedState");
        var moveNext = Method(machine, "MoveNext");
        moveNext.Body.Instructions.Add(Instruction.Create(OpCodes.Call, External(main, "Engine", "TaleWorlds.CampaignSystem.Actions", "AwardAction", "Apply")));
        moveNext.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        var entry = Method(Type(main, "Fixture", "Commands"), "ExecuteAsync");
        var ctor = main.MainModule.ImportReference(typeof(System.Runtime.CompilerServices.AsyncStateMachineAttribute).GetConstructor([typeof(System.Type)])!);
        var attribute = new CustomAttribute(ctor);
        attribute.ConstructorArguments.Add(new CustomAttributeArgument(main.MainModule.ImportReference(typeof(System.Type)), machine));
        entry.CustomAttributes.Add(attribute); entry.Body.Instructions.Add(Instruction.Create(OpCodes.Ret)); Write(main);
        var operation = Analyze("Fixture.dll").Operations.Single(o => o.EntryPoint.Contains("ExecuteAsync"));
        Assert.Contains(operation.Evidence, e => e.Effect == "campaign-mutation" && e.CallChain.Any(c => c.Contains("MoveNext")));
    }
}
