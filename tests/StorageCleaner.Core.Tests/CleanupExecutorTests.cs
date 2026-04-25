using StorageCleaner.Core.Abstractions;
using StorageCleaner.Core.Models;
using StorageCleaner.Core.Services;

namespace StorageCleaner.Core.Tests;

public sealed class CleanupExecutorTests
{
    [Fact]
    public async Task ExecuteAsync_BlocksProtectedPathsAndLogsFailure()
    {
        var recycle = new FakeRecycleBinService();
        var history = new InMemoryHistoryStore();
        var runStore = new InMemoryRunStore();
        var pathSafety = new PathSafetyService();
        var rebootScheduler = new NoopRebootDeletionScheduler();
        var lockInspector = new NoopLockInspector();
        var executor = new CleanupExecutor(recycle, history, runStore, pathSafety, rebootScheduler, lockInspector);

        var windowsPath = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var result = await executor.ExecuteAsync(
            [new CleanupCandidate
            {
                Category = CleanupCategory.ManualSelection,
                FullPath = windowsPath,
                IsDirectory = true,
                SizeBytes = 1,
                LastModifiedUtc = DateTime.UtcNow,
                Risk = pathSafety.Evaluate(windowsPath)
            }],
            new CleanupExecutionOptions(UseRecycleBin: true, AllowRiskyPaths: true));

        Assert.Equal(0, result.SuccessCount);
        Assert.Equal(1, result.FailureCount);
        Assert.Equal(0, recycle.DeleteFileCalls + recycle.DeleteDirectoryCalls);
        Assert.Single(history.Entries);
        Assert.False(history.Entries[0].Success);
    }

    [Fact]
    public async Task ExecuteAsync_DeletesAllowedFileThroughRecycleService()
    {
        var recycle = new FakeRecycleBinService();
        var history = new InMemoryHistoryStore();
        var runStore = new InMemoryRunStore();
        var pathSafety = new PathSafetyService();
        var rebootScheduler = new NoopRebootDeletionScheduler();
        var lockInspector = new NoopLockInspector();
        var executor = new CleanupExecutor(recycle, history, runStore, pathSafety, rebootScheduler, lockInspector);

        var tempFile = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".tmp");
        await File.WriteAllTextAsync(tempFile, "test");

        try
        {
            var risk = pathSafety.Evaluate(tempFile);
            var result = await executor.ExecuteAsync(
                [new CleanupCandidate
                {
                    Category = CleanupCategory.ManualSelection,
                    FullPath = tempFile,
                    IsDirectory = false,
                    SizeBytes = 4,
                    LastModifiedUtc = DateTime.UtcNow,
                    Risk = risk
                }],
                new CleanupExecutionOptions(UseRecycleBin: true, AllowRiskyPaths: true));

            Assert.Equal(1, result.SuccessCount);
            Assert.Equal(4, result.ReclaimedBytes);
            Assert.Equal(1, recycle.DeleteFileCalls);
            Assert.Single(history.Entries);
            Assert.True(history.Entries[0].Success);
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }

    [Fact]
    public async Task ExecuteAsync_BlocksProjectWorkspacePathWhenRiskOverrideDisabled()
    {
        var recycle = new FakeRecycleBinService();
        var history = new InMemoryHistoryStore();
        var runStore = new InMemoryRunStore();
        var pathSafety = new PathSafetyService();
        var rebootScheduler = new NoopRebootDeletionScheduler();
        var lockInspector = new NoopLockInspector();
        var executor = new CleanupExecutor(recycle, history, runStore, pathSafety, rebootScheduler, lockInspector);

        var workspaceRoot = Path.Combine(Path.GetTempPath(), "storage-cleaner-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspaceRoot);
        Directory.CreateDirectory(Path.Combine(workspaceRoot, ".git"));
        var candidatePath = Path.Combine(workspaceRoot, "artifact.log");
        await File.WriteAllTextAsync(candidatePath, "test");

        try
        {
            var risk = pathSafety.Evaluate(candidatePath);
            Assert.True(risk.RequiresExplicitOverride);

            var result = await executor.ExecuteAsync(
                [new CleanupCandidate
                {
                    Category = CleanupCategory.OldLogFiles,
                    FullPath = candidatePath,
                    IsDirectory = false,
                    SizeBytes = 4,
                    LastModifiedUtc = DateTime.UtcNow,
                    Risk = risk
                }],
                new CleanupExecutionOptions(UseRecycleBin: true, AllowRiskyPaths: false));

            Assert.Equal(0, result.SuccessCount);
            Assert.Equal(1, result.FailureCount);
            Assert.Equal(0, recycle.DeleteFileCalls);
            Assert.True(File.Exists(candidatePath));
            Assert.Single(history.Entries);
            Assert.False(history.Entries[0].Success);
        }
        finally
        {
            if (Directory.Exists(workspaceRoot))
            {
                Directory.Delete(workspaceRoot, recursive: true);
            }
        }
    }

    private sealed class FakeRecycleBinService : IRecycleBinService
    {
        public int DeleteFileCalls { get; private set; }

        public int DeleteDirectoryCalls { get; private set; }

        public Task DeleteFileAsync(string filePath, bool useRecycleBin, CancellationToken cancellationToken = default)
        {
            DeleteFileCalls++;
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }

            return Task.CompletedTask;
        }

        public Task DeleteDirectoryAsync(string directoryPath, bool useRecycleBin, CancellationToken cancellationToken = default)
        {
            DeleteDirectoryCalls++;
            if (Directory.Exists(directoryPath))
            {
                Directory.Delete(directoryPath, recursive: true);
            }

            return Task.CompletedTask;
        }
    }

