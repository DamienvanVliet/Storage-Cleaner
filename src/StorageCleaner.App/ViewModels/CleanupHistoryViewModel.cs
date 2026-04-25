using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StorageCleaner.Core.Extensions;
using StorageCleaner.Core.Abstractions;
using StorageCleaner.Core.Models;

namespace StorageCleaner.App.ViewModels;

public partial class CleanupHistoryViewModel : ViewModelBase
{
    private readonly ICleanupHistoryStore _historyStore;

    public CleanupHistoryViewModel(ICleanupHistoryStore historyStore)
    {
        _historyStore = historyStore;
        Entries = [];
        FilteredEntries = [];
        CategoryFilters = ["All"];
        _ = RefreshAsync();
    }

    public ObservableCollection<CleanupHistoryEntry> Entries { get; }

    public ObservableCollection<CleanupHistoryEntry> FilteredEntries { get; }

    public ObservableCollection<string> CategoryFilters { get; }

    [ObservableProperty]
    private string searchText = string.Empty;

    [ObservableProperty]
    private string selectedCategoryFilter = "All";

    [ObservableProperty]
    private string emptyStateText = "No cleanup history yet.";

    [ObservableProperty]
    private string statusText = "History shows every cleanup action for auditing and restore lookup.";

    public bool ShowEmptyState => FilteredEntries.Count == 0;

    partial void OnSearchTextChanged(string value)
    {
        ApplyFilters();
    }

    partial void OnSelectedCategoryFilterChanged(string value)
    {
        ApplyFilters();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        var entries = await _historyStore.ReadAsync(maxEntries: 1000);
        Entries.Clear();
        foreach (var entry in entries)
        {
            Entries.Add(entry);
        }

        BuildCategoryFilters();
        ApplyFilters();
        StatusText = $"Loaded {Entries.Count:N0} history record(s).";
    }

    private void BuildCategoryFilters()
    {
        CategoryFilters.Clear();
        CategoryFilters.Add("All");
        foreach (var category in Entries
                     .Select(static entry => entry.Category.ToString())
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(static category => category, StringComparer.OrdinalIgnoreCase))
        {
            CategoryFilters.Add(category);
        }

        if (!CategoryFilters.Contains(SelectedCategoryFilter, StringComparer.OrdinalIgnoreCase))
        {
            SelectedCategoryFilter = "All";
        }
    }

    private void ApplyFilters()
    {
        IEnumerable<CleanupHistoryEntry> query = Entries;

        if (!string.IsNullOrWhiteSpace(SelectedCategoryFilter) &&
            !string.Equals(SelectedCategoryFilter, "All", StringComparison.OrdinalIgnoreCase))
        {
            query = query.Where(entry =>
                string.Equals(entry.Category.ToString(), SelectedCategoryFilter, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            query = query.Where(entry =>
                entry.FullPath.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
                entry.RunId.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
                (entry.ErrorMessage?.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        var filtered = query
            .OrderByDescending(static entry => entry.Timestamp)
            .ToArray();

        FilteredEntries.Clear();
        foreach (var entry in filtered)
        {
            FilteredEntries.Add(entry);
        }

        EmptyStateText = Entries.Count == 0
            ? "No cleanup history yet. Run a cleanup to populate this page."
            : "No history rows match your filters.";

        if (FilteredEntries.Count > 0)
        {
            var reclaimed = FilteredEntries.Where(static entry => entry.Success).Sum(static entry => entry.ReclaimedBytes);
            StatusText = $"Showing {FilteredEntries.Count:N0} row(s). Reclaimed: {reclaimed.ToSizeString()}.";
        }

        OnPropertyChanged(nameof(ShowEmptyState));
    }
}
