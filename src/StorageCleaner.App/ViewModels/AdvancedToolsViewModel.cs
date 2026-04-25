using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StorageCleaner.App.Services;
using StorageCleaner.Core.Abstractions;
using StorageCleaner.Core.Extensions;
using StorageCleaner.Core.Models;

namespace StorageCleaner.App.ViewModels;

public partial class AdvancedToolsViewModel : ViewModelBase
{
    private readonly IFileDuplicateFinder _duplicateFinder;
    private readonly IWasteAnalysisService _wasteAnalysisService;
    private readonly IStorageAnalyticsService _storageAnalyticsService;
    private readonly ICleanupExecutor _cleanupExecutor;
    private readonly ICleanupRunStore _cleanupRunStore;
    private readonly IStorageSnapshotStore _snapshotStore;
    private readonly ISnapshotDiffService _snapshotDiffService;
    private readonly IRestoreVaultService _restoreVaultService;
    private readonly ScanWorkspaceService _scanWorkspaceService;
    private readonly IPathSafetyService _pathSafetyService;
    private readonly ISettingsService _settingsService;
    private readonly IDialogService _dialogService;
    private List<TreemapTileViewModel> _allTreemapTiles = [];

    public AdvancedToolsViewModel(
        IFileDuplicateFinder duplicateFinder,
        IWasteAnalysisService wasteAnalysisService,
        IStorageAnalyticsService storageAnalyticsService,
        ICleanupExecutor cleanupExecutor,
        ICleanupRunStore cleanupRunStore,
        IStorageSnapshotStore snapshotStore,
        ISnapshotDiffService snapshotDiffService,
        IRestoreVaultService restoreVaultService,
        ScanWorkspaceService scanWorkspaceService,
        IPathSafetyService pathSafetyService,
        ISettingsService settingsService,
        IDialogService dialogService)
    {
        _duplicateFinder = duplicateFinder;
        _wasteAnalysisService = wasteAnalysisService;
        _storageAnalyticsService = storageAnalyticsService;
        _cleanupExecutor = cleanupExecutor;
        _cleanupRunStore = cleanupRunStore;
        _snapshotStore = snapshotStore;
        _snapshotDiffService = snapshotDiffService;
        _restoreVaultService = restoreVaultService;
        _scanWorkspaceService = scanWorkspaceService;
        _pathSafetyService = pathSafetyService;
        _settingsService = settingsService;
        _dialogService = dialogService;

        AvailableRoots = [];
        DuplicateGroups = [];
        TopExtensions = [];
        AgeBuckets = [];
        NeverAccessedFiles = [];
        RecentRuns = [];
        Snapshots = [];
        FolderDiffRows = [];
        CategoryDiffRows = [];
        DiffActions = [];
        RestoreEntries = [];
        FileTypeAnalyticsRows = [];
        TreemapTiles = [];
        TreemapCategoryFilters = ["All"];
        RefreshRoots();
        _ = RefreshRunsAsync();
        _ = RefreshSnapshotsAsync();
        _ = RefreshRestoreEntriesAsync();
    }

    public ObservableCollection<string> AvailableRoots { get; }

    public ObservableCollection<DuplicateGroupViewModel> DuplicateGroups { get; }

    public ObservableCollection<WasteCategoryBucket> TopExtensions { get; }

    public ObservableCollection<WasteAgeBucket> AgeBuckets { get; }

    public ObservableCollection<FileItemViewModel> NeverAccessedFiles { get; }

    public ObservableCollection<CleanupRunManifest> RecentRuns { get; }

    public ObservableCollection<StorageSnapshot> Snapshots { get; }

    public ObservableCollection<SnapshotDiffFolderChange> FolderDiffRows { get; }

    public ObservableCollection<SnapshotDiffCategoryChange> CategoryDiffRows { get; }

    public ObservableCollection<SnapshotDiffAction> DiffActions { get; }

    public ObservableCollection<RestoreVaultEntryViewModel> RestoreEntries { get; }

    public ObservableCollection<FileTypeAnalyticsBucket> FileTypeAnalyticsRows { get; }

    public ObservableCollection<TreemapTileViewModel> TreemapTiles { get; }

    public ObservableCollection<string> TreemapCategoryFilters { get; }

    [ObservableProperty]
    private string? selectedRootPath;

    [ObservableProperty]
    private bool isScanningDuplicates;

