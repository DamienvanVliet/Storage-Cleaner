using System.Collections.Concurrent;
using StorageCleaner.Core.Abstractions;
using StorageCleaner.Core.Models;

namespace StorageCleaner.Core.Services;

public sealed class MemoryScanCache : IScanCache
{
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeSpan _ttl;

    public MemoryScanCache(TimeSpan? ttl = null)
    {
        _ttl = ttl ?? TimeSpan.FromMinutes(10);
    }

    public bool TryGet(IReadOnlyCollection<string> roots, out ScanResult result)
    {
        var key = BuildKey(roots);
        if (_cache.TryGetValue(key, out var entry) &&
            entry.ExpiresAtUtc >= DateTimeOffset.UtcNow &&
            FingerprintsMatch(entry.RootFingerprints, BuildFingerprints(roots)))
        {
            result = entry.Result;
            return true;
        }

        result = null!;
        _cache.TryRemove(key, out _);
        return false;
    }

    public void Store(IReadOnlyCollection<string> roots, ScanResult result)
    {
        var key = BuildKey(roots);
        _cache[key] = new CacheEntry(
            result,
            DateTimeOffset.UtcNow.Add(_ttl),
            BuildFingerprints(roots));
    }

    public void Invalidate(IReadOnlyCollection<string> roots)
    {
        _cache.TryRemove(BuildKey(roots), out _);
    }

    public void InvalidateAll()
    {
        _cache.Clear();
    }

    private static string BuildKey(IReadOnlyCollection<string> roots)
    {
        return string.Join("|", roots
            .Where(static x => !string.IsNullOrWhiteSpace(x))
            .Select(NormalizePath)
            .OrderBy(static x => x, StringComparer.OrdinalIgnoreCase));
    }

    private static string NormalizePath(string path)
    {
        var fullPath = Path.GetFullPath(path.Trim());
        if (Path.GetPathRoot(fullPath)?.Equals(fullPath, StringComparison.OrdinalIgnoreCase) == true)
        {
            return fullPath;
        }

        return fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static IReadOnlyDictionary<string, long> BuildFingerprints(IReadOnlyCollection<string> roots)
    {
        var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            var normalized = NormalizePath(root);
            long stamp;
            try
            {
                stamp = Directory.Exists(normalized)
                    ? Directory.GetLastWriteTimeUtc(normalized).Ticks
                    : 0;
            }
            catch
            {
                stamp = 0;
            }

            result[normalized] = stamp;
        }

        return result;
    }

    private static bool FingerprintsMatch(IReadOnlyDictionary<string, long> previous, IReadOnlyDictionary<string, long> current)
    {
        if (previous.Count != current.Count)
        {
            return false;
        }

        foreach (var pair in previous)
        {
            if (!current.TryGetValue(pair.Key, out var currentValue))
            {
                return false;
            }

            if (pair.Value != currentValue)
            {
                return false;
            }
        }

        return true;
    }

    private sealed record CacheEntry(
        ScanResult Result,
        DateTimeOffset ExpiresAtUtc,
        IReadOnlyDictionary<string, long> RootFingerprints);
}
