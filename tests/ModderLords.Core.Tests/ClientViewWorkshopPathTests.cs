using ModderLords.Core.Launch;
using ModderLords.Core.Modules;
using ModderLords.Core.Overlay;

namespace ModderLords.Core.Tests;

/// <summary>
/// A path counted up from the game's exe reaches the real Workshop from inside a view. Reported 2026-10-22: Fourberie
/// opens "../../../../workshop/content/261550/2875710877/FourberieConfig.xml" from the exe folder, and the client died
/// at startup when the view put that path somewhere that did not exist.
/// </summary>
public class ClientViewWorkshopPathTests
{
    [Fact]
    public void A_Workshop_file_counted_up_from_the_exe_resolves_inside_a_view()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "ModderLords-viewworkshop-" + Guid.NewGuid().ToString("N"));
        var steamapps = Path.Combine(root, "steamapps");
        var game = Path.Combine(steamapps, "common", "Mount & Blade II Bannerlord");
        var item = Path.Combine(steamapps, "workshop", "content", "261550", "2875710877");
        Directory.CreateDirectory(GamePaths.ClientBin(game));
        Directory.CreateDirectory(Path.Combine(game, "bin", "Gaming.Desktop.x64_Shipping_Client"));
        Directory.CreateDirectory(item);
        File.WriteAllText(Path.Combine(item, "SubModule.xml"), "<Module><Id value='Fourberie'/><Name value='Fourberie'/><Version value='v1.0.0'/><SubModules/></Module>");
        File.WriteAllText(Path.Combine(item, "FourberieConfig.xml"), "<Config/>");
        string? view = null;
        try
        {
            var mod = ModuleCatalog.TryParse(item, ModuleSourceKind.Workshop, out _)!;
            var plan = new ClientLaunchPlan { GameRoot = game, ModuleIds = ["Fourberie"], SelectedModules = [mod], RequiresIsolatedView = true };
            view = ClientModuleView.Create(plan, Path.Combine(root, "views"));
            Assert.StartsWith(Path.Combine(root, "views"), view);
            foreach (var bin in new[] { "Win64_Shipping_Client", "Gaming.Desktop.x64_Shipping_Client" })
            {
                var path = Path.GetFullPath(Path.Combine(view, "bin", bin, "../../../../workshop/content/261550/2875710877/FourberieConfig.xml"));
                Assert.StartsWith(Path.Combine(root, "views"), path);
                Assert.Equal("<Config/>", File.ReadAllText(path));
            }
        }
        finally
        {
            // Unlink before removing the owned fixture; never recurse through a junction.
            if (view is not null)
            {
                foreach (var link in Directory.GetDirectories(Path.Combine(view, "Modules"))) Junction.Remove(link);
                foreach (var dir in Directory.GetDirectories(view)) if (Junction.IsJunction(dir)) Junction.Remove(dir);
                Junction.Remove(Path.Combine(view, "..", "..", "workshop"));
            }
            Directory.Delete(root, true);
        }
    }
}
