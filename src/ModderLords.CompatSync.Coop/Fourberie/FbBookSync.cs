using System;
using System.Collections.Generic;
using System.Linq;
using ModderLords.CompatSync.Coop.LivingEconomy;
using Newtonsoft.Json.Linq;

namespace ModderLords.CompatSync.Coop.Fourberie;

/// <summary>
/// Keeping one player's Fourberie book the same on the server and on that player's game, rows travelling both ways.
///
/// The server runs the player's ticks (income, timers, schemes); the player's game runs the player's menus, screens and
/// missions. Each side sends only the rows it changed since the state both last agreed on, and a row from the other
/// side overwrites that row only. A row both sides changed since they last agreed (the player sends troops to agent
/// training while the server's daily tick moves a trainee on) keeps both changes (FbRowMerge) instead of one side's
/// overwriting the other's. A client that misses a server message asks for the whole book again.
///
/// Free of game and Coop types so the tests compile it in; the JSON is the book's persisted fields (FbBookCodec).
/// </summary>
public sealed class FbServerLedger
{
    private const int MaxHistory = 32;

    /// <summary>A message sent to the player's game: the rows in it, and what those fields held on that game before it.</summary>
    private sealed class Pushed
    {
        public int Sequence;
        public JObject Rows = new JObject();
        public JObject Before = new JObject();
    }

    /// <summary>The messages the player's game may not have received yet, oldest first.</summary>
    private readonly List<Pushed> _unseen = new List<Pushed>();

    /// <summary>What the player's game was last sent, with the rows it reported folded in. Null until the first full send.</summary>
    public JObject? Sent { get; private set; }

    public int Sequence { get; private set; }

    /// <summary>The whole book for a player who asked for it (joining, or after a missed message).</summary>
    public string Full(JObject current)
    {
        Sent = (JObject)current.DeepClone();
        _unseen.Clear();
        Sequence++;
        return LeMirrorDelta.Pack(LeMirrorDelta.KindFull, 0, Sequence, current);
    }

    /// <summary>The rows the server changed since the last send, or null when there are none or the player has no book yet.</summary>
    public string? DeltaIfChanged(JObject current)
    {
        if (Sent == null) return null;
        var diff = LeMirrorDelta.Diff(Sent, current);
        if (diff == null) return null;
        var pushed = new Pushed { Sequence = Sequence + 1, Rows = diff };
        foreach (var prop in diff.Properties())
            if (Sent[prop.Name] is { } was) pushed.Before[prop.Name] = was.DeepClone();
        _unseen.Add(pushed);
        if (_unseen.Count > MaxHistory) _unseen.RemoveAt(0);
        Sent = (JObject)current.DeepClone();
        var payload = LeMirrorDelta.Pack(LeMirrorDelta.KindDelta, Sequence, Sequence + 1, diff);
        Sequence++;
        return payload;
    }

    /// <summary>
    /// Folds the rows a player's game reported into <paramref name="current"/> (the server's book as JSON) and returns the
    /// merged book to install. They are also folded into what was sent, so they are not echoed back to the player.
    /// <paramref name="baseSequence"/> is the last server message that game had received when it made the report: a row
    /// the server changed after that, sent or not, is merged with the reported one instead of being overwritten by it.
    /// </summary>
    public JObject Merge(JObject current, JObject reported, int baseSequence = int.MaxValue)
    {
        var merged = (JObject)current.DeepClone();
        if (Sent == null)
        {
            LeMirrorDelta.Apply(merged, reported);
            return merged;
        }
        _unseen.RemoveAll(p => p.Sequence <= baseSequence);

        // The book as the player's game had it agreed when it made this report: without the messages it had not received.
        var agreed = (JObject)Sent.DeepClone();
        for (var i = _unseen.Count - 1; i >= 0; i--)
            foreach (var prop in _unseen[i].Rows.Properties())
            {
                if (_unseen[i].Before[prop.Name] is { } was) agreed[prop.Name] = was.DeepClone();
                else agreed.Remove(prop.Name);
            }
        var theirs = (JObject)agreed.DeepClone();
        LeMirrorDelta.Apply(theirs, reported);
        foreach (var prop in reported.Properties())
        {
            var value = FbRowMerge.Merge(agreed[prop.Name], current[prop.Name], theirs[prop.Name]);
            if (value == null) merged.Remove(prop.Name);
            else merged[prop.Name] = value.DeepClone();
        }

        // That game then receives those messages: their rows overwrite what it reported, the other reported rows stay.
        var later = new List<JObject>();
        foreach (var pushed in _unseen)
        {
            Fold(pushed.Before, reported, later, onlyFieldsThere: true);
            later.Add(pushed.Rows);
        }
        Fold(Sent, reported, later, onlyFieldsThere: false);
        return merged;
    }

