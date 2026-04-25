using System.Threading;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using StorageCleaner.Core;
using StorageCleaner.Core.Abstractions;
using StorageCleaner.Core.Models;

namespace StorageCleaner.App.Services;

public partial class ScanWorkspaceService : ObservableObject
{
    private readonly IStorageScanner _storageScanner;
    private readonly IAppLogger _logger;
    private readonly object _sync = new();
    private readonly object _progressSync = new();
    private readonly TimeSpan _uiProgressInterval = TimeSpan.FromMilliseconds(250);
    private readonly TimeSpan _stalledProgressThreshold = TimeSpan.FromSeconds(8);

    private CancellationTokenSource? _scanCts;
    private PauseTokenSource? _pauseTokenSource;
    private DispatcherTimer? _progressTimer;
    private ScanProgress? _latestProgress;
    private DateTimeOffset _lastProgressUpdateUtc = DateTimeOffset.UtcNow;
    private bool _stallWarningLogged;

    public ScanWorkspaceService(IStorageScanner storageScanner, IAppLogger logger)
    {
        _storageScanner = storageScanner;
        _logger = logger;
    }

    [ObservableProperty]
    private bool isScanning;

    [ObservableProperty]
    private bool isPaused;

    [ObservableProperty]
    private ScanProgress? currentProgress;

    [ObservableProperty]
    private ScanResult? currentResult;

    [ObservableProperty]
    private string statusMessage = "Ready to scan.";

    [ObservableProperty]
    private string? lastErrorMessage;

    [ObservableProperty]
    private IReadOnlyList<ScanIssue> lastIssues = [];

    public IReadOnlyList<string> LastRoots { get; private set; } = [];

    public ScanMode LastScanMode { get; private set; } = ScanMode.Standard;

    public async Task StartScanAsync(
        IReadOnlyCollection<string> roots,
        int maxParallelism,
        ScanMode scanMode = ScanMode.Standard,
        bool useCache = true,
        CancellationToken cancellationToken = default)
    {
        if (roots is null || roots.Count == 0)
        {
            throw new ArgumentException("A root path is required.", nameof(roots));
        }

        lock (_sync)
        {
            if (IsScanning)
            {
                throw new InvalidOperationException("A scan is already running.");
            }

            _scanCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _pauseTokenSource = new PauseTokenSource();
        }

        SetState(() =>
        {
            IsScanning = true;
            IsPaused = false;
            LastErrorMessage = null;
            LastIssues = [];
            CurrentProgress = new ScanProgress(0, 0, 0, 0, 0, null, TimeSpan.Zero, false);
            StatusMessage = "Scanning started...";
        });
        _logger.LogInfo($"Scan started for roots: {string.Join(", ", roots)}");
        StartProgressTimer();

        try
        {
            var progress = new ScanProgressForwarder(UpdateLatestProgress);

            var token = _scanCts.Token;
            var result = await Task.Factory.StartNew(
                    () => _storageScanner.ScanAsync(
                        new ScanRequest(roots, Math.Max(1, maxParallelism), useCache, scanMode),
                        _pauseTokenSource!.Token,
                        progress,
                        token),
                    token,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default)
                .Unwrap()
                .ConfigureAwait(false);

            SetState(() =>
            {
                CurrentResult = result;
                LastRoots = roots.ToArray();
                LastScanMode = scanMode;
                LastIssues = result.Issues;
                StatusMessage = result.Issues.Count > 0
                    ? $"Scan completed with {result.Issues.Count} warning(s) in {result.Duration.TotalSeconds:0.0}s."
                    : $"Scan completed in {result.Duration.TotalSeconds:0.0}s.";
            });

            if (result.Issues.Count > 0)
            {
                _logger.LogWarning($"Scan finished with {result.Issues.Count} issue(s).");
                foreach (var issue in result.Issues.Take(250))
                {
                    _logger.LogWarning($"{issue.ExceptionType}: {issue.Path} | {issue.Message}");
                }
            }
            else
            {
                _logger.LogInfo($"Scan completed successfully in {result.Duration.TotalSeconds:0.0}s.");
            }
        }
        catch (OperationCanceledException)
        {
            SetState(() =>
            {
                StatusMessage = "Scan canceled.";
            });
            _logger.LogWarning("Scan canceled by user.");
        }
        catch (Exception ex)
        {
            _logger.LogError("Scan failed with unhandled exception.", ex);
            SetState(() =>
            {
                LastErrorMessage = ex.Message;
                StatusMessage = "Scan failed. See logs for details.";
            });
        }
        finally
        {
            StopProgressTimer();

            lock (_sync)
            {
                _scanCts?.Dispose();
                _scanCts = null;
                _pauseTokenSource = null;
            }

            lock (_progressSync)
            {
                _latestProgress = null;
            }

            SetState(() =>
            {
                IsScanning = false;
                IsPaused = false;
            });
        }
    }

