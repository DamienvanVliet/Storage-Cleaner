using StorageCleaner.Core.Models;

namespace StorageCleaner.Core.Abstractions;

public interface IAppUninstallerService
{
    Task<IReadOnlyList<InstalledAppInfo>> GetInstalledAppsAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AppLeftoverCandidate>> DetectLeftoversAsync(
        InstalledAppInfo app,
        CancellationToken cancellationToken = default);

    Task<UninstallLaunchResult> LaunchUninstallAsync(
        InstalledAppInfo app,
        CancellationToken cancellationToken = default);
}
