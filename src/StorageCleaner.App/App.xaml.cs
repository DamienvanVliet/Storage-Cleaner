using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using StorageCleaner.App.Services;
using StorageCleaner.App.ViewModels;
using StorageCleaner.Core.Abstractions;
using StorageCleaner.Core.Services;

namespace StorageCleaner.App;

public partial class App : Application
{
    private ServiceProvider? _serviceProvider;
    private IAppLogger? _logger;
    private AutomationSchedulerService? _automationScheduler;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var services = new ServiceCollection();
        ConfigureServices(services);

        _serviceProvider = services.BuildServiceProvider();
        _logger = _serviceProvider.GetRequiredService<IAppLogger>();

        ConfigureGlobalExceptionHandlers();
        _logger.LogInfo("Application startup initiated.");
        _logger.LogInfo($"Environment: OS={Environment.OSVersion} Machine={Environment.MachineName} User={Environment.UserName} ProcId={Environment.ProcessId}");

        var settingsService = _serviceProvider.GetRequiredService<ISettingsService>();
        await settingsService.LoadAsync();

        var runStore = _serviceProvider.GetRequiredService<ICleanupRunStore>();
        var recoveredRuns = await runStore.RecoverInterruptedRunsAsync(TimeSpan.FromMinutes(10));
        if (recoveredRuns.Count > 0)
        {
            _logger.LogWarning($"Recovered {recoveredRuns.Count} interrupted cleanup run(s).");
        }

        var themeService = _serviceProvider.GetRequiredService<IThemeService>();
        themeService.ApplyTheme(settingsService.Current.ThemeMode);

        _automationScheduler = _serviceProvider.GetRequiredService<AutomationSchedulerService>();
        _automationScheduler.Start();

        var mainWindow = _serviceProvider.GetRequiredService<MainWindow>();
        mainWindow.Show();
        _logger.LogInfo("Main window shown.");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_automationScheduler is not null)
        {
            _automationScheduler.StopAsync().GetAwaiter().GetResult();
        }

        _logger?.LogInfo("Application exiting.");
        _serviceProvider?.Dispose();
        base.OnExit(e);
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<IScanCache, MemoryScanCache>();
        services.AddSingleton<IStorageScanner, StorageScanner>();
        services.AddSingleton<IFileSearchService, FileSearchService>();
        services.AddSingleton<IExclusionService, FileExclusionService>();
        services.AddSingleton<IPathSafetyService, PathSafetyService>();
        services.AddSingleton<IRecycleBinService, RecycleBinService>();
        services.AddSingleton<ICleanupHistoryStore, FileCleanupHistoryStore>();
        services.AddSingleton<ICleanupRunStore, FileCleanupRunStore>();
        services.AddSingleton<IStorageSnapshotStore, FileStorageSnapshotStore>();
        services.AddSingleton<ISnapshotDiffService, SnapshotDiffService>();
        services.AddSingleton<IRestoreVaultService, FileRestoreVaultService>();
        services.AddSingleton<ICleanupAutomationService, FileCleanupAutomationService>();
        services.AddSingleton<IRebootDeletionScheduler, WindowsRebootDeletionScheduler>();
        services.AddSingleton<ILockInspector, WindowsLockInspector>();
        services.AddSingleton<ISafeCleanupAnalyzer, SafeCleanupAnalyzer>();
        services.AddSingleton<IFileDuplicateFinder, FileDuplicateFinder>();
        services.AddSingleton<IWasteAnalysisService, WasteAnalysisService>();
        services.AddSingleton<IStorageAnalyticsService, StorageAnalyticsService>();
        services.AddSingleton<IPhotoCleanupService, PhotoCleanupService>();
        services.AddSingleton<IAppUninstallerService, WindowsAppUninstallerService>();
        services.AddSingleton<ICleanupExecutor, CleanupExecutor>();

        services.AddSingleton<ISettingsService, JsonSettingsService>();
        services.AddSingleton<IThemeService, ThemeService>();
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<IAppLogger, FileAppLogger>();
        services.AddSingleton<ScanWorkspaceService>();
        services.AddSingleton<AutomationSchedulerService>();

        services.AddSingleton<DashboardViewModel>();
        services.AddSingleton<ScanDriveViewModel>();
        services.AddSingleton<FolderExplorerViewModel>();
        services.AddSingleton<SafeCleanupViewModel>();
        services.AddSingleton<PhotoCleanupViewModel>();
        services.AddSingleton<AppUninstallerViewModel>();
        services.AddSingleton<AdvancedToolsViewModel>();
        services.AddSingleton<CleanupHistoryViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<MainViewModel>();

        services.AddSingleton<MainWindow>();
    }

    private void ConfigureGlobalExceptionHandlers()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            _logger?.LogError("Unhandled dispatcher exception.", args.Exception);
            MessageBox.Show(
                $"An unexpected error occurred.\n\n{args.Exception.Message}\n\nDetails are in:\n{_logger?.LogFilePath}",
                "Storage Cleaner Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            args.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception exception)
            {
                _logger?.LogError("Unhandled AppDomain exception.", exception);
            }
            else
            {
                _logger?.LogError("Unhandled AppDomain exception object.");
            }
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            _logger?.LogError("Unobserved task exception.", args.Exception);
            args.SetObserved();
        };
    }
}
