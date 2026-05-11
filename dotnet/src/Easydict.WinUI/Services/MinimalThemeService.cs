using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Controls;

namespace Easydict.WinUI.Services;

internal static class MinimalThemeService
{
    public const string ThemeName = "Minimal";

    private static readonly SolidColorBrush _transparentBrush =
        new(Microsoft.UI.Colors.Transparent);

    private static ResourceDictionary? _minimalResources;

    public static bool ResourcesApplied =>
        _minimalResources is not null &&
        Application.Current.Resources.MergedDictionaries.Contains(_minimalResources);

    public static bool IsActive =>
        string.Equals(SettingsService.Instance.AppTheme, ThemeName, StringComparison.OrdinalIgnoreCase);

    public static bool IsMinimal(string theme) =>
        string.Equals(theme, ThemeName, StringComparison.OrdinalIgnoreCase);

    public static ElementTheme ToElementTheme(string theme)
    {
        return theme switch
        {
            "Light" => ElementTheme.Light,
            "Dark" => ElementTheme.Dark,
            ThemeName => ElementTheme.Light,
            _ => ElementTheme.Default
        };
    }

    /// <summary>
    /// Resolves "Light"/"Dark" for ThemeDictionaries lookup. Returns null when the
    /// caller should fall back to per-element <see cref="FrameworkElement.ActualTheme"/>
    /// (or the system theme, for non-elemented sites). Minimal mode pins to "Light".
    /// </summary>
    public static string? TryGetExplicitThemeDictionaryName()
    {
        if (IsActive) return "Light";
        return SettingsService.Instance.AppTheme switch
        {
            "Dark" => "Dark",
            "Light" => "Light",
            _ => null
        };
    }

    public static void ApplyResources(bool enabled)
    {
        var dictionaries = Application.Current.Resources.MergedDictionaries;

        if (enabled)
        {
            _minimalResources ??= new ResourceDictionary
            {
                Source = new Uri("ms-appx:///Themes/MinimalResources.xaml")
            };

            if (!dictionaries.Contains(_minimalResources))
            {
                dictionaries.Add(_minimalResources);
            }

            return;
        }

        if (_minimalResources is not null && dictionaries.Contains(_minimalResources))
        {
            dictionaries.Remove(_minimalResources);
        }
    }

    public static void ApplyWindowBackdrop(Window window)
    {
        if (IsActive)
        {
            window.SystemBackdrop = null;
        }
    }

    public static void ApplyFloatingWindowRootBackground(FrameworkElement root)
    {
        if (root is not Panel panel)
        {
            return;
        }

        if (IsActive)
        {
            panel.Background = GetBrush("ApplicationPageBackgroundThemeBrush")
                ?? new SolidColorBrush(Microsoft.UI.Colors.White);
        }
        else
        {
            panel.Background = _transparentBrush;
        }
    }

    public static Brush? GetBrush(string key)
    {
        var resources = Application.Current.Resources;

        if (resources.TryGetValue(key, out var value) && value is Brush brush)
        {
            return brush;
        }

        var merged = resources.MergedDictionaries;
        var themeName = IsActive ? "Light" : null;
        for (int i = merged.Count - 1; i >= 0; i--)
        {
            var dict = merged[i];
            if (dict.TryGetValue(key, out value) && value is Brush mergedBrush)
            {
                return mergedBrush;
            }

            if (themeName is not null &&
                dict.ThemeDictionaries.TryGetValue(themeName, out var themeObj) &&
                themeObj is ResourceDictionary themeDictionary &&
                themeDictionary.TryGetValue(key, out value) &&
                value is Brush themeBrush)
            {
                return themeBrush;
            }
        }

        return null;
    }

    public static void ApplyAccentIconForeground(FontIcon icon, ProgressRing? progressRing = null)
    {
        var foreground = IsActive
            ? GetBrush("ButtonForeground") ?? new SolidColorBrush(Microsoft.UI.Colors.Black)
            : new SolidColorBrush(Microsoft.UI.Colors.White);

        icon.Foreground = foreground;
        if (progressRing is not null)
        {
            progressRing.Foreground = foreground;
        }
    }
}
