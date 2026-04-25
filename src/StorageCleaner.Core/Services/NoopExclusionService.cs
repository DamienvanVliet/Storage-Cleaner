using StorageCleaner.Core.Abstractions;
using StorageCleaner.Core.Models;

namespace StorageCleaner.Core.Services;

public sealed class NoopExclusionService : IExclusionService
{
    public static NoopExclusionService Instance { get; } = new();

    private NoopExclusionService()
    {
    }

    public IReadOnlyList<ExclusionRule> GetRules()
    {
        return [];
    }

    public Task<IReadOnlyList<ExclusionRule>> AddRuleAsync(
        ExclusionRuleKind kind,
        string value,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IReadOnlyList<ExclusionRule>>([]);
    }

    public Task<IReadOnlyList<ExclusionRule>> RemoveRuleAsync(
        string ruleId,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IReadOnlyList<ExclusionRule>>([]);
    }

    public ExclusionMatch Match(string path, CleanupCategory? category = null, string? appName = null)
    {
        return new ExclusionMatch(false, null, "No exclusion rule matched.");
    }
}
