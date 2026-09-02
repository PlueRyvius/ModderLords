using System.Collections.ObjectModel;
using System.Collections.Specialized;

namespace ModularCoop.App.ViewModels;

/// <summary>ObservableCollection that can drop a block of leading items with one Reset instead of N removals.</summary>
public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
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
