using StorageCleaner.Core.Abstractions;

namespace StorageCleaner.App.Services;

public sealed class AutomationSchedulerService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(1);

    private readonly ICleanupAutomationService _automationService;
    private readonly IAppLogger _logger;
    private readonly object _sync = new();
    private CancellationTokenSource? _cts;
    private Task? _loopTask;

    public AutomationSchedulerService(
        ICleanupAutomationService automationService,
        IAppLogger logger)
    {
        _automationService = automationService;
        _logger = logger;
    }

    public void Start()
    {
        lock (_sync)
        {
            if (_loopTask is not null)
            {
                return;
            }

            _cts = new CancellationTokenSource();
            _loopTask = Task.Run(() => RunLoopAsync(_cts.Token));
        }
    }

    public async Task StopAsync()
    {
        Task? loop;
        lock (_sync)
        {
            if (_loopTask is null)
            {
                return;
            }

            _cts?.Cancel();
            loop = _loopTask;
            _loopTask = null;
        }

        try
        {
            if (loop is not null)
            {
                await loop.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when shutdown cancels the loop.
        }
        catch (Exception ex)
        {
            _logger.LogError("Automation scheduler stop failed.", ex);
        }
        finally
        {
            lock (_sync)
            {
                _cts?.Dispose();
                _cts = null;
            }
        }
    }

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInfo("Automation scheduler started.");

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var runs = await _automationService.RunDueRulesAsync(
                    allowDestructive: false,
                    now: DateTimeOffset.Now,
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                foreach (var run in runs)
                {
                    _logger.LogInfo(
                        $"Automation run: rule={run.RuleName} simulation={run.IsSimulation} success={run.Success} candidates={run.CandidateCount} reclaimed={run.ReclaimedBytes}");
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError("Automation scheduler iteration failed.", ex);
            }

            try
            {
                await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInfo("Automation scheduler stopped.");
    }
}
