namespace StorageCleaner.Core.Models;

public enum CleanupAutomationFrequency
{
    Daily,
    Weekly
}

public sealed class CleanupAutomationRule
{
    public required string RuleId { get; init; }

    public required string Name { get; set; }

    public bool Enabled { get; set; } = true;

    public required IReadOnlyList<CleanupCategory> Categories { get; set; }

    public CleanupAutomationFrequency Frequency { get; set; } = CleanupAutomationFrequency.Daily;

    public DayOfWeek? DayOfWeek { get; set; }

    public TimeSpan RunAtLocalTime { get; set; } = new(2, 0, 0);

    public bool PreviewOnly { get; set; } = true;

    public bool StrictSafety { get; set; } = true;

    public int MaxRetryCount { get; set; } = 2;

    public TimeSpan RetryBackoff { get; set; } = TimeSpan.FromMinutes(2);

    public TimeSpan? SafeWindowStartLocalTime { get; set; }

    public TimeSpan? SafeWindowEndLocalTime { get; set; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? LastRunAt { get; set; }

    public DateTimeOffset NextRunAt { get; set; }
}

public sealed class CleanupAutomationRun
{
    public required string AutomationRunId { get; init; }

    public required string RuleId { get; init; }

    public required string RuleName { get; init; }

    public required DateTimeOffset StartedAt { get; init; }

    public required DateTimeOffset CompletedAt { get; init; }

    public required bool Success { get; init; }

    public required bool IsSimulation { get; init; }

    public required int CandidateCount { get; init; }

    public required long ReclaimedBytes { get; init; }

    public required string Message { get; init; }

    public string? CleanupRunId { get; init; }

    public int AttemptCount { get; init; } = 1;

    public bool Skipped { get; init; }

    public string? SkipReason { get; init; }

    public bool ConflictDetected { get; init; }

    public DateTimeOffset? NextRetryAt { get; init; }

    public IReadOnlyList<string> AuditTrail { get; init; } = [];
}
