using System.Collections.ObjectModel;
using System.Collections.Specialized;

namespace ModularCoop.App.ViewModels;

/// <summary>ObservableCollection that can drop a block of leading items with one Reset instead of N removals.</summary>
public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    /// <summary>Drops the oldest items matching <paramref name="expendable"/> first, then oldest of anything, until at most <paramref name="keep"/> remain. One Reset.</summary>
    public void TrimTo(int keep, Func<T, bool> expendable)
    {
        if (Items.Count <= keep) return;
        var toDrop = Items.Count - keep;
        var kept = new List<T>(keep);
        // First pass: skip expendable items from the head until enough are gone.
        foreach (var item in Items)
        {
            if (toDrop > 0 && expendable(item)) { toDrop--; continue; }
            kept.Add(item);
        }
        if (toDrop > 0) kept.RemoveRange(0, Math.Min(toDrop, kept.Count));
        Items.Clear();
        foreach (var k in kept) Items.Add(k);
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs("Count"));
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    public void RemoveFirst(int count)
    {
        if (count <= 0) return;
        count = Math.Min(count, Items.Count);
        for (int i = 0; i < count; i++) Items.RemoveAt(0);
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs("Count"));
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