    [ObservableProperty]
    private bool isAnalyzingWaste;

    [ObservableProperty]
    private string duplicateStatus = "Pick a root and run duplicate scan.";

    [ObservableProperty]
    private string wasteStatus = "Analyze waste categories by extension and age.";

    [ObservableProperty]
    private DuplicateGroupViewModel? selectedDuplicateGroup;

    [ObservableProperty]
    private CleanupRunManifest? selectedRun;

    [ObservableProperty]
    private StorageSnapshot? selectedBeforeSnapshot;

    [ObservableProperty]
    private StorageSnapshot? selectedAfterSnapshot;

    [ObservableProperty]
    private string snapshotStatus = "Capture two snapshots and compare before/after changes.";

    [ObservableProperty]
    private long beforeSnapshotBytes;

    [ObservableProperty]
    private long afterSnapshotBytes;

    [ObservableProperty]
    private long snapshotDeltaBytes;

    [ObservableProperty]
    private FileItemViewModel? selectedNeverAccessedFile;

    [ObservableProperty]
    private RestoreVaultEntryViewModel? selectedRestoreEntry;

    [ObservableProperty]
    private string restoreStatus = "Restore Center keeps pre-cleanup backups for safe recovery.";

    [ObservableProperty]
    private bool isAnalyzingStorageAnalytics;

    [ObservableProperty]
    private string analyticsStatus = "Run analytics to populate file-type and treemap views.";

    [ObservableProperty]
    private TreemapTileViewModel? selectedTreemapTile;

    [ObservableProperty]
    private string treemapSearchText = string.Empty;

    [ObservableProperty]
    private string selectedTreemapCategoryFilter = "All";

    [ObservableProperty]
    private long duplicateSelectedBytes;

    [ObservableProperty]
    private long duplicatePotentialBytes;

    partial void OnTreemapSearchTextChanged(string value)
    {
        ApplyTreemapFilter();
    }

    partial void OnSelectedTreemapCategoryFilterChanged(string value)
    {
        ApplyTreemapFilter();
    }

    [RelayCommand]
    private void RefreshRoots()
    {
        AvailableRoots.Clear();
        foreach (var drive in DriveInfo.GetDrives().Where(static drive => drive.IsReady))
        {
            AvailableRoots.Add(drive.RootDirectory.FullName);
        }

        SelectedRootPath ??= AvailableRoots.FirstOrDefault();
    }

    [RelayCommand]
    private async Task CaptureSnapshotAsync()
    {
        var currentScan = _scanWorkspaceService.CurrentResult;
        var roots = _scanWorkspaceService.LastRoots;

        if (currentScan is null || roots.Count == 0)
        {
            if (string.IsNullOrWhiteSpace(SelectedRootPath) || !Directory.Exists(SelectedRootPath))
            {
                _dialogService.ShowError("Snapshots", "Run a scan first or select a valid root.");
                return;
            }

            if (!_dialogService.Confirm(
                    "Capture Snapshot",
                    $"No active scan result found.\n\nRun a scan for {SelectedRootPath} now and capture a snapshot?"))
            {
                return;
            }

            await _scanWorkspaceService.StartScanAsync(
                [SelectedRootPath],
                _settingsService.Current.MaxScanParallelism,
                useCache: _settingsService.Current.EnableIncrementalScan);

            currentScan = _scanWorkspaceService.CurrentResult;
            roots = _scanWorkspaceService.LastRoots;
        }

        if (currentScan is null || roots.Count == 0)
        {
            _dialogService.ShowError("Snapshots", "Unable to capture snapshot because no scan result is available.");
            return;
        }

        var label = $"Snapshot {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}";
        var snapshot = await _snapshotStore.CreateAsync(currentScan, label, roots);
        SnapshotStatus = $"Captured snapshot {snapshot.SnapshotId} ({snapshot.TotalBytes.ToSizeString()}).";
        await RefreshSnapshotsAsync();
        UseLatestSnapshotPair();
    }

    [RelayCommand]
    private async Task RefreshSnapshotsAsync()
    {
        var snapshots = await _snapshotStore.ReadRecentAsync(maxSnapshots: 200);
        Snapshots.Clear();
        foreach (var snapshot in snapshots)
        {
            Snapshots.Add(snapshot);
        }

        if (SelectedBeforeSnapshot is null && Snapshots.Count > 1)
        {
            SelectedBeforeSnapshot = Snapshots[1];
        }

        if (SelectedAfterSnapshot is null && Snapshots.Count > 0)
        {
            SelectedAfterSnapshot = Snapshots[0];
        }
    }

