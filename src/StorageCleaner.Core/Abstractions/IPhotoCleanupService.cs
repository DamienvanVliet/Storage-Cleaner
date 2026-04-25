using StorageCleaner.Core.Models;

namespace StorageCleaner.Core.Abstractions;

public interface IPhotoCleanupService
{
    Task<PhotoCleanupScanResult> ScanAsync(
        IReadOnlyCollection<string> roots,
        PhotoCleanupScanOptions? options = null,
        IProgress<PhotoCleanupProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
