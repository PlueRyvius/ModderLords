using System.IO;
using ModderLords.App.ViewModels;
using Xunit;

namespace ModderLords.App.Tests;

/// <summary>
/// The Folders window's line under the Coop server folder names the game version the server runs, and says when it is
/// not the game installed here: players on this game cannot join it, so hosting uses the package for this game.
/// </summary>
public sealed class FolderChecksServerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ModderLords-folder-checks-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    /// <summary>A complete server package, as <c>ServerPaths.Validate</c> wants it, whose engine runs <paramref name="runs"/>.</summary>
    private string Package(string name, string runs)
    {
        var root = Path.Combine(_root, name);
        var engine = Path.Combine(root, "engine");
        Directory.CreateDirectory(Path.Combine(engine, "dotnet"));
        Directory.CreateDirectory(Path.Combine(engine, "bin", "Win64_Shipping_Server"));
        File.WriteAllText(Path.Combine(root, "BannerlordCoopServer.exe"), "");
        File.WriteAllText(Path.Combine(engine, "dotnet", "dotnet.exe"), "");
        File.WriteAllText(Path.Combine(engine, "bin", "Win64_Shipping_Server", "TaleWorlds.Starter.DotNetCore.dll"), "");
        foreach (var module in new[] { "Native", "SandBoxCore", "SandBox", "Coop", "DedicatedServer.Windows" })
        {
            Directory.CreateDirectory(Path.Combine(engine, "Modules", module));
            File.WriteAllText(Path.Combine(engine, "Modules", module, "SubModule.xml"),
                $"<Module><Id value=\"{module}\"/><Version value=\"{runs}\"/></Module>");
        }
        return root;
    }

    [Fact]
    public void A_server_for_this_game_is_ok_and_says_which_game()
    {
        var nightly = Package("nightly", "v1.5.7");

        var (ok, text) = FolderChecks.Server(nightly, null, "v1.5.7");

        Assert.True(ok);
        Assert.Contains("(game v1.5.7)", text);
    }

    [Fact]
    public void A_server_for_another_game_is_flagged_and_names_the_one_hosting_uses()
    {
        var workshop = Package("workshop", "v1.4.8");
        var nightly = Package("nightly", "v1.5.7");

        var (ok, text) = FolderChecks.Server(workshop, nightly, "v1.5.7");

        Assert.False(ok);
        Assert.Contains("runs game v1.4.8, and the game here is v1.5.7", text);
        Assert.Contains(nightly, text);
    }

    [Fact]
    public void The_automatic_choice_names_its_game()
    {
        var nightly = Package("nightly", "v1.5.7");

        var (ok, text) = FolderChecks.Server(null, nightly, "v1.5.7");

        Assert.True(ok);
        Assert.Equal("Automatic: " + nightly + " (game v1.5.7)", text);
    }
}
