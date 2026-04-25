using StorageCleaner.Core.Models;
using StorageCleaner.Core;

namespace StorageCleaner.Core.Abstractions;

public interface IStorageScanner
{
    Task<ScanResult> ScanAsync(
        ScanRequest request,
        PauseToken pauseToken,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
