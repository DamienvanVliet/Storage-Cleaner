using StorageCleaner.Core.Models;

namespace StorageCleaner.Core.Abstractions;

public interface ISafeCleanupAnalyzer
{
    Task<IReadOnlyList<CleanupCandidate>> AnalyzeAsync(
        IReadOnlyCollection<CleanupCategory> categories,
        IProgress<CleanupProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
