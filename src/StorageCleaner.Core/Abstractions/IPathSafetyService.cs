using StorageCleaner.Core.Models;

namespace StorageCleaner.Core.Abstractions;

public interface IPathSafetyService
{
    PathRisk Evaluate(string path);

    IReadOnlyList<string> GetProtectedProfileRoots();

    Task AddProtectedProfileRootAsync(string path, CancellationToken cancellationToken = default);

    Task RemoveProtectedProfileRootAsync(string path, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> DiscoverAndProtectDefaultProfilesAsync(
        bool replaceExisting = false,
        CancellationToken cancellationToken = default);
}
