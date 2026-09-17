using System;
using System.Collections.Generic;

namespace ModderLords.Operations;

/// <summary>Bounded join ledger. The host serializes calls with its transport lifecycle.
/// Acknowledgement permits one validation; it never extends the original deadline.</summary>
public sealed class JoinAdmission<TPeer, TValidation> where TPeer : class
{
    private sealed class Entry
    {
        public long Deadline;
        public bool Acknowledged, HasValidation, Consumed;
        public string Identity = "";
        public TValidation Validation = default!;
    }
    private readonly Dictionary<TPeer, Entry> entries = new Dictionary<TPeer, Entry>();
    private readonly int capacity;
    private readonly long timeout;
    public JoinAdmission(int capacity = 128, long timeoutMilliseconds = 30000)
    {
        if (capacity < 1 || timeoutMilliseconds < 1) throw new ArgumentOutOfRangeException();
        this.capacity = capacity; timeout = timeoutMilliseconds;
    }
    public bool Begin(TPeer peer, long now)
    {
        if (entries.TryGetValue(peer, out var existing)) return now < existing.Deadline && !existing.Consumed;
        if (entries.Count >= capacity) return false;
        entries.Add(peer, new Entry { Deadline = checked(now + timeout) }); return true;
    }
    public bool Queue(TPeer peer, string identity, TValidation validation, long now)
    {
        if (string.IsNullOrEmpty(identity) || identity.Length > 256 || !Begin(peer, now)) return false;
        var entry = entries[peer];
        if (entry.HasValidation) return entry.Identity == identity;
        entry.Identity = identity; entry.Validation = validation; entry.HasValidation = true; return true;
    }
    public bool Acknowledge(TPeer peer, long now)
    {
        if (!entries.TryGetValue(peer, out var entry)) return false;
        if (entry.Consumed) return true;
        if (now >= entry.Deadline) return false;
        entry.Acknowledged = true; return true;
    }
    public bool TryTake(TPeer peer, long now, out TValidation validation)
    {
        validation = default!;
        if (!entries.TryGetValue(peer, out var entry) || entry.Consumed || !entry.Acknowledged || !entry.HasValidation || now >= entry.Deadline) return false;
        validation = entry.Validation; entry.Validation = default!;
        entry.Consumed = true; return true;
    }
    public bool IsExpired(TPeer peer, long now) => entries.TryGetValue(peer, out var entry) && !entry.Consumed && now >= entry.Deadline;
    public TPeer[] Peers { get { var result = new TPeer[entries.Count]; entries.Keys.CopyTo(result, 0); return result; } }
    public void Disconnect(TPeer peer) => entries.Remove(peer);
    public void Clear() => entries.Clear();
}
