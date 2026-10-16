using ModderLords.Core.Launch;
using ModderLords.Core.Modules;
using ModderLords.Core.Overlay;

namespace ModderLords.Core.Tests;

/// <summary>
/// The private client view names each mod's folder as it is installed, not by the mod's ID. Reported 2026-10-15: ABUV
/// Expanded (ID ABUVFiefBorders, installed as "ABUV Expanded by FrannCC") loads its button pictures by folder name, so
/// in co-op, where the view had renamed the folder to the ID, its buttons showed no pictures.
/// </summary>
public class ClientViewFolderNameTests
{
    private static DiscoveredModule Mod(string id, string folder) =>
        new(id, "v1.0.0", folder, ModuleSourceKind.Custom, new Bannerlord.ModuleManager.ModuleInfoExtended { Id = id, Name = id });

    [Fact]
    public void A_mod_keeps_the_folder_name_it_is_installed_under()
    {
        var names = ClientModuleView.FolderNames([Mod("ABUVFiefBorders", @"G\Modules\ABUV Expanded by FrannCC"), Mod("Plain", @"G\Modules\Plain\")]);
        Assert.Equal("ABUV Expanded by FrannCC", names["ABUVFiefBorders"]);
        Assert.Equal("Plain", names["Plain"]);
    }

    [Fact]
    public void A_Workshop_copy_keeps_its_item_number_as_a_normal_start_shows_it()
        => Assert.Equal("2859188632", ClientModuleView.FolderNames([Mod("Bannerlord.Harmony", @"W\261550\2859188632")])["Bannerlord.Harmony"]);

    [Fact]
    public void Two_mods_in_folders_of_the_same_name_are_told_apart_by_their_IDs()
    {
        var names = ClientModuleView.FolderNames([Mod("ModA", @"G\Modules\Shared"), Mod("ModB", @"L\mods\shared"), Mod("ModC", @"G\Modules\Own")]);
        Assert.Equal(("ModA", "ModB", "Own"), (names["ModA"], names["ModB"], names["ModC"]));
    }

    [Fact]
    public void A_folder_name_that_meets_another_mods_ID_falls_back_to_IDs_for_every_mod()
    {
        // B lives in a folder called "A"; A's folder name is shared with C, so A falls back to "A". Both would be "A".
        var names = ClientModuleView.FolderNames([Mod("A", @"x\Same"), Mod("C", @"y\Same"), Mod("B", @"z\A")]);
        Assert.Equal(new[] { "A", "C", "B" }, new[] { names["A"], names["C"], names["B"] });
        Assert.Equal(3, names.Values.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void The_view_shows_the_mod_under_its_installed_folder_name()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "ModderLords-viewname-" + Guid.NewGuid().ToString("N"));
        var game = Path.Combine(root, "game");
        var folder = Path.Combine(root, "local", "ABUV Expanded by FrannCC");
        Directory.CreateDirectory(GamePaths.ClientBin(game));
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "SubModule.xml"), "<Module><Id value='ABUVFiefBorders'/><Name value='ABUV'/><Version value='v1.0.0'/><SubModules/></Module>");
        string? view = null;
        try
        {
            var mod = ModuleCatalog.TryParse(folder, ModuleSourceKind.Custom, out _)!;
            var plan = new ClientLaunchPlan { GameRoot = game, ModuleIds = ["ABUVFiefBorders"], SelectedModules = [mod], RequiresIsolatedView = true };
            view = ClientModuleView.Create(plan, Path.Combine(root, "views"));
            Assert.True(Junction.PointsTo(Path.Combine(view, "Modules", "ABUV Expanded by FrannCC"), folder));
            Assert.False(Directory.Exists(Path.Combine(view, "Modules", "ABUVFiefBorders")));
            // The game still finds it by its manifest.
            Assert.Equal("ABUVFiefBorders", Assert.Single(ModuleCatalog.Scan("", view, [], []).Modules).Id);
        }
        finally
        {
            // Unlink before removing the owned fixture; never recurse through a junction.
            if (view is not null)
            {
                foreach (var link in Directory.GetDirectories(Path.Combine(view, "Modules"))) Junction.Remove(link);
                foreach (var dir in Directory.GetDirectories(view)) if (Junction.IsJunction(dir)) Junction.Remove(dir);
            }
            Directory.Delete(root, true);
        }
    }
}
