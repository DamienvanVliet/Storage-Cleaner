using StorageCleaner.Core.Models;

namespace StorageCleaner.Core.Abstractions;

public interface ISnapshotDiffService
{
    Task<SnapshotDiffResult> CompareAsync(
        StorageSnapshot before,
        StorageSnapshot after,
        CancellationToken cancellationToken = default);
}