    [RelayCommand]
    private async Task CompareSnapshotsAsync()
    {
        if (SelectedBeforeSnapshot is null || SelectedAfterSnapshot is null)
        {
            _dialogService.ShowInfo("Snapshot Comparison", "Select both a before snapshot and an after snapshot.");
            return;
        }

        if (string.Equals(SelectedBeforeSnapshot.SnapshotId, SelectedAfterSnapshot.SnapshotId, StringComparison.OrdinalIgnoreCase))
        {
            _dialogService.ShowInfo("Snapshot Comparison", "Choose two different snapshots.");
            return;
        }

        SnapshotStatus = "Comparing snapshots...";

        var diff = await _snapshotDiffService.CompareAsync(SelectedBeforeSnapshot, SelectedAfterSnapshot);
        ApplyDiff(diff);
        SnapshotStatus = $"Compared {diff.Before.Label} -> {diff.After.Label}. Net change: {diff.DeltaBytes.ToSizeString()}.";
    }

    [RelayCommand]
    private void UseLatestSnapshotPair()
    {
        if (Snapshots.Count < 2)
        {
            return;
        }

        SelectedAfterSnapshot = Snapshots[0];
        SelectedBeforeSnapshot = Snapshots[1];
    }

    [RelayCommand]
    private async Task AnalyzeDuplicatesAsync()
    {
        if (IsScanningDuplicates)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(SelectedRootPath) || !Directory.Exists(SelectedRootPath))
        {
            _dialogService.ShowError("Duplicate Finder", "Select a valid folder or drive root.");
            return;
        }

        IsScanningDuplicates = true;
        DuplicateGroups.Clear();
        DuplicateSelectedBytes = 0;
        DuplicatePotentialBytes = 0;
        DuplicateStatus = "Scanning for duplicate files...";

        try
        {
            var progress = new Progress<DuplicateScanProgress>(value =>
            {
                DuplicateStatus = $"Scanning {value.ScannedFiles:N0} files... {value.CurrentPath}";
            });

            var groups = await _duplicateFinder.FindDuplicatesAsync([SelectedRootPath], progress);
            foreach (var group in groups)
            {
                var vm = new DuplicateGroupViewModel(group);
                vm.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName == nameof(DuplicateGroupViewModel.SelectedDeleteBytes))
                    {
                        RecalculateDuplicateTotals();
                    }
                };

                foreach (var item in vm.Files)
                {
                    item.PropertyChanged += (_, args) =>
                    {
                        if (args.PropertyName == nameof(DuplicateFileCandidateViewModel.DeleteCandidate))
                        {
                            vm.RefreshMetrics();
                            RecalculateDuplicateTotals();
                        }
                    };
                }

