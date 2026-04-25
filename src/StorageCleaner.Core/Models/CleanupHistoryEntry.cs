namespace StorageCleaner.Core.Models;

public sealed record CleanupHistoryEntry(
    string RunId,
    DateTimeOffset Timestamp,
    string FullPath,
    bool Success,
    long ReclaimedBytes,
    string? ErrorMessage,
    CleanupCategory Category,
    bool IsDirectory,
    bool SentToRecycleBin,
    bool IsSimulation,
    bool QueuedForReboot,
    string? LockDetails,
    string? RestoreEntryId = null);
