using StorageCleaner.Core.Models;
using StorageCleaner.Core.Services;

namespace StorageCleaner.Core.Tests;

public sealed class PathSafetyServiceTests
{
    [Fact]
    public async Task Evaluate_FlagsWorkspaceMarkerAsHighRiskEvenInTemp()
    {
        var root = CreateTempDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, ".git"));
            var candidatePath = Path.Combine(root, "build.log");
            await File.WriteAllTextAsync(candidatePath, "workspace test");

            var service = new PathSafetyService();
            var risk = service.Evaluate(candidatePath);

            Assert.Equal(PathRiskLevel.HighRisk, risk.Level);
            Assert.Contains(".git", risk.Reason, StringComparison.OrdinalIgnoreCase);
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
    public async Task Evaluate_RecognizesUserTempAsSafeCleanupPath()
    {
        var filePath = Path.Combine(Path.GetTempPath(), $"storage-cleaner-safety-{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(filePath, "tmp");

            var service = new PathSafetyService();
            var risk = service.Evaluate(filePath);

            Assert.Equal(PathRiskLevel.Safe, risk.Level);
            Assert.Contains("Temp", risk.Reason, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
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
