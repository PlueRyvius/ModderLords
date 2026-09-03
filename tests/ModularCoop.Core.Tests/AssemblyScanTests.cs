using ModularCoop.Core.Compat;

namespace ModularCoop.Core.Tests;

// Synthetic shapes for the metadata scan (this test assembly is scanned as if it were a mod).
public class ViewModel { }
public sealed class FakeModSettings { public bool Enabled { get; set; } public int Max { get; set; } public float Rate { get; set; } public string Name { get; set; } = ""; public int[] Ignored { get; set; } = []; public int ReadOnly { get; } }
public sealed class FakeSettingsVM : ViewModel { public bool Enabled { get; set; } }
public sealed class FakeStaticConfig { public static int Count; public static bool On { get; set; } public static readonly int Const = 1; }
public sealed class FakeManager { public static FakeManager Instance { get; } = new(); public FakeModSettings Settings { get; } = new(); public FakePlain Plain { get; } = new(); }   // Holder.Instance.Settings reach
public sealed class FakePlain { public static FakePlain? Current { get; set; } public int Value { get; set; } public FakeMode Mode { get; set; } }
public enum FakeMode { A, B }
public sealed class FakeThing { public int Value { get; set; } }                       // no name match, no singleton
public sealed class FakeTemplateSettings { public int Value { get; set; } }             // excluded by name part
public sealed class FakeUIManager { public static FakeUIManager Instance = new(); public int Value { get; set; } } // singleton only + Manager suffix
public sealed class FakeOrphanSettings { public int Value { get; set; } }              // name match but unreachable (no statics, no singleton, no holder)
public abstract class FakeMcmBase<T> { }
public sealed class FakeMcmSettings : FakeMcmBase<FakeMcmSettings> { public static FakeMcmSettings Instance = new(); public int Value { get; set; } }

public sealed class AssemblyScanTests
{
    private static ScanResult ScanSelf() => AssemblyScan.ScanDlls("Tests", [typeof(AssemblyScanTests).Assembly.Location]);

    [Fact]
    public void FindsSettingsShapedClasses_WithValueCounts()
    {
        var r = ScanSelf();
        Assert.Contains("ModularCoop.Core.Tests.FakeModSettings (4 values)", r.SettingsClasses);   // Enabled, Max, Rate, Name
        Assert.Contains("ModularCoop.Core.Tests.FakeStaticConfig (2 values)", r.SettingsClasses);  // Count, On (Const is readonly)
        Assert.Contains("ModularCoop.Core.Tests.FakePlain (2 values)", r.SettingsClasses);         // singleton Current + Value, Mode(enum)
    }

    [Fact]
    public void ExcludesViewModels_NoNameMatch_TemplateNames()
    {
        var r = ScanSelf();
        Assert.DoesNotContain(r.SettingsClasses, s => s.StartsWith("ModularCoop.Core.Tests.FakeSettingsVM"));
        Assert.DoesNotContain(r.SettingsClasses, s => s.StartsWith("ModularCoop.Core.Tests.FakeThing"));
        Assert.DoesNotContain(r.SettingsClasses, s => s.StartsWith("ModularCoop.Core.Tests.FakeTemplateSettings"));
        Assert.DoesNotContain(r.SettingsClasses, s => s.StartsWith("ModularCoop.Core.Tests.FakeManager"));
        Assert.DoesNotContain(r.SettingsClasses, s => s.StartsWith("ModularCoop.Core.Tests.FakeUIManager"));      // singleton-only + Manager suffix
        Assert.DoesNotContain(r.SettingsClasses, s => s.StartsWith("ModularCoop.Core.Tests.FakeOrphanSettings"));  // unreachable
        Assert.DoesNotContain(r.SettingsClasses, s => s.StartsWith("SaveData."));                                   // SaveData namespace segment
        Assert.False(r.UsesMcm);
        Assert.StartsWith("own settings (", r.SettingsSummary);
        Assert.Contains("ModularCoop.Core.Tests.FakeMcmSettings (1 value)", r.SettingsClasses);   // its generic base is local, not from an MCM assembly
        Assert.Equal(9, r.SettingsValueCount);
    }

    [Fact]
    public void Summary_Variants()
    {
        var none = new ScanResult("x", ServerVerdict.ServerSafe, [], [], [], [], [], [], [], false);
        Assert.Equal("none found", none.SettingsSummary);
        var mcm = none with { UsesMcm = true };
        Assert.Equal("MCM", mcm.SettingsSummary);
        var both = mcm with { SettingsClasses = ["A.B (3 values)"] };
        Assert.Equal("MCM + own settings (3 values)", both.SettingsSummary);
        var data = none with { Verdict = ServerVerdict.DataOnly };
        Assert.Equal("", data.SettingsSummary);
    }

    [Fact]
    public void Recipes_CarrySettingsHints_ForUngatedMods()
    {
        var scan = new ScanResult("IG", ServerVerdict.ServerSafe, [], [], [], [], ["IG.Behavior"], [], [], false);
        var set = RecipeSet.Build([("IG", scan, Array.Empty<string>())], "test",
            [("IG", new[] { "IG.Config" }, new[] { "IG.SaveSystem.*" }), ("Other", new[] { "Other.Settings" }, Array.Empty<string>()), ("Empty", Array.Empty<string>(), Array.Empty<string>())]);
        var json = set.ToJson();
        var back = RecipeSet.FromJson(json);
        Assert.Equal(2, back.Mods.Count);
        var ig = back.Mods.Single(m => m.Id == "IG");
        Assert.Single(ig.CampaignBehaviors);
        Assert.Equal(["IG.Config"], ig.Settings!.Include);
        Assert.Equal(["IG.SaveSystem.*"], ig.Settings.Exclude);
        var other = back.Mods.Single(m => m.Id == "Other");
        Assert.Empty(other.CampaignBehaviors);
        Assert.Equal(["Other.Settings"], other.Settings!.Include);
        // The module reads this with MiniJson: Mods[].Settings.Include
        var root = ModularCoop.CompatSync.MiniJson.ParseObject(json);
        var mods = ModularCoop.CompatSync.MiniJson.GetArray(root, "Mods")!;
        var first = (Dictionary<string, object?>)mods[0]!;
        Assert.NotNull(ModularCoop.CompatSync.MiniJson.GetObject(first, "Settings"));
    }
}
