using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StorageCleaner.App.Services;
using StorageCleaner.Core.Abstractions;
using StorageCleaner.Core.Extensions;
using StorageCleaner.Core.Models;

namespace StorageCleaner.App.ViewModels;

public partial class AppUninstallerViewModel : ViewModelBase
{
    private static readonly JsonSerializerOptions ReportJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly IAppUninstallerService _appUninstallerService;
    private readonly ICleanupExecutor _cleanupExecutor;
    private readonly IPathSafetyService _pathSafetyService;
    private readonly ISettingsService _settingsService;
    private readonly IDialogService _dialogService;
    private readonly IAppLogger _logger;

    public AppUninstallerViewModel(
        IAppUninstallerService appUninstallerService,
        ICleanupExecutor cleanupExecutor,
        IPathSafetyService pathSafetyService,
        ISettingsService settingsService,
        IDialogService dialogService,
        IAppLogger logger)
    {
        _appUninstallerService = appUninstallerService;
        _cleanupExecutor = cleanupExecutor;
        _pathSafetyService = pathSafetyService;
        _settingsService = settingsService;
        _dialogService = dialogService;
        _logger = logger;

        InstalledApps = [];
        FilteredInstalledApps = [];
        Leftovers = [];
        FilteredLeftovers = [];
        UninstallPreviewItems = [];
        CleanupPreviewItems = [];
        AppRecommendationFilters = ["All", "Recommended", "Needs Review", "Blocked"];
        LeftoverRecommendationFilters = ["All", "Recommended", "Needs Review", "Blocked"];

        StatusText = "Review installed apps and cleanup leftovers safely.";
    }

    public ObservableCollection<InstalledAppViewModel> InstalledApps { get; }

    public ObservableCollection<InstalledAppViewModel> FilteredInstalledApps { get; }

    public ObservableCollection<AppLeftoverViewModel> Leftovers { get; }

    public ObservableCollection<AppLeftoverViewModel> FilteredLeftovers { get; }

    public ObservableCollection<UninstallPreviewItemViewModel> UninstallPreviewItems { get; }

    public ObservableCollection<LeftoverPreviewItemViewModel> CleanupPreviewItems { get; }

    public ObservableCollection<string> AppRecommendationFilters { get; }

    public ObservableCollection<string> LeftoverRecommendationFilters { get; }

    [ObservableProperty]
    private InstalledAppViewModel? selectedApp;

    [ObservableProperty]
    private AppLeftoverViewModel? selectedLeftover;

    [ObservableProperty]
    private bool isLoadingApps;

    [ObservableProperty]
    private bool isScanningLeftovers;

    [ObservableProperty]
    private bool isUninstalling;

    [ObservableProperty]
    private bool isDeletingLeftovers;

    [ObservableProperty]
    private bool isCompactMode;

    [ObservableProperty]
    private string statusText;

    [ObservableProperty]
    private int selectedAppCount;

    [ObservableProperty]
    private long selectedAppEstimatedBytes;

    [ObservableProperty]
    private int selectedLeftoverCount;

    [ObservableProperty]
    private long selectedLeftoverBytes;

    [ObservableProperty]
    private int totalInstalledApps;

    [ObservableProperty]
    private int leftoversFoundCount;

    [ObservableProperty]
    private long reclaimableBytes;

    [ObservableProperty]
    private int selectedCombinedCount;

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
    private string appSearchText = string.Empty;

    [ObservableProperty]
    private string leftoverSearchText = string.Empty;

    [ObservableProperty]
    private string selectedAppRecommendationFilter = "All";

    [ObservableProperty]
    private string selectedLeftoverRecommendationFilter = "All";

    [ObservableProperty]
    private string appEmptyStateText = "Load installed apps to begin uninstall review.";

    [ObservableProperty]
    private string leftoverEmptyStateText = "Select apps and run leftover scan.";

    public bool IsBusy => IsLoadingApps || IsScanningLeftovers || IsUninstalling || IsDeletingLeftovers;

