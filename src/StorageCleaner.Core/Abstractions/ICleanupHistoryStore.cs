using StorageCleaner.Core.Models;

namespace StorageCleaner.Core.Abstractions;

public interface ICleanupHistoryStore
{
    Task AppendAsync(CleanupHistoryEntry entry, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CleanupHistoryEntry>> ReadAsync(int maxEntries = 500, CancellationToken cancellationToken = default);
}
