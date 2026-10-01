using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace ModderLords.Operations;

/// <summary>Bounded, text-preserving chunking for operation snapshots carried by protobuf strings.</summary>
public static class SnapshotTransfer
{
    public const int MaxChunkBytes = 224 * 1024;
    public const int MaxChunks = 32;
    public const int MaxSnapshotBytes = MaxChunkBytes * MaxChunks;

    /// <summary>How a reassembled payload is spelled (OperationSnapshotV1.Encoding). Zero is the original plain JSON.</summary>
    public const int PlainJson = 0, GzipFull = 1, GzipDelta = 2;

    public static IReadOnlyList<string> Split(string payload)
    {
        if (payload == null) throw new ArgumentNullException(nameof(payload));
        var total = Encoding.UTF8.GetByteCount(payload);
        if (total > MaxSnapshotBytes) throw new ArgumentException("Snapshot exceeds transfer bounds", nameof(payload));
        if (total <= MaxChunkBytes) return new[] { payload };

        var chunks = new List<string>();
        var offset = 0;
        while (offset < payload.Length)
        {
            var low = 1;
            var high = payload.Length - offset;
            var fit = 0;
            while (low <= high)
            {
                var middle = low + (high - low) / 2;
                if (offset + middle < payload.Length && char.IsHighSurrogate(payload[offset + middle - 1]) && char.IsLowSurrogate(payload[offset + middle])) middle--;
                if (middle <= 0) { high = 0; continue; }
                if (Encoding.UTF8.GetByteCount(payload.Substring(offset, middle)) <= MaxChunkBytes) { fit = middle; low = middle + 1; }
                else high = middle - 1;
            }
            if (fit <= 0) throw new InvalidOperationException("Snapshot contains an unchunkable UTF-8 sequence");
            chunks.Add(payload.Substring(offset, fit));
            offset += fit;
            if (chunks.Count > MaxChunks) throw new ArgumentException("Snapshot exceeds chunk count", nameof(payload));
        }
        return chunks;
    }
}

/// <summary>Accepts one ordered or out-of-order snapshot revision, rejecting conflicting or unbounded chunks.</summary>
public sealed class SnapshotReassembler
{
    private string epoch = "";
    private long revision = -1;
    private long completedRevision = -1;
    private string?[] chunks = Array.Empty<string?>();
    private int bytes;

    public void BeginSession(string authenticatedEpoch)
    {
        if (string.IsNullOrEmpty(authenticatedEpoch)) throw new ArgumentException("Authenticated epoch required");
        epoch = authenticatedEpoch;
        revision = -1;
        completedRevision = -1;
        chunks = Array.Empty<string?>();
        bytes = 0;
    }

    public void Disconnect()
    {
        epoch = "";
        revision = -1;
        chunks = Array.Empty<string?>();
        bytes = 0;
    }

    /// <summary>
    /// Forgets that revisions after <paramref name="lastGood"/> completed, after a completed payload turned out unusable (a
    /// diff whose base this player does not have): the full copy asked for instead may carry the same revision.
    /// </summary>
    public void RewindTo(long lastGood)
    {
        if (completedRevision > lastGood) completedRevision = lastGood;
    }

    public bool Offer(string authenticatedEpoch, long incomingRevision, int index, int count, string payload, out string complete)
    {
        complete = "";
        if (epoch.Length == 0 || authenticatedEpoch != epoch || incomingRevision < 0 || incomingRevision <= completedRevision ||
            count < 1 || count > SnapshotTransfer.MaxChunks || index < 0 || index >= count || payload == null) return false;
        var payloadBytes = Encoding.UTF8.GetByteCount(payload);
        if (payloadBytes > SnapshotTransfer.MaxChunkBytes) return false;

        if (incomingRevision > revision)
        {
            revision = incomingRevision;
            chunks = new string?[count];
            bytes = 0;
        }
        else if (incomingRevision < revision || chunks.Length != count) return false;

        if (chunks[index] != null)
        {
            if (!string.Equals(chunks[index], payload, StringComparison.Ordinal))
            {
                revision = -1;
                chunks = Array.Empty<string?>();
                bytes = 0;
            }
            return false;
        }

        if (bytes + payloadBytes > SnapshotTransfer.MaxSnapshotBytes) return false;
        chunks[index] = payload;
        bytes += payloadBytes;
        if (chunks.Any(c => c == null)) return false;

        complete = string.Concat(chunks!);
        completedRevision = revision;
        revision = -1;
        chunks = Array.Empty<string?>();
        bytes = 0;
        return true;
    }
}
