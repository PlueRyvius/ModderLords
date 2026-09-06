using ModderLords.Core.Modules;
using Xunit;

namespace ModderLords.Core.Tests;

/// <summary>
/// Bannerlord versions are a prefix letter then numbers only. BUTR's parser returns alpha 0.0.0 for anything
/// else rather than failing, so a manifest reading "v0.9.30a" was shown in the mod list as "a0.0.0" - a version
/// the mod does not have, and one that sorts below every real version.
/// </summary>
public class ModuleVersionTests
{
    private static string WriteModule(string dir, string id, string version)
    {
        var folder = Path.Combine(dir, id);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "SubModule.xml"), $"""
            <?xml version="1.0" encoding="utf-8"?>
            <Module>
              <Id value="{id}" />
              <Name value="{id}" />
              <Version value="{version}" />
              <ModuleCategory value="Singleplayer" />
              <ModuleType value="Community" />
            </Module>
            """);
        return folder;
    }

    private static DiscoveredModule Parse(string dir, string id, string version)
    {
        var m = ModuleCatalog.TryParse(WriteModule(dir, id, version), ModuleSourceKind.Workshop, out var problem);
        Assert.Null(problem);
        Assert.NotNull(m);
        return m!;
    }

    private static void InTempDir(Action<string> body)
    {
        var dir = Path.Combine(Path.GetTempPath(), "ml-ver-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try { body(dir); }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void A_normal_version_is_reported_as_parsed()
        => InTempDir(dir => Assert.Equal("v0.9.30", Parse(dir, "Good", "v0.9.30").Version));

    [Fact]
    public void A_normal_version_is_not_flagged()
        => InTempDir(dir => Assert.False(Parse(dir, "Good", "v0.9.30").HasUnparsableVersion));

    /// <summary>The case Andy hit: a trailing letter the game cannot read.</summary>
    [Fact]
    public void A_trailing_letter_shows_what_the_manifest_says_not_a0_0_0()
        => InTempDir(dir => Assert.Equal("v0.9.30a", Parse(dir, "Trailing", "v0.9.30a").Version));

    [Fact]
    public void A_trailing_letter_is_flagged()
        => InTempDir(dir => Assert.True(Parse(dir, "Trailing", "v0.9.30a").HasUnparsableVersion));

    /// <summary>The parsed version is what dependency resolution uses, so it must be left alone.</summary>
    [Fact]
    public void The_parsed_version_is_untouched()
        => InTempDir(dir => Assert.Equal(Bannerlord.ModuleManager.ApplicationVersion.Empty,
                                         Parse(dir, "Trailing", "v0.9.30a").Info.Version));

    /// <summary>A mod genuinely at alpha 0.0.0 is not a parse failure and must not be flagged.</summary>
    [Fact]
    public void A_real_alpha_zero_version_is_not_flagged()
        => InTempDir(dir => Assert.False(Parse(dir, "Alpha", "a0.0.0").HasUnparsableVersion));
}
