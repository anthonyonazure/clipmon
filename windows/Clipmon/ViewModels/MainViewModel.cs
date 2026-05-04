using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using Clipmon.Models;
using Clipmon.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Clipmon.ViewModels;

public enum EntryScope
{
    All,
    Pinned
}

public sealed partial class MainViewModel : ObservableObject
{
    private readonly ClipboardDatabase _database;
    private readonly ClipboardMonitor _monitor;

    public ObservableCollection<ClipboardEntry> Entries { get; } = new();
    public ICollectionView EntriesView { get; }

    [ObservableProperty]
    private string searchText = string.Empty;

    [ObservableProperty]
    private EntryScope scope = EntryScope.All;

    [ObservableProperty]
    private bool isMonitoring;

    [ObservableProperty]
    private string statusMessage = "Ready to watch the clipboard";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private ClipboardEntry? selectedEntry;

    public bool HasSelection => SelectedEntry is not null;

    public int TotalCount => Entries.Count;
    public int PinnedCount => Entries.Count(e => e.IsPinned);
    public int VisibleCount
    {
        get
        {
            var count = 0;
            foreach (var _ in EntriesView.OfType<object>()) count++;
            return count;
        }
    }

    public MainViewModel(ClipboardDatabase database, ClipboardMonitor monitor)
    {
        _database = database;
        _monitor = monitor;

        _monitor.EntryCaptured += OnEntryCaptured;
        _monitor.StatusMessage += (_, msg) => RunOnUi(() => StatusMessage = msg);

        EntriesView = CollectionViewSource.GetDefaultView(Entries);
        EntriesView.SortDescriptions.Add(new SortDescription(nameof(ClipboardEntry.IsPinned), ListSortDirection.Descending));
        EntriesView.SortDescriptions.Add(new SortDescription(nameof(ClipboardEntry.UpdatedAt), ListSortDirection.Descending));
        EntriesView.Filter = FilterEntry;

        LoadFromDatabase();

        _monitor.Start();
        IsMonitoring = _monitor.IsMonitoring;

        // Capture once on startup so first-launch users immediately have something visible.
        _monitor.CaptureNow();
    }

    private void LoadFromDatabase()
    {
        Entries.Clear();
        foreach (var entry in _database.GetAll())
        {
            Entries.Add(entry);
        }
        RefreshCounts();
    }

    private bool FilterEntry(object obj)
    {
        if (obj is not ClipboardEntry entry) return false;
        if (Scope == EntryScope.Pinned && !entry.IsPinned) return false;

        var query = (SearchText ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(query)) return true;

        return entry.SearchableText.Contains(query, StringComparison.Ordinal);
    }

    partial void OnSearchTextChanged(string value) => RefreshFilter();
    partial void OnScopeChanged(EntryScope value) => RefreshFilter();

    private void RefreshFilter()
    {
        EntriesView.Refresh();
        OnPropertyChanged(nameof(VisibleCount));
        EnsureSelectionStillVisible();
    }

    private void RefreshCounts()
    {
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(PinnedCount));
        OnPropertyChanged(nameof(VisibleCount));
    }

    private void EnsureSelectionStillVisible()
    {
        if (SelectedEntry is not null && FilterEntry(SelectedEntry)) return;
        SelectedEntry = EntriesView.OfType<ClipboardEntry>().FirstOrDefault();
    }

    private void OnEntryCaptured(object? sender, ClipboardEntry entry)
    {
        RunOnUi(() =>
        {
            var existing = Entries.FirstOrDefault(e => e.Fingerprint == entry.Fingerprint);
            if (existing is not null && !ReferenceEquals(existing, entry))
            {
                Entries.Remove(existing);
            }

            if (!Entries.Contains(entry))
            {
                Entries.Insert(0, entry);
            }
            else
            {
                EntriesView.Refresh();
            }

            RefreshCounts();
            EnsureSelectionStillVisible();
        });
    }

    [RelayCommand]
    private void CaptureNow()
    {
        _monitor.CaptureNow();
    }

    [RelayCommand]
    private void TogglePauseResume()
    {
        if (_monitor.IsMonitoring)
        {
            _monitor.Stop();
        }
        else
        {
            _monitor.Start();
        }
        IsMonitoring = _monitor.IsMonitoring;
    }

    [RelayCommand]
    private void TogglePin(ClipboardEntry? entry)
    {
        if (entry is null) return;

        entry.IsPinned = !entry.IsPinned;
        entry.UpdatedAt = DateTime.UtcNow;
        _database.UpdatePinned(entry.Fingerprint, entry.IsPinned);

        EntriesView.Refresh();
        OnPropertyChanged(nameof(PinnedCount));
        StatusMessage = entry.IsPinned ? "Pinned item" : "Unpinned item";
    }

    [RelayCommand]
    private void Delete(ClipboardEntry? entry)
    {
        if (entry is null) return;

        _database.Delete(entry.Fingerprint);
        Entries.Remove(entry);

        if (SelectedEntry == entry)
        {
            SelectedEntry = EntriesView.OfType<ClipboardEntry>().FirstOrDefault();
        }

        RefreshCounts();
        StatusMessage = "Deleted item";
    }

    [RelayCommand]
    private void Copy(ClipboardEntry? entry)
    {
        if (entry is null) return;
        _monitor.CopyToClipboard(entry);
        EntriesView.Refresh();
    }

    [RelayCommand]
    private void ClearKeepingPinned()
    {
        _database.Clear(keepPinned: true);
        var toRemove = Entries.Where(e => !e.IsPinned).ToList();
        foreach (var entry in toRemove) Entries.Remove(entry);
        RefreshCounts();
        EnsureSelectionStillVisible();
        StatusMessage = "Cleared unpinned items";
    }

    [RelayCommand]
    private void ClearEverything()
    {
        _database.Clear(keepPinned: false);
        Entries.Clear();
        SelectedEntry = null;
        RefreshCounts();
        StatusMessage = "Cleared clipboard history";
    }

    public void ImportFiles(IEnumerable<string> paths)
    {
        _monitor.ImportFiles(paths);
        RunOnUi(LoadFromDatabase);
    }

    private static void RunOnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.Invoke(action);
        }
    }
}
