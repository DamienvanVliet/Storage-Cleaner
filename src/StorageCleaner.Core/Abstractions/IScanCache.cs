using StorageCleaner.Core.Models;

namespace StorageCleaner.Core.Abstractions;

public interface IScanCache
{
    bool TryGet(IReadOnlyCollection<string> roots, out ScanResult result);

    void Store(IReadOnlyCollection<string> roots, ScanResult result);

    void Invalidate(IReadOnlyCollection<string> roots);

    void InvalidateAll();
}
