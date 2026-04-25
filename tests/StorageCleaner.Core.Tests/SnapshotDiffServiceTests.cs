using StorageCleaner.Core.Abstractions;
using StorageCleaner.Core.Models;
using StorageCleaner.Core.Services;

namespace StorageCleaner.Core.Tests;

public sealed class SnapshotDiffServiceTests
{
    [Fact]
    public async Task CompareAsync_ComputesDeltaAndCategoryImpact()
    {
        var now = DateTimeOffset.UtcNow;
        var before = new StorageSnapshot
        {
            SnapshotId = "before",
            Label = "Before",
            CreatedAt = now,
            Roots = ["C:\\"],
            TotalBytes = 1000,
            TotalFiles = 10,
            TotalFolders = 5,
            Folders =
            [
                new StorageSnapshotFolderEntry("C:\\A", 700, 7, 2, DateTime.UtcNow),
                new StorageSnapshotFolderEntry("C:\\B", 300, 3, 1, DateTime.UtcNow)
            ]
        };

        var after = new StorageSnapshot
        {
            SnapshotId = "after",
            Label = "After",
            CreatedAt = now.AddMinutes(2),
            Roots = ["C:\\"],
            TotalBytes = 700,
            TotalFiles = 8,
            TotalFolders = 5,
            Folders =
            [
                new StorageSnapshotFolderEntry("C:\\A", 500, 5, 2, DateTime.UtcNow),
                new StorageSnapshotFolderEntry("C:\\B", 200, 3, 1, DateTime.UtcNow)
            ]
        };

        var history = new InMemoryHistoryStore
        {
            Entries =
            [
                new CleanupHistoryEntry(
                    "run-1",
                    now.AddMinutes(1),
                    "C:\\A\\x.tmp",
                    Success: true,
                    ReclaimedBytes: 300,
                    ErrorMessage: null,
                    CleanupCategory.UserTemp,
                    IsDirectory: false,
                    SentToRecycleBin: true,
                    IsSimulation: false,
                    QueuedForReboot: false,
                    LockDetails: null)
            ]
        };

        var sut = new SnapshotDiffService(history);
        var diff = await sut.CompareAsync(before, after);

        Assert.Equal(-300, diff.DeltaBytes);
        Assert.Equal(-2, diff.DeltaFiles);
        Assert.Single(diff.CategoryChanges);
        Assert.Equal(CleanupCategory.UserTemp, diff.CategoryChanges[0].Category);
        Assert.Equal(300, diff.CategoryChanges[0].ReclaimedBytes);
        Assert.NotEmpty(diff.TopFolderChanges);
        Assert.Single(diff.Actions);
    }

    private sealed class InMemoryHistoryStore : ICleanupHistoryStore
    {
        public List<CleanupHistoryEntry> Entries { get; set; } = [];

        public Task AppendAsync(CleanupHistoryEntry entry, CancellationToken cancellationToken = default)
        {
            Entries.Add(entry);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<CleanupHistoryEntry>> ReadAsync(int maxEntries = 500, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<CleanupHistoryEntry>>(
                Entries.OrderByDescending(static entry => entry.Timestamp).Take(maxEntries).ToArray());
        }
    }
}
