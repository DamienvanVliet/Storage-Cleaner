using StorageCleaner.Core.Models;

namespace StorageCleaner.Core.Abstractions;

public interface IExclusionService
{
    IReadOnlyList<ExclusionRule> GetRules();

    Task<IReadOnlyList<ExclusionRule>> AddRuleAsync(
        ExclusionRuleKind kind,
        string value,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ExclusionRule>> RemoveRuleAsync(
        string ruleId,
        CancellationToken cancellationToken = default);

    ExclusionMatch Match(
        string path,
        CleanupCategory? category = null,
        string? appName = null);
}
