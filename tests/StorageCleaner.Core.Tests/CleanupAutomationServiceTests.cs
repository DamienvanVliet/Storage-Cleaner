using StorageCleaner.Core.Abstractions;
using StorageCleaner.Core.Models;
using StorageCleaner.Core.Services;

namespace StorageCleaner.Core.Tests;

public sealed class CleanupAutomationServiceTests
{
    [Fact]
    public async Task RunDueRulesAsync_ExecutesRuleAndWritesRun()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "storage-cleaner-automation-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var statePath = Path.Combine(tempRoot, "automation-state.json");
        var analyzer = new FakeSafeCleanupAnalyzer();
        var executor = new FakeCleanupExecutor();
        var service = new FileCleanupAutomationService(analyzer, executor, statePath);

        try
        {
            var rule = new CleanupAutomationRule
            {
                RuleId = Guid.NewGuid().ToString("N"),
                Name = "Preview Rule",
                Enabled = true,
                Categories = [CleanupCategory.UserTemp],
                Frequency = CleanupAutomationFrequency.Daily,
                DayOfWeek = null,
                RunAtLocalTime = new TimeSpan(1, 0, 0),
                PreviewOnly = true,
                StrictSafety = true,
                CreatedAt = DateTimeOffset.UtcNow,
                LastRunAt = null,
                NextRunAt = DateTimeOffset.UtcNow.AddMinutes(-1)
            };

            await service.UpsertRuleAsync(rule);
            var runs = await service.RunDueRulesAsync(allowDestructive: false, now: DateTimeOffset.UtcNow);

            Assert.Single(runs);
            Assert.True(runs[0].IsSimulation);
            Assert.True(executor.LastOptions?.SimulationOnly);

            var storedRuns = await service.ReadRunsAsync();
            Assert.Single(storedRuns);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    private sealed class FakeSafeCleanupAnalyzer : ISafeCleanupAnalyzer
    {
        public Task<IReadOnlyList<CleanupCandidate>> AnalyzeAsync(
            IReadOnlyCollection<CleanupCategory> categories,
            IProgress<CleanupProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<CleanupCandidate>>(
            [
                new CleanupCandidate
                {
                    Category = CleanupCategory.UserTemp,
                    FullPath = Path.Combine(Path.GetTempPath(), "automation-preview.tmp"),
                    IsDirectory = false,
                    SizeBytes = 128,
                    LastModifiedUtc = DateTime.UtcNow,
                    Risk = new PathRisk(PathRiskLevel.Safe, "test")
                }
            ]);
        }
    }

    private sealed class FakeCleanupExecutor : ICleanupExecutor
    {
        public CleanupExecutionOptions? LastOptions { get; private set; }

        public Task<CleanupExecutionResult> ExecuteAsync(
            IReadOnlyCollection<CleanupCandidate> candidates,
            CleanupExecutionOptions options,
            CancellationToken cancellationToken = default)
        {
            LastOptions = options;
            return Task.FromResult(new CleanupExecutionResult
            {
                RunId = $"{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-testrun",
                IsSimulation = options.SimulationOnly,
                Items = candidates
                    .Select(candidate => new CleanupItemResult(
                        candidate.FullPath,
                        Success: true,
                        ReclaimedBytes: candidate.SizeBytes,
                        ErrorMessage: null,
                        candidate.Category,
                        candidate.IsDirectory))
                    .ToArray()
            });
        }
    }
}
