using System.Text;
using ModderLords.Core.Compat;

namespace ModderLords.Core.Tests;

/// <summary>
/// The compat database's "this config file must contain this line" rule. First user: TAOM.Dependencies'
/// coop-modules.txt, which lists "Coop" while the Workshop build loads as "CoopNightly".
/// </summary>
public sealed class EnsureLinesApplierTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ml-ensure-" + Guid.NewGuid().ToString("N"));
    public EnsureLinesApplierTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, true);

    private const string TaomShape =
        "# TAOM co-op interop list\r\n" +
        "\r\n" +
        "[modules]\r\n" +
        "# ids that mean a co-op mod is running\r\n" +
        "BannerlordTogether\r\n" +
        "Coop\r\n" +
        "\r\n" +
        "[harmony-owner-prefixes]\r\n" +
        "# empty by default\r\n";

    private static readonly Dictionary<string, string> Tokens = new() { [EnsureLinesApplier.CoopModuleIdToken] = "CoopNightly" };

    [Fact]
    public void Adds_the_value_after_the_last_entry_of_its_section_keeping_everything_else()
    {
        var change = EnsureLinesApplier.Ensure(TaomShape, "modules", "CoopNightly", out var updated);

        Assert.Equal("added CoopNightly under [modules]", change);
        Assert.Equal(TaomShape.Replace("Coop\r\n\r\n", "Coop\r\nCoopNightly\r\n\r\n"), updated);
    }

    [Fact]
    public void A_value_already_present_is_left_alone_case_insensitively()
    {
        var text = TaomShape.Replace("Coop\r\n", "Coop\r\ncoopnightly\r\n");
        Assert.Null(EnsureLinesApplier.Ensure(text, "modules", "CoopNightly", out var updated));
        Assert.Equal(text, updated);
    }

    [Fact]
    public void A_commented_out_copy_does_not_count()
    {
        var text = TaomShape.Replace("Coop\r\n", "Coop\r\n# CoopNightly\r\n");
        Assert.NotNull(EnsureLinesApplier.Ensure(text, "modules", "CoopNightly", out _));
    }

    [Fact]
    public void The_same_value_in_another_section_does_not_count()
    {
        var text = TaomShape + "CoopNightly\r\n";
        Assert.NotNull(EnsureLinesApplier.Ensure(text, "modules", "CoopNightly", out var updated));
        Assert.Contains("Coop\r\nCoopNightly\r\n", updated);
    }

    [Fact]
    public void A_missing_section_is_appended()
    {
        var change = EnsureLinesApplier.Ensure("# header only\r\n", "modules", "CoopNightly", out var updated);
        Assert.Equal("added [modules] with CoopNightly", change);
        Assert.Equal("# header only\r\n\r\n[modules]\r\nCoopNightly\r\n", updated);
    }

    [Fact]
    public void A_missing_file_gets_the_section_and_value()
    {
        EnsureLinesApplier.Ensure(null, "modules", "CoopNightly", out var updated);
        Assert.Equal("[modules]\r\nCoopNightly\r\n", updated);
    }

    [Fact]
    public void Unix_line_endings_are_kept()
    {
        EnsureLinesApplier.Ensure("[modules]\nCoop\n", "modules", "CoopNightly", out var updated);
        Assert.Equal("[modules]\nCoop\nCoopNightly\n", updated);
    }

    [Fact]
    public void Apply_substitutes_the_coop_id_writes_once_and_keeps_the_bom()
    {
        var path = Path.Combine(_dir, "coop-modules.txt");
        File.WriteAllText(path, TaomShape, new UTF8Encoding(true));
        var rules = new[] { new EnsureLine { File = "coop-modules.txt", Section = "modules", Value = "{coopModuleId}" } };

        var first = EnsureLinesApplier.Apply(_dir, rules, Tokens);
        var second = EnsureLinesApplier.Apply(_dir, rules, Tokens);

        Assert.Equal(["coop-modules.txt: added CoopNightly under [modules]"], first);
        Assert.Empty(second);
        var bytes = File.ReadAllBytes(path);
        Assert.True(bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
        Assert.Contains("Coop\r\nCoopNightly\r\n", File.ReadAllText(path));
    }

    [Fact]
    public void An_unresolved_token_leaves_the_file_untouched()
    {
        var path = Path.Combine(_dir, "coop-modules.txt");
        File.WriteAllText(path, TaomShape);
        var messages = EnsureLinesApplier.Apply(_dir, [new EnsureLine { File = "coop-modules.txt", Section = "modules", Value = "{coopModuleId}" }],
            new Dictionary<string, string>());

        Assert.StartsWith("WARNING", Assert.Single(messages));
        Assert.Equal(TaomShape, File.ReadAllText(path));
    }

    [Theory]
    [InlineData("..\\outside.txt")]
    [InlineData("C:\\Windows\\win.ini")]
    public void A_path_outside_the_module_folder_is_refused(string file)
    {
        var messages = EnsureLinesApplier.Apply(_dir, [new EnsureLine { File = file, Value = "x" }], Tokens);
        Assert.StartsWith("WARNING", Assert.Single(messages));
    }

    [Fact]
    public void The_bundled_taom_dependencies_record_carries_the_rule()
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "compat-db.json");
        if (!File.Exists(bundled)) bundled = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "ModderLords.Core", "compat-db.json"));
        var db = CompatDb.Load(bundled, Path.Combine(_dir, "none.local.json"));

        var rule = Assert.Single(db.Find("TAOM.Dependencies")!.EnsureLines);
        Assert.Equal(("coop-modules.txt", "modules", "{coopModuleId}"), (rule.File, rule.Section, rule.Value));
    }
}
