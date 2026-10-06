using System.Runtime.Versioning;
using ModderLords.Coop.Launch;
using ModderLords.Core.Modules;
using ModderLords.Core.Overlay;
using Xunit;

namespace ModderLords.Core.Tests;

/// <summary>The private server view on a throwaway server tree: real junctions. Windows only.</summary>
[SupportedOSPlatform("windows")]
public sealed class ServerViewTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mbc-view-" + Guid.NewGuid().ToString("N"));
    private readonly string _server;
    private readonly string _views;

    public ServerViewTests()
    {
        _server = Path.Combine(_root, "DedicatedServer");
        _views = Path.Combine(_root, "views");
        foreach (var module in new[] { "Native", "Coop" })
        {
            Directory.CreateDirectory(Path.Combine(_server, "engine", "Modules", module));
            File.WriteAllText(Path.Combine(_server, "engine", "Modules", module, "SubModule.xml"),
                $"<Module><Name value=\"{module}\"/><Id value=\"{module}\"/><Version value=\"v1.0.0\"/><SubModules/></Module>");
        }
        Directory.CreateDirectory(Path.Combine(_server, "engine", "bin", "Win64_Shipping_Server"));
        File.WriteAllText(Path.Combine(_server, "engine", "bin", "Win64_Shipping_Server", "starter.dll"), "x");
        Directory.CreateDirectory(Path.Combine(_server, "server-data"));
        File.WriteAllText(Path.Combine(_server, "engine", "loose.txt"), "loose");
        File.WriteAllText(Path.Combine(_server, "BannerlordCoopServer.exe"), "host");
    }

    public void Dispose()
    {
        // Junctions must be removed as links, never recursed into.
        foreach (var d in Directory.EnumerateDirectories(_root, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
            if (Junction.IsJunction(d)) Directory.Delete(d, false);
        Directory.Delete(_root, true);
    }

    private ServerPaths Paths(bool force) =>
        ServerView.For(ServerPaths.Create(_server, Path.Combine(_root, "data"), Path.Combine(_root, "coop")), _views, force);

    [Fact]
    public void A_drive_that_holds_junctions_gets_no_view()
    {
        var paths = Paths(force: false);

        Assert.Null(paths.ViewRoot);
        Assert.Equal(paths.StockModulesRoot, paths.ModulesRoot);
        Assert.Null(ServerView.Describe(paths));
        ServerView.Build(paths);
        Assert.False(Directory.Exists(_views));
    }

    [Fact]
    public void The_view_links_every_server_folder_and_keeps_modules_real()
    {
        var paths = Paths(force: true);
        ServerView.Build(paths);

        Assert.StartsWith(_views, paths.ViewRoot!);
        Assert.StartsWith(paths.ViewRoot!, paths.ServerBin);
        Assert.True(Junction.PointsTo(Path.Combine(paths.ViewRoot!, "server-data"), Path.Combine(_server, "server-data")));
        Assert.True(Junction.PointsTo(Path.Combine(paths.EngineRoot, "bin"), Path.Combine(_server, "engine", "bin")));
        Assert.True(File.Exists(Path.Combine(paths.ServerBin, "starter.dll")));
        Assert.Equal("loose", File.ReadAllText(Path.Combine(paths.EngineRoot, "loose.txt")));

        Assert.False(Junction.IsJunction(paths.EngineRoot));
        Assert.False(Junction.IsJunction(paths.ModulesRoot));
        Assert.True(Junction.PointsTo(Path.Combine(paths.ModulesRoot, "Native"), Path.Combine(paths.StockModulesRoot, "Native")));
        Assert.True(File.Exists(Path.Combine(paths.ModulesRoot, "Coop", "SubModule.xml")));
        // The installation itself is validated, and nothing was written into it.
        Assert.DoesNotContain(Directory.EnumerateDirectories(paths.StockModulesRoot), Junction.IsJunction);
    }

    [Fact]
    public void A_mod_is_linked_into_the_view_and_not_into_the_installation()
    {
        var paths = Paths(force: true);
        ServerView.Build(paths);
        var modFolder = Path.Combine(_root, "mods", "SomeMod");
        Directory.CreateDirectory(Path.Combine(modFolder, "bin", "Win64_Shipping_Server"));
        File.WriteAllText(Path.Combine(modFolder, "SubModule.xml"),
            "<Module><Name value=\"SomeMod\"/><Id value=\"SomeMod\"/><Version value=\"v1.0.0\"/><SubModules/></Module>");
        var mod = ModuleCatalog.TryParse(modFolder, ModuleSourceKind.Custom, out _)!;

        var plan = OverlayPlanner.Plan(Path.Combine(_root, "overlay"), paths.ModulesRoot, new[] { new ModSelection(mod, ServerRole.AsShipped) });
        var result = new OverlayApplier().Apply(plan);

        Assert.Empty(result.Failed);
        Assert.True(File.Exists(Path.Combine(paths.ModulesRoot, "SomeMod", "SubModule.xml")));
        Assert.False(Directory.Exists(Path.Combine(paths.StockModulesRoot, "SomeMod")));

        // Refreshing the view leaves the overlay's link alone and still finds only the stock modules as stock.
        ServerView.Build(paths);
        Assert.True(Junction.IsJunction(Path.Combine(paths.ModulesRoot, "SomeMod")));
        var catalog = ModuleCatalog.Scan(paths.StockModulesRoot, null, Array.Empty<string>(), Array.Empty<string>());
        Assert.Equal(new[] { "Coop", "Native" }, catalog.Modules.Where(m => m.IsStock).Select(m => m.FolderName).OrderBy(n => n));
    }

    [Fact]
    public void A_stock_folder_that_has_gone_loses_its_link()
    {
        var paths = Paths(force: true);
        Directory.CreateDirectory(Path.Combine(paths.StockModulesRoot, "Retired"));
        ServerView.Build(paths);
        Assert.True(Junction.IsJunction(Path.Combine(paths.ModulesRoot, "Retired")));

        Directory.Delete(Path.Combine(paths.StockModulesRoot, "Retired"));
        ServerView.Build(paths);

        Assert.False(Directory.Exists(Path.Combine(paths.ModulesRoot, "Retired")));
        Assert.True(Junction.IsJunction(Path.Combine(paths.ModulesRoot, "Native")));
    }

    [Fact]
    public void A_mod_that_cannot_be_linked_is_reported_as_failed()
    {
        var paths = Paths(force: true);
        ServerView.Build(paths);
        var modFolder = Path.Combine(_root, "mods", "Gone");
        Directory.CreateDirectory(modFolder);
        File.WriteAllText(Path.Combine(modFolder, "SubModule.xml"),
            "<Module><Name value=\"Gone\"/><Id value=\"Gone\"/><Version value=\"v1.0.0\"/><SubModules/></Module>");
        var mod = ModuleCatalog.TryParse(modFolder, ModuleSourceKind.Custom, out _)!;
        var plan = OverlayPlanner.Plan(Path.Combine(_root, "overlay"), paths.ModulesRoot, new[] { new ModSelection(mod, ServerRole.AsShipped) });
        Directory.Delete(modFolder, true);

        var result = new OverlayApplier().Apply(plan);

        Assert.Single(result.Failed);
        Assert.StartsWith("Gone: ", result.Failed[0]);
    }
}
