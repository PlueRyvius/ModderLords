using ModderLords.CompatSync.Coop.Admin;
using ModderLords.Coop.Admin;
using Xunit;

namespace ModderLords.Core.Tests;

/// <summary>
/// The character editor's two halves agree without a server: every edit token the launcher builds (HeroEdits) is read
/// back by the module's parser (HeroEditWire) as the same edit, and a hero reply shaped like the module's is read whole.
/// </summary>
public class CharacterEditWireTests
{
    public static TheoryData<string, HeroEditKind, string, int> LauncherTokens => new()
    {
        { HeroEdits.Gold(25000), HeroEditKind.Gold, "", 25000 },
        { HeroEdits.HitPoints(80), HeroEditKind.HitPoints, "", 80 },
        { HeroEdits.UnspentAttributePoints(3), HeroEditKind.UnspentAttributePoints, "", 3 },
        { HeroEdits.UnspentFocusPoints(0), HeroEditKind.UnspentFocusPoints, "", 0 },
        { HeroEdits.Attribute("vigor", 6), HeroEditKind.Attribute, "vigor", 6 },
        { HeroEdits.Focus("OneHanded", 5), HeroEditKind.Focus, "OneHanded", 5 },
        { HeroEdits.Skill("OneHanded", 175), HeroEditKind.Skill, "OneHanded", 175 },
        { HeroEdits.Trait("Mercy", -2), HeroEditKind.Trait, "Mercy", -2 },
        // A modded skill id with a dot in it still splits at the first dot only.
        { HeroEdits.Skill("mod.Alchemy", 40), HeroEditKind.Skill, "mod.Alchemy", 40 },
    };

    [Theory]
    [MemberData(nameof(LauncherTokens))]
    public void Every_token_the_launcher_sends_is_read_as_the_same_edit(string token, HeroEditKind kind, string id, int value)
    {
        Assert.True(HeroEditWire.TryParse(token, out var edit, out var error), error);
        Assert.Equal(kind, edit!.Kind);
        Assert.Equal(id, edit.Id);
        Assert.Equal(value, edit.Value);
        Assert.Equal(token, edit.ToString());
    }

    [Theory]
    [InlineData("gold")]
    [InlineData("gold=")]
    [InlineData("=5")]
    [InlineData("gold=lots")]
    [InlineData("gold=1.5")]
    [InlineData("level=30")]
    [InlineData("skill.=5")]
    [InlineData("perk.Duelist=1")]
    public void A_malformed_or_unknown_token_is_refused_with_a_reason(string token)
    {
        Assert.False(HeroEditWire.TryParse(token, out var edit, out var error));
        Assert.Null(edit);
        Assert.Contains(token, error);
    }

    [Fact]
    public void An_id_that_would_split_on_the_console_is_refused_before_sending()
    {
        Assert.Throws<ArgumentException>(() => HeroEdits.Skill("One Handed", 10));
        Assert.Throws<ArgumentException>(() => HeroEdits.Trait("a=b", 1));
        Assert.Throws<ArgumentException>(() => HeroEdits.Attribute("", 1));
    }

    [Fact]
    public void A_hero_reply_shaped_like_the_modules_is_read_whole()
    {
        var line = AdminWire.Reply("edit", "r3", null, new Dictionary<string, object?>
        {
            ["steamId"] = "76561198000000001", ["heroId"] = "Hero_Player", ["name"] = "Aldric", ["culture"] = "Vlandia",
            ["age"] = 27, ["female"] = false, ["level"] = 12, ["gold"] = 5000, ["hp"] = 90, ["maxHp"] = 110,
            ["unspentAttr"] = 1, ["unspentFocus"] = 4, ["maxAttribute"] = 10, ["maxFocus"] = 5, ["maxSkill"] = 330,
            ["busy"] = "", ["perks"] = 7,
            ["attributes"] = new List<object?> { new Dictionary<string, object?> { ["id"] = "vigor", ["name"] = "Vigor", ["value"] = 4 } },
            ["skills"] = new List<object?>
            {
                new Dictionary<string, object?> { ["id"] = "OneHanded", ["name"] = "One Handed", ["attribute"] = "vigor", ["value"] = 120, ["focus"] = 3 },
            },
            ["traits"] = new List<object?>
            {
                new Dictionary<string, object?> { ["id"] = "Mercy", ["name"] = "Mercy", ["value"] = -1, ["min"] = -2, ["max"] = 2, ["personality"] = true },
            },
            ["changes"] = new List<object?> { "Gold 1000 -> 5000" },
            ["notApplied"] = new List<object?>(),
        });

        var hero = HeroDetail.From(AdminClient.Parse(line)!);
        Assert.Equal("Aldric", hero.Name);
        Assert.Equal(12, hero.Level);
        Assert.Equal(5000, hero.Gold);
        Assert.Equal((90, 110), (hero.HitPoints, hero.MaxHitPoints));
        Assert.Equal((10, 5, 330), (hero.MaxAttribute, hero.MaxFocus, hero.MaxSkill));
        Assert.Equal(new HeroAttribute("vigor", "Vigor", 4), Assert.Single(hero.Attributes));
        Assert.Equal(new HeroSkill("OneHanded", "One Handed", "vigor", 120, 3), Assert.Single(hero.Skills));
        Assert.Equal(new HeroTrait("Mercy", "Mercy", -1, -2, 2, true), Assert.Single(hero.Traits));
        Assert.Equal("Gold 1000 -> 5000", Assert.Single(hero.Changes));
        Assert.Empty(hero.NotApplied);
    }
}
