using StorageCleaner.Core.Abstractions;
using StorageCleaner.Core.Models;

namespace StorageCleaner.Core.Services;

public sealed class SnapshotDiffService : ISnapshotDiffService
{
    private readonly ICleanupHistoryStore _historyStore;

    public SnapshotDiffService(ICleanupHistoryStore historyStore)
    {
        _historyStore = historyStore;
    }

    public async Task<SnapshotDiffResult> CompareAsync(
        StorageSnapshot before,
        StorageSnapshot after,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        var beforeMap = before.Folders.ToDictionary(
            static entry => entry.FullPath,
            StringComparer.OrdinalIgnoreCase);

        var afterMap = after.Folders.ToDictionary(
            static entry => entry.FullPath,
            StringComparer.OrdinalIgnoreCase);

        var allPaths = new HashSet<string>(beforeMap.Keys, StringComparer.OrdinalIgnoreCase);
        allPaths.UnionWith(afterMap.Keys);

        var folderChanges = new List<SnapshotDiffFolderChange>(Math.Min(4096, allPaths.Count));
        foreach (var path in allPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            beforeMap.TryGetValue(path, out var beforeEntry);
            afterMap.TryGetValue(path, out var afterEntry);

            var beforeBytes = beforeEntry?.SizeBytes ?? 0;
            var afterBytes = afterEntry?.SizeBytes ?? 0;
            var deltaBytes = afterBytes - beforeBytes;

            var beforeFiles = beforeEntry?.FileCount ?? 0;
            var afterFiles = afterEntry?.FileCount ?? 0;
            var deltaFiles = afterFiles - beforeFiles;

            if (deltaBytes == 0 && deltaFiles == 0)
            {
                continue;
            }

            folderChanges.Add(new SnapshotDiffFolderChange(
                path,
                beforeBytes,
                afterBytes,
                deltaBytes,
                beforeFiles,
                afterFiles,
                deltaFiles));
        }

        var from = before.CreatedAt <= after.CreatedAt ? before.CreatedAt : after.CreatedAt;
        var to = before.CreatedAt <= after.CreatedAt ? after.CreatedAt : before.CreatedAt;

        var history = await _historyStore.ReadAsync(maxEntries: 20_000, cancellationToken).ConfigureAwait(false);
        var relevantActions = history
            .Where(entry => entry.Timestamp >= from && entry.Timestamp <= to)
            .OrderByDescending(static entry => entry.Timestamp)
            .ThenBy(static entry => entry.FullPath, StringComparer.OrdinalIgnoreCase)
            .Take(3_000)
            .ToArray();

        var actions = relevantActions
            .Select(static entry => new SnapshotDiffAction(
                entry.RunId,
                entry.Timestamp,
                entry.Category,
                entry.FullPath,
                entry.ReclaimedBytes,
                entry.Success,
                entry.ErrorMessage,
                entry.IsSimulation,
                entry.QueuedForReboot))
            .ToArray();

        var categories = relevantActions
            .GroupBy(static entry => entry.Category)
            .Select(group =>
            {
                var rows = group.ToArray();
                return new SnapshotDiffCategoryChange(
                    group.Key,
                    rows.Where(static row => row.Success).Sum(static row => row.ReclaimedBytes),
                    rows.Count(static row => row.Success),
                    rows.Count(static row => !row.Success));
            })
            .OrderByDescending(static row => row.ReclaimedBytes)
            .ThenBy(static row => row.Category)
            .ToArray();

        return new SnapshotDiffResult
        {
            Before = before,
            After = after,
            DeltaBytes = after.TotalBytes - before.TotalBytes,
            DeltaFiles = after.TotalFiles - before.TotalFiles,
            DeltaFolders = after.TotalFolders - before.TotalFolders,
            TopFolderChanges = folderChanges
                .OrderByDescending(static row => Math.Abs(row.DeltaBytes))
                .ThenBy(static row => row.FullPath, StringComparer.OrdinalIgnoreCase)
                .Take(150)
                .ToArray(),
            CategoryChanges = categories,
            Actions = actions
        };
    }
}
