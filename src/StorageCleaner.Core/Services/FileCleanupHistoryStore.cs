using System.Text.Json;
using StorageCleaner.Core.Abstractions;
using StorageCleaner.Core.Models;

namespace StorageCleaner.Core.Services;

public sealed class FileCleanupHistoryStore : ICleanupHistoryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    private readonly string _historyPath;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public FileCleanupHistoryStore(string? historyPath = null)
    {
        _historyPath = historyPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "StorageCleaner",
            "cleanup-history.jsonl");
    }

    public async Task AppendAsync(CleanupHistoryEntry entry, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(_historyPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var line = JsonSerializer.Serialize(entry, JsonOptions) + Environment.NewLine;

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await File.AppendAllTextAsync(_historyPath, line, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<CleanupHistoryEntry>> ReadAsync(int maxEntries = 500, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_historyPath))
        {
            return [];
        }

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var lines = await File.ReadAllLinesAsync(_historyPath, cancellationToken).ConfigureAwait(false);
            var entries = new List<CleanupHistoryEntry>(Math.Min(lines.Length, maxEntries));

            for (var i = lines.Length - 1; i >= 0 && entries.Count < maxEntries; i--)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (string.IsNullOrWhiteSpace(lines[i]))
                {
                    continue;
                }

                try
                {
                    var entry = JsonSerializer.Deserialize<CleanupHistoryEntry>(lines[i], JsonOptions);
                    if (entry is not null)
                    {
                        entries.Add(entry);
                    }
                }
                catch (JsonException)
                {
                    continue;
                }
            }

            return entries;
        }
        finally
        {
            _lock.Release();
        }
    }
}