    public bool ShowAppsEmptyState => !IsLoadingApps && FilteredInstalledApps.Count == 0;

    public bool ShowLeftoversEmptyState => !IsScanningLeftovers && FilteredLeftovers.Count == 0;

    partial void OnIsLoadingAppsChanged(bool value)
    {
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(ShowAppsEmptyState));
    }

    partial void OnIsScanningLeftoversChanged(bool value)
    {
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(ShowLeftoversEmptyState));
    }

    partial void OnIsUninstallingChanged(bool value)
    {
        OnPropertyChanged(nameof(IsBusy));
    }

    partial void OnIsDeletingLeftoversChanged(bool value)
    {
        OnPropertyChanged(nameof(IsBusy));
    }

    partial void OnAppSearchTextChanged(string value)
    {
        ApplyAppFilters();
    }

    partial void OnLeftoverSearchTextChanged(string value)
    {
        ApplyLeftoverFilters();
    }

    partial void OnSelectedAppRecommendationFilterChanged(string value)
    {
        ApplyAppFilters();
    }

    partial void OnSelectedLeftoverRecommendationFilterChanged(string value)
    {
        ApplyLeftoverFilters();
    }
    [RelayCommand]
    private async Task RefreshAppsAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsLoadingApps = true;
        ShowPermissionWarning = false;
        ShowSuccessBanner = false;
        ShowUndoBanner = false;
        StatusText = "Loading installed applications...";

        foreach (var app in InstalledApps)
        {
            app.PropertyChanged -= OnInstalledAppPropertyChanged;
        }

        foreach (var leftover in Leftovers)
        {
            leftover.PropertyChanged -= OnLeftoverPropertyChanged;
        }

        InstalledApps.Clear();
        FilteredInstalledApps.Clear();
        Leftovers.Clear();
        FilteredLeftovers.Clear();
        UninstallPreviewItems.Clear();
        CleanupPreviewItems.Clear();

        try
        {
            var apps = await _appUninstallerService.GetInstalledAppsAsync();
            foreach (var app in apps)
            {
                var vm = new InstalledAppViewModel(app, EvaluateInstallRisk(app));
                vm.PropertyChanged += OnInstalledAppPropertyChanged;
                InstalledApps.Add(vm);
            }

            TotalInstalledApps = InstalledApps.Count;
            SelectedApp = InstalledApps.FirstOrDefault();
            RecalculateSelectedApps();
            ApplyAppFilters();
            ApplyLeftoverFilters();
            StatusText = $"Loaded {InstalledApps.Count:N0} installed app(s).";
        }
        catch (Exception ex)
        {
            _logger.LogError("AppUninstallerViewModel.RefreshAppsAsync failed.", ex);
            StatusText = "Failed to load installed apps.";
            _dialogService.ShowError("App Uninstaller", $"{ex.Message}\n\nDetails are in:\n{_logger.LogFilePath}");
        }
        finally
        {
            IsLoadingApps = false;
        }
    }

    [RelayCommand]
    private async Task ScanLeftoversAsync()
    {
        if (IsBusy)
        {
            return;
        }

        var targets = InstalledApps.Where(static app => app.IsSelected).ToArray();
        if (targets.Length == 0 && SelectedApp is not null)
        {
            targets = [SelectedApp];
        }

        if (targets.Length == 0)
        {
            _dialogService.ShowInfo("App Uninstaller", "Select one or more apps first.");
            return;
        }

        IsScanningLeftovers = true;
        ShowSuccessBanner = false;
        ShowUndoBanner = false;
        ShowPermissionWarning = false;
        PermissionWarningText = string.Empty;
        StatusText = "Scanning for leftover files...";

        foreach (var leftover in Leftovers)
        {
            leftover.PropertyChanged -= OnLeftoverPropertyChanged;
        }

        Leftovers.Clear();

        var permissionWarnings = new List<string>();

        try
        {
            foreach (var app in targets)
            {
                if (HasLimitedAccess(app.InstallLocation))
                {
                    permissionWarnings.Add(app.DisplayName);
                }

                IReadOnlyList<AppLeftoverCandidate> leftovers;
                try
                {
                    leftovers = await _appUninstallerService.DetectLeftoversAsync(app.App);
                }
                catch (Exception ex)
                {
                    _logger.LogError($"AppUninstallerViewModel.ScanLeftoversAsync failed for app {app.DisplayName}.", ex);
                    permissionWarnings.Add(app.DisplayName);
                    continue;
                }

                foreach (var leftover in leftovers)
                {
                    var risk = _pathSafetyService.Evaluate(leftover.FullPath);
                    var vm = new AppLeftoverViewModel(app.App.DisplayName, leftover, risk);
                    vm.PropertyChanged += OnLeftoverPropertyChanged;
                    Leftovers.Add(vm);
                }
            }

            LeftoversFoundCount = Leftovers.Count;
            ReclaimableBytes = Leftovers.Sum(static item => item.EstimatedBytes);
            SelectedLeftover = Leftovers.FirstOrDefault();
            RecalculateSelectedLeftovers();
            ApplyLeftoverFilters();

            if (permissionWarnings.Count > 0)
            {
                ShowPermissionWarning = true;
                PermissionWarningText =
                    $"Limited system access while scanning {permissionWarnings.Count:N0} app(s). Some leftover paths may be missing.";
            }

            StatusText = $"Leftover scan complete. Candidates: {Leftovers.Count:N0}.";
        }
        catch (Exception ex)
        {
            _logger.LogError("AppUninstallerViewModel.ScanLeftoversAsync failed.", ex);
            StatusText = "Leftover scan failed.";
            _dialogService.ShowError("App Uninstaller", $"{ex.Message}\n\nDetails are in:\n{_logger.LogFilePath}");
        }
        finally
        {
            IsScanningLeftovers = false;
            OnPropertyChanged(nameof(ShowLeftoversEmptyState));
        }
    }

    [RelayCommand]
    private void SelectAllSafeItems()
    {
        foreach (var app in InstalledApps)
        {
            app.IsSelected = app.CanSelectSafely && app.IsRecommended;
        }

        foreach (var leftover in Leftovers)
        {
            leftover.IsSelected = leftover.CanSelectSafely;
        }

        RecalculateSelectedApps();
        RecalculateSelectedLeftovers();
        StatusText = "Selected safe recommended items.";
    }

    [RelayCommand]
    private void SelectNone()
    {
        foreach (var app in InstalledApps)
        {
            app.IsSelected = false;
        }

        foreach (var leftover in Leftovers)
        {
            leftover.IsSelected = false;
        }

        RecalculateSelectedApps();
        RecalculateSelectedLeftovers();
    }

    [RelayCommand]
    private void PreviewUninstallSelected()
    {
        BuildUninstallPreview();
        if (UninstallPreviewItems.Count == 0)
        {
            _dialogService.ShowInfo("App Uninstaller", "Select one or more apps first.");
            return;
        }

        var lines = UninstallPreviewItems
            .Take(10)
            .Select(static item => $"- {item.DisplayName} ({item.SizeDisplay})")
            .ToArray();

        var suffix = UninstallPreviewItems.Count > lines.Length
            ? $"\n...and {UninstallPreviewItems.Count - lines.Length:N0} more app(s)."
            : string.Empty;

        _dialogService.ShowInfo(
            "Uninstall Preview",
            $"Selected apps: {UninstallPreviewItems.Count:N0}\nEstimated app size: {SelectedAppEstimatedBytes.ToSizeString()}\n\n{string.Join('\n', lines)}{suffix}\n\nNo uninstall starts until you confirm typed validation.");
    }

    [RelayCommand]
    private void PreviewLeftoverCleanup()
    {
        BuildLeftoverPreview();
        if (CleanupPreviewItems.Count == 0)
        {
            _dialogService.ShowInfo("Leftover Preview", "Select leftover paths first.");
            return;
        }

        var lines = CleanupPreviewItems
            .Take(12)
            .Select(static item => $"- {item.Path} ({item.SizeDisplay})")
            .ToArray();

        var suffix = CleanupPreviewItems.Count > lines.Length
            ? $"\n...and {CleanupPreviewItems.Count - lines.Length:N0} more path(s)."
            : string.Empty;

        _dialogService.ShowInfo(
            "Leftover Cleanup Preview",
            $"Selected leftovers: {CleanupPreviewItems.Count:N0}\nEstimated reclaim: {SelectedLeftoverBytes.ToSizeString()}\n\n{string.Join('\n', lines)}{suffix}");
    }
    [RelayCommand]
    private async Task UninstallSelectedAsync()
    {
        if (IsBusy)
        {
            return;
        }

        var selected = InstalledApps.Where(static app => app.IsSelected).ToArray();
        if (selected.Length == 0)
        {
            _dialogService.ShowInfo("App Uninstaller", "Select one or more apps first.");
            return;
        }

        var confirmed = _dialogService.ConfirmTyped(
            "Confirm Uninstall",
            $"This launches uninstall workflows for {selected.Length:N0} selected app(s).\n\nType UNINSTALL to continue.",
            expectedText: "UNINSTALL",
            warning: true);
        if (!confirmed)
        {
            return;
        }

        IsUninstalling = true;
        ShowSuccessBanner = false;
        StatusText = "Launching uninstall workflows...";

        try
        {
            var started = 0;
            var failed = new List<string>();
            foreach (var app in selected)
            {
                var result = await _appUninstallerService.LaunchUninstallAsync(app.App);
                if (result.Started)
                {
                    started++;
                }
                else
                {
                    failed.Add($"{app.DisplayName}: {result.Message}");
                }
            }

            ShowSuccessBanner = true;
            SuccessBannerText = $"Uninstall workflows launched for {started:N0} app(s).";
            StatusText = $"Uninstall launch complete. Started: {started:N0}, failed: {failed.Count:N0}.";

            var failText = failed.Count == 0 ? string.Empty : $"\n\nFailures:\n{string.Join('\n', failed.Take(10))}";
            _dialogService.ShowInfo(
                "App Uninstaller",
                $"Started uninstall workflows for {started:N0} app(s).{failText}");
        }
        catch (Exception ex)
        {
            _logger.LogError("AppUninstallerViewModel.UninstallSelectedAsync failed.", ex);
            StatusText = "Uninstall launch failed.";
            _dialogService.ShowError("App Uninstaller", $"{ex.Message}\n\nDetails are in:\n{_logger.LogFilePath}");
        }
        finally
        {
            IsUninstalling = false;
        }
    }

    [RelayCommand]
    private async Task DeleteSelectedLeftoversAsync()
    {
        if (IsBusy)
        {
            return;
        }

        var selected = Leftovers.Where(static leftover => leftover.IsSelected).ToArray();
        if (selected.Length == 0)
        {
            _dialogService.ShowInfo("App Uninstaller", "Select leftover items first.");
            return;
        }

        if (!_dialogService.Confirm(
                "Confirm Leftover Cleanup",
                $"Move {selected.Length:N0} selected leftover item(s) to Recycle Bin?\n\nEstimated reclaim: {SelectedLeftoverBytes.ToSizeString()}\nRestore backup will be captured before cleanup."))
        {
            return;
        }

        var riskyCount = selected.Count(static item => item.Risk.RequiresExplicitOverride);
        var allowRisky = false;
        if (riskyCount > 0)
        {
            allowRisky = _dialogService.ConfirmTyped(
                "Risky Leftover Paths",
                $"{riskyCount:N0} selected leftover path(s) are in risky locations.\n\nType DELETE to continue.",
                expectedText: "DELETE",
                warning: true);
            if (!allowRisky)
            {
                return;
            }
        }

        IsDeletingLeftovers = true;
        ShowSuccessBanner = false;
        ShowUndoBanner = false;
        StatusText = "Cleaning selected leftovers...";

        try
        {
            var candidates = selected
                .Select(item => new CleanupCandidate
                {
                    Category = CleanupCategory.ManualSelection,
                    FullPath = item.FullPath,
                    IsDirectory = item.IsDirectory,
                    SizeBytes = item.EstimatedBytes,
                    LastModifiedUtc = DateTime.UtcNow,
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

            foreach (var removed in result.Items.Where(static item => item.Success).ToArray())
            {
                var vm = Leftovers.FirstOrDefault(item => string.Equals(item.FullPath, removed.FullPath, StringComparison.OrdinalIgnoreCase));
                if (vm is not null)
                {
                    vm.PropertyChanged -= OnLeftoverPropertyChanged;
                    Leftovers.Remove(vm);
                }
            }

            LeftoversFoundCount = Leftovers.Count;
            ReclaimableBytes = Leftovers.Sum(static item => item.EstimatedBytes);
            ApplyLeftoverFilters();
            RecalculateSelectedLeftovers();

            LastCleanupRunId = result.RunId;
            ShowSuccessBanner = true;
            SuccessBannerText =
                $"Leftover cleanup complete. Reclaimed {result.ReclaimedBytes.ToSizeString()} (success {result.SuccessCount:N0}, failed {result.FailureCount:N0}, queued {result.QueuedForRebootCount:N0}).";
            ShowUndoBanner = true;
            UndoBannerText = $"Undo available. Restore backup captured for run {result.RunId}.";
            StatusText = $"Leftover cleanup completed. Run {result.RunId}.";

            _dialogService.ShowInfo(
                "App Uninstaller",
                $"Run: {result.RunId}\nReclaimed: {result.ReclaimedBytes.ToSizeString()}\nSuccess: {result.SuccessCount}\nFailed: {result.FailureCount}\nQueued: {result.QueuedForRebootCount}");
        }
        catch (Exception ex)
        {
            _logger.LogError("AppUninstallerViewModel.DeleteSelectedLeftoversAsync failed.", ex);
            StatusText = "Leftover cleanup failed.";
            _dialogService.ShowError("App Uninstaller", $"{ex.Message}\n\nDetails are in:\n{_logger.LogFilePath}");
        }
        finally
        {
            IsDeletingLeftovers = false;
        }
    }

    [RelayCommand]
    private void OpenInstallLocation()
    {
        var path = SelectedApp?.InstallLocation;
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            _dialogService.ShowInfo("App Uninstaller", "Install location is not available.");
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{path}\"")
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _dialogService.ShowError("App Uninstaller", ex.Message);
        }
    }

    [RelayCommand]
    private void OpenLeftoverLocation()
    {
        if (SelectedLeftover is null)
        {
            _dialogService.ShowInfo("App Uninstaller", "Select a leftover path first.");
            return;
        }

        try
        {
            var path = SelectedLeftover.FullPath;
            var arguments = File.Exists(path) ? $"/select,\"{path}\"" : $"\"{path}\"";
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", arguments)
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _dialogService.ShowError("App Uninstaller", ex.Message);
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
                $"app-uninstaller-report-{DateTime.Now:yyyyMMdd-HHmmss}.json");

            var report = new AppUninstallerReport(
                GeneratedAtLocal: DateTimeOffset.Now,
                TotalInstalledApps: TotalInstalledApps,
                SelectedApps: SelectedAppCount,
                SelectedAppEstimatedBytes: SelectedAppEstimatedBytes,
                LeftoversFound: LeftoversFoundCount,
                SelectedLeftovers: SelectedLeftoverCount,
                SelectedLeftoverBytes: SelectedLeftoverBytes,
                ReclaimableBytes: ReclaimableBytes,
                AppFilter: new AppFilterReport(AppSearchText, SelectedAppRecommendationFilter),
                LeftoverFilter: new AppFilterReport(LeftoverSearchText, SelectedLeftoverRecommendationFilter),
                Apps: InstalledApps
                    .OrderByDescending(static app => app.EstimatedSizeBytes ?? 0)
                    .Take(250)
                    .Select(static app => new AppReportItem(
                        app.DisplayName,
                        app.Version,
                        app.Publisher,
                        app.InstallLocation,
                        app.EstimatedSizeBytes,
                        app.Recommendation,
                        app.SafetyScore,
                        app.RiskReason,
                        app.LastUsedLocal))
                    .ToArray(),
                Leftovers: Leftovers
                    .OrderByDescending(static item => item.EstimatedBytes)
                    .Take(500)
                    .Select(static item => new LeftoverReportItem(
                        item.AppName,
                        item.FullPath,
                        item.EstimatedBytes,
                        item.Reason,
                        item.Recommendation,
                        item.SafetyScore,
                        item.RiskReason))
                    .ToArray());

            await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, ReportJsonOptions));
            _dialogService.ShowInfo("App Uninstaller", $"Cleanup report exported to:\n{reportPath}");
        }
        catch (Exception ex)
        {
            _logger.LogError("AppUninstallerViewModel.ExportReportAsync failed.", ex);
            _dialogService.ShowError("App Uninstaller", $"Report export failed.\n\n{ex.Message}");
        }
    }
    private void OnInstalledAppPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(InstalledAppViewModel.IsSelected))
        {
            return;
        }

        RecalculateSelectedApps();
        BuildUninstallPreview();
    }

    private void OnLeftoverPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(AppLeftoverViewModel.IsSelected))
        {
            return;
        }

        RecalculateSelectedLeftovers();
        BuildLeftoverPreview();
    }

    private void ApplyAppFilters()
    {
        IEnumerable<InstalledAppViewModel> query = InstalledApps;

        query = SelectedAppRecommendationFilter switch
        {
            "Recommended" => query.Where(static app => app.IsRecommended),
            "Needs Review" => query.Where(static app => string.Equals(app.Recommendation, "Needs Review", StringComparison.OrdinalIgnoreCase)),
            "Blocked" => query.Where(static app => string.Equals(app.Recommendation, "Blocked", StringComparison.OrdinalIgnoreCase)),
            _ => query
        };

        if (!string.IsNullOrWhiteSpace(AppSearchText))
        {
            query = query.Where(app =>
                app.DisplayName.Contains(AppSearchText, StringComparison.OrdinalIgnoreCase) ||
                (app.Publisher?.Contains(AppSearchText, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (app.InstallLocation?.Contains(AppSearchText, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        var filtered = query
            .OrderByDescending(static app => app.EstimatedSizeBytes ?? 0)
            .ThenBy(static app => app.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        FilteredInstalledApps.Clear();
        foreach (var app in filtered)
        {
            FilteredInstalledApps.Add(app);
        }

        AppEmptyStateText = IsLoadingApps
            ? "Loading installed apps..."
            : "No apps match the current filters.";

        OnPropertyChanged(nameof(ShowAppsEmptyState));
    }

    private void ApplyLeftoverFilters()
    {
        IEnumerable<AppLeftoverViewModel> query = Leftovers;

        query = SelectedLeftoverRecommendationFilter switch
        {
            "Recommended" => query.Where(static item => item.IsRecommended),
            "Needs Review" => query.Where(static item => string.Equals(item.Recommendation, "Needs Review", StringComparison.OrdinalIgnoreCase)),
            "Blocked" => query.Where(static item => string.Equals(item.Recommendation, "Blocked", StringComparison.OrdinalIgnoreCase)),
            _ => query
        };

        if (!string.IsNullOrWhiteSpace(LeftoverSearchText))
        {
            query = query.Where(item =>
                item.FullPath.Contains(LeftoverSearchText, StringComparison.OrdinalIgnoreCase) ||
                item.AppName.Contains(LeftoverSearchText, StringComparison.OrdinalIgnoreCase) ||
                item.Reason.Contains(LeftoverSearchText, StringComparison.OrdinalIgnoreCase));
        }

        var filtered = query
            .OrderByDescending(static item => item.EstimatedBytes)
            .ThenBy(static item => item.FullPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        FilteredLeftovers.Clear();
        foreach (var item in filtered)
        {
            FilteredLeftovers.Add(item);
        }

        LeftoverEmptyStateText = IsScanningLeftovers
            ? "Scanning leftovers..."
            : "No leftover paths match the current filters.";

        OnPropertyChanged(nameof(ShowLeftoversEmptyState));
    }

    private void RecalculateSelectedApps()
    {
        var selected = InstalledApps.Where(static app => app.IsSelected).ToArray();
        SelectedAppCount = selected.Length;
        SelectedAppEstimatedBytes = selected.Sum(static app => app.EstimatedSizeBytes ?? 0);
        SelectedCombinedCount = SelectedAppCount + SelectedLeftoverCount;
        BuildUninstallPreview();
    }

    private void RecalculateSelectedLeftovers()
    {
        var selected = Leftovers.Where(static item => item.IsSelected).ToArray();
        SelectedLeftoverCount = selected.Length;
        SelectedLeftoverBytes = selected.Sum(static item => item.EstimatedBytes);
        SelectedCombinedCount = SelectedAppCount + SelectedLeftoverCount;
        BuildLeftoverPreview();
    }

    private void BuildUninstallPreview()
    {
        var selected = InstalledApps
            .Where(static app => app.IsSelected)
            .OrderByDescending(static app => app.EstimatedSizeBytes ?? 0)
            .ToArray();

        UninstallPreviewItems.Clear();
        foreach (var app in selected.Take(200))
        {
            UninstallPreviewItems.Add(new UninstallPreviewItemViewModel(app));
        }
    }

    private void BuildLeftoverPreview()
    {
        var selected = Leftovers
            .Where(static item => item.IsSelected)
            .OrderByDescending(static item => item.EstimatedBytes)
            .ToArray();

        CleanupPreviewItems.Clear();
        foreach (var item in selected.Take(300))
        {
            CleanupPreviewItems.Add(new LeftoverPreviewItemViewModel(item));
        }
    }

    private PathRisk EvaluateInstallRisk(InstalledAppInfo app)
    {
        if (string.IsNullOrWhiteSpace(app.InstallLocation))
        {
            return new PathRisk(PathRiskLevel.Caution, "Install location is unavailable.");
        }

        return _pathSafetyService.Evaluate(app.InstallLocation);
    }

    private static bool HasLimitedAccess(string? installLocation)
    {
        if (string.IsNullOrWhiteSpace(installLocation) || !Directory.Exists(installLocation))
        {
            return false;
        }

        try
        {
            _ = Directory.EnumerateFileSystemEntries(installLocation).Take(1).Any();
            return false;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or PathTooLongException or IOException)
        {
            return true;
        }
    }

    private sealed record AppUninstallerReport(
        DateTimeOffset GeneratedAtLocal,
        int TotalInstalledApps,
        int SelectedApps,
        long SelectedAppEstimatedBytes,
        int LeftoversFound,
        int SelectedLeftovers,
        long SelectedLeftoverBytes,
        long ReclaimableBytes,
        AppFilterReport AppFilter,
        AppFilterReport LeftoverFilter,
        IReadOnlyList<AppReportItem> Apps,
        IReadOnlyList<LeftoverReportItem> Leftovers);

    private sealed record AppFilterReport(string Search, string RecommendationFilter);

    private sealed record AppReportItem(
        string DisplayName,
        string? Version,
        string? Publisher,
        string? InstallLocation,
        long? EstimatedSizeBytes,
        string Recommendation,
        int SafetyScore,
        string RiskReason,
        DateTime? LastUsedLocal);

    private sealed record LeftoverReportItem(
        string AppName,
        string FullPath,
        long EstimatedBytes,
        string Reason,
        string Recommendation,
        int SafetyScore,
        string RiskReason);
}
public sealed partial class InstalledAppViewModel : ObservableObject
{
    public InstalledAppViewModel(InstalledAppInfo app, PathRisk installRisk)
    {
        App = app;
        InstallRisk = installRisk;
    }

    public InstalledAppInfo App { get; }

    public PathRisk InstallRisk { get; }

    public string DisplayName => App.DisplayName;

    public string? Publisher => App.Publisher;

    public string? Version => App.Version;

    public string? InstallLocation => App.InstallLocation;

    public long? EstimatedSizeBytes => App.EstimatedSizeBytes;

    public DateTime? LastUsedLocal => App.LastUsedLocal;

    public string? UninstallCommand => App.UninstallCommand;

    public string RiskLabel => InstallRisk.Level.ToString();

    public string RiskReason => InstallRisk.Reason;

    public int SafetyScore
    {
        get
        {
            var score = SafetyScoreCalculator.FromRisk(InstallRisk);
            if (string.IsNullOrWhiteSpace(UninstallCommand))
            {
                score = Math.Min(score, 45);
            }

            return score;
        }
    }

    public bool IsRecommended
    {
        get
        {
            if (InstallRisk.IsProtected || InstallRisk.RequiresExplicitOverride)
            {
                return false;
            }

            var isLarge = (EstimatedSizeBytes ?? 0) >= 500L * 1024L * 1024L;
            var isStale = LastUsedLocal is not null && LastUsedLocal.Value <= DateTime.Now.AddDays(-90);
            return !string.IsNullOrWhiteSpace(UninstallCommand) && (isLarge || isStale);
        }
    }

    public bool CanSelectSafely => !InstallRisk.IsProtected && !string.IsNullOrWhiteSpace(UninstallCommand);

    public string Recommendation
    {
        get
        {
            if (InstallRisk.IsProtected)
            {
                return "Blocked";
            }

            if (string.IsNullOrWhiteSpace(UninstallCommand) || InstallRisk.RequiresExplicitOverride)
            {
                return "Needs Review";
            }

            return IsRecommended ? "Recommended" : "Review";
        }
    }

    [ObservableProperty]
    private bool isSelected;
}

public sealed partial class AppLeftoverViewModel : ObservableObject
{
    public AppLeftoverViewModel(string appName, AppLeftoverCandidate leftover, PathRisk risk)
    {
        AppName = appName;
        Leftover = leftover;
        Risk = risk;
    }

    public string AppName { get; }

    public AppLeftoverCandidate Leftover { get; }

    public PathRisk Risk { get; }

    public string FullPath => Leftover.FullPath;

    public bool IsDirectory => Leftover.IsDirectory;

    public long EstimatedBytes => Leftover.EstimatedBytes;

    public string Reason => Leftover.Reason;

    public int SafetyScore => SafetyScoreCalculator.FromRisk(Risk);

    public string RiskLabel => Risk.Level.ToString();

    public string RiskReason => Risk.Reason;

    public bool IsRecommended => !Risk.IsProtected && !Risk.RequiresExplicitOverride;

    public bool CanSelectSafely => !Risk.IsProtected && !Risk.RequiresExplicitOverride;

    public string Recommendation => SafetyScoreCalculator.Recommendation(Risk, preferred: true);

    [ObservableProperty]
    private bool isSelected;
}

public sealed class UninstallPreviewItemViewModel
{
    public UninstallPreviewItemViewModel(InstalledAppViewModel app)
    {
        DisplayName = app.DisplayName;
        SizeBytes = app.EstimatedSizeBytes ?? 0;
        Recommendation = app.Recommendation;
        SafetyScore = app.SafetyScore;
    }

    public string DisplayName { get; }

    public long SizeBytes { get; }

    public string SizeDisplay => SizeBytes.ToSizeString();

    public string Recommendation { get; }

    public int SafetyScore { get; }
}

public sealed class LeftoverPreviewItemViewModel
{
    public LeftoverPreviewItemViewModel(AppLeftoverViewModel item)
    {
        Path = item.FullPath;
        SizeBytes = item.EstimatedBytes;
        Recommendation = item.Recommendation;
        SafetyScore = item.SafetyScore;
    }

    public string Path { get; }

    public long SizeBytes { get; }

    public string SizeDisplay => SizeBytes.ToSizeString();

    public string Recommendation { get; }

    public int SafetyScore { get; }
}
