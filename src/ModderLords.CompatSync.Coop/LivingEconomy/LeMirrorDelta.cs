using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ModderLords.CompatSync.Coop.LivingEconomy;

/// <summary>
/// The wire form of the Living Economy state mirror, free of game and Coop types so the tests compile it in.
///
/// A behaviour's SyncData values are one JSON object: top-level keys are the mod's save keys, and most values are
/// record books (a JSON object keyed by settlement id). Only the rows that changed are sent: a delta names, per save
/// key, the rows to set and the rows to drop, or replaces the whole value when it is not a book. Each message carries
/// the sequence number it builds on, so a client that missed one asks for everything again instead of drifting.
/// Payloads are gzip + base64: a full copy of Living Economy's books is a few hundred KB of JSON and compresses well.
/// </summary>
public static class LeMirrorDelta
{
    public const string KindFull = "full";
    public const string KindDelta = "delta";

    /// <summary>Row-level difference from <paramref name="previous"/> to <paramref name="current"/>; null when nothing changed.</summary>
    public static JObject? Diff(JObject previous, JObject current)
    {
        var changes = new JObject();
        foreach (var prop in current.Properties())
        {
            var before = previous[prop.Name];
            if (before != null && JToken.DeepEquals(before, prop.Value)) continue;
            if (before is JObject beforeBook && prop.Value is JObject currentBook)
            {
                var set = new JObject();
                foreach (var row in currentBook.Properties())
                {
                    var old = beforeBook[row.Name];
                    if (old == null || !JToken.DeepEquals(old, row.Value)) set[row.Name] = row.Value.DeepClone();
                }
                var dropped = new JArray(beforeBook.Properties().Select(p => p.Name).Where(n => currentBook[n] == null));
                var entry = new JObject();
                if (set.HasValues) entry["$set"] = set;
                if (dropped.Count > 0) entry["$del"] = dropped;
                if (entry.HasValues) changes[prop.Name] = entry;
            }
            else
            {
                changes[prop.Name] = new JObject { ["$all"] = prop.Value.DeepClone() };
            }
        }
        foreach (var gone in previous.Properties().Where(p => current[p.Name] == null))
            changes[gone.Name] = new JObject { ["$all"] = JValue.CreateNull() };
        return changes.HasValues ? changes : null;
    }

    /// <summary>Applies a <see cref="Diff"/> result to <paramref name="target"/> in place.</summary>
    public static void Apply(JObject target, JObject changes)
    {
        foreach (var prop in changes.Properties())
        {
            if (prop.Value is not JObject entry) continue;
            if (entry.TryGetValue("$all", out var all))
            {
                if (all.Type == JTokenType.Null) target.Remove(prop.Name);
                else target[prop.Name] = all.DeepClone();
                continue;
            }
            if (target[prop.Name] is not JObject book)
            {
                book = new JObject();
                target[prop.Name] = book;
            }
            if (entry["$set"] is JObject set)
                foreach (var row in set.Properties()) book[row.Name] = row.Value.DeepClone();
            if (entry["$del"] is JArray dropped)
                foreach (var name in dropped) book.Remove(name.ToString());
        }
    }

    /// <summary>One mirror message: kind, the sequence it builds on (deltas only), its own sequence, and the values.</summary>
    public static string Pack(string kind, int baseSequence, int sequence, JObject values)
    {
        var envelope = new JObject
        {
            ["k"] = kind,
            ["b"] = baseSequence,
            ["s"] = sequence,
            ["v"] = values,
        };
        return Compress(envelope.ToString(Formatting.None));
    }

    public static bool TryUnpack(string payload, out string kind, out int baseSequence, out int sequence, out JObject values)
    {
        kind = "";
        baseSequence = 0;
        sequence = 0;
        values = new JObject();
        try
        {
            var envelope = JObject.Parse(Decompress(payload));
            kind = envelope.Value<string>("k") ?? "";
            baseSequence = envelope.Value<int?>("b") ?? 0;
            sequence = envelope.Value<int?>("s") ?? 0;
            if (envelope["v"] is JObject v) values = v;
            return kind == KindFull || kind == KindDelta;
        }
        catch (Exception) { return false; }
    }

    public static string Compress(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
            gzip.Write(bytes, 0, bytes.Length);
        return Convert.ToBase64String(output.ToArray());
    }

    public static string Decompress(string payload)
    {
        using var input = new MemoryStream(Convert.FromBase64String(payload));
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
