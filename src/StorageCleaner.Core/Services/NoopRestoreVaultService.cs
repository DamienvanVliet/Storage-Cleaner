using StorageCleaner.Core.Abstractions;
using StorageCleaner.Core.Models;

namespace StorageCleaner.Core.Services;

public sealed class NoopRestoreVaultService : IRestoreVaultService
{
    public static NoopRestoreVaultService Instance { get; } = new();

    private NoopRestoreVaultService()
    {
    }

    public Task<RestoreVaultBackupResult> BackupAsync(
        string runId,
        CleanupCandidate candidate,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new RestoreVaultBackupResult(false, null, "Restore vault disabled."));
    }

    public Task<IReadOnlyList<RestoreVaultEntry>> ReadEntriesAsync(int maxEntries = 1000, CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IReadOnlyList<RestoreVaultEntry>>([]);
    }

    public Task<(bool Success, string Message)> RestoreAsync(string entryId, CancellationToken cancellationToken = default)
    {
        return Task.FromResult((false, "Restore vault disabled."));
    }

    public Task<(bool Success, string Message)> PurgeAsync(string entryId, CancellationToken cancellationToken = default)
    {
        return Task.FromResult((false, "Restore vault disabled."));
    }
}
