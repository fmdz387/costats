using System.Windows;
using WpfApplication = System.Windows.Application;
using Microsoft.Win32;
using costats.Application.Settings;

namespace costats.App.Services;

public sealed class ThemeService : IDisposable
{
    private static readonly Uri LightThemeUri = new("Themes/Theme.Light.xaml", UriKind.Relative);
    private static readonly Uri DarkThemeUri = new("Themes/Theme.Dark.xaml", UriKind.Relative);

    private readonly WpfApplication _application;
    private ResourceDictionary? _activeThemeDictionary;
    private bool _hasAppliedTheme;
    private AppThemeMode _preferredTheme = AppThemeMode.System;
    private AppThemeMode _effectiveTheme = AppThemeMode.Light;

    public ThemeService(WpfApplication application)
    {
        _application = application;
        _activeThemeDictionary = _application.Resources.MergedDictionaries.FirstOrDefault(IsThemeDictionary);
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    public AppThemeMode PreferredTheme => _preferredTheme;

    public AppThemeMode EffectiveTheme => _effectiveTheme;

    public void ApplyTheme(AppThemeMode preferredTheme)
    {
        _application.Dispatcher.VerifyAccess();

        _preferredTheme = preferredTheme;
        var effectiveTheme = ResolveEffectiveTheme(preferredTheme);
        if (_hasAppliedTheme && effectiveTheme == _effectiveTheme)
        {
            return;
        }

        var mergedDictionaries = _application.Resources.MergedDictionaries;
        var themeDictionary = new ResourceDictionary
        {
            Source = effectiveTheme == AppThemeMode.Dark ? DarkThemeUri : LightThemeUri
        };

        if (_activeThemeDictionary is not null)
        {
            var index = mergedDictionaries.IndexOf(_activeThemeDictionary);
            if (index >= 0)
            {
                mergedDictionaries[index] = themeDictionary;
            }
            else
            {
                mergedDictionaries.Add(themeDictionary);
            }
        }
        else
        {
            mergedDictionaries.Add(themeDictionary);
        }

        _activeThemeDictionary = themeDictionary;
        _effectiveTheme = effectiveTheme;
        _hasAppliedTheme = true;
    }

    public void Dispose()
    {
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
    }

    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (_preferredTheme != AppThemeMode.System || _application.Dispatcher.HasShutdownStarted)
        {
            return;
        }

        _ = _application.Dispatcher.BeginInvoke(() => ApplyTheme(AppThemeMode.System));
    }

    private static AppThemeMode ResolveEffectiveTheme(AppThemeMode preferredTheme)
    {
        if (preferredTheme != AppThemeMode.System)
        {
            return preferredTheme;
        }

        try
        {
            using var personalizeKey = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                writable: false);

            var value = personalizeKey?.GetValue("AppsUseLightTheme");
            return value switch
            {
                0 => AppThemeMode.Dark,
                int intValue when intValue == 0 => AppThemeMode.Dark,
                _ => AppThemeMode.Light
            };
        }
        catch
        {
            return AppThemeMode.Light;
        }
    }

    private static bool IsThemeDictionary(ResourceDictionary dictionary)
    {
        var source = dictionary.Source?.OriginalString;
        return string.Equals(source, LightThemeUri.OriginalString, StringComparison.OrdinalIgnoreCase)
            || string.Equals(source, DarkThemeUri.OriginalString, StringComparison.OrdinalIgnoreCase);
    }
}
