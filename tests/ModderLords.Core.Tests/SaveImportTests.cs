using System.Text;
using ModderLords.Coop.Launch;
using ModderLords.Coop.Saves;
using Xunit;

namespace ModderLords.Core.Tests;

/// <summary>
/// Phase 1b: the server can only load a world that exists, and the only world it can make itself is a copy of the
/// pre-baked vanilla template. A world built with the server's own modules has to come from the real game.
/// </summary>
public class SaveImportTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("modderlords-import").FullName;
    private readonly string _client;
    private readonly ServerPaths _paths;

    public SaveImportTests()
    {
        _client = Path.Combine(_root, "client");
        Directory.CreateDirectory(_client);
        _paths = ServerPaths.Create(Path.Combine(_root, "DedicatedServer"), Path.Combine(_root, "CoopData"));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string ClientSave(string name, string modules, string versions = "")
    {
        var json = "{\"List\":{\"Modules\":\"" + modules + "\"" + versions + "}}";
        var path = Path.Combine(_client, name + ".sav");
        var body = Encoding.UTF8.GetBytes(json);
        using var fs = File.Create(path);
        fs.Write(BitConverter.GetBytes(body.Length));
        fs.Write(body);
        fs.Write(new byte[128]);   // stand-in for the world payload, so the copy is not trivially empty
        return path;
    }

    [Fact]
    public void Imports_a_modded_client_world_into_the_servers_saves()
    {
        var src = ClientSave("MyCampaign", "Native;SandBoxCore;Sandbox;Coop;TAOM;TAOM_Map");
        var r = SavePreparer.ImportFrom(src, _paths, "taomworld");

        Assert.Equal(SavePreparer.SavePath(_paths, "taomworld"), r.SavePath);
        Assert.True(File.Exists(r.SavePath));
        Assert.Equal(new FileInfo(src).Length, new FileInfo(r.SavePath).Length);
        Assert.False(r.Overwrote);
        Assert.Empty(r.Warnings);
    }

    [Fact]
    public void Warns_when_the_world_was_not_built_with_coop()
    {
        // Measured on the real machine: the only client save present listed [ModularSmithing2] and no Coop module.
        // That world cannot be expected to satisfy Coop's handshake, and the import is the last place to say so.
        var src = ClientSave("Testing", "Native;SandBoxCore;Sandbox;ModularSmithing2");
        var r = SavePreparer.ImportFrom(src, _paths, "testing");
        Assert.Contains(r.Warnings, w => w.Contains("does not list a Coop module"));
        Assert.True(File.Exists(r.SavePath));   // warned, not blocked
    }

    [Fact]
    public void Refuses_to_replace_an_existing_save_unless_asked()
    {
        var src = ClientSave("MyCampaign", "Native;Coop;TAOM");
        SavePreparer.ImportFrom(src, _paths, "world");
        var ex = Assert.Throws<InvalidOperationException>(() => SavePreparer.ImportFrom(src, _paths, "world"));
        Assert.Contains("already exists", ex.Message);
    }

    [Fact]
    public void Overwriting_keeps_the_save_it_replaced()
    {
        var first = ClientSave("First", "Native;Coop;TAOM");
        SavePreparer.ImportFrom(first, _paths, "world");
        var second = ClientSave("Second", "Native;Coop;TAOM;TAOM_Map");

        var r = SavePreparer.ImportFrom(second, _paths, "world", overwrite: true);
        Assert.True(r.Overwrote);
        Assert.Contains(r.Warnings, w => w.Contains("has been kept as"));
        // A hosted world is not something to destroy on a flag; the previous one is still on disk.
        Assert.NotEmpty(Directory.GetFiles(_paths.SavesDir, "world.sav.replaced-*"));
    }

    [Fact]
    public void A_missing_source_names_the_path_rather_than_half_importing()
    {
        var missing = Path.Combine(_client, "nope.sav");
        Assert.Throws<FileNotFoundException>(() => SavePreparer.ImportFrom(missing, _paths, "world"));
        Assert.False(File.Exists(SavePreparer.SavePath(_paths, "world")));
    }

    [Fact]
    public void A_file_that_is_not_a_save_warns_rather_than_throwing()
    {
        var junk = Path.Combine(_client, "junk.sav");
        File.WriteAllBytes(junk, new byte[] { 1, 2, 3 });
        var r = SavePreparer.ImportFrom(junk, _paths, "world");
        Assert.Contains(r.Warnings, w => w.Contains("header could not be read"));
    }

    [Fact]
    public void The_save_name_is_still_validated()
    {
        var src = ClientSave("MyCampaign", "Native;Coop");
        Assert.Throws<ArgumentException>(() => SavePreparer.ImportFrom(src, _paths, "sub/dir"));
        Assert.Throws<ArgumentException>(() => SavePreparer.ImportFrom(src, _paths, "world.sav"));
    }

    [Fact]
    public void An_imported_world_is_accepted_by_the_preflight_check_that_rejects_a_mismatched_one()
    {
        // The two halves of the plan have to compose: importing a world built with TAOM is only worth doing if
        // launching TAOM against it then stops warning. Verified against the real CLI too.
        var src = ClientSave("MyCampaign", "Native;SandBoxCore;Sandbox;Coop;TAOM;TAOM_Map",
            ",\"Module_TAOM\":\"v1.0.0.0\",\"Module_TAOM_Map\":\"v1.0.0.0\"");
        var r = SavePreparer.ImportFrom(src, _paths, "taomworld");

        var taom = new Dictionary<string, string> { ["TAOM"] = "v1.0.0", ["TAOM_Map"] = "v1.0.0" };
        Assert.Empty(Core.Saves.SaveModuleCheck.Messages(r.SavePath, taom));

        var wrong = new Dictionary<string, string> { ["ModularSmithing2"] = "v0.9.30" };
        Assert.Contains("WARNING", Assert.Single(Core.Saves.SaveModuleCheck.Messages(r.SavePath, wrong)));
    }
}
