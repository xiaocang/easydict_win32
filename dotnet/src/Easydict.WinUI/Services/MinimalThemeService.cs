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

    public static void ApplyRequestedTheme(
        FrameworkElement root,
        ElementTheme theme,
        bool forceResourceRefresh = false)
    {
        if (forceResourceRefresh)
        {
            // Minimal mode pins the app to ElementTheme.Light but also swaps in a
            // resource dictionary. Switching Minimal -> Light does not otherwise
            // change RequestedTheme, so existing ThemeResource bindings can keep
            // resolving to the removed Minimal resources. Pulse the theme first so
            // already-loaded controls requery their resources.
            root.RequestedTheme = theme == ElementTheme.Dark
                ? ElementTheme.Light
                : ElementTheme.Dark;
        }

        root.RequestedTheme = theme;
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
        return TryGetResource<Brush>(key, (FrameworkElement?)null, out var brush)
            ? brush
            : null;
    }

    public static Brush? GetBrush(string key, FrameworkElement? root)
    {
        return TryGetResource<Brush>(key, root, out var brush)
            ? brush
            : null;
    }

    public static T GetResourceOrDefault<T>(string key, FrameworkElement? root, T fallback)
    {
        return TryGetResource<T>(key, root, out var value) ? value : fallback;
    }

    public static bool TryGetResource<T>(string key, FrameworkElement? root, out T resource)
    {
        if (TryGetResourceValue(key, GetThemeDictionaryName(root), out var value) && value is T typed)
        {
            resource = typed;
            return true;
        }

        resource = default!;
        return false;
    }

    /// <summary>
    /// Resolves a resource against an explicit theme dictionary name (e.g. "Dark", "Light",
    /// "HighContrast"), bypassing the active app theme and any element's ActualTheme. Used
    /// when a control needs to render in a context that doesn't match its element theme
    /// (e.g. a Light app being hosted on a Dark surface).
    /// </summary>
    public static bool TryGetResource<T>(string key, string themeName, out T resource)
    {
        if (TryGetResourceValue(key, themeName, out var value) && value is T typed)
        {
            resource = typed;
            return true;
        }

        resource = default!;
        return false;
    }

    private static bool TryGetResourceValue(string key, string? themeName, out object? value)
    {
        var resources = Application.Current.Resources;

        if (themeName is not null)
        {
            if (resources.ThemeDictionaries.TryGetValue(themeName, out var topObj) &&
                topObj is ResourceDictionary topThemeDictionary &&
                topThemeDictionary.TryGetValue(key, out value))
            {
                return true;
            }

            var merged = resources.MergedDictionaries;
            for (var i = merged.Count - 1; i >= 0; i--)
            {
                if (merged[i].ThemeDictionaries.TryGetValue(themeName, out var themeObj) &&
                    themeObj is ResourceDictionary themeDictionary &&
                    themeDictionary.TryGetValue(key, out value))
                {
                    return true;
                }
            }
        }

        for (var i = resources.MergedDictionaries.Count - 1; i >= 0; i--)
        {
            if (resources.MergedDictionaries[i].TryGetValue(key, out value))
            {
                return true;
            }
        }

        return resources.TryGetValue(key, out value);
    }

    private static string? GetThemeDictionaryName(FrameworkElement? root)
    {
        var explicitTheme = TryGetExplicitThemeDictionaryName();
        if (explicitTheme is not null)
        {
            return explicitTheme;
        }

        return root?.ActualTheme switch
        {
            ElementTheme.Dark => "Dark",
            ElementTheme.Light => "Light",
            _ => null
        };
    }

    /// <summary>
    /// Returns true when the system is currently in High Contrast mode. Callers that paint
    /// custom chrome should defer to system colors / theme dictionaries in this case.
    /// </summary>
    public static bool IsHighContrastActive()
    {
        try
        {
            return new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Apply chrome for the floating MiniWindow / FixedWindow surfaces. Both windows share
    /// the same root-padding, surface-clear, and source-input container layout.
    /// </summary>
    public static void ApplyFloatingChrome(
        Grid? rootGrid,
        Border surface,
        Border sourceContainer,
        bool minimal,
        FrameworkElement? themeRoot)
    {
        if (rootGrid is not null)
        {
            rootGrid.Padding = minimal
                ? new Thickness(8)
                : new Thickness(0);
        }

        surface.BorderThickness = new Thickness(0);
        surface.CornerRadius = new CornerRadius(0);

        if (minimal)
        {
            surface.Padding = new Thickness(0);
            surface.Background = GetBrush("ApplicationPageBackgroundThemeBrush");
            sourceContainer.Background = GetBrush("CardBackgroundFillColorDefaultBrush");
            sourceContainer.BorderBrush = GetBrush("CardStrokeColorDefaultBrush");
            sourceContainer.BorderThickness = new Thickness(1);
            sourceContainer.CornerRadius = new CornerRadius(0);
            sourceContainer.Padding = new Thickness(8);
            sourceContainer.Margin = new Thickness(0, 0, 0, 4);
            return;
        }

        surface.Background = GetBrush("ApplicationPageBackgroundThemeBrush", themeRoot);
        surface.Padding = GetResourceOrDefault(
            "FloatingWindowContentPadding",
            themeRoot,
            new Thickness(16));
        sourceContainer.Background = GetBrush("TextControlBackground", themeRoot);
        sourceContainer.BorderBrush = GetBrush("TextControlBorderBrush", themeRoot);
        sourceContainer.BorderThickness = new Thickness(1);
        sourceContainer.CornerRadius = GetResourceOrDefault(
            "ControlCornerRadius",
            themeRoot,
            new CornerRadius(8));
        sourceContainer.Padding = GetResourceOrDefault(
            "FloatingInputPadding",
            themeRoot,
            new Thickness(10, 9, 10, 9));
        sourceContainer.Margin = GetResourceOrDefault(
            "FloatingInputMargin",
            themeRoot,
            new Thickness(0, 2, 0, 6));
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
