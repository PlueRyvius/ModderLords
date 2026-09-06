using System.Text.Json;
using ModderLords.Core.Config;
using ModderLords.Coop.Config;
using Xunit;

namespace ModderLords.Core.Tests;

public class CommentedJsonTests
{
    private const string Sample = """
        {
          // Campaign difficulty
          "difficulty": {
            "playerReceivedDamage": "VeryEasy", // VeryEasy: -75%
            "birthAndDeath": false,
          },
          /* options */
          "modOptions": {
            "battleSize": 1000,
            "looterPartySizeMultiplier": 1.0,
            "goldFoodInfluenceChangeInBattles": "OneDayMax", // "Disabled" | "Enabled"
            "note": "has } and : inside",
          }
        }
        """;

    [Fact]
    public void Leaves_lists_every_scalar_with_dotted_paths()
    {
        var leaves = CommentedJson.Leaves(Sample);
        Assert.Contains(leaves, l => l.Path == "difficulty.playerReceivedDamage" && l.Kind == JsonValueKind.String);
        Assert.Contains(leaves, l => l.Path == "modOptions.battleSize" && l.RawValue == "1000");
        Assert.Equal(6, leaves.Count);
    }

    [Fact]
    public void SetScalar_replaces_only_the_value_and_keeps_comments()
    {
        var t = CommentedJson.SetScalar(Sample, "difficulty.playerReceivedDamage", "\"Realistic\"");
        t = CommentedJson.SetScalar(t, "modOptions.battleSize", "500");
        t = CommentedJson.SetScalar(t, "difficulty.birthAndDeath", "true");
        Assert.Contains("\"playerReceivedDamage\": \"Realistic\", // VeryEasy: -75%", t);
        Assert.Contains("\"battleSize\": 500,", t);
        Assert.Contains("\"birthAndDeath\": true,", t);
        Assert.Contains("/* options */", t);
        Assert.Contains("has } and : inside", t);
        Assert.Equal(Sample.Length - "VeryEasy".Length + "Realistic".Length - 4 + 3 - "false".Length + "true".Length, t.Length);
        // Still valid JSON-with-comments
        var leaves = CommentedJson.Leaves(t);
        Assert.Equal("500", leaves.First(l => l.Path == "modOptions.battleSize").RawValue);
    }

    [Fact]
    public void SetScalar_is_not_fooled_by_braces_or_colons_inside_strings()
    {
        var t = CommentedJson.SetScalar(Sample, "modOptions.note", "\"x\"");
        Assert.Contains("\"note\": \"x\"", t);
        Assert.DoesNotContain("has } and : inside", t);
    }

    [Fact]
    public void Missing_path_throws()
    {
        Assert.Throws<KeyNotFoundException>(() => CommentedJson.SetScalar(Sample, "modOptions.nope", "1"));
    }

    [Fact]
    public void Encode_validates_by_kind()
    {
        Assert.Equal("true", ModConfig.Encode(JsonValueKind.False, "True"));
        Assert.Equal("0.5", ModConfig.Encode(JsonValueKind.Number, "0.5"));
        Assert.Equal("\"Easy\"", ModConfig.Encode(JsonValueKind.String, "Easy"));
        Assert.Throws<FormatException>(() => ModConfig.Encode(JsonValueKind.Number, "abc"));
    }
}
