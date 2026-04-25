using System.Windows;
using StorageCleaner.App.Models;

namespace StorageCleaner.App.Services;

public sealed class ThemeService : IThemeService
{
    private ResourceDictionary? _currentThemeDictionary;

    public ThemeMode CurrentTheme { get; private set; } = ThemeMode.Dark;

    public void ApplyTheme(ThemeMode themeMode)
    {
        var effectiveTheme = ThemeMode.Dark;
        var app = Application.Current;
        if (app is null)
        {
            return;
        }

        var themePath = effectiveTheme == ThemeMode.Dark ? "Themes/Theme.Dark.xaml" : "Themes/Theme.Light.xaml";
        var dictionary = new ResourceDictionary
        {
            Source = new Uri(themePath, UriKind.Relative)
        };

        if (_currentThemeDictionary is not null)
        {
            app.Resources.MergedDictionaries.Remove(_currentThemeDictionary);
        }

        app.Resources.MergedDictionaries.Add(dictionary);
        _currentThemeDictionary = dictionary;
        CurrentTheme = effectiveTheme;
    }
}
