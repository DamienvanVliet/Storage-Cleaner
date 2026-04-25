using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using StorageCleaner.App.Services;
using StorageCleaner.Core.Abstractions;
using StorageCleaner.Core.Extensions;
using StorageCleaner.Core.Models;

namespace StorageCleaner.App.ViewModels;

public partial class PhotoCleanupViewModel : ViewModelBase
{
    private static readonly JsonSerializerOptions ReportJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly IPhotoCleanupService _photoCleanupService;
    private readonly ICleanupExecutor _cleanupExecutor;
    private readonly IPathSafetyService _pathSafetyService;
    private readonly ISettingsService _settingsService;
    private readonly IDialogService _dialogService;
    private readonly IAppLogger _logger;
    private readonly Dictionary<string, PhotoCleanupItemViewModel> _itemIndex = new(StringComparer.OrdinalIgnoreCase);

    public PhotoCleanupViewModel(
        IPhotoCleanupService photoCleanupService,
        ICleanupExecutor cleanupExecutor,
        IPathSafetyService pathSafetyService,
        ISettingsService settingsService,
        IDialogService dialogService,
        IAppLogger logger)
    {
        _photoCleanupService = photoCleanupService;
        _cleanupExecutor = cleanupExecutor;
        _pathSafetyService = pathSafetyService;
        _settingsService = settingsService;
        _dialogService = dialogService;
        _logger = logger;

        AvailableRoots = [];
        AvailableCategoryFilters = ["All", "Recommended", "Screenshots", "Large Videos", "Blurry Photos", "Similar Photos", "Images", "Videos"];
        FilteredResults = [];
        SimilarGroups = [];
        SimilarGroupItems = [];
        PreviewItems = [];

        RefreshRoots();
        SelectedRootPath = AvailableRoots.FirstOrDefault();
    }

    public ObservableCollection<string> AvailableRoots { get; }

    public ObservableCollection<string> AvailableCategoryFilters { get; }

    public ObservableCollection<PhotoCleanupItemViewModel> FilteredResults { get; }

    public ObservableCollection<SimilarPhotoGroupViewModel> SimilarGroups { get; }

    public ObservableCollection<PhotoCleanupItemViewModel> SimilarGroupItems { get; }

    public ObservableCollection<PhotoCleanupPreviewItemViewModel> PreviewItems { get; }

    [ObservableProperty]
    private string? selectedRootPath;

    [ObservableProperty]
    private string selectedCategoryFilter = "All";

    [ObservableProperty]
    private string searchText = string.Empty;

    [ObservableProperty]
    private bool isScanning;

    [ObservableProperty]
    private bool isCleaning;

    [ObservableProperty]
    private bool isCompactMode;

    [ObservableProperty]
    private string statusText = "Choose a folder or drive and run Photo Scan.";

    [ObservableProperty]
    private string emptyStateText = "Run a scan to discover screenshots, similar photos, blurry items, and large videos.";

    [ObservableProperty]
    private PhotoCleanupItemViewModel? selectedItem;

    [ObservableProperty]
    private SimilarPhotoGroupViewModel? selectedSimilarGroup;

    [ObservableProperty]
    private long selectedBytes;

    [ObservableProperty]
    private int selectedCount;

    [ObservableProperty]
    private int selectedSafeCount;

    [ObservableProperty]
    private int selectedRiskyCount;

    [ObservableProperty]
    private long totalMediaBytes;

    [ObservableProperty]
    private int totalMediaCount;

    [ObservableProperty]
    private int screenshotCount;

    [ObservableProperty]
    private int largeVideoCount;

    [ObservableProperty]
    private int blurryCount;

    [ObservableProperty]
    private int similarGroupCount;

    [ObservableProperty]
    private bool showPermissionWarning;

    [ObservableProperty]
    private string permissionWarningText = string.Empty;

    [ObservableProperty]
    private bool showSuccessBanner;

    [ObservableProperty]
    private string successBannerText = string.Empty;

    [ObservableProperty]
    private bool showUndoBanner;

    [ObservableProperty]
    private string undoBannerText = string.Empty;

    [ObservableProperty]
    private string lastCleanupRunId = string.Empty;

    [ObservableProperty]
    private int skippedFiles;

    [ObservableProperty]
    private int scanErrorCount;

    public bool IsBusy => IsScanning || IsCleaning;

    public bool ShowEmptyState => !IsBusy && FilteredResults.Count == 0;

