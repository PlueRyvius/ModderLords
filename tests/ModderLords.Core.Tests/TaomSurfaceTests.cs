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
        { "TAOM.Features.FieldCamp.CampService", "CanEstablish", ["TAOM.Features.FieldCamp.Domain.CampType"], "TAOM.Features.FieldCamp.CampBlockReason" },
        { "TAOM.Features.FieldCamp.CampService", "ToggleForaging", [], "System.Boolean" },
        { "TAOM.Features.FieldCamp.CampService", "BreakPlayerCamp", [], "System.Void" },
        { "TAOM.Features.FieldCamp.CampService", "HourlyTick", [], "System.Void" },
        { "TAOM.Features.FieldCamp.CampService", "AddMoraleToMainParty", ["System.Single"], "System.Void" },
        { "TAOM.Features.FieldCamp.CampService", "ForageHour", ["TAOM.Features.FieldCamp.Domain.CampState"], "System.Void" },
        { "TAOM.Features.FieldCamp.UI.FieldCampOverlayVM", "ExecuteOpenCampMenu", [], "System.Void" },
        { "TAOM.Features.FieldCamp.UI.FieldCampOverlayVM", "Refresh", [], "System.Void" },
        { "TAOM.Features.FieldCamp.CampService", "IsMainPartyMoving", [], "System.Boolean" },
        { "TAOM.Features.EliteEmissary.Hooks.EliteEmissaryInquiryPresenter", "ExecutePurchase",
            ["TAOM.Adapters.SettlementOwnerInfo", "System.String", "System.String", "System.Int32"], "System.Void" },
        { "TAOM.Features.EliteEmissary.IEliteEmissaryService", "Purchase",
            ["System.String", "System.String", "System.String", "System.String", "System.Int32"], "TAOM.Features.EliteEmissary.Domain.EmissaryPurchaseResult" },
        { "TAOM.Features.EliteEmissary.IEliteEmissaryService", "IsKeySettlement", ["System.String"], "System.Boolean" },
        { "TAOM.Features.EliteEmissary.Hooks.EliteEmissaryBehavior", "IsEmissaryAccessBlocked", ["TaleWorlds.CampaignSystem.Settlements.Settlement"], "System.Boolean" },
        { "TAOM.Adapters.ISettlementOwnerAdapter", "GetOwnerInfo", ["TaleWorlds.CampaignSystem.Settlements.Settlement"], "TAOM.Adapters.SettlementOwnerInfo" },
        { "TAOM.Features.Siege.SiegeDefenseService", "GrantReward", ["TAOM.Features.Siege.Models.ActiveSiegeDefenseEvent"], "System.Void" },
        { "TAOM.Features.Siege.SiegeDefenseService", "UntrackSettlement", ["System.String"], "System.Void" },
        { "TAOM.Features.Siege.SiegeDefenseService", "GetMessages", ["System.String"], "TAOM.Features.Siege.Models.KingdomSiegeMessages" },
        { "TAOM.Features.Siege.SiegeDefenseService", "Resolve",
            ["System.String", "System.String", "System.String", "System.Int32", "System.Int32", "System.Int32"], "System.String" },
        { "TAOM.Features.Messengers.MessengerCampaignBehavior", "SendMessenger", ["TaleWorlds.CampaignSystem.Hero"], "System.Void" },
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
        var siege = module.GetType("TAOM.Features.Siege.SiegeDefenseService");
        Assert.Equal("System.Collections.Generic.HashSet`1<System.String>", siege?.Fields.FirstOrDefault(f => f.Name == "_locallyClaimed")?.FieldType.FullName);
        Assert.NotNull(siege?.Fields.FirstOrDefault(f => f.Name == "_config"));
        var messages = module.GetType("TAOM.Features.Siege.Models.KingdomSiegeMessages");
        Assert.NotNull(messages?.Properties.FirstOrDefault(p => p.Name == "RewardMessage"));
        var config = module.GetType("TAOM.Features.Siege.Models.SiegeDefenseConfig");
        Assert.NotNull(config?.Properties.FirstOrDefault(p => p.Name == "RewardInfluence"));
        Assert.NotNull(config?.Properties.FirstOrDefault(p => p.Name == "RewardRelation"));
        Assert.NotNull(module.GetType("TAOM.Features.Siege.ISiegeDefenseService"));
        Assert.NotNull(module.GetType("TAOM.Features.FieldCamp.ICampService"));
        var resolve = module.GetType("TAOM.IoC")?.Methods.FirstOrDefault(m => m.Name == "Resolve" && m.IsStatic && m.Parameters.Count == 0);
        Assert.True(resolve is { HasGenericParameters: true }, "TAOM.IoC.Resolve<T>() is missing");
    }

    // Keep in step with MirrorStore.Supported and TaomStateMirror.Mirrored (src/ModderLords.CompatSync.Coop/Taom).
    private static readonly HashSet<string> MirrorTypes = new()
    {
        "System.Int32", "System.Int64", "System.Single", "System.Double", "System.Boolean", "System.String",
        "System.Collections.Generic.List`1<System.String>", "System.Collections.Generic.List`1<System.Int32>",
        "System.Collections.Generic.Dictionary`2<System.String,System.String>",
        "System.Collections.Generic.Dictionary`2<System.String,System.Int32>",
        "System.Collections.Generic.Dictionary`2<System.String,System.Single>",
    };

    [Theory]
    [InlineData("TAOM.Features.Diplomacy.WarOfTheRingBehavior")]
    [InlineData("TAOM.Features.WarOfTheRingMomentum.WarOfTheRingMomentumBehavior")]
    public void MirroredBehaviourSyncsOnlyCarriedTypes(string type)
    {
        if (TaomDll() is not { } dll) return;
        using var module = ModuleDefinition.ReadModule(dll);
        var syncData = module.GetType(type)?.Methods.FirstOrDefault(m => m.Name == "SyncData" && m.Parameters.Count == 1);
        Assert.True(syncData?.HasBody == true, $"{type}.SyncData is missing");
        var used = syncData!.Body.Instructions
            .Select(i => i.Operand).OfType<GenericInstanceMethod>()
            .Where(m => m.Name == "SyncData" && m.DeclaringType.FullName == "TaleWorlds.CampaignSystem.IDataStore")
            .Select(m => m.GenericArguments[0].FullName)
            .ToList();
        Assert.NotEmpty(used);
        Assert.All(used, t => Assert.True(MirrorTypes.Contains(t), $"{type}.SyncData syncs a {t}, which the state mirror does not carry"));
    }

    /// <summary>
    /// The IL check above can fail: Field Camp saves CampState objects, which the mirror cannot carry. It is not in
    /// TaomStateMirror.Mirrored for that reason (camps are relayed by the field-camp component instead).
    /// </summary>
    [Fact]
    public void FieldCampSyncsATypeTheMirrorCannotCarry()
    {
        if (TaomDll() is not { } dll) return;
        using var module = ModuleDefinition.ReadModule(dll);
        var used = module.Types.SelectMany(t => t.Methods)
            .Where(m => m.Name == "SyncData" && m.HasBody && m.DeclaringType.Namespace.StartsWith("TAOM.Features.FieldCamp"))
            .SelectMany(m => m.Body.Instructions).Select(i => i.Operand).OfType<GenericInstanceMethod>()
            .Where(m => m.Name == "SyncData" && m.DeclaringType.FullName == "TaleWorlds.CampaignSystem.IDataStore")
            .Select(m => m.GenericArguments[0].FullName).ToList();
        Assert.Contains(used, t => !MirrorTypes.Contains(t));
    }
}
