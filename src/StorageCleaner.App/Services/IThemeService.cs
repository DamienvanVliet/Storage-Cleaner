using StorageCleaner.App.Models;

namespace StorageCleaner.App.Services;

public interface IThemeService
{
    ThemeMode CurrentTheme { get; }

    void ApplyTheme(ThemeMode themeMode);
}
