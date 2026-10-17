using System.Text.RegularExpressions;
using ModderLords.App.Localization;
using Xunit;

namespace ModderLords.App.Tests;

public class LocalizationTests
{
    private static readonly Regex Placeholder = new(@"(?<!\{)\{(\d+)[^{}]*\}(?!\})");

    private static string[] Holes(string s) =>
        Placeholder.Matches(s).Select(m => m.Value).Order(StringComparer.Ordinal).ToArray();

    /// <summary>A translation that drops or invents a {0} would throw in string.Format, or show the wrong value.</summary>
    [Theory]
    [InlineData("zh-Hans")]
    public void Translations_keep_every_placeholder(string code)
    {
        var table = Loc.LoadTable(code);
        Assert.NotEmpty(table);
        foreach (var (english, translated) in table)
        {
            Assert.False(string.IsNullOrWhiteSpace(translated), $"empty translation for: {english}");
            Assert.Equal(Holes(english), Holes(translated));
        }
    }

    [Fact]
    public void Untranslated_text_falls_back_to_English() =>
        Assert.Equal("No such string, surely", Loc.T("No such string, surely"));

    [Theory]
    [InlineData("zh-Hans")]
    [InlineData("en")]
    public void Saved_choice_wins_over_Windows(string saved) => Assert.Equal(saved, Loc.Resolve(saved));
}
