using System.Runtime.InteropServices;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
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
        if (ThemeResourceService.IsHighContrastActive() && !IsMinimal(theme))
        {
            return ElementTheme.Default;
        }

        return theme switch
        {
            "Light" => ElementTheme.Light,
            "Dark" => ElementTheme.Dark,
            ThemeName => ElementTheme.Light,
            _ => ResolveSystemElementTheme()
        };
    }

    private static ElementTheme ResolveSystemElementTheme()
    {
        if (ThemeResourceService.IsHighContrastActive())
        {
            return ElementTheme.Default;
        }

        return SystemThemeProbe.IsSystemDark() switch
        {
            true => ElementTheme.Dark,
            false => ElementTheme.Light,
            null => ElementTheme.Default
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

    public static bool ApplyWindowBackdrop(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        if (IsActive || ThemeResourceService.IsHighContrastActive())
        {
            window.SystemBackdrop = null;
            return false;
        }

        try
        {
            window.SystemBackdrop = new MicaBackdrop();
            return true;
        }
        catch (COMException)
        {
            window.SystemBackdrop = null;
            return false;
        }
        catch (InvalidOperationException)
        {
            window.SystemBackdrop = null;
            return false;
        }
    }

    public static void ApplyWindowRootBackground(FrameworkElement root, bool usesMica)
    {
        ArgumentNullException.ThrowIfNull(root);
        var background = usesMica
            ? _transparentBrush
            : ThemeResourceService.GetBrush("EasydictWindowSurfaceBrush", root)
                ?? _transparentBrush;

        switch (root)
        {
            case Panel panel:
                panel.Background = background;
                break;
            case Control control:
                control.Background = background;
                break;
        }
    }

    public static void ApplyAccentIconForeground(
        FontIcon icon,
        ProgressRing? progressRing = null,
        FrameworkElement? themeRoot = null)
    {
        var foreground = ThemeResourceService.GetBrush("EasydictAccentForegroundBrush", themeRoot)
            ?? _transparentBrush;

        icon.Foreground = foreground;
        if (progressRing is not null)
        {
            progressRing.Foreground = foreground;
        }
    }
}
