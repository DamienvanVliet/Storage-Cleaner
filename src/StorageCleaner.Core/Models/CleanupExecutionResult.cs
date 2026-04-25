namespace StorageCleaner.Core.Models;

public sealed class CleanupExecutionResult
{
    public required string RunId { get; init; }

    public required bool IsSimulation { get; init; }

    public required IReadOnlyList<CleanupItemResult> Items { get; init; }

    public long ReclaimedBytes => Items.Where(x => x.Success).Sum(x => x.ReclaimedBytes);

    public int SuccessCount => Items.Count(x => x.Success);

    public int FailureCount => Items.Count(x => !x.Success);

    public int QueuedForRebootCount => Items.Count(x => x.QueuedForReboot);
}
