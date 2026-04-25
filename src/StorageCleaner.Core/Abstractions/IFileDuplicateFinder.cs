using StorageCleaner.Core.Models;

namespace StorageCleaner.Core.Abstractions;

public interface IFileDuplicateFinder
{
    Task<IReadOnlyList<DuplicateFileGroup>> FindDuplicatesAsync(
        IReadOnlyCollection<string> roots,
        IProgress<DuplicateScanProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
