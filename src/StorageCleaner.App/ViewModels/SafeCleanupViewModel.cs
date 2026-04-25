using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StorageCleaner.App.Services;
using StorageCleaner.Core.Abstractions;
using StorageCleaner.Core.Extensions;
using StorageCleaner.Core.Models;

namespace StorageCleaner.App.ViewModels;

public partial class SafeCleanupViewModel : ViewModelBase
{
    private readonly ISafeCleanupAnalyzer _safeCleanupAnalyzer;
    private readonly ICleanupExecutor _cleanupExecutor;
    private readonly ISettingsService _settingsService;
    private readonly IDialogService _dialogService;

    public SafeCleanupViewModel(
        ISafeCleanupAnalyzer safeCleanupAnalyzer,
        ICleanupExecutor cleanupExecutor,
        ISettingsService settingsService,
        IDialogService dialogService)
    {
        _safeCleanupAnalyzer = safeCleanupAnalyzer;
        _cleanupExecutor = cleanupExecutor;
        _settingsService = settingsService;
        _dialogService = dialogService;

        Categories =
        [
            new CleanupCategoryOptionViewModel(CleanupCategory.WindowsTemp, "Windows Temp", "Temporary files under C:\\Windows\\Temp"),
            new CleanupCategoryOptionViewModel(CleanupCategory.UserTemp, "User Temp", "Temporary files in your user temp folder"),
            new CleanupCategoryOptionViewModel(CleanupCategory.RecycleBin, "Recycle Bin", "Items currently in recycle bin"),
            new CleanupCategoryOptionViewModel(CleanupCategory.BrowserCache, "Browser Cache", "Cache files from Chrome, Edge, Firefox and Brave"),
            new CleanupCategoryOptionViewModel(CleanupCategory.OldLogFiles, "Old Log Files", "Log files older than 30 days in safe locations")
        ];

        Candidates = [];
        FilteredCandidates = [];
        CategoryFilters = ["All"];
        RecommendationFilters = ["All", "Recommended", "Needs Review", "Blocked"];
    }

    public ObservableCollection<CleanupCategoryOptionViewModel> Categories { get; }

    public ObservableCollection<CleanupCandidateViewModel> Candidates { get; }

    public ObservableCollection<CleanupCandidateViewModel> FilteredCandidates { get; }

    public ObservableCollection<string> CategoryFilters { get; }

    public ObservableCollection<string> RecommendationFilters { get; }

    [ObservableProperty]
    private bool isAnalyzing;

    [ObservableProperty]
    private bool isCleaning;

    [ObservableProperty]
    private string statusText = "Choose categories and run Preview. Nothing is deleted until you confirm.";

    [ObservableProperty]
    private long estimatedBytes;

    [ObservableProperty]
    private long selectedBytes;

    [ObservableProperty]
    private bool strictSafetyMode = true;

    [ObservableProperty]
    private int selectedItemCount;

    [ObservableProperty]
    private int selectedRiskyCount;

    [ObservableProperty]
    private bool riskOverrideUnlockedForSession;

    [ObservableProperty]
    private string simulationSummary = "No simulation run yet.";

    [ObservableProperty]
    private CleanupCandidateViewModel? selectedCandidate;

    [ObservableProperty]
    private string searchText = string.Empty;

    [ObservableProperty]
    private string selectedCategoryFilter = "All";

    [ObservableProperty]
    private string selectedRecommendationFilter = "All";

    [ObservableProperty]
    private bool showSuccessBanner;

    [ObservableProperty]
    private string successBannerText = string.Empty;

    [ObservableProperty]
    private bool showUndoBanner;

    [ObservableProperty]
    private string undoBannerText = string.Empty;

    [ObservableProperty]
    private string emptyStateText = "Run Preview to find cleanup candidates.";

    [ObservableProperty]
    private string safetyExplanation = "Select a candidate to see why it is considered safe or risky.";

    public bool ShowEmptyState => !IsAnalyzing && !IsCleaning && FilteredCandidates.Count == 0;

    partial void OnStrictSafetyModeChanged(bool value)
    {
        if (Candidates.Count == 0)
        {
            return;
        }

        foreach (var candidate in Candidates)
        {
            candidate.IsSelected = IsCandidateSelectedByDefault(candidate.Candidate);
        }

        RecalculateSelectionTotals();
        StatusText = value
            ? "Strict safety mode enabled. Project-like or high-risk paths are deselected."
            : "Strict safety mode disabled. Review risky candidates before cleaning.";
    }

    partial void OnSelectedCandidateChanged(CleanupCandidateViewModel? value)
    {
        if (value is null)
        {
            SafetyExplanation = "Select a candidate to see why it is considered safe or risky.";
            return;
        }

        SafetyExplanation = value.IsRecommended
            ? $"Recommended: {value.RiskReason}"
            : $"Needs review: {value.RiskReason}";
    }

