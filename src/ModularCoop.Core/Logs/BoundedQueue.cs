using System.Collections.Concurrent;

namespace ModularCoop.Core.Logs;

/// <summary>
/// A thread-safe queue that discards its oldest entries instead of growing without limit, counting
/// what it dropped so a caller can say so out loud.
///
/// This exists because the console's pending queue was unbounded while draining on a timer: an engine
/// printing faster than the drain rate grew it forever, and one session with a trace switch on reached
/// 19.8 GB of working set and pushed the machine into paging. Producers here are engine output callbacks
/// on arbitrary threads, so the bound has to hold under concurrent writes.
/// </summary>
public sealed class BoundedQueue<T>
{
    private readonly ConcurrentQueue<T> _items = new();
    private readonly int _capacity;
    private long _dropped;

    public BoundedQueue(int capacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
    }

    public int Capacity => _capacity;
    public int Count => _items.Count;
    public bool IsEmpty => _items.IsEmpty;

    /// <summary>Total entries discarded to stay within capacity.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    /// <summary>Adds an entry, discarding oldest entries if that puts the queue over capacity.</summary>
    public void Enqueue(T item)
    {
        _items.Enqueue(item);
        while (_items.Count > _capacity && _items.TryDequeue(out _))
            Interlocked.Increment(ref _dropped);
    }

    public bool TryDequeue(out T item) => _items.TryDequeue(out item!);

    /// <summary>Reads the drop counter and resets it, so a caller reports each drop exactly once.</summary>
    public long TakeDropped() => Interlocked.Exchange(ref _dropped, 0);

    public void Clear()
    {
        while (_items.TryDequeue(out _)) { }
        Interlocked.Exchange(ref _dropped, 0);
    }
}