    partial void OnSelectedSimilarGroupChanged(SimilarPhotoGroupViewModel? value)
    {
        SimilarGroupItems.Clear();
        if (value is null)
        {
            return;
        }

        foreach (var item in value.Items)
        {
            SimilarGroupItems.Add(item);
        }

        SelectedItem = SimilarGroupItems.FirstOrDefault();
    }

    partial void OnSelectedCategoryFilterChanged(string value)
    {
        ApplyFilters();
    }

    partial void OnSearchTextChanged(string value)
    {
        ApplyFilters();
    }

    partial void OnIsScanningChanged(bool value)
    {
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(ShowEmptyState));
    }

    partial void OnIsCleaningChanged(bool value)
    {
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(ShowEmptyState));
    }

    [RelayCommand]
    private void RefreshRoots()
    {
        AvailableRoots.Clear();
        foreach (var drive in DriveInfo.GetDrives().Where(static drive => drive.IsReady))
        {
            AvailableRoots.Add(drive.RootDirectory.FullName);
        }
    }

    [RelayCommand]
    private void BrowseFolder()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select Folder For Photo Cleanup",
            Multiselect = false
        };

        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.FolderName))
        {
            SelectedRootPath = dialog.FolderName;
        }
    }

    [RelayCommand]
    private async Task ScanMediaAsync()
    {
        if (IsScanning || IsCleaning)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(SelectedRootPath) || !Directory.Exists(SelectedRootPath))
        {
            _dialogService.ShowError("Photo Cleanup", "Select a valid folder or drive.");
            return;
        }

        IsScanning = true;
        ShowSuccessBanner = false;
        ShowUndoBanner = false;
        ShowPermissionWarning = false;
        PermissionWarningText = string.Empty;
        StatusText = "Scanning photos and videos...";
        ClearResults();

        try
        {
            var progress = new Progress<PhotoCleanupProgress>(value =>
            {
                StatusText = $"Scanning {value.FilesVisited:N0} files, media found {value.MediaFilesFound:N0}...";
            });

            var result = await _photoCleanupService.ScanAsync([SelectedRootPath], progress: progress);
            ApplyResult(result);

            StatusText =
                $"Scan complete. Media: {TotalMediaCount:N0}, screenshots: {ScreenshotCount:N0}, large videos: {LargeVideoCount:N0}, blurry photos: {BlurryCount:N0}, similar groups: {SimilarGroupCount:N0}.";

            if (result.ErrorCount > 0 || result.SkippedFiles > 0)
            {
                ShowPermissionWarning = true;
                PermissionWarningText =
                    $"Limited access detected. Scan skipped {result.SkippedFiles:N0} file(s) and hit {result.ErrorCount:N0} access/IO issue(s). Results remain safe and usable.";
            }
        }
        catch (OperationCanceledException)
        {
            StatusText = "Photo scan canceled.";
        }
        catch (Exception ex)
        {
            _logger.LogError("PhotoCleanupViewModel.ScanMediaAsync failed.", ex);
            StatusText = "Photo scan failed.";
            _dialogService.ShowError("Photo Cleanup", $"{ex.Message}\n\nDetails are in:\n{_logger.LogFilePath}");
        }
        finally
        {
            IsScanning = false;
        }
    }

    [RelayCommand]
    private void SelectAllSafeItems()
    {
        foreach (var item in _itemIndex.Values)
        {
            item.IsSelected = item.CanSelectSafely;
        }

        RecalculateSelectionTotals();
        StatusText = "Selected all safe recommended cleanup items.";
    }

    [RelayCommand]
    private void SelectNone()
    {
        foreach (var item in _itemIndex.Values)
        {
            item.IsSelected = false;
        }

        RecalculateSelectionTotals();
    }

    [RelayCommand]
    private void SelectSimilarDuplicates()
    {
        if (SelectedSimilarGroup is null)
        {
            _dialogService.ShowInfo("Similar Photos", "Select a similar-photo group first.");
            return;
        }

        foreach (var item in SelectedSimilarGroup.Items)
        {
            item.IsSelected = !item.IsRecommendedKeep && item.CanSelectSafely;
        }

        RecalculateSelectionTotals();
        StatusText = $"Selected duplicate candidates for group {SelectedSimilarGroup.GroupId}.";
    }

    [RelayCommand]
    private void PreviewDeleteSelected()
    {
        BuildPreviewRows();
        if (SelectedCount == 0)
        {
            _dialogService.ShowInfo("Photo Cleanup", "Select media items to preview first.");
            return;
        }

        var topLines = PreviewItems
            .Take(8)
            .Select(static item => $"- {item.Name} ({item.SizeDisplay})")
            .ToArray();

        var suffix = SelectedCount > topLines.Length
            ? $"\n...and {SelectedCount - topLines.Length:N0} more item(s)."
            : string.Empty;

        _dialogService.ShowInfo(
            "Delete Preview",
            $"Selected items: {SelectedCount:N0}\nEstimated reclaim: {SelectedBytes.ToSizeString()}\nSafe selections: {SelectedSafeCount:N0}\nNeeds review: {SelectedRiskyCount:N0}\n\n{string.Join('\n', topLines)}{suffix}");
    }

    [RelayCommand]
    private async Task DeleteSelectedAsync()
    {
        if (IsScanning || IsCleaning)
        {
            return;
        }

        var selected = _itemIndex.Values.Where(static item => item.IsSelected).ToArray();
        if (selected.Length == 0)
        {
            _dialogService.ShowInfo("Photo Cleanup", "Select media items first.");
            return;
        }

        if (!_dialogService.Confirm(
                "Confirm Cleanup",
                $"Move {selected.Length:N0} selected item(s) to Recycle Bin?\n\nEstimated reclaim: {selected.Sum(static item => item.SizeBytes).ToSizeString()}\nRestore backup will be captured before cleanup."))
        {
            return;
        }

        IsCleaning = true;
        StatusText = "Cleaning selected media...";

        try
        {
            var riskyCount = selected.Count(static item => item.Risk.RequiresExplicitOverride);
            var allowRisky = false;
            if (riskyCount > 0)
            {
                allowRisky = _dialogService.ConfirmTyped(
                    "Risky Media Paths",
                    $"{riskyCount:N0} selected item(s) are in risky locations.\n\nType DELETE to continue.",
                    expectedText: "DELETE",
                    warning: true);

                if (!allowRisky)
                {
                    StatusText = "Cleanup canceled.";
                    return;
                }
            }

            var candidates = selected
                .Select(item => new CleanupCandidate
                {
                    Category = CleanupCategory.ManualSelection,
                    FullPath = item.FullPath,
                    IsDirectory = false,
                    SizeBytes = item.SizeBytes,
                    LastModifiedUtc = item.LastModifiedUtc,
                    Risk = item.Risk
                })
                .ToArray();

            var result = await _cleanupExecutor.ExecuteAsync(
                candidates,
                new CleanupExecutionOptions(
                    UseRecycleBin: _settingsService.Current.UseRecycleBinByDefault,
                    AllowRiskyPaths: allowRisky,
                    SimulationOnly: false,
                    QueueLockedForReboot: _settingsService.Current.QueueLockedDeletesOnReboot,
                    CaptureRestoreBackup: true));

            foreach (var removed in result.Items.Where(static item => item.Success))
            {
                if (_itemIndex.TryGetValue(removed.FullPath, out var vm))
                {
                    RemoveItemFromAllViews(vm);
                }
            }

            RecalculateSelectionTotals();
            ShowSuccessBanner = true;
            SuccessBannerText =
                $"Cleanup complete. Reclaimed {result.ReclaimedBytes.ToSizeString()} (success {result.SuccessCount:N0}, failed {result.FailureCount:N0}, queued {result.QueuedForRebootCount:N0}).";

            LastCleanupRunId = result.RunId;
            ShowUndoBanner = true;
            UndoBannerText = $"Undo available. Restore backup captured for run {result.RunId}.";

            StatusText = $"Cleanup completed. Run {result.RunId}.";

            _dialogService.ShowInfo(
                "Photo Cleanup",
                $"Run: {result.RunId}\nReclaimed: {result.ReclaimedBytes.ToSizeString()}\nSuccess: {result.SuccessCount}\nFailed: {result.FailureCount}\nQueued: {result.QueuedForRebootCount}");
        }
        catch (Exception ex)
        {
            _logger.LogError("PhotoCleanupViewModel.DeleteSelectedAsync failed.", ex);
            StatusText = "Cleanup failed.";
            _dialogService.ShowError("Photo Cleanup", $"{ex.Message}\n\nDetails are in:\n{_logger.LogFilePath}");
        }
        finally
        {
            IsCleaning = false;
        }
    }

    [RelayCommand]
    private void OpenSelectedPath()
    {
        var target = SelectedItem?.FullPath ?? PreviewItems.FirstOrDefault()?.FullPath;
        if (string.IsNullOrWhiteSpace(target))
        {
            _dialogService.ShowInfo("Photo Cleanup", "Select an item first.");
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{target}\"")
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _logger.LogError("PhotoCleanupViewModel.OpenSelectedPath failed.", ex);
            _dialogService.ShowError("Photo Cleanup", $"Unable to open file location.\n\n{ex.Message}");
        }
    }

    [RelayCommand]
    private async Task ExportReportAsync()
    {
        try
        {
            var reportDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                "StorageCleanerReports");
            Directory.CreateDirectory(reportDirectory);

            var reportPath = Path.Combine(
                reportDirectory,
                $"photo-cleanup-report-{DateTime.Now:yyyyMMdd-HHmmss}.json");

            var report = new PhotoCleanupReport(
                GeneratedAtLocal: DateTimeOffset.Now,
                Root: SelectedRootPath,
                TotalMediaCount: TotalMediaCount,
                TotalMediaBytes: TotalMediaBytes,
                ScreenshotCount: ScreenshotCount,
                LargeVideoCount: LargeVideoCount,
                BlurryCount: BlurryCount,
                SimilarGroupCount: SimilarGroupCount,
                SelectedCount: SelectedCount,
                SelectedBytes: SelectedBytes,
                SelectedRiskyCount: SelectedRiskyCount,
                ScanErrors: ScanErrorCount,
                SkippedFiles: SkippedFiles,
                Filters: new PhotoCleanupFilterReport(SelectedCategoryFilter, SearchText),
                TopResults: _itemIndex.Values
                    .OrderByDescending(static item => item.SizeBytes)
                    .Take(100)
                    .Select(static item => new PhotoCleanupReportItem(
                        item.Name,
                        item.FullPath,
                        item.TypeLabel,
                        item.SizeBytes,
                        item.Recommendation,
                        item.SafetyScore,
                        item.RiskReason))
                    .ToArray());

            await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, ReportJsonOptions));
            _dialogService.ShowInfo("Photo Cleanup", $"Cleanup report exported to:\n{reportPath}");
        }
        catch (Exception ex)
        {
            _logger.LogError("PhotoCleanupViewModel.ExportReportAsync failed.", ex);
            _dialogService.ShowError("Photo Cleanup", $"Report export failed.\n\n{ex.Message}");
        }
    }

    private void ClearResults()
    {
        _itemIndex.Clear();
        FilteredResults.Clear();
        SimilarGroups.Clear();
        SimilarGroupItems.Clear();
        PreviewItems.Clear();

        TotalMediaBytes = 0;
        TotalMediaCount = 0;
        ScreenshotCount = 0;
        LargeVideoCount = 0;
        BlurryCount = 0;
        SimilarGroupCount = 0;
        SelectedCount = 0;
        SelectedBytes = 0;
        SelectedSafeCount = 0;
        SelectedRiskyCount = 0;
        SkippedFiles = 0;
        ScanErrorCount = 0;
        OnPropertyChanged(nameof(ShowEmptyState));
    }

    private void ApplyResult(PhotoCleanupScanResult result)
    {
        foreach (var item in result.MediaItems)
        {
            _ = GetOrCreate(item);
        }

        foreach (var group in result.SimilarGroups)
        {
            var items = group.Items.Select(GetOrCreate).ToArray();
            var vm = new SimilarPhotoGroupViewModel(group.GroupId, items);
            SimilarGroups.Add(vm);

            var keepPath = vm.RecommendedKeepPath;
            foreach (var groupItem in vm.Items)
            {
                groupItem.SetSimilarGroup(group.GroupId, string.Equals(groupItem.FullPath, keepPath, StringComparison.OrdinalIgnoreCase));
            }
        }

        SimilarGroupCount = SimilarGroups.Count;
        SelectedSimilarGroup = SimilarGroups.FirstOrDefault();

        TotalMediaCount = result.MediaItems.Count;
        TotalMediaBytes = result.MediaItems.Sum(static item => item.SizeBytes);
        ScreenshotCount = _itemIndex.Values.Count(static item => item.IsScreenshot);
        LargeVideoCount = _itemIndex.Values.Count(static item => item.IsLargeVideo);
        BlurryCount = _itemIndex.Values.Count(static item => item.IsBlurry);
        SkippedFiles = result.SkippedFiles;
        ScanErrorCount = result.ErrorCount;

        ApplyFilters();
        RecalculateSelectionTotals();
    }

    private PhotoCleanupItemViewModel GetOrCreate(PhotoCleanupItem item)
    {
        if (_itemIndex.TryGetValue(item.FullPath, out var existing))
        {
            return existing;
        }

        var risk = _pathSafetyService.Evaluate(item.FullPath);
        var created = new PhotoCleanupItemViewModel(item, risk);
        created.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(PhotoCleanupItemViewModel.IsSelected))
            {
                RecalculateSelectionTotals();
            }
        };

        _itemIndex[item.FullPath] = created;
        return created;
    }

    private void ApplyFilters()
    {
        IEnumerable<PhotoCleanupItemViewModel> query = _itemIndex.Values;

        query = SelectedCategoryFilter switch
        {
            "Recommended" => query.Where(static item => string.Equals(item.Recommendation, "Recommended", StringComparison.OrdinalIgnoreCase)),
            "Screenshots" => query.Where(static item => item.IsScreenshot),
            "Large Videos" => query.Where(static item => item.IsLargeVideo),
            "Blurry Photos" => query.Where(static item => item.IsBlurry),
            "Similar Photos" => query.Where(static item => item.IsSimilar),
            "Images" => query.Where(static item => item.IsImage),
            "Videos" => query.Where(static item => item.IsVideo),
            _ => query
        };

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            query = query.Where(item =>
                item.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
                item.FullPath.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
                item.ParentFolder.Contains(SearchText, StringComparison.OrdinalIgnoreCase));
        }

        var filtered = query
            .OrderByDescending(static item => item.SizeBytes)
            .ThenBy(static item => item.FullPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        FilteredResults.Clear();
        foreach (var item in filtered)
        {
            FilteredResults.Add(item);
        }

        EmptyStateText = IsScanning
            ? "Scanning in progress..."
            : "No media items match the current filters.";

        OnPropertyChanged(nameof(ShowEmptyState));
    }

    private void BuildPreviewRows()
    {
        var selected = _itemIndex.Values
            .Where(static item => item.IsSelected)
            .OrderByDescending(static item => item.SizeBytes)
            .ToArray();

        PreviewItems.Clear();
        foreach (var item in selected.Take(200))
        {
            PreviewItems.Add(new PhotoCleanupPreviewItemViewModel(item));
        }
    }

    private void RemoveItemFromAllViews(PhotoCleanupItemViewModel item)
    {
        _itemIndex.Remove(item.FullPath);
        FilteredResults.Remove(item);
        SimilarGroupItems.Remove(item);

        foreach (var group in SimilarGroups.ToArray())
        {
            group.Remove(item);
            if (group.Items.Count == 0)
            {
                SimilarGroups.Remove(group);
            }
        }

        SimilarGroupCount = SimilarGroups.Count;
        if (SelectedSimilarGroup is not null && SelectedSimilarGroup.Items.Count == 0)
        {
            SelectedSimilarGroup = SimilarGroups.FirstOrDefault();
        }
    }

    private void RecalculateSelectionTotals()
    {
        var selected = _itemIndex.Values.Where(static item => item.IsSelected).ToArray();
        SelectedCount = selected.Length;
        SelectedBytes = selected.Sum(static item => item.SizeBytes);
        SelectedSafeCount = selected.Count(static item => item.CanSelectSafely);
        SelectedRiskyCount = selected.Count(static item => item.Risk.RequiresExplicitOverride);

        BuildPreviewRows();
    }

    private sealed record PhotoCleanupReport(
        DateTimeOffset GeneratedAtLocal,
        string? Root,
        int TotalMediaCount,
        long TotalMediaBytes,
        int ScreenshotCount,
        int LargeVideoCount,
        int BlurryCount,
        int SimilarGroupCount,
        int SelectedCount,
        long SelectedBytes,
        int SelectedRiskyCount,
        int ScanErrors,
        int SkippedFiles,
        PhotoCleanupFilterReport Filters,
        IReadOnlyList<PhotoCleanupReportItem> TopResults);

    private sealed record PhotoCleanupFilterReport(string Category, string SearchText);

    private sealed record PhotoCleanupReportItem(
        string Name,
        string Path,
        string Type,
        long SizeBytes,
        string Recommendation,
        int SafetyScore,
        string RiskReason);
}

