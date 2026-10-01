using Mono.Cecil;
using Mono.Cecil.Cil;
using ModderLords.CompatSync.Coop.Fourberie;

namespace ModderLords.Core.Tests;

/// <summary>
/// The Fourberie layer (src/ModderLords.CompatSync.Coop/Fourberie) binds to Fourberie by name and switches a component
/// off when something moved. This checks those names against a real Fourberie.dll, and reads Fourberie's own IL so that
/// an update which saves a new field or registers a new event handler fails here rather than going unnoticed. Runs
/// against MODDERLORDS_FOURBERIE_DLL or the Workshop/Modules install; does nothing when none exists (CI has no Fourberie).
/// </summary>
public sealed class FourberieSurfaceTests
{
    private static string? FourberieDll()
    {
        var candidates = new[]
        {
            Environment.GetEnvironmentVariable("MODDERLORDS_FOURBERIE_DLL"),
            @"D:\Program Files (x86)\Steam\steamapps\workshop\content\261550\2875710877\bin\Win64_Shipping_Client\Fourberie.dll",
            @"C:\Program Files (x86)\Steam\steamapps\workshop\content\261550\2875710877\bin\Win64_Shipping_Client\Fourberie.dll",
            @"D:\Program Files (x86)\Steam\steamapps\common\Mount & Blade II Bannerlord\Modules\Fourberie\bin\Win64_Shipping_Client\Fourberie.dll",
        };
        return candidates.FirstOrDefault(p => !string.IsNullOrEmpty(p) && File.Exists(p));
    }

    private static ModuleDefinition Module(string dll) => ModuleDefinition.ReadModule(dll, new ReaderParameters { ReadSymbols = false });

    /// <summary>The shapes FbBookCodec and FbGameRefs can write; a saved field of any other type would be left out of the book.</summary>
    private static readonly HashSet<string> WritableTypes = new(StringComparer.Ordinal)
    {
        "System.Boolean",
        "TaleWorlds.CampaignSystem.Hero",
        "TaleWorlds.CampaignSystem.Party.MobileParty",
        "TaleWorlds.CampaignSystem.Settlements.Settlement",
        "TaleWorlds.CampaignSystem.Settlements.Village",
        "System.Collections.Generic.List`1<System.String>",
        "System.Collections.Generic.List`1<TaleWorlds.CampaignSystem.Party.MobileParty>",
        "System.Collections.Generic.List`1<TaleWorlds.CampaignSystem.Roster.TroopRosterElement>",
        "System.Collections.Generic.Dictionary`2<System.String,TaleWorlds.CampaignSystem.CampaignTime>",
        "System.Collections.Generic.Dictionary`2<System.String,System.Int32>",
        "System.Collections.Generic.Dictionary`2<System.String,System.String>",
        "System.Collections.Generic.Dictionary`2<System.String,TaleWorlds.CampaignSystem.Hero>",
        "System.Collections.Generic.Dictionary`2<System.Int32,System.Int32>",
        "System.Collections.Generic.Dictionary`2<System.Int32,TaleWorlds.CampaignSystem.CampaignTime>",
    };

    [Fact]
    public void TheBookHoldsExactlyWhatFourberieSaves()
    {
        if (FourberieDll() is not { } dll) return;
        using var module = Module(dll);
        var behavior = module.GetType(FbFields.Behavior);
        Assert.NotNull(behavior);
        var syncData = behavior.Methods.Single(m => m.Name == "SyncData" && m.Parameters.Count == 1);
        var saved = syncData.Body.Instructions
            .Where(i => i.OpCode == OpCodes.Ldsflda && i.Operand is FieldReference)
            .Select(i => ((FieldReference)i.Operand).Name)
            .ToHashSet(StringComparer.Ordinal);
        Assert.Equal(saved.OrderBy(n => n), FbFields.Persisted.OrderBy(n => n));

        foreach (var name in FbFields.Persisted)
        {
            var field = behavior.Fields.Single(f => f.Name == name);
            Assert.True(field.IsStatic && !field.IsInitOnly, name + " must be a writable static");
            Assert.True(WritableTypes.Contains(field.FieldType.FullName), name + " has type " + field.FieldType.FullName + ", which the book format does not carry");
        }
    }

    [Fact]
    public void EveryScratchFieldExistsAndIsWritable()
    {
        if (FourberieDll() is not { } dll) return;
        using var module = Module(dll);
        foreach (var (type, name) in FbFields.Scratch)
        {
            var field = module.GetType(type)?.Fields.SingleOrDefault(f => f.Name == name);
            Assert.True(field != null, type + "." + name + " not found");
            Assert.True(field!.IsStatic && !field.IsInitOnly, type + "." + name + " must be a writable static");
        }
    }

