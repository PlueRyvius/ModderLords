using Mono.Cecil;

namespace ModderLords.Core.Tests;

/// <summary>
/// The TAOM layer (src/ModderLords.CompatSync.Coop/Taom) binds to TAOM by name and switches a component off when a
/// member it needs is missing. This checks those names against a real TAOM.dll, so a TAOM update that moves one shows
/// up here instead of as a silent "off" line in a player's log. Runs against MODDERLORDS_TAOM_DLL, or the usual Steam
/// install; does nothing when neither exists (CI has no TAOM).
/// </summary>
public sealed class TaomSurfaceTests
{
    private static string? TaomDll()
    {
        var candidates = new[]
        {
            Environment.GetEnvironmentVariable("MODDERLORDS_TAOM_DLL"),
            @"D:\Program Files (x86)\Steam\steamapps\common\Mount & Blade II Bannerlord\Modules\TAOM\bin\Win64_Shipping_Client\TAOM.dll",
            @"C:\Program Files (x86)\Steam\steamapps\common\Mount & Blade II Bannerlord\Modules\TAOM\bin\Win64_Shipping_Client\TAOM.dll",
        };
        return candidates.FirstOrDefault(p => !string.IsNullOrEmpty(p) && File.Exists(p));
    }

    // Type, member, parameter types (full names), return type (full name) — exactly what TaomJoinGrant.Bind and
    // TaomFieldCamp.Bind look up.
    public static TheoryData<string, string, string[], string> Methods => new()
    {
        { "TAOM.Features.PlayerPossession.PlayerPossessionService", "CaptureCharacterCreationChoices",
            ["TAOM.Features.PlayerPossession.PlayerCharacterCreationChoices"], "System.Void" },
        { "TAOM.Features.PlayerPossession.IJoinReconciliationService", "ReapplyCharacterCreationPackage",
            ["TAOM.Features.PlayerPossession.PlayerCharacterCreationChoices", "System.String", "System.String"], "System.Boolean" },
        { "TAOM.Features.PlayerPossession.PlayerCharacterCreationChoices", ".ctor",
            ["System.String", "System.String", "System.Int32", "System.String"], "System.Void" },
        { "TAOM.Features.FieldCamp.CampService", "Establish", ["TAOM.Features.FieldCamp.Domain.CampType"], "System.Boolean" },
        { "TAOM.Features.FieldCamp.CampService", "Fortify", [], "System.Boolean" },
        { "TAOM.Features.FieldCamp.CampService", "ToggleForaging", [], "System.Boolean" },
        { "TAOM.Features.FieldCamp.CampService", "BreakPlayerCamp", [], "System.Void" },
        { "TAOM.Features.FieldCamp.CampService", "HourlyTick", [], "System.Void" },
        { "TAOM.Features.FieldCamp.CampService", "AddMoraleToMainParty", ["System.Single"], "System.Void" },
        { "TAOM.Features.FieldCamp.CampService", "ForageHour", ["TAOM.Features.FieldCamp.Domain.CampState"], "System.Void" },
        { "TAOM.Features.FieldCamp.UI.FieldCampOverlayVM", "ExecuteOpenCampMenu", [], "System.Void" },
        { "TAOM.Features.FieldCamp.UI.FieldCampOverlayVM", "Refresh", [], "System.Void" },
        { "TAOM.Features.FieldCamp.UI.MapScreenCampMenuActivationQuery", "get_IsMainPartyStationary", [], "System.Boolean" },
    };

    [Theory]
    [MemberData(nameof(Methods))]
    public void TaomMethodTheLayerBindsToExists(string type, string method, string[] parameters, string returns)
    {
        if (TaomDll() is not { } dll) return;
        using var module = ModuleDefinition.ReadModule(dll);
        var t = module.GetType(type);
        Assert.True(t != null, $"{type} is missing from {dll}");
        var match = t!.Methods.Where(m => m.Name == method).FirstOrDefault(m =>
            m.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(parameters) && m.ReturnType.FullName == returns);
        Assert.True(match != null, $"{type}.{method}({string.Join(", ", parameters)}) : {returns} is missing or changed shape");
    }

    [Fact]
    public void TaomMembersTheLayerReadsExist()
    {
        if (TaomDll() is not { } dll) return;
        using var module = ModuleDefinition.ReadModule(dll);
        Assert.Equal("System.Collections.Generic.List`1<System.String>",
            module.GetType("TAOM.Features.PlayerPossession.PlayerPossessionBehavior")?.Fields.FirstOrDefault(f => f.Name == "_reconciledHeroIds")?.FieldType.FullName);
        Assert.NotNull(module.GetType("TAOM.Features.FieldCamp.CampService")?.Properties.FirstOrDefault(p => p.Name == "PlayerCamp"));
        Assert.NotNull(module.GetType("TAOM.Features.FieldCamp.UI.FieldCampOverlayVM")?.Fields.FirstOrDefault(f => f.Name == "_activation"));
        Assert.NotNull(module.GetType("TAOM.Features.FieldCamp.UI.FieldCampOverlayVM")?.Properties.FirstOrDefault(p => p.Name == "CanMakeCamp"));
        Assert.True(module.GetType("TAOM.Features.FieldCamp.Domain.CampType")?.IsEnum == true);
        Assert.NotNull(module.GetType("TAOM.Features.FieldCamp.ICampService"));
        var resolve = module.GetType("TAOM.IoC")?.Methods.FirstOrDefault(m => m.Name == "Resolve" && m.IsStatic && m.Parameters.Count == 0);
        Assert.True(resolve is { HasGenericParameters: true }, "TAOM.IoC.Resolve<T>() is missing");
    }
}
