using StorageCleaner.Core.Models;

namespace StorageCleaner.Core.Abstractions;

public interface IRestoreVaultService
{
    Task<RestoreVaultBackupResult> BackupAsync(
        string runId,
        CleanupCandidate candidate,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RestoreVaultEntry>> ReadEntriesAsync(
        int maxEntries = 1000,
        CancellationToken cancellationToken = default);

    Task<(bool Success, string Message)> RestoreAsync(
        string entryId,
        CancellationToken cancellationToken = default);

    Task<(bool Success, string Message)> PurgeAsync(
        string entryId,
        CancellationToken cancellationToken = default);
}