    private sealed class InMemoryHistoryStore : ICleanupHistoryStore
    {
        public List<CleanupHistoryEntry> Entries { get; } = [];

        public Task AppendAsync(CleanupHistoryEntry entry, CancellationToken cancellationToken = default)
        {
            Entries.Add(entry);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<CleanupHistoryEntry>> ReadAsync(int maxEntries = 500, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<CleanupHistoryEntry>>(Entries.ToArray());
        }
    }

    private sealed class InMemoryRunStore : ICleanupRunStore
    {
        private readonly Dictionary<string, CleanupRunManifest> _runs = new(StringComparer.OrdinalIgnoreCase);

        public Task<CleanupRunManifest> StartRunAsync(
            string runId,
            IReadOnlyCollection<CleanupCandidate> candidates,
            CleanupExecutionOptions options,
            CancellationToken cancellationToken = default)
        {
            var manifest = new CleanupRunManifest
            {
                RunId = runId,
                StartedAt = DateTimeOffset.UtcNow,
                IsSimulation = options.SimulationOnly,
                UseRecycleBin = options.UseRecycleBin,
                AllowRiskyPaths = options.AllowRiskyPaths,
                BeforeState = candidates
                    .Select(static candidate => new CleanupRunCandidateState(
                        candidate.FullPath,
                        candidate.IsDirectory,
                        candidate.SizeBytes,
                        candidate.LastModifiedUtc,
                        ExistedBefore: true,
                        candidate.Category,
                        candidate.Risk.Level,
                        candidate.Risk.Reason))
                    .ToArray()
            };

            _runs[runId] = manifest;
            return Task.FromResult(manifest);
        }

        public Task CompleteRunAsync(string runId, IReadOnlyList<CleanupItemResult> results, CancellationToken cancellationToken = default)
        {
            if (_runs.TryGetValue(runId, out var manifest))
            {
                manifest.Results = results.ToArray();
                manifest.CompletedAt = DateTimeOffset.UtcNow;
                manifest.State = CleanupRunState.Completed;
            }

            return Task.CompletedTask;
        }

        public Task AppendCheckpointAsync(
            string runId,
            string message,
            string? fullPath = null,
            bool isError = false,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<CleanupRunManifest>> RecoverInterruptedRunsAsync(
            TimeSpan staleThreshold,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<CleanupRunManifest>>([]);
        }

        public Task<IReadOnlyList<CleanupRunManifest>> ReadRecentRunsAsync(int maxRuns = 100, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<CleanupRunManifest>>(
                _runs.Values.OrderByDescending(static run => run.StartedAt).Take(maxRuns).ToArray());
        }

        public Task<string> ExportRunManifestAsync(string runId, string destinationDirectory, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Path.Combine(destinationDirectory, $"{runId}.json"));
        }
    }

    private sealed class NoopRebootDeletionScheduler : IRebootDeletionScheduler
    {
        public bool TryScheduleDelete(string path, out string? errorMessage)
        {
            errorMessage = null;
            return false;
        }
    }

    private sealed class NoopLockInspector : ILockInspector
    {
        public IReadOnlyList<string> TryGetLockingProcesses(string path)
        {
            return [];
        }
    }
}
