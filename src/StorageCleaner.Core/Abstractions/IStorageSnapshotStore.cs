using StorageCleaner.Core.Models;

namespace StorageCleaner.Core.Abstractions;

public interface IStorageSnapshotStore
{
    Task<StorageSnapshot> CreateAsync(
        ScanResult scanResult,
        string label,
        IReadOnlyCollection<string> roots,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<StorageSnapshot>> ReadRecentAsync(
        int maxSnapshots = 100,
        CancellationToken cancellationToken = default);

    Task<StorageSnapshot?> ReadByIdAsync(
        string snapshotId,
        CancellationToken cancellationToken = default);
}
