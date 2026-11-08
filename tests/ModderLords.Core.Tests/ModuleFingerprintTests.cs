using ModderLords.Core.Testing;
using ModderLords.Testing;
using Xunit;

namespace ModderLords.Core.Tests;

/// <summary>The install fingerprint the launcher and the games both compute (decisions 23 and 24).</summary>
public sealed class ModuleFingerprintTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ml-fingerprint-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private string Mod(string name, string data = "<Items/>", string dll = "code", string art = "pixels")
    {
        var folder = Path.Combine(_root, name);
        Write(folder, "SubModule.xml", "<Module/>");
        Write(folder, "ModuleData/items.xml", data);
        Write(folder, "bin/Win64_Shipping_Client/Mod.dll", dll);
        Write(folder, "bin/Win64_Shipping_Server/Mod.dll", "server " + dll);
        Write(folder, "AssetPackages/art.tpac", art);
        return folder;
    }

    private static void Write(string folder, string relative, string text)
    {
        var path = Path.Combine(folder, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    [Fact]
    public void Two_copies_with_the_same_files_match_wherever_they_are()
    {
        var a = ModuleFingerprint.Of(Mod("a"), ModuleFingerprint.ClientBin);
        var b = ModuleFingerprint.Of(Mod("b"), ModuleFingerprint.ClientBin);
        Assert.Equal(a.ToString(), b.ToString());
        Assert.Equal(5, a.Files);
    }

    [Fact]
    public void Changed_data_changes_the_data_hash_only()
    {
        // The MS2 failure: new DLLs, old data files, same version string.
        var a = ModuleFingerprint.Of(Mod("a"), ModuleFingerprint.ClientBin);
        var b = ModuleFingerprint.Of(Mod("b", data: "<Items stat='ammo.missile_speed'/>"), ModuleFingerprint.ClientBin);
        Assert.NotEqual(a.Data, b.Data);
        Assert.Equal(a.Code, b.Code);
    }

    [Fact]
    public void Code_is_read_from_the_sides_own_bin_folder()
    {
        var folder = Mod("a");
        Assert.NotEqual(ModuleFingerprint.Of(folder, ModuleFingerprint.ClientBin).Code, ModuleFingerprint.Of(folder, ModuleFingerprint.ServerBin).Code);
        Assert.Equal(ModuleFingerprint.Of(folder, ModuleFingerprint.ClientBin).Data, ModuleFingerprint.Of(folder, ModuleFingerprint.ServerBin).Data);
    }

    [Fact]
    public void Other_files_count_by_size_not_content()
    {
        var same = ModuleFingerprint.Of(Mod("a", art: "pixels"), ModuleFingerprint.ClientBin);
        var sameSize = ModuleFingerprint.Of(Mod("b", art: "PIXELS"), ModuleFingerprint.ClientBin);
        var bigger = ModuleFingerprint.Of(Mod("c", art: "more pixels"), ModuleFingerprint.ClientBin);
        Assert.Equal(same.Other, sameSize.Other);
        Assert.NotEqual(same.Other, bigger.Other);
    }

    [Fact]
    public void Excluded_folders_are_left_out()
    {
        var folder = Mod("a");
        Write(folder, "ModuleData/ServerOnly/rules.xml", "<Rules/>");
        var all = ModuleFingerprint.Of(folder, ModuleFingerprint.ClientBin);
        var without = ModuleFingerprint.Of(folder, ModuleFingerprint.ClientBin, ["ModuleData\\ServerOnly"]);
        Assert.NotEqual(all.Data, without.Data);
        Assert.Equal(ModuleFingerprint.Of(Mod("b"), ModuleFingerprint.ClientBin).Data, without.Data);
    }

    [Fact]
    public void The_picked_copies_file_round_trips_and_leaves_server_code_out()
    {
        var folder = Mod("Armor");
        var module = new ModderLords.Core.Modules.DiscoveredModule("ModularArmor2", "v1.3.0", folder, ModderLords.Core.Modules.ModuleSourceKind.GameModules, null!);
        Directory.CreateDirectory(Path.Combine(_root, "run"));
        PickedCopies.Write(Path.Combine(_root, "run"), "server", [module]);
        PickedCopies.Write(Path.Combine(_root, "run"), "client:1", [module]);

        var server = ModuleFingerprint.ReadExpected(File.ReadAllText(Path.Combine(_root, "run", ModuleFingerprint.ExpectedFile("server")))).Single();
        var client = ModuleFingerprint.ReadExpected(File.ReadAllText(Path.Combine(_root, "run", ModuleFingerprint.ExpectedFile("client:1")))).Single();
        Assert.Equal(("ModularArmor2", ModuleFingerprint.Of(folder, ModuleFingerprint.ClientBin).Data, "", Path.GetFullPath(folder)), server);
        Assert.Equal(ModuleFingerprint.Of(folder, ModuleFingerprint.ClientBin).Code, client.Code);
    }
}