                DuplicateGroups.Add(vm);
            }

            RecalculateDuplicateTotals();
            DuplicateStatus = $"Duplicate scan complete. Groups: {DuplicateGroups.Count:N0}, potential reclaim: {DuplicatePotentialBytes.ToSizeString()}.";
            SelectedDuplicateGroup = DuplicateGroups.FirstOrDefault();
        }
        catch (Exception ex)
        {
            DuplicateStatus = "Duplicate scan failed.";
            _dialogService.ShowError("Duplicate Finder", ex.Message);
        }
        finally
        {
            IsScanningDuplicates = false;
        }
    }

    [RelayCommand]
    private async Task SimulateDuplicateCleanupAsync()
    {
        await ExecuteDuplicateCleanupAsync(simulationOnly: true);
    }

    [RelayCommand]
    private async Task CommitDuplicateCleanupAsync()
    {
        await ExecuteDuplicateCleanupAsync(simulationOnly: false);
    }

    [RelayCommand]
    private void OpenPath(string? fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath))
        {
            return;
        }

        try
        {
            var arguments = File.Exists(fullPath)
                ? $"/select,\"{fullPath}\""
                : $"\"{fullPath}\"";
            Process.Start(new ProcessStartInfo("explorer.exe", arguments) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _dialogService.ShowError("Open Path", ex.Message);
        }
    }

    [RelayCommand]
    private async Task AnalyzeWasteAsync()
    {
        if (IsAnalyzingWaste)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(SelectedRootPath) || !Directory.Exists(SelectedRootPath))
        {
            _dialogService.ShowError("Waste Analysis", "Select a valid folder or drive root.");
            return;
        }

        IsAnalyzingWaste = true;
        WasteStatus = "Analyzing extensions, age buckets, and stale files...";
        TopExtensions.Clear();
        AgeBuckets.Clear();
        NeverAccessedFiles.Clear();

        try
        {
            var progress = new Progress<WasteAnalysisProgress>(value =>
            {
                WasteStatus = $"Processed {value.ProcessedFiles:N0} files... {value.CurrentPath}";
            });

            var analysis = await _wasteAnalysisService.AnalyzeAsync([SelectedRootPath], progress: progress);
            foreach (var bucket in analysis.TopExtensions)
            {
                TopExtensions.Add(bucket);
            }

            foreach (var bucket in analysis.AgeBuckets)
            {
                AgeBuckets.Add(bucket);
            }

            foreach (var file in analysis.NeverAccessedCandidates)
            {
                NeverAccessedFiles.Add(new FileItemViewModel(file));
            }

            WasteStatus = $"Waste analysis complete. Files: {analysis.TotalFiles:N0}, data: {analysis.TotalBytes.ToSizeString()}.";
        }
        catch (Exception ex)
        {
            WasteStatus = "Waste analysis failed.";
            _dialogService.ShowError("Waste Analysis", ex.Message);
        }
        finally
        {
            IsAnalyzingWaste = false;
        }
    }

    [RelayCommand]
    private async Task AnalyzeStorageAnalyticsAsync()
    {
        if (IsAnalyzingStorageAnalytics)
        {
            return;
        }

        IReadOnlyCollection<string> roots;
        if (!string.IsNullOrWhiteSpace(SelectedRootPath) && Directory.Exists(SelectedRootPath))
        {
            roots = [SelectedRootPath];
        }
        else if (_scanWorkspaceService.LastRoots.Count > 0)
        {
            roots = _scanWorkspaceService.LastRoots;
        }
        else
        {
            _dialogService.ShowError("Storage Analytics", "Select a valid root or run a scan first.");
            return;
        }

        IsAnalyzingStorageAnalytics = true;
        AnalyticsStatus = "Building file-type analytics and treemap data...";
        FileTypeAnalyticsRows.Clear();
        TreemapTiles.Clear();

        try
        {
            var progress = new Progress<StorageAnalyticsProgress>(value =>
            {
                AnalyticsStatus = $"Analyzing {value.ProcessedFiles:N0} files... {value.CurrentPath}";
            });

            var analytics = await _storageAnalyticsService.AnalyzeAsync(
                roots,
                scanResult: _scanWorkspaceService.CurrentResult,
                maxTreemapTiles: 400,
                progress: progress);

            foreach (var row in analytics.Categories)
            {
                FileTypeAnalyticsRows.Add(row);
            }

            _allTreemapTiles = analytics.TreemapTiles
                .Select(static tile => new TreemapTileViewModel(tile))
                .ToList();

            var filters = analytics.Categories
                .Select(static row => row.Category)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(static category => category, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            TreemapCategoryFilters.Clear();
            TreemapCategoryFilters.Add("All");
            foreach (var filter in filters)
            {
                TreemapCategoryFilters.Add(filter);
            }

            if (!TreemapCategoryFilters.Contains(SelectedTreemapCategoryFilter, StringComparer.OrdinalIgnoreCase))
            {
                SelectedTreemapCategoryFilter = "All";
            }

            ApplyTreemapFilter();
            SelectedTreemapTile = TreemapTiles.FirstOrDefault();
            AnalyticsStatus = $"Analytics complete. Files: {analytics.TotalFiles:N0}, bytes: {analytics.TotalBytes.ToSizeString()}, treemap tiles: {TreemapTiles.Count:N0}.";
        }
        catch (Exception ex)
        {
            AnalyticsStatus = "Storage analytics failed.";
            _dialogService.ShowError("Storage Analytics", ex.Message);
        }
        finally
        {
            IsAnalyzingStorageAnalytics = false;
        }
    }

    [RelayCommand]
    private void OpenSelectedTreemapPath()
    {
        OpenPath(SelectedTreemapTile?.FullPath);
    }

    [RelayCommand]
    private async Task DrillDownSelectedTreemapAsync()
    {
        var selected = SelectedTreemapTile;
        if (selected is null || string.IsNullOrWhiteSpace(selected.FullPath) || !Directory.Exists(selected.FullPath))
        {
            _dialogService.ShowInfo("Treemap Drill-Down", "Select a folder tile first.");
            return;
        }

        SelectedRootPath = selected.FullPath;
        await AnalyzeStorageAnalyticsAsync();
    }

    [RelayCommand]
    private async Task CleanNeverAccessedSelectedAsync()
    {
        if (SelectedNeverAccessedFile is null)
        {
            _dialogService.ShowInfo("Never Accessed Files", "Select a file first.");
            return;
        }

        var risk = _pathSafetyService.Evaluate(SelectedNeverAccessedFile.FullPath);
        if (risk.RequiresExplicitOverride)
        {
            _dialogService.ShowError("Never Accessed Files", $"Blocked by safety policy: {risk.Reason}");
            return;
        }

        if (!_dialogService.Confirm(
                "Delete Never Accessed File",
                $"Delete this file?\n\n{SelectedNeverAccessedFile.FullPath}\n\nSize: {SelectedNeverAccessedFile.SizeBytes.ToSizeString()}"))
        {
            return;
        }

        var result = await _cleanupExecutor.ExecuteAsync(
            [
                new CleanupCandidate
                {
                    Category = CleanupCategory.NeverAccessedFiles,
                    FullPath = SelectedNeverAccessedFile.FullPath,
                    IsDirectory = false,
                    SizeBytes = SelectedNeverAccessedFile.SizeBytes,
                    LastModifiedUtc = SelectedNeverAccessedFile.LastModifiedUtc,
                    Risk = risk
                }
            ],
            new CleanupExecutionOptions(
                UseRecycleBin: _settingsService.Current.UseRecycleBinByDefault,
                AllowRiskyPaths: false,
                SimulationOnly: false,
                QueueLockedForReboot: _settingsService.Current.QueueLockedDeletesOnReboot,
                CaptureRestoreBackup: true));

        if (result.SuccessCount > 0)
        {
            NeverAccessedFiles.Remove(SelectedNeverAccessedFile);
            SelectedNeverAccessedFile = null;
        }

        await RefreshRunsAsync();
        await RefreshRestoreEntriesAsync();
    }

    [RelayCommand]
    private async Task RefreshRunsAsync()
    {
        var runs = await _cleanupRunStore.ReadRecentRunsAsync(maxRuns: 100);
        RecentRuns.Clear();
        foreach (var run in runs)
        {
            RecentRuns.Add(run);
        }

        SelectedRun = RecentRuns.FirstOrDefault();
    }

    [RelayCommand]
    private async Task RefreshRestoreEntriesAsync()
    {
        var entries = await _restoreVaultService.ReadEntriesAsync(maxEntries: 2_000);
        RestoreEntries.Clear();
        foreach (var entry in entries)
        {
            RestoreEntries.Add(new RestoreVaultEntryViewModel(entry));
        }

        SelectedRestoreEntry = RestoreEntries.FirstOrDefault();
        RestoreStatus = RestoreEntries.Count == 0
            ? "Restore Center is empty."
            : $"Restore entries: {RestoreEntries.Count:N0}.";
    }

    [RelayCommand]
    private async Task RestoreSelectedEntryAsync()
    {
        if (SelectedRestoreEntry is null)
        {
            _dialogService.ShowInfo("Restore Center", "Select an entry first.");
            return;
        }

        var entry = SelectedRestoreEntry.Entry;
        if (!entry.CanRestore)
        {
            _dialogService.ShowError("Restore Center", "This entry can no longer be restored.");
            return;
        }

        if (!_dialogService.Confirm(
                "Restore Item",
                $"Restore this item back to its original location?\n\n{entry.OriginalPath}\n\nBackup size: {entry.SizeBytes.ToSizeString()}"))
        {
            return;
        }

        var restore = await _restoreVaultService.RestoreAsync(entry.EntryId);
        if (!restore.Success)
        {
            _dialogService.ShowError("Restore Center", restore.Message);
            return;
        }

        _dialogService.ShowInfo("Restore Center", restore.Message);
        await RefreshRestoreEntriesAsync();
    }

    [RelayCommand]
    private async Task PurgeSelectedEntryAsync()
    {
        if (SelectedRestoreEntry is null)
        {
            _dialogService.ShowInfo("Restore Center", "Select an entry first.");
            return;
        }

        var entry = SelectedRestoreEntry.Entry;
        var confirmed = _dialogService.ConfirmTyped(
            "Permanently Remove Backup",
            $"This permanently removes the backup from Restore Center.\n\nOriginal path: {entry.OriginalPath}\n\nType PURGE to continue.",
            expectedText: "PURGE",
            warning: true);
        if (!confirmed)
        {
            return;
        }

        var purge = await _restoreVaultService.PurgeAsync(entry.EntryId);
        if (!purge.Success)
        {
            _dialogService.ShowError("Restore Center", purge.Message);
            return;
        }

        _dialogService.ShowInfo("Restore Center", purge.Message);
        await RefreshRestoreEntriesAsync();
    }

    [RelayCommand]
    private void OpenRestoreOriginalPath()
    {
        OpenPath(SelectedRestoreEntry?.Entry.OriginalPath);
    }

    [RelayCommand]
    private void OpenRestoreVaultPath()
    {
        OpenPath(SelectedRestoreEntry?.Entry.VaultPath);
    }

    [RelayCommand]
    private async Task ExportSelectedRunAsync()
    {
        if (SelectedRun is null)
        {
            _dialogService.ShowInfo("Run Export", "Select a run first.");
            return;
        }

        var destination = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            "StorageCleanerExports");

        var filePath = await _cleanupRunStore.ExportRunManifestAsync(SelectedRun.RunId, destination);
        _dialogService.ShowInfo("Run Export", $"Manifest exported to:\n{filePath}");
    }

    private async Task ExecuteDuplicateCleanupAsync(bool simulationOnly)
    {
        var deleteItems = DuplicateGroups
            .SelectMany(static group => group.Files)
            .Where(static file => file.DeleteCandidate && !file.IsHardLinkAlias)
            .ToArray();

        if (deleteItems.Length == 0)
        {
            _dialogService.ShowInfo("Duplicate Cleanup", "No duplicate files selected for deletion.");
            return;
        }

        var totalBytes = deleteItems.Sum(static file => file.SizeBytes);
        if (!simulationOnly)
        {
            if (!_dialogService.Confirm(
                    "Commit Duplicate Cleanup",
                    $"Delete {deleteItems.Length:N0} duplicate files?\n\nEstimated reclaim: {totalBytes.ToSizeString()}"))
            {
                return;
            }
        }

        var candidates = deleteItems
            .Select(file =>
            {
                var risk = _pathSafetyService.Evaluate(file.FullPath);
                return new CleanupCandidate
                {
                    Category = CleanupCategory.DuplicateFiles,
                    FullPath = file.FullPath,
                    IsDirectory = false,
                    SizeBytes = file.SizeBytes,
                    LastModifiedUtc = file.LastModifiedUtc,
                    Risk = risk
                };
            })
            .ToArray();

        var riskyCount = candidates.Count(static candidate => candidate.Risk.RequiresExplicitOverride);
        var allowRiskyPaths = false;
        if (!simulationOnly && riskyCount > 0)
        {
            allowRiskyPaths = _dialogService.ConfirmTyped(
                "Risky Duplicate Cleanup",
                $"{riskyCount} duplicate file(s) are in risky locations.\n\nType DELETE to continue.",
                expectedText: "DELETE",
                warning: true);

            if (!allowRiskyPaths)
            {
                return;
            }
        }

        var result = await _cleanupExecutor.ExecuteAsync(
            candidates,
            new CleanupExecutionOptions(
                UseRecycleBin: _settingsService.Current.UseRecycleBinByDefault,
                AllowRiskyPaths: allowRiskyPaths,
                SimulationOnly: simulationOnly,
                QueueLockedForReboot: _settingsService.Current.QueueLockedDeletesOnReboot,
                CaptureRestoreBackup: !simulationOnly));

        await RefreshRunsAsync();
        await RefreshRestoreEntriesAsync();
        if (simulationOnly)
        {
            DuplicateStatus = $"Simulation complete: {result.SuccessCount:N0} files, {result.ReclaimedBytes.ToSizeString()} possible reclaim. Run: {result.RunId}";
            _dialogService.ShowInfo("Duplicate Simulation", $"Run {result.RunId}\nPotential reclaim: {result.ReclaimedBytes.ToSizeString()}");
            return;
        }

        DuplicateStatus = $"Duplicate cleanup complete: success {result.SuccessCount:N0}, failed {result.FailureCount:N0}, queued reboot {result.QueuedForRebootCount:N0}.";
        _dialogService.ShowInfo(
            "Duplicate Cleanup",
            $"Run {result.RunId}\nReclaimed: {result.ReclaimedBytes.ToSizeString()}\nSuccess: {result.SuccessCount}\nFailed: {result.FailureCount}\nQueued for reboot: {result.QueuedForRebootCount}");

        if (result.SuccessCount > 0)
        {
            await AnalyzeDuplicatesAsync();
        }
    }

    private void ApplyDiff(SnapshotDiffResult diff)
    {
        BeforeSnapshotBytes = diff.Before.TotalBytes;
        AfterSnapshotBytes = diff.After.TotalBytes;
        SnapshotDeltaBytes = diff.DeltaBytes;

        FolderDiffRows.Clear();
        foreach (var row in diff.TopFolderChanges)
        {
            FolderDiffRows.Add(row);
        }

        CategoryDiffRows.Clear();
        foreach (var row in diff.CategoryChanges)
        {
            CategoryDiffRows.Add(row);
        }

        DiffActions.Clear();
        foreach (var action in diff.Actions)
        {
            DiffActions.Add(action);
        }
    }

    private void RecalculateDuplicateTotals()
    {
        DuplicatePotentialBytes = DuplicateGroups.Sum(static group => group.WastedBytes);
        DuplicateSelectedBytes = DuplicateGroups.Sum(static group => group.SelectedDeleteBytes);
    }

    private void ApplyTreemapFilter()
    {
        TreemapTiles.Clear();
        if (_allTreemapTiles.Count == 0)
        {
            return;
        }

        var query = TreemapSearchText?.Trim();
        var hasQuery = !string.IsNullOrWhiteSpace(query);
        var selectedCategory = SelectedTreemapCategoryFilter;
        var filterByCategory = !string.IsNullOrWhiteSpace(selectedCategory) &&
                               !string.Equals(selectedCategory, "All", StringComparison.OrdinalIgnoreCase);

        foreach (var tile in _allTreemapTiles
                     .Where(tile =>
                         (!hasQuery || tile.FullPath.Contains(query!, StringComparison.OrdinalIgnoreCase) || tile.Name.Contains(query!, StringComparison.OrdinalIgnoreCase)) &&
                         (!filterByCategory || string.Equals(tile.CategoryHint, selectedCategory, StringComparison.OrdinalIgnoreCase)))
                     .OrderByDescending(static tile => tile.SizeBytes)
                     .Take(500))
        {
            TreemapTiles.Add(tile);
        }
    }
}

