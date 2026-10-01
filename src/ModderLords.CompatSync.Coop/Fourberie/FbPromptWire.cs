using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ModderLords.CompatSync.Coop.Fourberie;

/// <summary>A yes/no prompt the server raised for a player, as sent to that player's game.</summary>
public sealed class FbInquiry
{
    public int Id;
    public string Title = "", Text = "", AffirmativeText = "", NegativeText = "";
    public bool AffirmativeShown, NegativeShown;
    public bool AffirmativeEnabled = true, NegativeEnabled = true;
    public string AffirmativeHint = "", NegativeHint = "";
}

/// <summary>A pick-from-a-list prompt; elements are sent by position and answered by position.</summary>
public sealed class FbMultiInquiry
{
    public int Id;
    public string Title = "", Description = "", AffirmativeText = "", NegativeText = "";
    public bool ExitShown;
    public int Min, Max;
    public List<(string Title, string Hint, bool Enabled)> Elements = new List<(string, string, bool)>();
}

/// <summary>Fourberie's "report from your informants" map notice.</summary>
public sealed class FbMapNotice
{
    public string NotifType = "", ActionType = "", ActionStringId = "", Description = "";
}

/// <summary>A map conversation the server opened for a player, with the text variables its dialog lines read.</summary>
public sealed class FbConversation
{
    public string CharacterId = "";
    public List<(string Name, string Value)> Variables = new List<(string, string)>();
}

/// <summary>
/// The wire form of the Fourberie layer's prompts (fourb-prompts): string lists on the shared action channel, under
/// feature "fourberie-ui" server to player, and op "answer" player to server. Everything is bounded and malformed
/// input is refused whole. Free of game and Coop types so the tests compile it in.
/// </summary>
public static class FbPromptWire
{
    public const string Feature = "fourberie-ui";
    public const int MaxText = 8000;
    public const int MaxElements = 200;
    public const int MaxVariables = 64;

    public const string Yes = "yes", No = "no", Pick = "pick";

    // ---- server -> player ------------------------------------------------------------------------------------

    public static List<string> Pack(FbInquiry q) => new List<string>
    {
        "inquiry", I(q.Id), Cut(q.Title), Cut(q.Text), Cut(q.AffirmativeText), Cut(q.NegativeText),
        B(q.AffirmativeShown), B(q.NegativeShown), B(q.AffirmativeEnabled), Cut(q.AffirmativeHint), B(q.NegativeEnabled), Cut(q.NegativeHint),
    };

    public static List<string> Pack(FbMultiInquiry q)
    {
        var list = new List<string>
        {
            "multi", I(q.Id), Cut(q.Title), Cut(q.Description), Cut(q.AffirmativeText), Cut(q.NegativeText),
            B(q.ExitShown), I(q.Min), I(q.Max), I(Math.Min(q.Elements.Count, MaxElements)),
        };
        foreach (var (title, hint, enabled) in q.Elements.Take(MaxElements)) { list.Add(Cut(title)); list.Add(Cut(hint)); list.Add(B(enabled)); }
        return list;
    }

    public static List<string> Pack(FbMapNotice n) => new List<string> { "mapnotice", Cut(n.NotifType), Cut(n.ActionType), Cut(n.ActionStringId), Cut(n.Description) };

    public static List<string> Pack(FbConversation c)
    {
        var vars = c.Variables.Take(MaxVariables).ToList();
        var list = new List<string> { "conversation", Cut(c.CharacterId), I(vars.Count) };
        foreach (var (name, value) in vars) { list.Add(Cut(name)); list.Add(Cut(value)); }
        return list;
    }

    /// <summary>An FbInquiry, FbMultiInquiry, FbMapNotice or FbConversation; null when the message is malformed.</summary>
    public static object? Unpack(IList<string> d)
    {
        try
        {
            if (d.Count == 0 || d.Any(s => s == null || s.Length > MaxText)) return null;
            switch (d[0])
            {
                case "inquiry" when d.Count == 12:
                    return new FbInquiry
                    {
                        Id = Int(d[1]), Title = d[2], Text = d[3], AffirmativeText = d[4], NegativeText = d[5],
                        AffirmativeShown = Bool(d[6]), NegativeShown = Bool(d[7]), AffirmativeEnabled = Bool(d[8]),
                        AffirmativeHint = d[9], NegativeEnabled = Bool(d[10]), NegativeHint = d[11],
                    };
                case "multi" when d.Count >= 10:
                {
                    var count = Int(d[9]);
                    if (count < 0 || count > MaxElements || d.Count != 10 + 3 * count) return null;
                    var q = new FbMultiInquiry
                    {
                        Id = Int(d[1]), Title = d[2], Description = d[3], AffirmativeText = d[4], NegativeText = d[5],
                        ExitShown = Bool(d[6]), Min = Int(d[7]), Max = Int(d[8]),
                    };
                    if (q.Min < 0 || q.Max < q.Min) return null;
                    for (var i = 0; i < count; i++) q.Elements.Add((d[10 + 3 * i], d[11 + 3 * i], Bool(d[12 + 3 * i])));
                    return q;
                }
                case "mapnotice" when d.Count == 5:
                    return new FbMapNotice { NotifType = d[1], ActionType = d[2], ActionStringId = d[3], Description = d[4] };
                case "conversation" when d.Count >= 3:
                {
                    var count = Int(d[2]);
                    if (count < 0 || count > MaxVariables || d.Count != 3 + 2 * count || d[1].Length == 0) return null;
                    var c = new FbConversation { CharacterId = d[1] };
                    for (var i = 0; i < count; i++) c.Variables.Add((d[3 + 2 * i], d[4 + 2 * i]));
                    return c;
                }
                default:
                    return null;
            }
        }
        catch (FormatException) { return null; }
        catch (OverflowException) { return null; }
    }

    // ---- player -> server ------------------------------------------------------------------------------------

    public static List<string> Answer(int id, bool yes) => new List<string> { I(id), yes ? Yes : No };

    public static List<string> AnswerPicked(int id, IEnumerable<int> picked) =>
        new List<string> { I(id), Pick }.Concat(picked.Select(I)).ToList();

    /// <summary>
    /// Reads a player's answer. <paramref name="picked"/> is set for a list answer: distinct positions inside
    /// <paramref name="elementCount"/>, between <paramref name="min"/> and <paramref name="max"/> of them. False when
    /// the answer is malformed or does not fit the prompt.
    /// </summary>
    public static bool TryReadAnswer(IList<string> args, out int id, out bool yes, out List<int>? picked, int elementCount = 0, int min = 0, int max = 0)
    {
        id = 0;
        yes = false;
        picked = null;
        if (args.Count < 2 || !int.TryParse(args[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out id)) return false;
        if (args[1] == Yes && args.Count == 2) { yes = true; return true; }
        if (args[1] == No && args.Count == 2) return true;
        if (args[1] != Pick) return false;
        var list = new List<int>();
        foreach (var s in args.Skip(2))
        {
            if (!int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) || i < 0 || i >= elementCount || list.Contains(i)) return false;
            list.Add(i);
        }
        if (list.Count < min || list.Count > max) return false;
        yes = true;
        picked = list;
        return true;
    }

    private static string I(int i) => i.ToString(CultureInfo.InvariantCulture);
    private static int Int(string s) => int.Parse(s, NumberStyles.Integer, CultureInfo.InvariantCulture);
    private static string B(bool b) => b ? "1" : "0";
    private static bool Bool(string s) => s == "1" ? true : s == "0" ? false : throw new FormatException();
    private static string Cut(string? s) => s == null ? "" : s.Length <= MaxText ? s : s.Substring(0, MaxText);
}
