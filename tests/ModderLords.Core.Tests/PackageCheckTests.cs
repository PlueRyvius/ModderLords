using System.Formats.Tar;
using System.IO.Compression;
using ModderLords.Core.Testing;
using Xunit;

namespace ModderLords.Core.Tests;

/// <summary>A mod's release zip is checked before it is published, and unpacked for a run without touching Modules.</summary>
public sealed class PackageCheckTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "ml-package-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static readonly Dictionary<string, int> Floors = new() { [""] = 4, ["ModuleData/Dyes/masks"] = 2 };

    /// <summary>A mod folder on disk: SubModule.xml with <paramref name="id"/> and <paramref name="version"/>, and <paramref name="masks"/> dye masks.</summary>
    private string Folder(string id = "ModularSmithing2", string version = "v0.9.50", int masks = 3, string? extraRoot = null)
    {
        var src = Path.Combine(_root, "src-" + Guid.NewGuid().ToString("N"));
        var mod = Directory.CreateDirectory(Path.Combine(src, "ModularSmithing2")).FullName;
        File.WriteAllText(Path.Combine(mod, "SubModule.xml"), $"<Module><Id value=\"{id}\"/><Version value=\"{version}\"/></Module>");
        Directory.CreateDirectory(Path.Combine(mod, "bin", "Win64_Shipping_Client"));
        File.WriteAllText(Path.Combine(mod, "bin", "Win64_Shipping_Client", "ModularSmithing.Core.dll"), "code");
        Directory.CreateDirectory(Path.Combine(mod, "ModuleData", "Dyes", "masks"));
        for (int i = 0; i < masks; i++) File.WriteAllText(Path.Combine(mod, "ModuleData", "Dyes", "masks", $"m{i}.png"), "mask");
        if (extraRoot is not null) File.WriteAllText(Path.Combine(src, extraRoot), "stray");
        return src;
    }

    private string Zip(string folder)
    {
        var zip = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".zip");
        ZipFile.CreateFromDirectory(folder, zip);
        return zip;
    }

    [Fact]
    public void A_good_release_zip_passes_with_its_counts_and_hash()
    {
        var report = PackageCheck.Inspect(Zip(Folder()), PackageCheck.ModIdOf(Zip(Folder())), "v0.9.50", Floors);
        Assert.True(report.Ok, report.ToText());
        Assert.Equal(("ModularSmithing2", "v0.9.50", 5), (report.ModId, report.Version, report.Files));
        Assert.Contains(report.Counts, c => c is ("ModuleData/Dyes/masks", 3, 2));
        Assert.Equal(64, report.Sha256.Length);
    }

    [Fact]
    public void A_tar_archive_named_zip_is_refused_before_anything_else()
    {
        // MS2's first v0.9.50 zip: Git Bash's GNU tar ignores -a and wrote a tar whatever the name said.
        var tar = Path.Combine(_root, "ModularSmithing2-v0.9.50.zip");
        TarFile.CreateFromDirectory(Folder(), tar, includeBaseDirectory: false);
        var report = PackageCheck.Inspect(tar, "ModularSmithing2", "v0.9.50", Floors);
        Assert.False(report.Ok);
        Assert.Contains("a tar archive", Assert.Single(report.Problems));
        Assert.Null(PackageCheck.ModIdOf(tar));
    }

    [Theory]
    [InlineData("ModularSmithing2", "v0.9.49", 3, null, "says version v0.9.49, not v0.9.50")]
    [InlineData("ModularSmithing3", "v0.9.50", 3, null, "says Id ModularSmithing3")]
    [InlineData("ModularSmithing2", "v0.9.50", 1, null, "ModuleData/Dyes/masks/ holds 1 files; a release has at least 2")]
    [InlineData("ModularSmithing2", "v0.9.50", 3, "readme.txt", "also holds readme.txt at its root")]
    public void A_zip_with_the_wrong_version_id_payload_or_extra_files_is_not_fit_to_publish(string id, string version, int masks, string? extra, string problem)
    {
        var report = PackageCheck.Inspect(Zip(Folder(id, version, masks, extra)), "ModularSmithing2", "v0.9.50", Floors);
        Assert.False(report.Ok);
        Assert.Contains(report.Problems, p => p.Contains(problem));
    }

    [Fact]
    public void Unpacking_puts_the_mod_folder_where_the_run_pins_it_and_replaces_an_older_one()
    {
        var zip = Zip(Folder());
        var parent = Path.Combine(_root, "package-tests", "ModularSmithing2-v0.9.50-abcd1234");
        Directory.CreateDirectory(Path.Combine(parent, "ModularSmithing2", "stale"));
        var folder = PackageCheck.Unpack(zip, "ModularSmithing2", parent);
        Assert.Equal(Path.Combine(parent, "ModularSmithing2"), folder);
        Assert.True(File.Exists(Path.Combine(folder, "SubModule.xml")));
        Assert.False(Directory.Exists(Path.Combine(folder, "stale")));
        Assert.False(Directory.Exists(parent + ".staging"));
    }

    [Theory]
    [InlineData("v0.9.50", "0.9.50", true)]
    [InlineData("v0.9.50.0", "v0.9.50", true)]
    [InlineData("v0.9.50", "v0.9.51", false)]
    [InlineData(null, "v0.9.50", false)]
    public void Versions_match_with_or_without_the_v_and_a_trailing_zero(string? a, string b, bool same) =>
        Assert.Equal(same, PackageCheck.SameVersion(a, b));

    [Fact]
    public void The_smithing_module_says_what_a_release_zip_holds_at_least()
    {
        var smithing = TestSuites.Module("smithing")!;
        Assert.Equal(1000, smithing.PackageAtLeast["ModuleData/Dyes/masks"]);
        Assert.True(smithing.PackageAtLeast[""] > 1000);
    }
}
