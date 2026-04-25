namespace StorageCleaner.Core.Models;

public sealed class RestoreVaultEntry
{
    public required string EntryId { get; init; }

    public required string RunId { get; init; }

    public required string OriginalPath { get; init; }

    public required string VaultPath { get; init; }

    public required bool IsDirectory { get; init; }

    public required long SizeBytes { get; init; }

    public required CleanupCategory Category { get; init; }

    public required DateTimeOffset BackedUpAt { get; init; }

    public DateTimeOffset? RestoredAt { get; set; }

    public bool Purged { get; set; }

    public bool CanRestore => !Purged && RestoredAt is null && (File.Exists(VaultPath) || Directory.Exists(VaultPath));
}

public sealed record RestoreVaultBackupResult(
    bool Success,
    string? EntryId,
    string? ErrorMessage);
