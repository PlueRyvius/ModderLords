using System.Runtime.CompilerServices;
using HarmonyLib;
using ModderLords.CompatSync.Coop;

namespace ModderLords.Core.Tests;

/// <summary>
/// SerializedState against the shape of Bellum's PrivyCouncilBehavior.EnsureRecordIndexes: a dirty flag, then Clear and
/// Add into dictionaries, hit from many threads at once the way the parallel moving-party tick hits it. The unguarded
/// copy proves the test reproduces "same key already added"; the guarded copy must never throw.
/// </summary>
public class SerializedStateTests
{
    // Two copies of the same code: Harmony patches are process-wide, so the control must be a method nobody guards.
    private interface ILazyIndex { void Invalidate(); int Lookup(string id); }

    private sealed class UnguardedCouncil : ILazyIndex
    {
        private readonly List<string> _records = Enumerable.Range(0, 40).Select(i => "kingdom_" + i).ToList();
        private readonly Dictionary<string, int> _byKingdom = new();
        private bool _dirty = true;

        public void Invalidate() => _dirty = true;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public int Lookup(string id)
        {
            EnsureIndex();
            return _byKingdom.TryGetValue(id, out var v) ? v : -1;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void EnsureIndex()
        {
            if (!_dirty) return;
            _byKingdom.Clear();
            for (var i = 0; i < _records.Count; i++)
            {
                Thread.SpinWait(200); // widen the window the way a real rebuild over every clan does
                _byKingdom.Add(_records[i], i);
            }
            _dirty = false;
        }
    }

    private sealed class GuardedCouncil : ILazyIndex
    {
        private readonly List<string> _records = Enumerable.Range(0, 40).Select(i => "kingdom_" + i).ToList();
        private readonly Dictionary<string, int> _byKingdom = new();
        private bool _dirty = true;

        public void Invalidate() => _dirty = true;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public int Lookup(string id)
        {
            EnsureIndex();
            return _byKingdom.TryGetValue(id, out var v) ? v : -1;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void EnsureIndex()
        {
            if (!_dirty) return;
            _byKingdom.Clear();
            for (var i = 0; i < _records.Count; i++)
            {
                Thread.SpinWait(200); // widen the window the way a real rebuild over every clan does
                _byKingdom.Add(_records[i], i);
            }
            _dirty = false;
        }
    }

    private static int Hammer(ILazyIndex index, int rounds = 60, int threads = 8)
    {
        var failures = 0;
        for (var round = 0; round < rounds; round++)
        {
            index.Invalidate();
            using var start = new Barrier(threads);
            var workers = Enumerable.Range(0, threads).Select(t => new Thread(() =>
            {
                start.SignalAndWait();
                try { for (var k = 0; k < 20; k++) index.Lookup("kingdom_" + (k + t) % 40); }
                catch (Exception) { Interlocked.Increment(ref failures); }
            })).ToList();
            workers.ForEach(w => w.Start());
            workers.ForEach(w => w.Join());
        }
        return failures;
    }

    [Fact]
    public void Unguarded_lazy_index_breaks_under_parallel_lookups()
    {
        Assert.True(Hammer(new UnguardedCouncil()) > 0, "the control should reproduce the Bellum crash; if it cannot, the guarded test proves nothing");
    }

    [Fact]
    public void Guarded_lazy_index_survives_parallel_lookups()
    {
        var warnings = new List<string>();
        var guarded = SerializedState.Guard(new Harmony("tests.serialized-state"), typeof(GuardedCouncil),
            new[] { "_byKingdom", "_dirty" }, warnings.Add);

        // Invalidate, Lookup and EnsureIndex; constructors are never guarded.
        Assert.Empty(warnings);
        Assert.Equal(3, guarded);
        Assert.Equal(0, Hammer(new GuardedCouncil()));
        Assert.Equal(7, new GuardedCouncil().Lookup("kingdom_7"));
    }

    [Fact]
    public void Missing_field_is_reported_not_ignored()
    {
        var warnings = new List<string>();
        var guarded = SerializedState.Guard(new Harmony("tests.serialized-state-missing"), typeof(UnguardedCouncil), new[] { "_gone" }, warnings.Add);
        Assert.Equal(0, guarded);
        Assert.Contains(warnings, w => w.Contains("_gone"));
    }
}
