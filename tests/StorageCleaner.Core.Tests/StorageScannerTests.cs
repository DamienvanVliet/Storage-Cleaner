using StorageCleaner.Core;
using StorageCleaner.Core.Models;
using StorageCleaner.Core.Services;

namespace StorageCleaner.Core.Tests;

public sealed class StorageScannerTests
{
    [Fact]
    public async Task ScanAsync_ComputesRecursiveSizesAndCounts()
    {
        var root = CreateTempDirectory();
        try
        {
            var folderA = Directory.CreateDirectory(Path.Combine(root, "A")).FullName;
            var folderB = Directory.CreateDirectory(Path.Combine(folderA, "B")).FullName;
            var folderC = Directory.CreateDirectory(Path.Combine(root, "C")).FullName;

            await File.WriteAllBytesAsync(Path.Combine(folderA, "file-a.bin"), new byte[128]);
            await File.WriteAllBytesAsync(Path.Combine(folderB, "file-b.bin"), new byte[256]);
            await File.WriteAllBytesAsync(Path.Combine(folderC, "file-c.bin"), new byte[64]);

            var scanner = new StorageScanner();
            var result = await scanner.ScanAsync(
                new ScanRequest([root], MaxDegreeOfParallelism: 2, UseCache: false),
                new PauseTokenSource().Token);

            Assert.Equal(448, result.TotalScannedBytes);
            Assert.Equal(3, result.TotalFiles);
            Assert.True(result.TotalFolders >= 4);

            var rootNode = result.Roots.Single();
            Assert.Equal(448, rootNode.SizeBytes);
            Assert.Equal(3, rootNode.FileCount);
            Assert.True(rootNode.FolderCount >= 3);

            var folderANode = result.FlattenedFolders.Single(node => string.Equals(node.FullPath, folderA, StringComparison.OrdinalIgnoreCase));
            Assert.Equal(384, folderANode.SizeBytes);
            Assert.Equal(2, folderANode.FileCount);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ScanAsync_ReturnsIssuesForMissingRootWithoutCrashing()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), "storage-cleaner-missing", Guid.NewGuid().ToString("N"));
        var scanner = new StorageScanner();

        var result = await scanner.ScanAsync(
            new ScanRequest([missingPath], MaxDegreeOfParallelism: 1, UseCache: false),
            new PauseTokenSource().Token);

        Assert.NotEmpty(result.Issues);
        var rootNode = result.Roots.Single();
        Assert.True(rootNode.IsInaccessible);
        Assert.Equal(0, rootNode.SizeBytes);
    }

    [Fact]
    public async Task ScanAsync_SkipsInvalidRootAndScansValidRoot()
    {
        var root = CreateTempDirectory();
        try
        {
            var validFile = Path.Combine(root, "payload.bin");
            await File.WriteAllBytesAsync(validFile, new byte[1024]);

            var scanner = new StorageScanner();
            var result = await scanner.ScanAsync(
                new ScanRequest([root, "C:\\invalid<>root"], MaxDegreeOfParallelism: 2, UseCache: false),
                new PauseTokenSource().Token);

            Assert.Equal(1024, result.TotalScannedBytes);
            Assert.NotEmpty(result.Issues);
            Assert.Contains(result.Issues, issue => issue.Path.Contains("invalid<>root", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(result.Roots, node => string.Equals(node.FullPath.TrimEnd('\\'), root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
            Assert.Contains(result.Roots, node => node.IsInaccessible && node.FullPath.Contains("invalid<>root", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ScanAsync_NtfsFastMode_FallsBackGracefullyAndStillScans()
    {
        var root = CreateTempDirectory();
        try
        {
            var payload = Path.Combine(root, "payload.bin");
            await File.WriteAllBytesAsync(payload, new byte[2048]);

            var scanner = new StorageScanner();
            var result = await scanner.ScanAsync(
                new ScanRequest([root], MaxDegreeOfParallelism: 2, UseCache: false, Mode: ScanMode.NtfsFast),
                new PauseTokenSource().Token);

            Assert.Equal(2048, result.TotalScannedBytes);
            Assert.Equal(1, result.TotalFiles);
            Assert.NotEmpty(result.Roots);
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
        var path = Path.Combine(Path.GetTempPath(), "storage-cleaner-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
