using StorageCleaner.Core.Models;

namespace StorageCleaner.Core.Abstractions;

public interface ICleanupRunStore
{
    Task<CleanupRunManifest> StartRunAsync(
        string runId,
        IReadOnlyCollection<CleanupCandidate> candidates,
        CleanupExecutionOptions options,
        CancellationToken cancellationToken = default);

    Task CompleteRunAsync(
        string runId,
        IReadOnlyList<CleanupItemResult> results,
        CancellationToken cancellationToken = default);

    Task AppendCheckpointAsync(
        string runId,
        string message,
        string? fullPath = null,
        bool isError = false,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CleanupRunManifest>> RecoverInterruptedRunsAsync(
        TimeSpan staleThreshold,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CleanupRunManifest>> ReadRecentRunsAsync(
        int maxRuns = 100,
        CancellationToken cancellationToken = default);

    Task<string> ExportRunManifestAsync(
        string runId,
        string destinationDirectory,
        CancellationToken cancellationToken = default);
}