    public void Pause()
    {
        lock (_sync)
        {
            if (!IsScanning || IsPaused || _pauseTokenSource is null)
            {
                return;
            }

            _pauseTokenSource.Pause();
        }

        SetState(() =>
        {
            IsPaused = true;
            StatusMessage = "Scan paused.";
        });
    }

    public void Resume()
    {
        lock (_sync)
        {
            if (!IsScanning || !IsPaused || _pauseTokenSource is null)
            {
                return;
            }

            _pauseTokenSource.Resume();
        }

        SetState(() =>
        {
            IsPaused = false;
            StatusMessage = "Scan resumed.";
        });
    }

    public void Cancel()
    {
        lock (_sync)
        {
            _scanCts?.Cancel();
        }
    }

    public Task RescanLastAsync(int maxParallelism, bool useCache = false, ScanMode? scanMode = null, CancellationToken cancellationToken = default)
    {
        if (LastRoots.Count == 0)
        {
            return Task.CompletedTask;
        }

        return StartScanAsync(LastRoots, maxParallelism, scanMode ?? LastScanMode, useCache, cancellationToken);
    }

    private void SetState(Action updateAction)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            updateAction();
            return;
        }

        _ = dispatcher.BeginInvoke(updateAction);
    }

    private void UpdateLatestProgress(ScanProgress progress)
    {
        lock (_progressSync)
        {
            _latestProgress = progress;
            _lastProgressUpdateUtc = DateTimeOffset.UtcNow;
        }
    }

    private void StartProgressTimer()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return;
        }

        lock (_progressSync)
        {
            _latestProgress = null;
            _lastProgressUpdateUtc = DateTimeOffset.UtcNow;
            _stallWarningLogged = false;
        }

        SetState(() =>
        {
            _progressTimer?.Stop();
            _progressTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
            {
                Interval = _uiProgressInterval
            };
            _progressTimer.Tick += OnProgressTimerTick;
            _progressTimer.Start();
        });
    }

    private void StopProgressTimer()
    {
        SetState(() =>
        {
            if (_progressTimer is null)
            {
                return;
            }

            _progressTimer.Stop();
            _progressTimer.Tick -= OnProgressTimerTick;
            _progressTimer = null;
        });
    }

    private void OnProgressTimerTick(object? sender, EventArgs e)
    {
        ScanProgress? snapshot;
        DateTimeOffset lastUpdateUtc;
        lock (_progressSync)
        {
            snapshot = _latestProgress;
            lastUpdateUtc = _lastProgressUpdateUtc;
        }

        if (snapshot is not null)
        {
            _stallWarningLogged = false;
            CurrentProgress = snapshot;
            StatusMessage = $"Scanning {snapshot.CurrentPath ?? "filesystem"}";
            return;
        }

        if (!IsScanning)
        {
            return;
        }

        var stallDuration = DateTimeOffset.UtcNow - lastUpdateUtc;
        if (stallDuration >= _stalledProgressThreshold)
        {
            if (!_stallWarningLogged)
            {
                _stallWarningLogged = true;
                _logger.LogWarning($"Scan progress heartbeat delayed for {stallDuration.TotalSeconds:0}s.");
            }
            StatusMessage = "Scanning large folders... still working.";
        }
    }

    private sealed class ScanProgressForwarder : IProgress<ScanProgress>
    {
        private readonly Action<ScanProgress> _onProgress;

        public ScanProgressForwarder(Action<ScanProgress> onProgress)
        {
            _onProgress = onProgress;
        }

        public void Report(ScanProgress value)
        {
            _onProgress(value);
        }
    }
}
