namespace StorageCleaner.Core.Models;

public enum ExclusionRuleKind
{
    PathPrefix,
    FileExtension,
    Category,
    AppKeyword
}

public sealed class ExclusionRule
{
    public required string RuleId { get; init; }

    public required ExclusionRuleKind Kind { get; init; }

    public required string Value { get; init; }

    public bool Enabled { get; init; } = true;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public string DisplayText => $"{Kind}: {Value}";
}

public sealed record ExclusionMatch(
    bool IsExcluded,
    string? RuleId,
    string Reason);
