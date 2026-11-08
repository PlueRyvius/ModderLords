using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using ModderLords.CompatSync;

namespace ModderLords.Testing;

/// <summary>
/// Which on-screen messages count as errors for <c>game.noErrors</c> (docs/INGAME-TEST-HARNESS.md, decision 25): a
/// default set, plus patterns a suite or mod declares (<c>"errorPatterns"</c>). TaleWorlds-free, so it is unit-tested.
/// </summary>
internal static class ErrorPatterns
{
    /// <summary>
    /// Words a mod's own error message carries. Whole words, any case: "failed", not "unfailing". Vanilla shows none of
    /// these during play; its asserts go to the error log, which is kept as evidence instead.
    /// </summary>
    public const string Default = @"\b(error|errors|exception|failed|failure|crash|crashed)\b";

    /// <summary>The default pattern plus <paramref name="extra"/>: a JSON array of patterns, or one pattern, or empty.</summary>
    public static IReadOnlyList<Regex> From(string extra)
    {
        var list = new List<Regex> { new Regex(Default, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) };
        foreach (var pattern in Parse(extra))
            list.Add(new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
        return list;
    }

    internal static IEnumerable<string> Parse(string extra)
    {
        var text = (extra ?? "").Trim();
        if (text.Length == 0) return Enumerable.Empty<string>();
        if (text.StartsWith("[", StringComparison.Ordinal) && MiniJson.Parse(text) is List<object?> items)
            return items.Select(i => i as string ?? "").Where(s => s.Length > 0).ToList();
        return new[] { text };
    }

    /// <summary>The lines among <paramref name="seen"/> that match any pattern, in order.</summary>
    public static List<string> Matches(IEnumerable<string> seen, IReadOnlyList<Regex> patterns) =>
        seen.Where(line => patterns.Any(p => p.IsMatch(line))).ToList();
}
