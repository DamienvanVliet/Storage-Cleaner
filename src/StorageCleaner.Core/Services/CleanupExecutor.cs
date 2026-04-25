using StorageCleaner.Core.Abstractions;
using StorageCleaner.Core.Models;

namespace StorageCleaner.Core.Services;

public sealed class CleanupExecutor : ICleanupExecutor
{
    private readonly IRecycleBinService _recycleBinService;
    private readonly ICleanupHistoryStore _historyStore;
    private readonly ICleanupRunStore _runStore;
    private readonly IPathSafetyService _pathSafetyService;
    private readonly IRebootDeletionScheduler _rebootDeletionScheduler;
    private readonly ILockInspector _lockInspector;
    private readonly IExclusionService _exclusionService;
    private readonly IRestoreVaultService _restoreVaultService;

    public CleanupExecutor(
        IRecycleBinService recycleBinService,
        ICleanupHistoryStore historyStore,
        ICleanupRunStore runStore,
        IPathSafetyService pathSafetyService,
        IRebootDeletionScheduler rebootDeletionScheduler,
        ILockInspector lockInspector,
        IExclusionService? exclusionService = null,
        IRestoreVaultService? restoreVaultService = null)
    {
        _recycleBinService = recycleBinService;
        _historyStore = historyStore;
        _runStore = runStore;
        _pathSafetyService = pathSafetyService;
        _rebootDeletionScheduler = rebootDeletionScheduler;
        _lockInspector = lockInspector;
        _exclusionService = exclusionService ?? NoopExclusionService.Instance;
        _restoreVaultService = restoreVaultService ?? NoopRestoreVaultService.Instance;
    }

