using System.Text.Json;
using StorageCleaner.Core.Abstractions;
using StorageCleaner.Core.Models;

namespace StorageCleaner.Core.Services;

public sealed class FileStorageSnapshotStore : IStorageSnapshotStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    private readonly string _snapshotsDirectory;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public FileStorageSnapshotStore(string? snapshotsDirectory = null)
    {
        _snapshotsDirectory = snapshotsDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "StorageCleaner",
            "snapshots");
    }

    public async Task<StorageSnapshot> CreateAsync(
        ScanResult scanResult,
        string label,
        IReadOnlyCollection<string> roots,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scanResult);
        if (roots is null || roots.Count == 0)
        {
            throw new ArgumentException("Snapshot roots are required.", nameof(roots));
        }

        var snapshotId = $"{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}";
        var snapshot = new StorageSnapshot
        {
            SnapshotId = snapshotId,
            Label = string.IsNullOrWhiteSpace(label) ? "Snapshot" : label.Trim(),
            CreatedAt = DateTimeOffset.UtcNow,
            Roots = roots.Where(static root => !string.IsNullOrWhiteSpace(root))
                .Select(static root => root.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(static root => root, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            TotalBytes = scanResult.TotalScannedBytes,
            TotalFiles = scanResult.TotalFiles,
            TotalFolders = scanResult.TotalFolders,
            Folders = scanResult.FlattenedFolders
                .Select(static folder => new StorageSnapshotFolderEntry(
                    folder.FullPath,
                    folder.SizeBytes,
                    folder.FileCount,
                    folder.FolderCount,
                    folder.LastModifiedUtc))
                .ToArray()
        };

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(_snapshotsDirectory);
            var filePath = GetSnapshotPath(snapshotId);
            await using var stream = File.Create(filePath);
            await JsonSerializer.SerializeAsync(stream, snapshot, JsonOptions, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }

        return snapshot;
    }

    public async Task<IReadOnlyList<StorageSnapshot>> ReadRecentAsync(
        int maxSnapshots = 100,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_snapshotsDirectory);
        var files = Directory.EnumerateFiles(_snapshotsDirectory, "*.json", SearchOption.TopDirectoryOnly)
            .OrderByDescending(static path => path, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(1, maxSnapshots))
            .ToArray();

        var result = new List<StorageSnapshot>(files.Length);
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await using var stream = File.OpenRead(file);
                var snapshot = await JsonSerializer.DeserializeAsync<StorageSnapshot>(stream, JsonOptions, cancellationToken)
                    .ConfigureAwait(false);
                if (snapshot is not null)
                {
                    result.Add(snapshot);
                }
            }
            catch (IOException)
            {
                continue;
            }
            catch (JsonException)
            {
                continue;
            }
        }

        return result
            .OrderByDescending(static snapshot => snapshot.CreatedAt)
            .Take(Math.Max(1, maxSnapshots))
            .ToArray();
    }

    public async Task<StorageSnapshot?> ReadByIdAsync(
        string snapshotId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(snapshotId))
        {
            return null;
        }

        var filePath = GetSnapshotPath(snapshotId.Trim());
        if (!File.Exists(filePath))
        {
            return null;
        }

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var stream = File.OpenRead(filePath);
            return await JsonSerializer.DeserializeAsync<StorageSnapshot>(stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (IOException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
        finally
        {
            _lock.Release();
        }
    }

    private string GetSnapshotPath(string snapshotId)
    {
        return Path.Combine(_snapshotsDirectory, $"{snapshotId}.json");
    }
}
