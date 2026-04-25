using StorageCleaner.Core.Models;

namespace StorageCleaner.Core.Abstractions;

public interface IWasteAnalysisService
{
    Task<WasteAnalysisResult> AnalyzeAsync(
        IReadOnlyCollection<string> roots,
        int maxNeverAccessedItems = 200,
        IProgress<WasteAnalysisProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
