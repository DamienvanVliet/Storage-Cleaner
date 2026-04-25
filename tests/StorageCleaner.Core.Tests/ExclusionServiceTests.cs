using StorageCleaner.Core.Models;
using StorageCleaner.Core.Services;

namespace StorageCleaner.Core.Tests;

public sealed class ExclusionServiceTests
{
    [Fact]
    public async Task Match_FindsPathAndExtensionAndCategoryRules()
    {
        var root = Path.Combine(Path.GetTempPath(), "storage-cleaner-exclusions", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var rulesPath = Path.Combine(root, "exclusions.json");

        try
        {
            var service = new FileExclusionService(rulesPath);
            await service.AddRuleAsync(ExclusionRuleKind.PathPrefix, @"C:\Temp\DoNotTouch");
            await service.AddRuleAsync(ExclusionRuleKind.FileExtension, ".iso");
            await service.AddRuleAsync(ExclusionRuleKind.Category, CleanupCategory.BrowserCache.ToString());

            var byPath = service.Match(@"C:\Temp\DoNotTouch\A\B.txt");
            var byExt = service.Match(@"D:\Downloads\movie.ISO");
            var byCategory = service.Match(@"D:\Anything\cache.bin", CleanupCategory.BrowserCache);

            Assert.True(byPath.IsExcluded);
            Assert.True(byExt.IsExcluded);
            Assert.True(byCategory.IsExcluded);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
