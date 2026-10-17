using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows.Markup;
using ModderLords.Core.Profiles;

namespace ModderLords.App.Localization;

/// <summary>
/// UI language. The English text is its own key: a string with no translation simply shows in English, so a new
/// feature never shows a blank or a key name. Translations are embedded JSON files, one per language, mapping the
/// exact English text to the translated text. The language is read once at startup; changing it needs a restart,
/// because the windows take their text when they are built.
/// </summary>
public static class Loc
{
    /// <summary>Languages offered in the picker. Code is what ui-state.json stores; null code = follow Windows.</summary>
    public static readonly (string? Code, string Name)[] Languages =
    [
        (null, "System default"),
        ("en", "English"),
        ("zh-Hans", "简体中文"),
    ];

    /// <summary>The language actually in use, e.g. "en" or "zh-Hans".</summary>
    public static string Current { get; private set; } = "en";

    private static readonly Dictionary<string, string> Table = Load(Resolve(UiStateStore.Load().Language));

    /// <summary>Translates <paramref name="english"/>, or returns it unchanged when there is no translation.</summary>
    public static string T(string english) => Table.TryGetValue(english, out var t) ? t : english;

    /// <summary>Translates a composite-format template, then fills it in: <c>Loc.F("{0} mods enabled", n)</c>.</summary>
    public static string F(string englishFormat, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, T(englishFormat), args);

    internal static string Resolve(string? saved)
    {
        if (!string.IsNullOrEmpty(saved)) return saved;
        var ui = CultureInfo.CurrentUICulture;
        // zh-CN, zh-SG and zh-Hans all read Simplified; Traditional regions have no translation yet.
        return ui.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase) && !IsTraditional(ui) ? "zh-Hans" : "en";
    }

    private static bool IsTraditional(CultureInfo c) =>
        c.Name is "zh-TW" or "zh-HK" or "zh-MO" || c.Name.StartsWith("zh-Hant", StringComparison.OrdinalIgnoreCase);

    private static Dictionary<string, string> Load(string code)
    {
        Current = "en";
        if (code == "en") return new();
        try
        {
            using var s = typeof(Loc).Assembly.GetManifestResourceStream($"ModderLords.App.Localization.Strings.{code}.json");
            if (s is null) return new();
            var table = JsonSerializer.Deserialize<Dictionary<string, string>>(s) ?? new();
            Current = code;
            return table;
        }
        // A broken translation file must cost the user their language, not their launcher.
        catch (Exception) { return new(); }
    }

    /// <summary>For tests: the raw table of a language, straight from the embedded file.</summary>
    internal static Dictionary<string, string> LoadTable(string code)
    {
        using var s = typeof(Loc).Assembly.GetManifestResourceStream($"ModderLords.App.Localization.Strings.{code}.json")
            ?? throw new FileNotFoundException(code);
        return JsonSerializer.Deserialize<Dictionary<string, string>>(s)!;
    }
}

/// <summary>XAML side of <see cref="Loc"/>: <c>Content="{l:T 'Save'}"</c>.</summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TExtension(string text) : MarkupExtension
{
    public string Text { get; set; } = text;
    public override object ProvideValue(IServiceProvider serviceProvider) => Loc.T(Text);
}
