using StorageCleaner.Core;
using StorageCleaner.Core.Models;
using StorageCleaner.Core.Services;

namespace StorageCleaner.Core.Tests;

public sealed class StorageAnalyticsServiceTests
{
    [Fact]
    public async Task AnalyzeAsync_BuildsCategoryBucketsAndTreemapTiles()
    {
        var root = CreateTempDirectory();
        try
        {
            var media = Directory.CreateDirectory(Path.Combine(root, "Media")).FullName;
            var docs = Directory.CreateDirectory(Path.Combine(root, "Docs")).FullName;
            var downloads = Directory.CreateDirectory(Path.Combine(root, "Downloads")).FullName;
            var logs = Directory.CreateDirectory(Path.Combine(root, "Logs")).FullName;

            await File.WriteAllBytesAsync(Path.Combine(media, "clip.mp4"), new byte[1024]);
            await File.WriteAllBytesAsync(Path.Combine(media, "image.jpg"), new byte[512]);
            await File.WriteAllBytesAsync(Path.Combine(docs, "manual.pdf"), new byte[256]);
            await File.WriteAllBytesAsync(Path.Combine(downloads, "archive.zip"), new byte[2048]);
            await File.WriteAllBytesAsync(Path.Combine(logs, "trace.log"), new byte[128]);

            var scanner = new StorageScanner();
            var scanResult = await scanner.ScanAsync(
                new ScanRequest([root], MaxDegreeOfParallelism: 2, UseCache: false),
                new PauseTokenSource().Token);

            var service = new StorageAnalyticsService();
            var result = await service.AnalyzeAsync([root], scanResult, maxTreemapTiles: 50);

            Assert.True(result.TotalFiles >= 5);
            Assert.True(result.TotalBytes >= 3968);

            Assert.Contains(result.Categories, bucket => string.Equals(bucket.Category, "Videos", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(result.Categories, bucket => string.Equals(bucket.Category, "Images", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(result.Categories, bucket => string.Equals(bucket.Category, "Documents", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(result.Categories, bucket => string.Equals(bucket.Category, "Downloads", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(result.Categories, bucket => string.Equals(bucket.Category, "Logs", StringComparison.OrdinalIgnoreCase));

            Assert.NotEmpty(result.TreemapTiles);
            Assert.Contains(result.TreemapTiles, tile => string.Equals(tile.FullPath.TrimEnd('\\'), root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "storage-cleaner-analytics-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