    /// <summary>Applies the reported rows that none of the <paramref name="later"/> server messages overwrite.</summary>
    private static void Fold(JObject target, JObject reported, List<JObject> later, bool onlyFieldsThere)
    {
        var kept = new JObject();
        foreach (var prop in reported.Properties())
        {
            if (prop.Value is not JObject entry || (onlyFieldsThere && target[prop.Name] == null)) continue;
            var overwrites = later.Select(rows => rows[prop.Name]).OfType<JObject>().ToList();
            if (overwrites.Count == 0) { kept[prop.Name] = entry.DeepClone(); continue; }
            if (entry.ContainsKey("$all") || overwrites.Any(o => o.ContainsKey("$all"))) continue;
            bool Overwritten(string row) =>
                overwrites.Any(o => (o["$set"] as JObject)?.ContainsKey(row) == true || (o["$del"] as JArray)?.Any(n => n.ToString() == row) == true);
            var part = new JObject();
            if (entry["$set"] is JObject set)
            {
                var rows = new JObject(set.Properties().Where(r => !Overwritten(r.Name)).Select(r => new JProperty(r.Name, r.Value.DeepClone())));
                if (rows.HasValues) part["$set"] = rows;
            }
            if (entry["$del"] is JArray dropped)
            {
                var names = new JArray(dropped.Where(n => !Overwritten(n.ToString())).Select(n => n.DeepClone()));
                if (names.Count > 0) part["$del"] = names;
            }
            if (part.HasValues) kept[prop.Name] = part;
        }
        if (kept.HasValues) LeMirrorDelta.Apply(target, kept);
    }

    public void Forget()
    {
        Sent = null;
        _unseen.Clear();
    }
}

/// <summary>The player's side of <see cref="FbServerLedger"/>.</summary>
public sealed class FbClientLedger
{
    /// <summary>The book as both sides last agreed it. Null until the server's full book arrives; nothing is reported before that.</summary>
    public JObject? Agreed { get; private set; }

    public int Sequence { get; private set; }

    public bool HasBook => Agreed != null;

    /// <summary>
    /// A server message. Returns the book to install into the statics: the server's rows over the local ones, so a row
    /// this game changed and has not reported yet survives; when the server changed that same row, the two changes are
    /// merged (FbRowMerge) and the result is reported next. Null when the message cannot be used;
    /// <paramref name="needFull"/> then says whether to ask for the whole book.
    /// </summary>
    public JObject? Receive(string payload, JObject local, out bool needFull)
    {
        needFull = false;
        if (!LeMirrorDelta.TryUnpack(payload, out var kind, out var baseSequence, out var sequence, out var values)) return null;
        if (kind == LeMirrorDelta.KindFull)
        {
            Agreed = (JObject)values.DeepClone();
            Sequence = sequence;
            return values;
        }
        if (Agreed == null || baseSequence != Sequence)
        {
            needFull = true;
            return null;
        }
        var agreed = (JObject)Agreed.DeepClone();
        LeMirrorDelta.Apply(agreed, values);
        var install = (JObject)local.DeepClone();
        foreach (var prop in values.Properties())
        {
            var value = FbRowMerge.Merge(Agreed[prop.Name], agreed[prop.Name], local[prop.Name]);
            if (value == null) install.Remove(prop.Name);
            else install[prop.Name] = value.DeepClone();
        }
        Agreed = agreed;
        Sequence = sequence;
        return install;
    }

    /// <summary>
    /// The rows this game changed since the last agreement, packed for the server, or null when nothing changed. A field
    /// that is missing locally (it could not be written) is never reported as deleted.
    /// </summary>
    public string? Report(JObject local)
    {
        if (Agreed == null) return null;
        var diff = LeMirrorDelta.Diff(Agreed, local);
        if (diff == null) return null;
        foreach (var gone in diff.Properties().Where(p => p.Value is JObject e && e["$all"]?.Type == JTokenType.Null).ToList())
            gone.Remove();
        if (!diff.HasValues) return null;
        LeMirrorDelta.Apply(Agreed, diff);
        return LeMirrorDelta.Pack(LeMirrorDelta.KindDelta, Sequence, Sequence, diff);
    }

    public void Forget()
    {
        Agreed = null;
        Sequence = 0;
    }
}

