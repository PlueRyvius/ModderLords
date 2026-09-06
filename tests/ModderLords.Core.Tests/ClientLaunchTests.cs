using System.Collections.Generic;
using System.Linq;
using Bannerlord.ModuleManager;
using ModderLords.Core.Launch;
using ModderLords.Coop.Launch;
using ModderLords.Core.Modules;
using Xunit;

namespace ModderLords.Core.Tests;

public class ClientLaunchTests
{
    /// <summary>An official module as it exists on the player's machine: real, in the game folder, not a phantom.
    /// The folder and the id differ for SandBox/Sandbox, which is exactly what the head lookup matches on.</summary>
    private static DiscoveredModule Official(string folder, string id) =>
        new(id, "v1.4.8", @"G\Modules\" + folder, ModuleSourceKind.GameModules, new ModuleInfoExtended
        { Id = id, Name = id, Version = ApplicationVersion.Empty, IsOfficial = true });

    private static DiscoveredModule Mod(string id, string[]? needs = null, string[]? loadAfterThis = null) =>
        new(id, "v1.0.0", @"W\" + id, ModuleSourceKind.Workshop, new ModuleInfoExtended
        {
            Id = id, Name = id, Version = ApplicationVersion.Empty,
            DependentModules = (needs ?? []).Select(n => new DependentModule { Id = n }).ToList(),
            ModulesToLoadAfterThis = (loadAfterThis ?? []).Select(n => new DependentModule { Id = n }).ToList(),
        });

    private static readonly DiscoveredModule[] ClientOfficials =
    [
        Official("Native", "Native"), Official("SandBoxCore", "SandBoxCore"),
        Official("SandBox", "Sandbox"), Official("StoryMode", "StoryMode"),
    ];

    [Fact]
    public void ModuleTokenIsTheEngineArgumentInOrder()
    {
        var plan = new ClientLaunchPlan { GameRoot = @"C:\game", ModuleIds = ["Native", "SandBoxCore", "Sandbox", "MyMod"] };
        Assert.Equal("_MODULES_*Native*SandBoxCore*Sandbox*MyMod*_MODULES_", plan.ModuleToken);
        Assert.Equal(@"C:\game\bin\Win64_Shipping_Client\Bannerlord.exe", plan.Exe);
        Assert.Equal([plan.ModuleToken], plan.Arguments);
    }

    [Fact]
    public void AStarInAModuleIdIsRejectedBecauseItSeparatesIds()
    {
        var plan = new ClientLaunchPlan { GameRoot = @"C:\game", ModuleIds = ["Native", "Bad*Mod"] };
        Assert.Contains(plan.Validate(), p => p.Contains("Bad*Mod"));
    }

    [Fact]
    public void EmptySelectionIsRejected()
    {
        var plan = new ClientLaunchPlan { GameRoot = @"C:\game", ModuleIds = [] };
        Assert.Contains(plan.Validate(), p => p.Contains("No modules"));
    }

    /// <summary>The client profile pins nothing after the mods and invents no modules: StoryMode and the rest are
    /// installed, so they are ordered as themselves rather than faked the way the dedicated server has to.</summary>
    [Fact]
    public void ClientProfileOrdersOfficialsFirstThenMods()
    {
        var r = LoadOrder.Compute(ClientOfficials, [Mod("MyMod", needs: ["Sandbox"])], null, LoadOrder.Profile.Client);

        Assert.Equal(["Native", "SandBoxCore", "Sandbox", "StoryMode", "MyMod"], r.ModuleIds);
        Assert.DoesNotContain("DedicatedServer.Windows", r.ModuleIds);
    }

    /// <summary>
    /// The rule the 2026-09-06 probe proved matters: ButterLib and the other frameworks declare that Native loads
    /// after them, and placing them anywhere else makes the game open a "terminate now?" warning on startup.
    /// </summary>
    [Fact]
    public void FrameworksDeclaringLoadNativeAfterMeGoBeforeNative()
    {
        var mods = new[] { Mod("Bannerlord.ButterLib", loadAfterThis: ["Native"]), Mod("PlainMod") };

        var order = LoadOrder.Compute(ClientOfficials, mods, null, LoadOrder.Profile.Client).ModuleIds.ToList();

        Assert.True(order.IndexOf("Bannerlord.ButterLib") < order.IndexOf("Native"));
        Assert.True(order.IndexOf("PlainMod") > order.IndexOf("Sandbox"));
    }

    /// <summary>An official the player has not asked for is simply absent from the token — the launcher never
    /// enables something just because it is installed.</summary>
    [Fact]
    public void OfficialsNotInTheProfileAreNotLaunched()
    {
        var chosen = ClientOfficials.Where(m => m.Id != "StoryMode").ToList();

        var r = LoadOrder.Compute(chosen, [Mod("MyMod")], null, LoadOrder.Profile.Client);

        Assert.DoesNotContain("StoryMode", r.ModuleIds);
    }

    /// <summary>The server profile is untouched by the client one: Coop and DedicatedServer.Windows stay pinned last.</summary>
    [Fact]
    public void ServerProfileStillPinsCoopAndDedicatedServerLast()
    {
        DiscoveredModule Stock(string folder, string id) =>
            new(id, "v1.4.8", @"S\" + folder, ModuleSourceKind.ServerStock, new ModuleInfoExtended
            { Id = id, Name = id, Version = ApplicationVersion.Empty, IsOfficial = true });
        var stock = new[]
        {
            Stock("Native", "Native"), Stock("SandBoxCore", "SandBoxCore"), Stock("SandBox", "Sandbox"),
            Stock("Coop", "CoopNightly"), Stock("DedicatedServer.Windows", "DedicatedServer.Windows"),
        };

        var r = LoadOrder.Compute(stock, [Mod("MyMod")], null);   // default profile = dedicated server

        Assert.Equal(["Native", "SandBoxCore", "Sandbox", "MyMod", "CoopNightly", "DedicatedServer.Windows"], r.ModuleIds);
    }
}
