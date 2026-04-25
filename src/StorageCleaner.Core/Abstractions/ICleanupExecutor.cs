using StorageCleaner.Core.Models;

namespace StorageCleaner.Core.Abstractions;

public interface ICleanupExecutor
{
    Task<CleanupExecutionResult> ExecuteAsync(
        IReadOnlyCollection<CleanupCandidate> candidates,
        CleanupExecutionOptions options,
        CancellationToken cancellationToken = default);
}