public sealed partial class DuplicateGroupViewModel : ObservableObject
{
    public DuplicateGroupViewModel(DuplicateFileGroup group)
    {
        Hash = group.Hash;
        SizeBytes = group.SizeBytes;
        Files = [];
        var keepCandidate = SelectRecommendedKeep(group.Files);
        RecommendedKeepPath = keepCandidate?.FullPath;
        RecommendationReason = keepCandidate is null
            ? "No clear keep recommendation."
            : "Keep suggestion favors non-temporary path and latest modified copy.";

        foreach (var file in group.Files.OrderBy(static f => f.FullPath, StringComparer.OrdinalIgnoreCase))
        {
            var shouldDelete = !file.IsHardLinkAlias &&
                               !string.Equals(file.FullPath, keepCandidate?.FullPath, StringComparison.OrdinalIgnoreCase);
            Files.Add(new DuplicateFileCandidateViewModel(file, shouldDelete));
        }

        RefreshMetrics();
    }

    public string Hash { get; }

    public string HashPrefix => Hash.Length <= 12 ? Hash : Hash[..12];

    public long SizeBytes { get; }

    public ObservableCollection<DuplicateFileCandidateViewModel> Files { get; }

    public string? RecommendedKeepPath { get; }

    public string RecommendedKeepName => string.IsNullOrWhiteSpace(RecommendedKeepPath)
        ? "(none)"
        : Path.GetFileName(RecommendedKeepPath);

