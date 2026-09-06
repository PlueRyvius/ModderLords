using System.Text;
using System.Text.Json;

namespace ModderLords.Core.Config;

/// <summary>
/// Minimal editing of JSON-with-comments files (the Coop configs allow // and /* */ comments and trailing commas)
/// that preserves the file byte-for-byte except for the one scalar value being replaced. Works on the raw text with
/// a small scanner instead of re-serialising, so the shipped comments and layout survive.
/// </summary>
public static class CommentedJson
{
    public sealed record Leaf(string Path, JsonValueKind Kind, string RawValue);

    private static readonly JsonDocumentOptions ReadOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    /// <summary>All scalar leaves as dotted paths (arrays are skipped; nothing in the Coop configs uses them for settings).</summary>
    public static IReadOnlyList<Leaf> Leaves(string text)
    {
        var list = new List<Leaf>();
        using var doc = JsonDocument.Parse(text, ReadOptions);
        Walk(doc.RootElement, "", list);
        return list;
    }

    private static void Walk(JsonElement e, string prefix, List<Leaf> list)
    {
        if (e.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in e.EnumerateObject()) Walk(p.Value, prefix.Length == 0 ? p.Name : prefix + "." + p.Name, list);
            return;
        }
        if (e.ValueKind is JsonValueKind.Array) return;
        list.Add(new Leaf(prefix, e.ValueKind, e.GetRawText()));
    }

    /// <summary>Replaces the value of the scalar at <paramref name="path"/> (dotted) with <paramref name="rawValue"/> (already JSON-encoded). Throws if the path is not present.</summary>
    public static string SetScalar(string text, string path, string rawValue)
    {
        var (start, end) = LocateValue(text, path.Split('.'));
        return string.Concat(text.AsSpan(0, start), rawValue, text.AsSpan(end));
    }

    // ---- scanner --------------------------------------------------------------------------------------------

    private static (int start, int end) LocateValue(string s, string[] path)
    {
        int i = 0;
        SkipTrivia(s, ref i);
        if (i >= s.Length || s[i] != '{') throw new InvalidDataException("root is not an object");
        return FindInObject(s, ref i, path, 0);
    }

    private static (int, int) FindInObject(string s, ref int i, string[] path, int depth)
    {
        i++; // '{'
        while (true)
        {
            SkipTrivia(s, ref i);
            if (i >= s.Length) throw new InvalidDataException("unexpected end");
            if (s[i] == '}') { i++; throw new KeyNotFoundException(string.Join(".", path)); }
            if (s[i] == ',') { i++; continue; }
            var key = ReadString(s, ref i);
            SkipTrivia(s, ref i);
            if (s[i] != ':') throw new InvalidDataException($"expected ':' at {i}");
            i++;
            SkipTrivia(s, ref i);
            if (key == path[depth])
            {
                if (depth == path.Length - 1)
                {
                    var start = i;
                    SkipValue(s, ref i);
                    return (start, i);
                }
                if (s[i] != '{') throw new InvalidDataException($"'{key}' is not an object");
                return FindInObject(s, ref i, path, depth + 1);
            }
            SkipValue(s, ref i);
        }
    }

    private static void SkipTrivia(string s, ref int i)
    {
        while (i < s.Length)
        {
            if (char.IsWhiteSpace(s[i])) { i++; continue; }
            if (s[i] == '/' && i + 1 < s.Length && s[i + 1] == '/') { while (i < s.Length && s[i] != '\n') i++; continue; }
            if (s[i] == '/' && i + 1 < s.Length && s[i + 1] == '*') { var e = s.IndexOf("*/", i + 2, StringComparison.Ordinal); i = e < 0 ? s.Length : e + 2; continue; }
            break;
        }
    }

    private static string ReadString(string s, ref int i)
    {
        if (s[i] != '"') throw new InvalidDataException($"expected string at {i}");
        var sb = new StringBuilder();
        i++;
        while (i < s.Length && s[i] != '"')
        {
            if (s[i] == '\\') { sb.Append(s[i]); i++; }
            sb.Append(s[i]); i++;
        }
        i++;
        return JsonSerializer.Deserialize<string>("\"" + sb + "\"") ?? "";
    }

    private static void SkipValue(string s, ref int i)
    {
        SkipTrivia(s, ref i);
        switch (s[i])
        {
            case '"': ReadString(s, ref i); return;
            case '{': case '[':
            {
                var open = s[i]; var close = open == '{' ? '}' : ']';
                int level = 0;
                while (i < s.Length)
                {
                    SkipTrivia(s, ref i);
                    if (i >= s.Length) return;
                    if (s[i] == '"') { ReadString(s, ref i); continue; }
                    if (s[i] == open) level++;
                    else if (s[i] == close) { level--; if (level == 0) { i++; return; } }
                    i++;
                }
                return;
            }
            default:
                while (i < s.Length && !char.IsWhiteSpace(s[i]) && s[i] != ',' && s[i] != '}' && s[i] != ']' && !(s[i] == '/' && i + 1 < s.Length && (s[i + 1] == '/' || s[i + 1] == '*'))) i++;
                return;
        }
    }
}
