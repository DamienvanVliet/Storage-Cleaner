namespace StorageCleaner.Core.Models;

public sealed record CleanupItemResult(
    string FullPath,
    bool Success,
    long ReclaimedBytes,
    string? ErrorMessage,
    CleanupCategory Category,
    bool IsDirectory,
    bool QueuedForReboot = false,
    string? LockDetails = null,
    string? RestoreEntryId = null);
