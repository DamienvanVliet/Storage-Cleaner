using StorageCleaner.Core.Models;

namespace StorageCleaner.Core.Abstractions;

public interface IStorageAnalyticsService
{
    Task<StorageAnalyticsResult> AnalyzeAsync(
        IReadOnlyCollection<string> roots,
        ScanResult? scanResult = null,
        int maxTreemapTiles = 300,
        IProgress<StorageAnalyticsProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
