using System.Text.Json;
using StorageCleaner.Core.Abstractions;
using StorageCleaner.Core.Models;

namespace StorageCleaner.Core.Services;

public sealed class FileRestoreVaultService : IRestoreVaultService
{
    private const long MaxBackupBytes = 20L * 1024L * 1024L * 1024L;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _vaultRoot;
    private readonly string _indexPath;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public FileRestoreVaultService(string? vaultRoot = null)
    {
        _vaultRoot = vaultRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "StorageCleaner",
            "restore-vault");
        _indexPath = Path.Combine(_vaultRoot, "index.json");
    }

    public async Task<RestoreVaultBackupResult> BackupAsync(
        string runId,
        CleanupCandidate candidate,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentNullException.ThrowIfNull(candidate);

        if (string.IsNullOrWhiteSpace(candidate.FullPath))
        {
            return new RestoreVaultBackupResult(false, null, "Candidate path is empty.");
        }

        if (candidate.SizeBytes > MaxBackupBytes)
        {
            return new RestoreVaultBackupResult(false, null, $"Backup skipped because item exceeds {MaxBackupBytes} bytes.");
        }

        var exists = candidate.IsDirectory ? Directory.Exists(candidate.FullPath) : File.Exists(candidate.FullPath);
        if (!exists)
        {
            return new RestoreVaultBackupResult(false, null, "Path no longer exists.");
        }

        var entryId = Guid.NewGuid().ToString("N");
        var targetName = candidate.IsDirectory
            ? Path.GetFileName(candidate.FullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            : Path.GetFileName(candidate.FullPath);
        if (string.IsNullOrWhiteSpace(targetName))
        {
            targetName = "item";
        }

        var entryDirectory = Path.Combine(_vaultRoot, runId, entryId);
        var vaultPath = Path.Combine(entryDirectory, targetName);

        try
        {
            Directory.CreateDirectory(entryDirectory);

            if (candidate.IsDirectory)
            {
                CopyDirectory(candidate.FullPath, vaultPath, cancellationToken);
            }
            else
            {
                await using var source = File.Open(candidate.FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                await using var destination = File.Create(vaultPath);
                await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PathTooLongException)
        {
            return new RestoreVaultBackupResult(false, null, ex.Message);
        }

        var entry = new RestoreVaultEntry
        {
            EntryId = entryId,
            RunId = runId,
            OriginalPath = candidate.FullPath,
            VaultPath = vaultPath,
            IsDirectory = candidate.IsDirectory,
            SizeBytes = candidate.SizeBytes,
            Category = candidate.Category,
            BackedUpAt = DateTimeOffset.UtcNow,
            RestoredAt = null,
            Purged = false
        };

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var entries = await LoadIndexCoreAsync(cancellationToken).ConfigureAwait(false);
            entries.Add(entry);
            await SaveIndexCoreAsync(entries, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }

        return new RestoreVaultBackupResult(true, entry.EntryId, null);
    }

    public async Task<IReadOnlyList<RestoreVaultEntry>> ReadEntriesAsync(
        int maxEntries = 1000,
        CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var entries = await LoadIndexCoreAsync(cancellationToken).ConfigureAwait(false);
            return entries
                .OrderByDescending(static entry => entry.BackedUpAt)
                .Take(Math.Max(1, maxEntries))
                .ToArray();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<(bool Success, string Message)> RestoreAsync(
        string entryId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(entryId))
        {
            return (false, "Entry id is required.");
        }

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var entries = await LoadIndexCoreAsync(cancellationToken).ConfigureAwait(false);
            var entry = entries.FirstOrDefault(item => string.Equals(item.EntryId, entryId, StringComparison.OrdinalIgnoreCase));
            if (entry is null)
            {
                return (false, "Restore entry was not found.");
            }

            if (!entry.CanRestore)
            {
                return (false, "Restore entry is no longer restorable.");
            }

            var parent = Path.GetDirectoryName(entry.OriginalPath);
            if (!string.IsNullOrWhiteSpace(parent))
            {
                Directory.CreateDirectory(parent);
            }

            if (entry.IsDirectory)
            {
                if (Directory.Exists(entry.OriginalPath) || File.Exists(entry.OriginalPath))
                {
                    return (false, "Restore target already exists.");
                }

                CopyDirectory(entry.VaultPath, entry.OriginalPath, cancellationToken);
            }
            else
            {
                if (File.Exists(entry.OriginalPath) || Directory.Exists(entry.OriginalPath))
                {
                    return (false, "Restore target already exists.");
                }

                await using var source = File.Open(entry.VaultPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                await using var destination = File.Create(entry.OriginalPath);
                await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
            }

            entry.RestoredAt = DateTimeOffset.UtcNow;
            await SaveIndexCoreAsync(entries, cancellationToken).ConfigureAwait(false);
            return (true, $"Restored to {entry.OriginalPath}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PathTooLongException)
        {
            return (false, ex.Message);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<(bool Success, string Message)> PurgeAsync(
        string entryId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(entryId))
        {
            return (false, "Entry id is required.");
        }

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var entries = await LoadIndexCoreAsync(cancellationToken).ConfigureAwait(false);
            var entry = entries.FirstOrDefault(item => string.Equals(item.EntryId, entryId, StringComparison.OrdinalIgnoreCase));
            if (entry is null)
            {
                return (false, "Restore entry was not found.");
            }

            try
            {
                if (File.Exists(entry.VaultPath))
                {
                    File.Delete(entry.VaultPath);
                }
                else if (Directory.Exists(entry.VaultPath))
                {
                    Directory.Delete(entry.VaultPath, recursive: true);
                }
            }
            catch
            {
                // Best-effort purge. Continue to mark state to avoid dangling restore actions.
            }

            entry.Purged = true;
            await SaveIndexCoreAsync(entries, cancellationToken).ConfigureAwait(false);
            return (true, "Vault entry purged.");
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<List<RestoreVaultEntry>> LoadIndexCoreAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_indexPath))
        {
            return [];
        }

        try
        {
            await using var stream = File.OpenRead(_indexPath);
            var entries = await JsonSerializer.DeserializeAsync<List<RestoreVaultEntry>>(stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            return entries ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
        catch (IOException)
        {
            return [];
        }
    }

    private async Task SaveIndexCoreAsync(IReadOnlyList<RestoreVaultEntry> entries, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_vaultRoot);
        await using var stream = File.Create(_indexPath);
        await JsonSerializer.SerializeAsync(stream, entries, JsonOptions, cancellationToken).ConfigureAwait(false);
    }

    private static void CopyDirectory(string sourceDirectory, string destinationDirectory, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destinationDirectory);

        foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var destination = Path.Combine(destinationDirectory, Path.GetFileName(file));
            using var sourceStream = File.Open(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var destinationStream = File.Create(destination);
            sourceStream.CopyTo(destinationStream);
        }

        foreach (var directory in Directory.EnumerateDirectories(sourceDirectory, "*", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var destination = Path.Combine(destinationDirectory, Path.GetFileName(directory));
            CopyDirectory(directory, destination, cancellationToken);
        }
    }
}
