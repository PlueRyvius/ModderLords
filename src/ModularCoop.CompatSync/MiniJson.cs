using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ModularCoop.CompatSync;

/// <summary>
/// Dependency-free JSON for the live-settings files. The submodule DLL must reference nothing beyond the engine
/// (the client eagerly checks a submodule's references), so no Newtonsoft here. Objects are
/// Dictionary&lt;string, object?&gt;, arrays List&lt;object?&gt;, scalars string / bool / long / double / null.
/// Also compiled into the launcher's tests (linked source) so both ends parse the same bytes.
/// </summary>
public static class MiniJson
{
    // ---- writing ---------------------------------------------------------------------------------------------

    public static string Serialize(object? value)
    {
        var sb = new StringBuilder();
        Write(sb, value, 0);
        sb.Append('\n');
        return sb.ToString();
    }

    private static void Write(StringBuilder sb, object? value, int depth)
    {
        switch (value)
        {
            case null: sb.Append("null"); break;
            case string s: WriteString(sb, s); break;
            case bool b: sb.Append(b ? "true" : "false"); break;
            case int i: sb.Append(i.ToString(CultureInfo.InvariantCulture)); break;
            case long l: sb.Append(l.ToString(CultureInfo.InvariantCulture)); break;
            case float f: WriteDouble(sb, f); break;
            case double d: WriteDouble(sb, d); break;
            case decimal m: sb.Append(m.ToString(CultureInfo.InvariantCulture)); break;
            case IDictionary<string, object?> obj:
                sb.Append('{');
                var first = true;
                foreach (var kv in obj)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    Indent(sb, depth + 1);
                    WriteString(sb, kv.Key);
                    sb.Append(": ");
                    Write(sb, kv.Value, depth + 1);
                }
                if (!first) Indent(sb, depth);
                sb.Append('}');
                break;
            case IEnumerable<object?> list:
                sb.Append('[');
                var any = false;
                foreach (var item in list)
                {
                    if (any) sb.Append(',');
                    any = true;
                    Indent(sb, depth + 1);
                    Write(sb, item, depth + 1);
                }
                if (any) Indent(sb, depth);
                sb.Append(']');
                break;
            default:
                WriteString(sb, Convert.ToString(value, CultureInfo.InvariantCulture) ?? "");
                break;
        }
    }

    private static void WriteDouble(StringBuilder sb, double d)
    {
        if (double.IsNaN(d) || double.IsInfinity(d)) { sb.Append("null"); return; }
        sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
    }

    private static void Indent(StringBuilder sb, int depth)
    {
        sb.Append('\n');
        for (var i = 0; i < depth; i++) sb.Append("  ");
    }

    private static void WriteString(StringBuilder sb, string s)
    {
        sb.Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
    }

    // ---- reading ---------------------------------------------------------------------------------------------

    /// <summary>Parses a document; throws FormatException on malformed input.</summary>
    public static object? Parse(string json)
    {
        var p = new Parser(json);
        var v = p.ReadValue();
        p.SkipWs();
        if (!p.AtEnd) throw new FormatException("trailing characters at " + p.Pos);
        return v;
    }

    public static Dictionary<string, object?> ParseObject(string json) =>
        Parse(json) as Dictionary<string, object?> ?? throw new FormatException("expected a JSON object");

    public static string? GetString(IDictionary<string, object?> obj, string key) =>
        obj.TryGetValue(key, out var v) ? v as string ?? (v is bool b ? (b ? "true" : "false") : v is null ? null : Convert.ToString(v, CultureInfo.InvariantCulture)) : null;

    public static Dictionary<string, object?>? GetObject(IDictionary<string, object?> obj, string key) =>
        obj.TryGetValue(key, out var v) ? v as Dictionary<string, object?> : null;

    public static List<object?>? GetArray(IDictionary<string, object?> obj, string key) =>
        obj.TryGetValue(key, out var v) ? v as List<object?> : null;

    private sealed class Parser
    {
        private readonly string _s;
        public int Pos;
        public Parser(string s) { _s = s; }
        public bool AtEnd => Pos >= _s.Length;

        public void SkipWs() { while (!AtEnd && char.IsWhiteSpace(_s[Pos])) Pos++; }

        public object? ReadValue()
        {
            SkipWs();
            if (AtEnd) throw new FormatException("unexpected end");
            var c = _s[Pos];
            switch (c)
            {
                case '{': return ReadObject();
                case '[': return ReadArray();
                case '"': return ReadString();
                case 't': Expect("true"); return true;
                case 'f': Expect("false"); return false;
                case 'n': Expect("null"); return null;
                default:
                    if (c == '-' || char.IsDigit(c)) return ReadNumber();
                    throw new FormatException("unexpected '" + c + "' at " + Pos);
            }
        }

        private void Expect(string word)
        {
            if (string.CompareOrdinal(_s, Pos, word, 0, word.Length) != 0) throw new FormatException("expected " + word + " at " + Pos);
            Pos += word.Length;
        }

        private Dictionary<string, object?> ReadObject()
        {
            var obj = new Dictionary<string, object?>(StringComparer.Ordinal);
            Pos++; // {
            SkipWs();
            if (!AtEnd && _s[Pos] == '}') { Pos++; return obj; }
            while (true)
            {
                SkipWs();
                if (AtEnd || _s[Pos] != '"') throw new FormatException("expected key at " + Pos);
                var key = ReadString();
                SkipWs();
                if (AtEnd || _s[Pos] != ':') throw new FormatException("expected ':' at " + Pos);
                Pos++;
                obj[key] = ReadValue();
                SkipWs();
                if (AtEnd) throw new FormatException("unterminated object");
                if (_s[Pos] == ',') { Pos++; continue; }
                if (_s[Pos] == '}') { Pos++; return obj; }
                throw new FormatException("expected ',' or '}' at " + Pos);
            }
        }

        private List<object?> ReadArray()
        {
            var list = new List<object?>();
            Pos++; // [
            SkipWs();
            if (!AtEnd && _s[Pos] == ']') { Pos++; return list; }
            while (true)
            {
                list.Add(ReadValue());
                SkipWs();
                if (AtEnd) throw new FormatException("unterminated array");
                if (_s[Pos] == ',') { Pos++; continue; }
                if (_s[Pos] == ']') { Pos++; return list; }
                throw new FormatException("expected ',' or ']' at " + Pos);
            }
        }

        private string ReadString()
        {
            var sb = new StringBuilder();
            Pos++; // opening quote
            while (true)
            {
                if (AtEnd) throw new FormatException("unterminated string");
                var c = _s[Pos++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }
                if (AtEnd) throw new FormatException("unterminated escape");
                var e = _s[Pos++];
                switch (e)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (Pos + 4 > _s.Length) throw new FormatException("bad \\u escape");
                        sb.Append((char)int.Parse(_s.Substring(Pos, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        Pos += 4;
                        break;
                    default: throw new FormatException("bad escape '\\" + e + "'");
                }
            }
        }

        private object ReadNumber()
        {
            var start = Pos;
            if (_s[Pos] == '-') Pos++;
            var isFloat = false;
            while (!AtEnd)
            {
                var c = _s[Pos];
                if (char.IsDigit(c)) { Pos++; continue; }
                if (c == '.' || c == 'e' || c == 'E' || c == '+' || c == '-') { isFloat = true; Pos++; continue; }
                break;
            }
            var text = _s.Substring(start, Pos - start);
            if (!isFloat && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)) return l;
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
            throw new FormatException("bad number '" + text + "'");
        }
    }
}
