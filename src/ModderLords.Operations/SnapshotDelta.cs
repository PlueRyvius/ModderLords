using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ModderLords.Operations;

/// <summary>
/// Compressed snapshots and snapshot diffs for operation state that is resent whenever it changes.
/// <para>
/// A shared snapshot is a JSON object of sections (arrays of records) and a few single values. Between two broadcasts
/// only a handful of records change, so after a player has one full copy the server sends a diff: for every array, the
/// new array as runs copied from the old one (<c>[start, count]</c>) and the records that are new (<c>{"n": record}</c>);
/// every other property as it now is. Records are compared by content, so a record removed from the middle costs one
/// break in a run, not a shift of everything after it, and the order is rebuilt exactly. The diff carries the
/// fingerprint of the whole new snapshot (<see cref="Fingerprint"/>); a player whose rebuild does not match asks for a
/// full copy again instead of drifting.
/// </para>
/// <para>Every payload is gzip-compressed and base64-encoded, so it travels in the existing string chunks.</para>
/// </summary>
public static class SnapshotDelta
{
    public const int Version = 1;

    /// <summary>Largest snapshot or diff accepted once decompressed: well above any real state, far below a zip bomb.</summary>
    public const int MaxInflatedBytes = 64 * 1024 * 1024;

    /// <summary>
    /// Parses a snapshot or diff the way every side must, so that writing it back gives the same text on all of them: no
    /// date conversion, and numbers as decimal. The dedicated server runs on .NET Core and players' games on .NET
    /// Framework, which write the same double differently ("R" is not shortest round-trip on Framework), so with doubles
    /// a full copy's fingerprint never matched on a player's game; a decimal keeps exactly the digits it was read from.
    /// </summary>
    public static JObject Parse(string json)
    {
        using var reader = new JsonTextReader(new StringReader(json)) { DateParseHandling = DateParseHandling.None, FloatParseHandling = FloatParseHandling.Decimal, MaxDepth = 64 };
        var token = JToken.ReadFrom(reader);
        if (reader.Read()) throw new InvalidDataException("Trailing content after the snapshot");
        return token as JObject ?? throw new InvalidDataException("A snapshot must be a JSON object");
    }

    /// <summary>One compact spelling of a parsed snapshot, the same on every side.</summary>
    public static string Canonical(JToken token) => token.ToString(Formatting.None);

    /// <summary>SHA-256 of the canonical spelling: what a rebuilt snapshot must match.</summary>
    public static string Fingerprint(JObject snapshot)
    {
        using var sha = SHA256.Create();
        return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(Canonical(snapshot))));
    }

    public static string Pack(string json)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            gzip.Write(bytes, 0, bytes.Length);
        }
        return Convert.ToBase64String(output.ToArray());
    }

    public static string Unpack(string packed)
    {
        var compressed = Convert.FromBase64String(packed);
        using var input = new GZipStream(new MemoryStream(compressed), CompressionMode.Decompress);
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (output.Length + read > MaxInflatedBytes) throw new InvalidDataException("Snapshot payload inflates past its bound");
            output.Write(buffer, 0, read);
        }
        return Encoding.UTF8.GetString(output.ToArray());
    }

    /// <summary>The diff that turns <paramref name="from"/> into <paramref name="to"/>.</summary>
    public static JObject Create(JObject from, JObject to)
    {
        var order = new JArray();
        var values = new JObject();
        var arrays = new JObject();
        foreach (var property in to.Properties())
        {
            order.Add(property.Name);
            if (property.Value is JArray next)
                arrays[property.Name] = Runs(from[property.Name] as JArray, next);
            else if (!JToken.DeepEquals(from[property.Name], property.Value) || from.Property(property.Name) == null)
                values[property.Name] = property.Value.DeepClone();
        }
        return new JObject { ["v"] = Version, ["order"] = order, ["values"] = values, ["arrays"] = arrays };
    }

    /// <summary>Rebuilds the new snapshot from the old one and a diff made by <see cref="Create"/>. Never modifies <paramref name="from"/>.</summary>
    public static JObject Apply(JObject from, JObject delta)
    {
        if ((int?)delta["v"] != Version) throw new InvalidDataException("Unsupported snapshot diff version");
        var order = delta["order"] as JArray ?? throw new InvalidDataException("Snapshot diff without an order");
        var values = delta["values"] as JObject ?? new JObject();
        var arrays = delta["arrays"] as JObject ?? new JObject();
        var result = new JObject();
        foreach (var nameToken in order)
        {
            var name = (string?)nameToken ?? throw new InvalidDataException("Snapshot diff property without a name");
            if (arrays[name] is JArray segments)
            {
                var source = from[name] as JArray;
                var array = new JArray();
                foreach (var segment in segments)
                {
                    if (segment is JArray run && run.Count == 2)
                    {
                        var start = (int)run[0]!; var count = (int)run[1]!;
                        if (source == null || start < 0 || count < 0 || start + count > source.Count) throw new InvalidDataException("Snapshot diff copies past the old array");
                        for (var i = start; i < start + count; i++) array.Add(source[i].DeepClone());
                    }
                    else if (segment is JObject added && added.TryGetValue("n", out var record)) array.Add(record.DeepClone());
                    else throw new InvalidDataException("Malformed snapshot diff segment");
                }
                result[name] = array;
            }
            else if (values.TryGetValue(name, out var value)) result[name] = value.DeepClone();
            else if (from.TryGetValue(name, out var unchanged)) result[name] = unchanged.DeepClone();
            else throw new InvalidDataException("Snapshot diff names a property it does not carry: " + name);
        }
        return result;
    }

    // The new array as copied runs of the old one and new records. A record is found in the old array by its content;
    // the next old record is tried first, so a run keeps going without a lookup and repeated records stay in order.
    private static JArray Runs(JArray? from, JArray to)
    {
        var segments = new JArray();
        var spellings = new List<string>();
        var firstIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        if (from != null)
            for (var i = 0; i < from.Count; i++)
            {
                var spelling = Canonical(from[i]);
                spellings.Add(spelling);
                if (!firstIndex.ContainsKey(spelling)) firstIndex[spelling] = i;
            }
        int runStart = -1, runCount = 0;
        void Close()
        {
            if (runCount > 0) segments.Add(new JArray(runStart, runCount));
            runStart = -1; runCount = 0;
        }
        foreach (var record in to)
        {
            var spelling = Canonical(record);
            var next = runStart + runCount;
            if (runCount > 0 && next < spellings.Count && spellings[next] == spelling) { runCount++; continue; }
            Close();
            if (firstIndex.TryGetValue(spelling, out var index)) { runStart = index; runCount = 1; }
            else segments.Add(new JObject { ["n"] = record.DeepClone() });
        }
        Close();
        return segments;
    }
}