    [Fact]
    public void EveryFannedOutHandlerExistsWithItsParameterCount()
    {
        if (FourberieDll() is not { } dll) return;
        using var module = Module(dll);
        foreach (var (type, method, count, _) in FbFields.Handlers)
        {
            var matches = module.GetType(type)?.Methods.Where(m => m.Name == method).ToList() ?? new List<MethodDefinition>();
            Assert.True(matches.Count == 1, $"{type}.{method}: {matches.Count} definition(s)");
            Assert.True(matches[0].Parameters.Count == count, $"{type}.{method} takes {matches[0].Parameters.Count} parameter(s), the layer expects {count}");
        }
    }

    [Fact]
    public void EveryRegisteredEventHandlerIsAccountedFor()
    {
        if (FourberieDll() is not { } dll) return;
        using var module = Module(dll);
        var known = FbFields.Handlers.Select(h => (h.Type, h.Method)).Concat(FbFields.NotFannedOut).ToHashSet();
        var unaccounted = new List<string>();
        foreach (var type in module.Types.Where(t => t.Methods.Any(m => m.Name == "RegisterEvents" && m.HasBody)))
        {
            var register = type.Methods.Single(m => m.Name == "RegisterEvents");
            foreach (var target in register.Body.Instructions.Where(i => i.OpCode == OpCodes.Ldftn).Select(i => (MethodReference)i.Operand))
            {
                var owner = target.DeclaringType.FullName;
                if (!known.Contains((owner, target.Name))) unaccounted.Add(owner + "." + target.Name);
            }
        }
        Assert.True(unaccounted.Count == 0, "handler(s) neither run per player nor listed as deliberately not: " + string.Join(", ", unaccounted));
    }

    /// <summary>Where each party Fourberie makes is handled (phase 4). Keyed by the method, or for a lambda the method it is written in.</summary>
    private static readonly Dictionary<string, string> PartyCreators = new(StringComparer.Ordinal)
    {
        ["Fourberie.FourbBanditBehavior.FOnDailyTickSettlement"] = "server tick",
        ["Fourberie.FourberieBehavior.HourlyTick"] = "server tick",
        ["Fourberie.FourberieBehavior.CreateVirtualParty"] = "ledger (FbLedgers)",
        ["Fourberie.HelperSubInsuScam.SpawnCaravan"] = "relayed",
        ["Fourberie.HelperSubInsuScam.SpawnBandits"] = "relayed",
        ["Fourberie.FourbBanditBehavior.LadsOnDoneClicked"] = "not in co-op (FbGaps.LadsDonePrefix)",
        ["Fourberie.FourbSafeHouseBehavior.CrookedEncounterStart"] = "not in co-op",
        ["Fourberie.FourbSafeHouseBehavior.BanditsRelEncounterStart"] = "not in co-op",
        ["Fourberie.HelperSubNotableExtortion.CreateExtoParty"] = "not in co-op (only TroopRosterManageExto calls it)",
        ["Fourberie.HelperSubCarambush.TroopRosterManageCarambush"] = "not in co-op",
        ["Fourberie.HelperSubSabotage.Menu"] = "not in co-op (the send-raiders lambda)",
    };

    private static readonly string[] Factories =
    {
        "TaleWorlds.CampaignSystem.Party.MobileParty::CreateParty",
        "TaleWorlds.CampaignSystem.Party.PartyComponents.BanditPartyComponent::CreateBanditParty",
        "TaleWorlds.CampaignSystem.Party.PartyComponents.BanditPartyComponent::CreateLooterParty",
        "TaleWorlds.CampaignSystem.Party.PartyComponents.CaravanPartyComponent::CreateCaravanParty",
        "TaleWorlds.CampaignSystem.Party.PartyComponents.CustomPartyComponent::CreateCustomPartyWithTroopRoster",
    };

    /// <summary>"Type.Method" of the method a body belongs to; a compiler-generated lambda maps to the method it is written in.</summary>
    private static string Owner(MethodDefinition m)
    {
        var type = m.DeclaringType;
        while (type.IsNested && type.Name.StartsWith("<", StringComparison.Ordinal)) type = type.DeclaringType;
        var name = m.Name.StartsWith("<", StringComparison.Ordinal) ? m.Name.Substring(1, m.Name.IndexOf('>') - 1) : m.Name;
        if (name.Length == 0 && m.DeclaringType.Name.StartsWith("<", StringComparison.Ordinal)) name = m.Name;
        return type.FullName + "." + name;
    }

