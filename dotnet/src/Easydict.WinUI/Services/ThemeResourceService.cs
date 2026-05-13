using Microsoft.UI.Xaml.Media;

namespace Easydict.WinUI.Services;

/// <summary>
/// Single entry point for code-created UI to resolve semantic theme resources.
/// XAML uses ThemeResource automatically; code-behind must pass the same themed root so
/// controls created before they enter the visual tree do not fall back to Light resources.
/// </summary>
internal static class ThemeResourceService
{
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

    public static Windows.UI.Color? GetColor(string key, FrameworkElement? root)
    {
        return TryGetResource<Windows.UI.Color>(key, root, out var color)
            ? color
            : null;
    }

    public static T GetResourceOrDefault<T>(string key, FrameworkElement? root, T fallback)
    {
        return TryGetResource<T>(key, root, out var value) ? value : fallback;
    }

    public static bool TryGetResource<T>(string key, FrameworkElement? root, out T resource)
    {
        if (TryGetResourceValue(key, GetThemeDictionaryName(root), root, out var value) && value is T typed)
        {
            resource = typed;
            return true;
        }

        resource = default!;
        return false;
    }

    public static bool TryGetResource<T>(string key, string themeName, out T resource)
    {
        if (TryGetResourceValue(key, themeName, null, out var value) && value is T typed)
        {
            resource = typed;
            return true;
        }

        resource = default!;
        return false;
    }

