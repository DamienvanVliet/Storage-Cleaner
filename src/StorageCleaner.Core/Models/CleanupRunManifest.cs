namespace StorageCleaner.Core.Models;

public sealed class CleanupRunManifest
{
    public required string RunId { get; init; }

    public required DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset? CompletedAt { get; set; }

    public required bool IsSimulation { get; init; }

    public required bool UseRecycleBin { get; init; }

    public required bool AllowRiskyPaths { get; init; }

    public required IReadOnlyList<CleanupRunCandidateState> BeforeState { get; init; }

    public IReadOnlyList<CleanupItemResult> Results { get; set; } = [];

    public CleanupRunState State { get; set; } = CleanupRunState.InProgress;

    public DateTimeOffset? LastCheckpointAt { get; set; }

    public string? LastCheckpointMessage { get; set; }

    public string? RecoveryMessage { get; set; }

    public IReadOnlyList<CleanupRunCheckpoint> Checkpoints { get; set; } = [];

    public long EstimatedBytes => BeforeState.Sum(static x => x.SizeBytes);

    public long ReclaimedBytes => Results.Where(static x => x.Success).Sum(static x => x.ReclaimedBytes);

    public string RollbackGuidance =>
        IsSimulation
            ? "Simulation mode: no files were deleted."
            : "Rollback path: open Recycle Bin and restore items from this run timestamp. For queued reboot deletions, cancel by rebooting into safe mode before normal startup.";
}

public sealed record CleanupRunCheckpoint(
    DateTimeOffset Timestamp,
    string Message,
    string? Path,
    bool IsError);

public sealed record CleanupRunCandidateState(
    string FullPath,
    bool IsDirectory,
    long SizeBytes,
    DateTime LastModifiedUtc,
    bool ExistedBefore,
    CleanupCategory Category,
    PathRiskLevel RiskLevel,
    string RiskReason);