    private static IEnumerable<MethodDefinition> AllBodies(ModuleDefinition module) =>
        module.Types.SelectMany(t => new[] { t }.Concat(t.NestedTypes).Concat(t.NestedTypes.SelectMany(n => n.NestedTypes)))
            .SelectMany(t => t.Methods).Where(m => m.HasBody);

    [Fact]
    public void EveryPlaceFourberieMakesAPartyIsHandled()
    {
        if (FourberieDll() is not { } dll) return;
        using var module = Module(dll);
        var unhandled = new SortedSet<string>();
        foreach (var body in AllBodies(module))
            foreach (var i in body.Body.Instructions)
                if (i.Operand is MethodReference r && Factories.Contains(r.DeclaringType.FullName + "::" + r.Name) && !PartyCreators.ContainsKey(Owner(body)))
                    unhandled.Add(Owner(body) + " (" + body.Name + " -> " + r.Name + ")");
        Assert.True(unhandled.Count == 0, "Fourberie makes parties where the layer does not handle it: " + string.Join("; ", unhandled));
    }

    [Fact]
    public void OnlyTheSendRaidersLambdaInSabotageMakesABanditParty()
    {
        if (FourberieDll() is not { } dll) return;
        using var module = Module(dll);
        var sabotage = module.GetType("Fourberie.HelperSubSabotage");
        var callers = new[] { sabotage }.Concat(sabotage.NestedTypes).SelectMany(t => t.Methods).Where(m => m.HasBody)
            .Where(m => m.Body.Instructions.Any(i => i.Operand is MethodReference r && r.Name == "CreateBanditParty")).ToList();
        Assert.Single(callers);
        Assert.Contains(callers[0].Body.Instructions, i => i.Operand is MethodReference r && r.Name == "SetMoveRaidSettlement");
    }

    [Fact]
    public void CreateVirtualPartyIsOnlyEverAskedForAKnownLedger()
    {
        if (FourberieDll() is not { } dll) return;
        using var module = Module(dll);
        var behavior = module.GetType(FbFields.Behavior);
        var create = behavior.Methods.Single(m => m.Name == "CreateVirtualParty");
        Assert.True(create.IsStatic && create.Parameters.Count == 2 && create.Parameters[0].ParameterType.FullName == "System.String"
            && create.ReturnType.FullName == "TaleWorlds.CampaignSystem.Party.MobileParty");
        var roles = FbLedgerRoles.Ids.ToHashSet(StringComparer.Ordinal);
        foreach (var body in AllBodies(module))
        {
            var list = body.Body.Instructions.ToList();
            for (var k = 0; k < list.Count; k++)
            {
                if (list[k].Operand is not MethodReference r || r.Name != "CreateVirtualParty") continue;
                // The role id is the string pushed for the first argument: the nearest ldstr before the name's TextObject.
                var role = list.Take(k).Reverse().Where(i => i.OpCode == OpCodes.Ldstr).Select(i => (string)i.Operand).Skip(1).FirstOrDefault();
                Assert.True(role != null && roles.Contains(role), Owner(body) + " asks CreateVirtualParty for '" + role + "'");
            }
        }
    }

    [Fact]
    public void RelayedAndBlockedMethodsExistWithTheirShapes()
    {
        if (FourberieDll() is not { } dll) return;
        using var module = Module(dll);
        foreach (var (type, method, count) in FbRelayTable.Methods)
        {
            var m = module.GetType(type)?.Methods.SingleOrDefault(x => x.Name == method);
            Assert.True(m != null && m.IsStatic && m.Parameters.Count == count && m.ReturnType.FullName == "System.Void", type + "." + method + " must be a static void with " + count + " parameter(s)");
        }
        foreach (var (type, method, count) in FbGapsTable.Blocked)
        {
            var m = module.GetType(type)?.Methods.SingleOrDefault(x => x.Name == method);
            Assert.True(m != null && m.Parameters.Count == count, type + "." + method + " not found with " + count + " parameter(s)");
        }
        var lads = module.GetType("Fourberie.FourbBanditBehavior").Methods.Single(m => m.Name == "LadsOnDoneClicked");
        Assert.True(lads.Parameters.Count == 9 && lads.ReturnType.FullName == "System.Boolean");
    }

    [Fact]
    public void TheReviewedVersionIsTheInstalledOne()
    {
        if (FourberieDll() is not { } dll) return;
        var manifest = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(dll)!, "..", "..", "SubModule.xml"));
        if (!File.Exists(manifest)) return;
        // Not a gate (the layer binds by name and checks each member); a reminder to re-review when Fourberie updates.
        Assert.Contains("v1.4.8.2", File.ReadAllText(manifest));
    }
}
