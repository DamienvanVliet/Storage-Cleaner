using StorageCleaner.Core.Models;

namespace StorageCleaner.Core.Abstractions;

public interface ICleanupAutomationService
{
    Task<IReadOnlyList<CleanupAutomationRule>> ReadRulesAsync(
        CancellationToken cancellationToken = default);

    Task<CleanupAutomationRule> UpsertRuleAsync(
        CleanupAutomationRule rule,
        CancellationToken cancellationToken = default);

    Task<bool> RemoveRuleAsync(
        string ruleId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CleanupAutomationRun>> ReadRunsAsync(
        int maxRuns = 500,
        CancellationToken cancellationToken = default);

    Task<CleanupAutomationRun> ExecuteRuleAsync(
        string ruleId,
        bool allowDestructive,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CleanupAutomationRun>> RunDueRulesAsync(
        bool allowDestructive,
        DateTimeOffset? now = null,
        CancellationToken cancellationToken = default);
}