    public static string? GetExplicitThemeDictionaryName()
    {
        var theme = SettingsService.Instance.AppTheme;
        if (string.Equals(theme, "Dark", StringComparison.OrdinalIgnoreCase))
        {
            return "Dark";
        }

        if (string.Equals(theme, "Light", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(theme, MinimalThemeService.ThemeName, StringComparison.OrdinalIgnoreCase))
        {
            return "Light";
        }

        return null;
    }

    public static string? GetThemeDictionaryName(FrameworkElement? root)
    {
        if (IsHighContrastActive())
        {
            return "HighContrast";
        }

        // Explicit Light/Dark/Minimal is the user's forced app theme. Cached
        // navigation pages can briefly retain an older RequestedTheme while
        // code-behind reassigns brushes, so explicit settings must win here.
        var explicitTheme = GetExplicitThemeDictionaryName();
        if (explicitTheme is not null)
        {
            return explicitTheme;
        }

        // System theme has no explicit dictionary choice. Prefer the theme
        // already requested on the visual root before consulting the registry so
        // code-assigned brushes stay aligned with ThemeResource-bound controls.
        var requestedTheme = GetRequestedThemeDictionaryName(root);
        if (requestedTheme is not null)
        {
            return requestedTheme;
        }

        // System theme has no explicit dictionary choice. Fall back to the XAML
        // ActualTheme before consulting the registry so code-assigned brushes stay
        // aligned with ThemeResource-bound controls.
        var rootTheme = root?.ActualTheme switch
        {
            ElementTheme.Dark => "Dark",
            ElementTheme.Light => "Light",
            _ => null
        };
        if (rootTheme is not null)
        {
            return rootTheme;
        }

        var xamlRootTheme = (root?.XamlRoot?.Content as FrameworkElement)?.ActualTheme switch
        {
            ElementTheme.Dark => "Dark",
            ElementTheme.Light => "Light",
            _ => null
        };
        if (xamlRootTheme is not null)
        {
            return xamlRootTheme;
        }

        var systemTheme = GetSystemThemeDictionaryName();
        if (systemTheme is not null)
        {
            return systemTheme;
        }

        return null;
    }

    private static string? GetRequestedThemeDictionaryName(FrameworkElement? root)
    {
        DependencyObject? current = root;
        while (current is not null)
        {
            if (current is FrameworkElement { RequestedTheme: ElementTheme.Dark })
            {
                return "Dark";
            }

            if (current is FrameworkElement { RequestedTheme: ElementTheme.Light })
            {
                return "Light";
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return (root?.XamlRoot?.Content as FrameworkElement)?.RequestedTheme switch
        {
            ElementTheme.Dark => "Dark",
            ElementTheme.Light => "Light",
            _ => null
        };
    }

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

    private static string? GetSystemThemeDictionaryName()
    {
        return SystemThemeProbe.IsSystemDark() switch
        {
            true => "Dark",
            false => "Light",
            null => null
        };
    }

    private static bool TryGetResourceValue(
        string key,
        string? themeName,
        FrameworkElement? root,
        out object? value)
    {
        // XAML resource lookup walks the element tree before falling back to
        // application resources, so directly scoped overrides win. Keep merged
        // framework dictionaries out of this first pass: several Easydict keys
        // intentionally shadow WinUI resource names, and framework Light values
        // must not beat the app's themed Colors.xaml dictionary in dark mode.
        if (TryGetElementResourceValue(root, key, themeName, out value))
        {
            return true;
        }

        if (themeName is not null &&
            TryGetThemeDictionaryResourceValue(Application.Current.Resources, key, themeName, out value))
        {
            return true;
        }

        return TryGetDictionaryResourceValue(Application.Current.Resources, key, themeName, out value);
    }

    private static bool TryGetThemeDictionaryResourceValue(
        ResourceDictionary resources,
        string key,
        string themeName,
        out object? value)
    {
        var merged = resources.MergedDictionaries;
        for (var i = merged.Count - 1; i >= 0; i--)
        {
            if (TryGetThemeDictionaryResourceValue(merged[i], key, themeName, out value))
            {
                return true;
            }
        }

        if (resources.ThemeDictionaries.TryGetValue(themeName, out var themeObj) &&
            themeObj is ResourceDictionary themeDictionary &&
            TryGetDictionaryResourceValue(themeDictionary, key, null, out value))
        {
            return true;
        }

        value = null;
        return false;
    }

    private static bool TryGetElementResourceValue(
        FrameworkElement? root,
        string key,
        string? themeName,
        out object? value)
    {
        DependencyObject? current = root;
        while (current is not null)
        {
            if (current is FrameworkElement element &&
                TryGetDirectElementResourceValue(element.Resources, key, themeName, out value))
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        value = null;
        return false;
    }

    private static bool TryGetDirectElementResourceValue(
        ResourceDictionary resources,
        string key,
        string? themeName,
        out object? value)
    {
        if (themeName is not null &&
            resources.ThemeDictionaries.TryGetValue(themeName, out var themeObj) &&
            themeObj is ResourceDictionary themeDictionary &&
            TryGetDictionaryResourceValue(themeDictionary, key, null, out value))
        {
            return true;
        }

        return TryGetDirectResourceValue(resources, key, out value);
    }

    private static bool TryGetDictionaryResourceValue(
        ResourceDictionary resources,
        string key,
        string? themeName,
        out object? value)
    {
        if (themeName is not null)
        {
            if (resources.ThemeDictionaries.TryGetValue(themeName, out var topObj) &&
                topObj is ResourceDictionary topThemeDictionary &&
                TryGetDictionaryResourceValue(topThemeDictionary, key, null, out value))
            {
                return true;
            }

            var merged = resources.MergedDictionaries;
            for (var i = merged.Count - 1; i >= 0; i--)
            {
                if (merged[i].ThemeDictionaries.TryGetValue(themeName, out var themeObj) &&
                    themeObj is ResourceDictionary themeDictionary &&
                    TryGetDictionaryResourceValue(themeDictionary, key, null, out value))
                {
                    return true;
                }
            }
        }

        for (var i = resources.MergedDictionaries.Count - 1; i >= 0; i--)
        {
            if (TryGetDictionaryResourceValue(resources.MergedDictionaries[i], key, themeName, out value))
            {
                return true;
            }
        }

        return TryGetDirectResourceValue(resources, key, out value);
    }

    private static bool TryGetDirectResourceValue(
        ResourceDictionary resources,
        string key,
        out object? value)
    {
        foreach (var resourceKey in resources.Keys)
        {
            if (!string.Equals(resourceKey as string, key, StringComparison.Ordinal))
            {
                continue;
            }

            value = resources[resourceKey];
            return true;
        }

        value = null;
        return false;
    }
}