public sealed partial class PhotoCleanupItemViewModel : ObservableObject
{
    public PhotoCleanupItemViewModel(PhotoCleanupItem item, PathRisk risk)
    {
        Item = item;
        Risk = risk;
    }

    public PhotoCleanupItem Item { get; }

    public PathRisk Risk { get; }

    public string Name => Item.Name;

    public string FullPath => Item.FullPath;

    public string ParentFolder => Item.ParentFolder;

    public long SizeBytes => Item.SizeBytes;

    public DateTime LastModifiedUtc => Item.LastModifiedUtc;

    public bool IsScreenshot => Item.IsScreenshot;

    public bool IsVideo => Item.IsVideo;

    public bool IsImage => Item.IsImage;

    public bool IsBlurry => Item.IsBlurry;

    public bool IsLargeVideo => Item.IsVideo && !string.IsNullOrWhiteSpace(Item.VideoHint);

    public bool IsSimilar => !string.IsNullOrWhiteSpace(SimilarGroupId);

    public double BlurScore => Item.BlurScore;

    public string RiskLabel => Risk.Level.ToString();

    public string RiskReason => Risk.Reason;

    public int SafetyScore => SafetyScoreCalculator.FromRisk(Risk);

    public bool CanSelectSafely => !Risk.IsProtected && !Risk.RequiresExplicitOverride;

