using System.Text;

namespace ModderLords.Core.Compat;

/// <summary>One line a mod's own config file must contain for the mod to behave correctly under Coop.</summary>
public sealed class EnsureLine
{
    /// <summary>Path relative to the module folder, e.g. <c>coop-modules.txt</c>.</summary>
    public string File { get; set; } = "";
    /// <summary>INI-style section name without brackets, e.g. <c>modules</c>. Null: anywhere in the file, appended at the end.</summary>
    public string? Section { get; set; }
    /// <summary>The line itself. May contain <see cref="EnsureLinesApplier.CoopModuleIdToken"/>.</summary>
    public string Value { get; set; } = "";
}

/// <summary>
/// Makes a mod's own config file contain the lines the compat database says it needs, without removing or rewriting
/// anything else in it.
///
/// Why this exists: mods that detect co-op by module id carry their own list, and it goes stale. TAOM.Dependencies'
/// <c>coop-modules.txt</c> lists <c>Coop</c>, but the Workshop build loads as <c>CoopNightly</c>, so on 2026-09-12 TAOM
/// logged "no co-op module detected" on server and client and every peer ran its single-player code paths. The mod
/// already reads the file for exactly this purpose; the fix is data, and it belongs in the compat database so the next
/// mod with the same problem needs a record, not code.
///
/// Additive only: a line already present (case-insensitive, commented-out copies do not count) is left alone, comments
/// and ordering are preserved, and the file's line endings and BOM are kept.
/// </summary>
public static class EnsureLinesApplier
{
    public const string CoopModuleIdToken = "{coopModuleId}";

    /// <summary>Applies every rule to files under <paramref name="moduleFolder"/>. Returns one message per change or problem.</summary>
    public static IReadOnlyList<string> Apply(string moduleFolder, IEnumerable<EnsureLine> rules, IReadOnlyDictionary<string, string> tokens)
    {
        var messages = new List<string>();
        foreach (var rule in rules)
        {
            var value = Substitute(rule.Value, tokens).Trim();
            if (value.Length == 0 || value.Contains('{'))
            {
                messages.Add($"WARNING {rule.File}: could not resolve '{rule.Value}'; file left unchanged");
                continue;
            }
            if (!TryResolve(moduleFolder, rule.File, out var path))
            {
                messages.Add($"WARNING {rule.File}: path is outside the module folder; ignored");
                continue;
            }
            try
            {
                var bytes = System.IO.File.Exists(path) ? System.IO.File.ReadAllBytes(path) : null;
                var hasBom = bytes is { Length: >= 3 } && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
                var text = bytes is null ? null : new UTF8Encoding(false).GetString(bytes, hasBom ? 3 : 0, bytes.Length - (hasBom ? 3 : 0));
                var change = Ensure(text, rule.Section, value, out var updated);
                if (change is null) continue;
                System.IO.File.WriteAllText(path, updated, new UTF8Encoding(hasBom));
                messages.Add($"{rule.File}: {change}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                messages.Add($"WARNING {rule.File}: could not update ({ex.Message})");
            }
        }
        return messages;
    }

    /// <summary>
    /// Pure text transform. Returns a description of the change, or null when <paramref name="value"/> was already
    /// present (then <paramref name="updated"/> is the input unchanged). <paramref name="text"/> null means no file.
    /// </summary>
    public static string? Ensure(string? text, string? section, string value, out string updated)
    {
        var newline = text is not null && !text.Contains("\r\n") && text.Contains('\n') ? "\n" : "\r\n";
        var lines = text is null ? new List<string>() : text.Replace("\r\n", "\n").Split('\n').ToList();
        if (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);

        int start = 0, end = lines.Count;
        if (section is not null)
        {
            var header = lines.FindIndex(l => HeaderName(l) is { } h && h.Equals(section, StringComparison.OrdinalIgnoreCase));
            if (header < 0)
            {
                if (lines.Count > 0 && lines[^1].Trim().Length > 0) lines.Add("");
                lines.Add("[" + section + "]");
                lines.Add(value);
                updated = Join(lines, newline);
                return $"added [{section}] with {value}";
            }
            start = header + 1;
            var next = lines.FindIndex(start, l => HeaderName(l) is not null);
            end = next < 0 ? lines.Count : next;
        }

        for (var i = start; i < end; i++)
        {
            if (lines[i].Trim().Equals(value, StringComparison.OrdinalIgnoreCase))
            {
                updated = text ?? "";
                return null;
            }
        }

        // After the section's last non-blank line, so the value joins its entries rather than the gap before the next header.
        var insertAt = end;
        while (insertAt > start && lines[insertAt - 1].Trim().Length == 0) insertAt--;
        lines.Insert(insertAt, value);
        updated = Join(lines, newline);
        return section is null ? $"added {value}" : $"added {value} under [{section}]";
    }

    private static string? HeaderName(string line)
    {
        var t = line.Trim();
        return t.Length >= 2 && t[0] == '[' && t[^1] == ']' ? t[1..^1].Trim() : null;
    }

    private static string Join(List<string> lines, string newline) => string.Join(newline, lines) + newline;

    private static string Substitute(string value, IReadOnlyDictionary<string, string> tokens)
    {
        foreach (var kv in tokens) value = value.Replace(kv.Key, kv.Value, StringComparison.OrdinalIgnoreCase);
        return value;
    }

    private static bool TryResolve(string folder, string relative, out string path)
    {
        path = "";
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative)) return false;
        var root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(root, relative));
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return false;
        path = full;
        return true;
    }
}