    partial void OnSearchTextChanged(string value)
    {
        ApplyCandidateFilters();
    }

    partial void OnSelectedCategoryFilterChanged(string value)
    {
        ApplyCandidateFilters();
    }

    partial void OnSelectedRecommendationFilterChanged(string value)
    {
        ApplyCandidateFilters();
    }

    partial void OnIsAnalyzingChanged(bool value)
    {
        EmptyStateText = value
            ? "Preview in progress..."
            : EmptyStateText;
        OnPropertyChanged(nameof(ShowEmptyState));
    }

    partial void OnIsCleaningChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowEmptyState));
    }

    [RelayCommand]
    private async Task AnalyzeAsync()
    {
        if (IsAnalyzing || IsCleaning)
        {
            return;
        }

        var selectedCategories = Categories
            .Where(static category => category.IsSelected)
            .Select(static category => category.Category)
            .ToArray();

        if (selectedCategories.Length == 0)
        {
            _dialogService.ShowInfo("Safe Cleanup", "Select at least one cleanup category.");
            return;
        }

        IsAnalyzing = true;
        StatusText = "Analyzing selected categories...";
        ShowSuccessBanner = false;
        ShowUndoBanner = false;
        Candidates.Clear();
        FilteredCandidates.Clear();
        EstimatedBytes = 0;
        SelectedBytes = 0;
        CategoryFilters.Clear();
        CategoryFilters.Add("All");
        SelectedCategoryFilter = "All";
        SelectedRecommendationFilter = "All";

        try
        {
            var progress = new Progress<CleanupProgress>(value =>
            {
                StatusText = $"Analyzing {value.Category}: {value.CandidatesFound} candidates";
            });

            var results = await _safeCleanupAnalyzer.AnalyzeAsync(selectedCategories, progress);
            foreach (var candidate in results)
            {
                RegisterCandidate(candidate);
            }

            EstimatedBytes = Candidates.Sum(static c => c.SizeBytes);
            RecalculateSelectionTotals();
            BuildCategoryFilters();
            ApplyCandidateFilters();
            var recommendedCount = Candidates.Count(static candidate => candidate.IsRecommended);
            var reviewCount = Candidates.Count - recommendedCount;
            StatusText = $"Preview ready: {Candidates.Count} items, {EstimatedBytes.ToSizeString()} estimated. Recommended: {recommendedCount}, Needs review: {reviewCount}.";
            EmptyStateText = "No candidates match the selected filters.";
            SelectedCandidate = FilteredCandidates.FirstOrDefault() ?? Candidates.FirstOrDefault();
        }
        catch (OperationCanceledException)
        {
            StatusText = "Analysis canceled.";
            EmptyStateText = "Preview canceled. Run Preview again when ready.";
        }
        catch (Exception ex)
        {
            StatusText = "Analysis failed.";
            _dialogService.ShowError("Safe Cleanup", ex.Message);
            EmptyStateText = "Preview failed. Check logs and try again.";
        }
        finally
        {
            IsAnalyzing = false;
        }
    }

    [RelayCommand]
    private async Task CleanSelectedAsync()
    {
        if (IsAnalyzing || IsCleaning)
        {
            return;
        }

        var selectedCandidates = Candidates.Where(static candidate => candidate.IsSelected).ToArray();
        if (selectedCandidates.Length == 0)
        {
            _dialogService.ShowInfo("Safe Cleanup", "Select at least one candidate to clean.");
            return;
        }

        var selectedBytes = selectedCandidates.Sum(static candidate => candidate.SizeBytes);
        var requiresOverrideCount = selectedCandidates.Count(static candidate => candidate.RequiresExplicitOverride);

        if (!_dialogService.Confirm(
                "Confirm Cleanup",
                $"Clean {selectedCandidates.Length} items?\n\nEstimated reclaim: {selectedBytes.ToSizeString()}\nItems needing review: {requiresOverrideCount}"))
        {
            return;
        }

        var allowRiskyPaths = false;
        if (requiresOverrideCount > 0)
        {
            if (StrictSafetyMode)
            {
                _dialogService.ShowError(
                    "Strict Safety Mode",
                    $"{requiresOverrideCount} selected item(s) are high-risk (project/workspace or sensitive paths).\n\nDisable Strict Safety Mode first if you really want to include them.");
                return;
            }

            if (!RiskOverrideUnlockedForSession)
            {
                _dialogService.ShowError(
                    "Session Risk Lock",
                    "Risky cleanup is still locked for this session.\n\nClick \"Unlock Risky Cleanup (Session)\" first.");
                return;
            }

            allowRiskyPaths = _dialogService.ConfirmTyped(
                "Risk Warning",
                $"{requiresOverrideCount} selected items require explicit override (project/workspace or sensitive location).\n\nType DELETE to continue cleanup.",
                expectedText: "DELETE",
                warning: true);
            if (!allowRiskyPaths)
            {
                return;
            }
        }

        IsCleaning = true;
        StatusText = "Cleaning selected items...";
        ShowSuccessBanner = false;
        ShowUndoBanner = false;

        try
        {
            var execution = await _cleanupExecutor.ExecuteAsync(
                selectedCandidates.Select(static candidate => candidate.Candidate).ToArray(),
                new CleanupExecutionOptions(
                    UseRecycleBin: _settingsService.Current.UseRecycleBinByDefault,
                    AllowRiskyPaths: allowRiskyPaths,
                    SimulationOnly: false,
                    QueueLockedForReboot: _settingsService.Current.QueueLockedDeletesOnReboot,
                    CaptureRestoreBackup: true));

            StatusText = $"Cleanup complete. Success: {execution.SuccessCount}, Failed: {execution.FailureCount}, Queued reboot: {execution.QueuedForRebootCount}.";
            ShowSuccessBanner = true;
            SuccessBannerText = $"Cleanup completed. Reclaimed {execution.ReclaimedBytes.ToSizeString()} (success {execution.SuccessCount:N0}, failed {execution.FailureCount:N0}, queued {execution.QueuedForRebootCount:N0}).";
            ShowUndoBanner = execution.SuccessCount > 0;
            UndoBannerText = execution.SuccessCount > 0
                ? $"Undo available in Restore Center for run {execution.RunId}."
                : "No items were cleaned, so there is nothing to restore.";
            _dialogService.ShowInfo(
                "Cleanup Complete",
                $"Reclaimed: {execution.ReclaimedBytes.ToSizeString()}\nSuccess: {execution.SuccessCount}\nFailed: {execution.FailureCount}\nQueued for reboot: {execution.QueuedForRebootCount}\nRun: {execution.RunId}");

            foreach (var successItem in execution.Items.Where(static item => item.Success))
            {
                var vm = Candidates.FirstOrDefault(candidate =>
                    string.Equals(candidate.FullPath, successItem.FullPath, StringComparison.OrdinalIgnoreCase));
                if (vm is not null)
                {
                    Candidates.Remove(vm);
                }
            }

            RecalculateSelectionTotals();
            BuildCategoryFilters();
            ApplyCandidateFilters();
            EstimatedBytes = Candidates.Sum(static c => c.SizeBytes);
        }
        catch (OperationCanceledException)
        {
            StatusText = "Cleanup canceled.";
        }
        catch (Exception ex)
        {
            StatusText = "Cleanup failed.";
            _dialogService.ShowError("Safe Cleanup", ex.Message);
        }
        finally
        {
            IsCleaning = false;
        }
    }

    [RelayCommand]
    private async Task SimulateSelectedAsync()
    {
        if (IsAnalyzing || IsCleaning)
        {
            return;
        }

        var selectedCandidates = Candidates.Where(static candidate => candidate.IsSelected).ToArray();
        if (selectedCandidates.Length == 0)
        {
            _dialogService.ShowInfo("Simulation", "Select at least one candidate first.");
            return;
        }

        var requiresOverrideCount = selectedCandidates.Count(static candidate => candidate.RequiresExplicitOverride);
        var allowRiskyPaths = false;
        if (requiresOverrideCount > 0)
        {
            if (StrictSafetyMode)
            {
                _dialogService.ShowError("Simulation", "Strict mode blocks risky candidates. Deselect them or disable strict mode.");
                return;
            }

            if (!RiskOverrideUnlockedForSession)
            {
                _dialogService.ShowError(
                    "Simulation",
                    "Risky simulation is locked for this session.\n\nClick \"Unlock Risky Cleanup (Session)\" first.");
                return;
            }

            allowRiskyPaths = _dialogService.Confirm(
                "Risk Warning",
                $"{requiresOverrideCount} selected item(s) are high-risk. Continue simulation?",
                warning: true);
            if (!allowRiskyPaths)
            {
                return;
            }
        }

        IsCleaning = true;
        StatusText = "Running simulation...";
        try
        {
            var result = await _cleanupExecutor.ExecuteAsync(
                selectedCandidates.Select(static candidate => candidate.Candidate).ToArray(),
                new CleanupExecutionOptions(
                    UseRecycleBin: _settingsService.Current.UseRecycleBinByDefault,
                    AllowRiskyPaths: allowRiskyPaths,
                    SimulationOnly: true,
                    QueueLockedForReboot: false,
                    CaptureRestoreBackup: false));

            SimulationSummary = $"Simulation run {result.RunId}: {result.SuccessCount} items, {result.ReclaimedBytes.ToSizeString()} potential reclaim.";
            StatusText = "Simulation complete.";
            _dialogService.ShowInfo(
                "Simulation Complete",
                $"{result.SuccessCount} items would be cleaned.\nPotential reclaim: {result.ReclaimedBytes.ToSizeString()}\nRun: {result.RunId}");
        }
        catch (Exception ex)
        {
            StatusText = "Simulation failed.";
            _dialogService.ShowError("Simulation", ex.Message);
        }
        finally
        {
            IsCleaning = false;
        }
    }

    [RelayCommand]
    private void UnlockRiskyCleanupForSession()
    {
        if (RiskOverrideUnlockedForSession)
        {
            return;
        }

        if (!_dialogService.Confirm(
                "Unlock Risky Cleanup",
                "Unlock risky cleanup for this session only?\n\nUse this only if you fully understand what will be deleted.",
                warning: true))
        {
            return;
        }

        RiskOverrideUnlockedForSession = true;
        StatusText = "Risky cleanup unlocked for this session.";
    }

    [RelayCommand]
    private void SelectAllSafe()
    {
        foreach (var candidate in Candidates)
        {
            candidate.IsSelected = candidate.IsRecommended;
        }

        RecalculateSelectionTotals();
        StatusText = "Selected recommended safe items.";
    }

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var candidate in Candidates)
        {
            candidate.IsSelected = true;
        }

        RecalculateSelectionTotals();
    }

    [RelayCommand]
    private void SelectNone()
    {
        foreach (var candidate in Candidates)
        {
            candidate.IsSelected = false;
        }

        RecalculateSelectionTotals();
    }

    [RelayCommand]
    private void OpenCandidateLocation(CleanupCandidateViewModel? candidate)
    {
        var target = candidate ?? SelectedCandidate ?? Candidates.FirstOrDefault(static c => c.IsSelected);
        if (target is null)
        {
            return;
        }

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{target.FullPath}\"")
        {
            UseShellExecute = true
        });
    }

    private void RegisterCandidate(CleanupCandidate candidate)
    {
        var vm = new CleanupCandidateViewModel(candidate, IsCandidateSelectedByDefault(candidate));
        vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(CleanupCandidateViewModel.IsSelected))
            {
                RecalculateSelectionTotals();
            }
        };

        Candidates.Add(vm);
    }

    private void BuildCategoryFilters()
    {
        CategoryFilters.Clear();
        CategoryFilters.Add("All");
        foreach (var category in Candidates
                     .Select(static candidate => candidate.Category.ToString())
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(static category => category, StringComparer.OrdinalIgnoreCase))
        {
            CategoryFilters.Add(category);
        }
    }

    private void ApplyCandidateFilters()
    {
        IEnumerable<CleanupCandidateViewModel> query = Candidates;

        if (!string.IsNullOrWhiteSpace(SelectedCategoryFilter) &&
            !string.Equals(SelectedCategoryFilter, "All", StringComparison.OrdinalIgnoreCase))
        {
            query = query.Where(candidate =>
                string.Equals(candidate.Category.ToString(), SelectedCategoryFilter, StringComparison.OrdinalIgnoreCase));
        }

        query = SelectedRecommendationFilter switch
        {
            "Recommended" => query.Where(static candidate => candidate.IsRecommended),
            "Needs Review" => query.Where(static candidate => !candidate.IsRecommended && !candidate.Candidate.Risk.IsProtected),
            "Blocked" => query.Where(static candidate => candidate.Candidate.Risk.IsProtected),
            _ => query
        };

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            query = query.Where(candidate =>
                candidate.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
                candidate.FullPath.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
                candidate.RiskReason.Contains(SearchText, StringComparison.OrdinalIgnoreCase));
        }

        var filtered = query
            .OrderByDescending(static candidate => candidate.SizeBytes)
            .ThenBy(static candidate => candidate.FullPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        FilteredCandidates.Clear();
        foreach (var candidate in filtered)
        {
            FilteredCandidates.Add(candidate);
        }

        if (SelectedCandidate is not null && !FilteredCandidates.Contains(SelectedCandidate))
        {
            SelectedCandidate = FilteredCandidates.FirstOrDefault();
        }

        OnPropertyChanged(nameof(ShowEmptyState));
    }

    private void RecalculateSelectionTotals()
    {
        var selected = Candidates.Where(static candidate => candidate.IsSelected).ToArray();
        SelectedBytes = selected.Sum(static candidate => candidate.SizeBytes);
        SelectedItemCount = selected.Length;
        SelectedRiskyCount = selected.Count(static candidate => candidate.RequiresExplicitOverride);
    }

    private bool IsCandidateSelectedByDefault(CleanupCandidate candidate)
    {
        if (candidate.Risk.IsProtected)
        {
            return false;
        }

        if (StrictSafetyMode)
        {
            return !candidate.Risk.RequiresExplicitOverride;
        }

        return true;
    }
}
