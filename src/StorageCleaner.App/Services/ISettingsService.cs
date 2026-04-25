using StorageCleaner.App.Models;

namespace StorageCleaner.App.Services;

public interface ISettingsService
{
    AppSettings Current { get; }

    Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default);
}