/// <summary>
/// One value of a book that the server and the player's game may both have changed since they last agreed on it.
///
/// A side that did not change it takes the other's. When both did: books are merged row by row; numbers keep both
/// changes (the server's value plus what the player's game added or took away); troop and item lists (rows of an id and
/// counts, as in the ledgers) do the same per troop or item; anything else is the server's.
/// </summary>
public static class FbRowMerge
{
    /// <summary>The merged value; null when the value is gone.</summary>
    public static JToken? Merge(JToken? agreed, JToken? server, JToken? client)
    {
        if (Same(client, agreed) || Same(client, server)) return server;
        if (Same(server, agreed)) return client;
        if (server is JObject serverBook && client is JObject clientBook)
        {
            var agreedBook = agreed as JObject;
            var merged = new JObject();
            foreach (var name in serverBook.Properties().Concat(clientBook.Properties()).Select(p => p.Name).Distinct().ToList())
                if (Merge(agreedBook?[name], serverBook[name], clientBook[name]) is { } value) merged[name] = value.DeepClone();
            return merged;
        }
        if (IsNumber(server) && IsNumber(client) && (agreed == null || IsNumber(agreed))) return Sum(agreed, server!, client!);
        if (server is JArray serverRows && client is JArray clientRows && (agreed == null || agreed is JArray))
            return Counts(agreed as JArray, serverRows, clientRows) ?? server;
        return server;
    }

    private static bool Same(JToken? a, JToken? b) => a == null ? b == null : b != null && JToken.DeepEquals(a, b);

    private static bool IsNumber(JToken? token) => token != null && (token.Type == JTokenType.Integer || token.Type == JTokenType.Float);

    private static JToken Sum(JToken? agreed, JToken server, JToken client)
    {
        if (server.Type == JTokenType.Integer && client.Type == JTokenType.Integer && (agreed == null || agreed.Type == JTokenType.Integer))
            return new JValue(Sum(agreed == null ? 0 : (long)agreed, (long)server, (long)client));
        double a = agreed == null ? 0 : (double)agreed, s = (double)server, c = (double)client;
        var sum = s + (c - a);
        return new JValue(a >= 0 && s >= 0 && c >= 0 && sum < 0 ? 0 : sum);
    }

    /// <summary>Both changes to a count. A count neither side saw below zero does not go below zero.</summary>
    private static long Sum(long agreed, long server, long client)
    {
        var sum = server + (client - agreed);
        return agreed >= 0 && server >= 0 && client >= 0 && sum < 0 ? 0 : sum;
    }

    /// <summary>
    /// Lists whose rows are an id (one or more strings or nulls) and then counts: [troop, number, wounded, xp] or
    /// [item, modifier, amount]. Merged per id, a row whose first count reaches zero is dropped. Null when the lists
    /// are not of that shape.
    /// </summary>
    private static JArray? Counts(JArray? agreed, JArray server, JArray client)
    {
        int ids = -1, width = -1;
        foreach (var list in new[] { agreed, server, client })
        {
            if (list == null) continue;
            foreach (var token in list)
            {
                if (token is not JArray row) return null;
                var n = 0;
                while (n < row.Count && (row[n].Type == JTokenType.String || row[n].Type == JTokenType.Null)) n++;
                if (n == 0 || n == row.Count || row.Skip(n).Any(t => t.Type != JTokenType.Integer)) return null;
                if (ids < 0) { ids = n; width = row.Count; }
                else if (ids != n || width != row.Count) return null;
            }
        }
        if (ids < 0) return null;

        string Id(JArray row) => string.Join("\u0001", row.Take(ids).Select(t => t.Type == JTokenType.Null ? "" : (string)t!));
        Dictionary<string, JArray>? ById(JArray? list)
        {
            var rows = new Dictionary<string, JArray>(StringComparer.Ordinal);
            if (list == null) return rows;
            foreach (var row in list.Cast<JArray>())
            {
                if (rows.ContainsKey(Id(row))) return null;
                rows[Id(row)] = row;
            }
            return rows;
        }
        if (ById(agreed) is not { } was || ById(server) is not { } theirs || ById(client) is not { } mine) return null;

        var merged = new JArray();
        var done = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in server.Concat(client).Cast<JArray>())
        {
            var id = Id(row);
            if (!done.Add(id)) continue;
            was.TryGetValue(id, out var a);
            theirs.TryGetValue(id, out var s);
            mine.TryGetValue(id, out var c);
            var result = new JArray(row.Take(ids).Select(t => t.DeepClone()));
            for (var i = ids; i < width; i++)
                result.Add(Sum(a == null ? 0 : (long)a[i], s == null ? 0 : (long)s[i], c == null ? 0 : (long)c[i]));
            if ((long)result[ids] > 0) merged.Add(result);
        }
        return merged;
    }
}