    public string RecommendationReason { get; }

    [ObservableProperty]
    private long wastedBytes;

    [ObservableProperty]
    private long selectedDeleteBytes;

    [ObservableProperty]
    private int selectedDeleteCount;

    public void RefreshMetrics()
    {
        var physical = Files.Where(static file => !file.IsHardLinkAlias).ToArray();
        WastedBytes = Math.Max(0, physical.Length - 1) * SizeBytes;
        var selected = Files.Where(static file => file.DeleteCandidate && !file.IsHardLinkAlias).ToArray();
        SelectedDeleteCount = selected.Length;
        SelectedDeleteBytes = selected.Sum(static file => file.SizeBytes);
    }

    private static DuplicateFileItem? SelectRecommendedKeep(IReadOnlyList<DuplicateFileItem> files)
    {
        return files
            .Where(static file => !file.IsHardLinkAlias)
            .OrderByDescending(static file => CalculateKeepScore(file))
            .ThenByDescending(static file => file.LastModifiedUtc)
            .ThenBy(static file => file.FullPath, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private static int CalculateKeepScore(DuplicateFileItem file)
    {
        var path = file.FullPath;
        var score = 0;

        if (!path.Contains(@"\temp\", StringComparison.OrdinalIgnoreCase) &&
            !path.Contains(@"\cache\", StringComparison.OrdinalIgnoreCase))
        {
            score += 5;
        }

        if (!path.Contains(@"\downloads\", StringComparison.OrdinalIgnoreCase))
        {
            score += 3;
        }

        if (path.Contains(@"\documents\", StringComparison.OrdinalIgnoreCase) ||
            path.Contains(@"\desktop\", StringComparison.OrdinalIgnoreCase) ||
            path.Contains(@"\pictures\", StringComparison.OrdinalIgnoreCase) ||
            path.Contains(@"\videos\", StringComparison.OrdinalIgnoreCase))
        {
            score += 2;
        }

        return score;
    }
}

public sealed partial class DuplicateFileCandidateViewModel : ObservableObject
{
    public DuplicateFileCandidateViewModel(DuplicateFileItem item, bool deleteCandidate)
    {
        FullPath = item.FullPath;
        SizeBytes = item.SizeBytes;
        LastModifiedUtc = item.LastModifiedUtc;
        LastAccessUtc = item.LastAccessUtc;
        IsHardLinkAlias = item.IsHardLinkAlias;
        DeleteCandidate = deleteCandidate;
    }

    public string FullPath { get; }

    public string Name => Path.GetFileName(FullPath);

    public long SizeBytes { get; }

    public DateTime LastModifiedUtc { get; }

    public DateTime LastAccessUtc { get; }

    public bool IsHardLinkAlias { get; }

    [ObservableProperty]
    private bool deleteCandidate;
}

public sealed class RestoreVaultEntryViewModel
{
    public RestoreVaultEntryViewModel(RestoreVaultEntry entry)
    {
        Entry = entry;
    }

    public RestoreVaultEntry Entry { get; }

    public string EntryId => Entry.EntryId;

    public string RunId => Entry.RunId;

    public string OriginalPath => Entry.OriginalPath;

    public string VaultPath => Entry.VaultPath;

    public long SizeBytes => Entry.SizeBytes;

    public CleanupCategory Category => Entry.Category;

    public DateTimeOffset BackedUpAt => Entry.BackedUpAt;

    public DateTimeOffset? RestoredAt => Entry.RestoredAt;

    public bool Purged => Entry.Purged;

    public bool CanRestore => Entry.CanRestore;

    public string State => Entry.Purged
        ? "Purged"
        : Entry.RestoredAt is not null
            ? "Restored"
            : Entry.CanRestore
                ? "Available"
                : "Unavailable";
}

public sealed class TreemapTileViewModel
{
    public TreemapTileViewModel(TreemapTile tile)
    {
        Tile = tile;
    }

    public TreemapTile Tile { get; }

    public string FullPath => Tile.FullPath;

    public string Name => Tile.Name;

    public long SizeBytes => Tile.SizeBytes;

    public double PercentageOfScanned => Tile.PercentageOfScanned;

    public int Depth => Tile.Depth;

    public long FileCount => Tile.FileCount;

    public long FolderCount => Tile.FolderCount;

    public bool IsInaccessible => Tile.IsInaccessible;

    public string CategoryHint => Tile.CategoryHint;
}