    public string TypeLabel
    {
        get
        {
            if (IsScreenshot)
            {
                return "Screenshot";
            }

            if (IsLargeVideo)
            {
                return "Large Video";
            }

            if (IsBlurry)
            {
                return "Blurry Photo";
            }

            if (IsSimilar)
            {
                return "Similar Photo";
            }

            return IsVideo ? "Video" : "Image";
        }
    }

    public bool IsRecommendedKeep => RecommendedKeep;

    public string Recommendation
    {
        get
        {
            var preferred = (IsScreenshot || IsLargeVideo || IsBlurry || IsSimilar) && !RecommendedKeep;
            if (RecommendedKeep)
            {
                return "Keep";
            }

            return SafetyScoreCalculator.Recommendation(Risk, preferred);
        }
    }

    [ObservableProperty]
    private bool isSelected;

    [ObservableProperty]
    private string? similarGroupId;

    [ObservableProperty]
    private bool recommendedKeep;

    partial void OnSimilarGroupIdChanged(string? value)
    {
        OnPropertyChanged(nameof(IsSimilar));
        OnPropertyChanged(nameof(TypeLabel));
        OnPropertyChanged(nameof(Recommendation));
    }

    partial void OnRecommendedKeepChanged(bool value)
    {
        OnPropertyChanged(nameof(IsRecommendedKeep));
        OnPropertyChanged(nameof(Recommendation));
    }