    public async Task<CleanupExecutionResult> ExecuteAsync(
        IReadOnlyCollection<CleanupCandidate> candidates,
        CleanupExecutionOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        var runId = $"{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}";
        await _runStore.StartRunAsync(runId, candidates, options, cancellationToken).ConfigureAwait(false);

        var results = new List<CleanupItemResult>(candidates.Count);
        try
        {
            await _runStore.AppendCheckpointAsync(
                runId,
                $"Processing {candidates.Count} candidate(s).",
                cancellationToken: cancellationToken).ConfigureAwait(false);

            foreach (var candidate in candidates
                         .Where(static x => !string.IsNullOrWhiteSpace(x.FullPath))
                         .DistinctBy(static x => x.FullPath, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await _runStore.AppendCheckpointAsync(
                    runId,
                    $"Evaluating candidate ({candidate.Category})",
                    candidate.FullPath,
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                var exclusion = _exclusionService.Match(candidate.FullPath, candidate.Category);
                if (exclusion.IsExcluded)
                {
                    var excludedResult = new CleanupItemResult(
                        candidate.FullPath,
                        Success: false,
                        ReclaimedBytes: 0,
                        ErrorMessage: exclusion.Reason,
                        candidate.Category,
                        candidate.IsDirectory);

                    results.Add(excludedResult);
                    await WriteHistoryAsync(runId, excludedResult, options, cancellationToken).ConfigureAwait(false);
                    await _runStore.AppendCheckpointAsync(
                        runId,
                        $"Skipped by exclusion rule: {exclusion.Reason}",
                        candidate.FullPath,
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                    continue;
                }

                var risk = _pathSafetyService.Evaluate(candidate.FullPath);
                if (risk.IsProtected)
                {
                    var blockedResult = new CleanupItemResult(
                        candidate.FullPath,
                        Success: false,
                        ReclaimedBytes: 0,
                        ErrorMessage: "Deletion blocked for protected system path.",
                        candidate.Category,
                        candidate.IsDirectory);

                    results.Add(blockedResult);
                    await WriteHistoryAsync(runId, blockedResult, options, cancellationToken).ConfigureAwait(false);
                    await _runStore.AppendCheckpointAsync(
                        runId,
                        "Blocked protected path.",
                        candidate.FullPath,
                        isError: true,
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (!options.AllowRiskyPaths && risk.Level >= PathRiskLevel.HighRisk)
                {
                    var deniedResult = new CleanupItemResult(
                        candidate.FullPath,
                        Success: false,
                        ReclaimedBytes: 0,
                        ErrorMessage: "Risky path requires explicit opt-in.",
                        candidate.Category,
                        candidate.IsDirectory);

                    results.Add(deniedResult);
                    await WriteHistoryAsync(runId, deniedResult, options, cancellationToken).ConfigureAwait(false);
                    await _runStore.AppendCheckpointAsync(
                        runId,
                        "Blocked risky path without explicit override.",
                        candidate.FullPath,
                        isError: true,
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                    continue;
                }

                CleanupItemResult result;
                if (options.SimulationOnly)
                {
                    result = new CleanupItemResult(
                        candidate.FullPath,
                        Success: true,
                        ReclaimedBytes: candidate.SizeBytes,
                        ErrorMessage: null,
                        candidate.Category,
                        candidate.IsDirectory);
                }
                else
                {
                    string? restoreEntryId = null;
                    string? backupWarning = null;
                    if (options.CaptureRestoreBackup)
                    {
                        var backup = await _restoreVaultService.BackupAsync(runId, candidate, cancellationToken).ConfigureAwait(false);
                        if (backup.Success)
                        {
                            restoreEntryId = backup.EntryId;
                        }
                        else if (!string.IsNullOrWhiteSpace(backup.ErrorMessage))
                        {
                            backupWarning = backup.ErrorMessage;
                        }
                    }

                    try
                    {
                        if (candidate.IsDirectory)
                        {
                            await _recycleBinService.DeleteDirectoryAsync(candidate.FullPath, options.UseRecycleBin, cancellationToken)
                                .ConfigureAwait(false);
                        }
                        else
                        {
                            await _recycleBinService.DeleteFileAsync(candidate.FullPath, options.UseRecycleBin, cancellationToken)
                                .ConfigureAwait(false);
                        }

                        result = new CleanupItemResult(
                            candidate.FullPath,
                            Success: true,
                            ReclaimedBytes: candidate.SizeBytes,
                            ErrorMessage: backupWarning is null ? null : $"Deleted with warning: restore backup unavailable ({backupWarning})",
                            candidate.Category,
                            candidate.IsDirectory,
                            RestoreEntryId: restoreEntryId);
                    }
                    catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or OperationCanceledException)
                    {
                        if (ex is OperationCanceledException)
                        {
                            throw;
                        }

                        var lockDetails = string.Empty;
                        var queuedForReboot = false;
                        var errorMessage = ex.Message;
                        if (IsLikelyLockedException(ex))
                        {
                            var lockingProcesses = _lockInspector.TryGetLockingProcesses(candidate.FullPath);
                            if (lockingProcesses.Count > 0)
                            {
                                lockDetails = string.Join(", ", lockingProcesses);
                            }

                            if (options.QueueLockedForReboot)
                            {
                                if (_rebootDeletionScheduler.TryScheduleDelete(candidate.FullPath, out var queueError))
                                {
                                    queuedForReboot = true;
                                    errorMessage = "File is locked now. Deletion queued for next reboot.";
                                }
                                else if (!string.IsNullOrWhiteSpace(queueError))
                                {
                                    errorMessage = $"{errorMessage} Queue failed: {queueError}";
                                }
                            }
                        }

                        result = new CleanupItemResult(
                            candidate.FullPath,
                            Success: queuedForReboot,
                            ReclaimedBytes: queuedForReboot ? 0 : 0,
                            ErrorMessage: queuedForReboot ? null : errorMessage,
                            candidate.Category,
                            candidate.IsDirectory,
                            QueuedForReboot: queuedForReboot,
                            LockDetails: string.IsNullOrWhiteSpace(lockDetails) ? null : lockDetails,
                            RestoreEntryId: restoreEntryId);
                    }
                }

                results.Add(result);
                await WriteHistoryAsync(runId, result, options, cancellationToken).ConfigureAwait(false);
                await _runStore.AppendCheckpointAsync(
                    runId,
                    result.Success
                        ? $"Completed candidate. Reclaimed {result.ReclaimedBytes} bytes."
                        : $"Candidate failed: {result.ErrorMessage}",
                    candidate.FullPath,
                    isError: !result.Success,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            await _runStore.AppendCheckpointAsync(
                runId,
                "Cleanup canceled by user.",
                isError: true,
                cancellationToken: CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            await _runStore.AppendCheckpointAsync(
                runId,
                $"Cleanup failed unexpectedly: {ex.Message}",
                isError: true,
                cancellationToken: CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        finally
        {
            await _runStore.CompleteRunAsync(runId, results, cancellationToken).ConfigureAwait(false);
        }

        return new CleanupExecutionResult
        {
            RunId = runId,
            IsSimulation = options.SimulationOnly,
            Items = results
        };
    }

    private Task WriteHistoryAsync(string runId, CleanupItemResult item, CleanupExecutionOptions options, CancellationToken cancellationToken)
    {
        var entry = new CleanupHistoryEntry(
            RunId: runId,
            Timestamp: DateTimeOffset.UtcNow,
            FullPath: item.FullPath,
            Success: item.Success,
            ReclaimedBytes: item.ReclaimedBytes,
            ErrorMessage: item.ErrorMessage,
            Category: item.Category,
            IsDirectory: item.IsDirectory,
            SentToRecycleBin: options.UseRecycleBin,
            IsSimulation: options.SimulationOnly,
            QueuedForReboot: item.QueuedForReboot,
            LockDetails: item.LockDetails,
            RestoreEntryId: item.RestoreEntryId);

        return _historyStore.AppendAsync(entry, cancellationToken);
    }

    private static bool IsLikelyLockedException(Exception ex)
    {
        if (ex is not IOException and not UnauthorizedAccessException)
        {
            return false;
        }

        var nativeCode = ex.HResult & 0xFFFF;
        return nativeCode == 32 || nativeCode == 33 ||
               ex.Message.Contains("used by another process", StringComparison.OrdinalIgnoreCase) ||
               ex.Message.Contains("because it is being used", StringComparison.OrdinalIgnoreCase);
    }
}