    public void SetSimilarGroup(string groupId, bool isRecommendedKeep)
    {
        SimilarGroupId = groupId;
        RecommendedKeep = isRecommendedKeep;
    }
}

public sealed partial class SimilarPhotoGroupViewModel : ObservableObject
{
    public SimilarPhotoGroupViewModel(string groupId, IReadOnlyList<PhotoCleanupItemViewModel> items)
    {
        GroupId = groupId;
        Items = new ObservableCollection<PhotoCleanupItemViewModel>(items);
    }

    public string GroupId { get; }

    public ObservableCollection<PhotoCleanupItemViewModel> Items { get; }

    public int Count => Items.Count;

    public long TotalBytes => Items.Sum(static item => item.SizeBytes);

    public string? RecommendedKeepPath => Items
        .OrderByDescending(static item => item.SizeBytes)
        .ThenByDescending(static item => item.LastModifiedUtc)
        .Select(static item => item.FullPath)
        .FirstOrDefault();

    public string RecommendedKeepName => Path.GetFileName(RecommendedKeepPath) ?? "(none)";

    public void Remove(PhotoCleanupItemViewModel item)
    {
        if (!Items.Remove(item))
        {
            return;
        }

        OnPropertyChanged(nameof(Count));
        OnPropertyChanged(nameof(TotalBytes));
        OnPropertyChanged(nameof(RecommendedKeepPath));
        OnPropertyChanged(nameof(RecommendedKeepName));
    }
}

public sealed class PhotoCleanupPreviewItemViewModel
{
    public PhotoCleanupPreviewItemViewModel(PhotoCleanupItemViewModel item)
    {
        Name = item.Name;
        FullPath = item.FullPath;
        Type = item.TypeLabel;
        SizeBytes = item.SizeBytes;
        Recommendation = item.Recommendation;
        SafetyScore = item.SafetyScore;
    }

    public string Name { get; }

    public string FullPath { get; }

    public string Type { get; }

    public long SizeBytes { get; }

    public string SizeDisplay => SizeBytes.ToSizeString();

    public string Recommendation { get; }

    public int SafetyScore { get; }
}
