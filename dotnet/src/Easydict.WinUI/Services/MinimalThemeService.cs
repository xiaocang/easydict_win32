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
            panel.Background = ThemeResourceService.GetBrush("ApplicationPageBackgroundThemeBrush")
                ?? _transparentBrush;
        }
        else
        {
            panel.Background = _transparentBrush;
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
                : ThemeResourceService.GetResourceOrDefault(
                    "FloatingWindowOuterPadding",
                    themeRoot,
                    new Thickness(0));
        }

        if (minimal)
        {
            surface.BorderThickness = new Thickness(0);
            surface.CornerRadius = new CornerRadius(0);
            surface.Padding = new Thickness(0);
            surface.Background = ThemeResourceService.GetBrush("ApplicationPageBackgroundThemeBrush");
            sourceContainer.Background = ThemeResourceService.GetBrush("CardBackgroundFillColorDefaultBrush");
            sourceContainer.BorderBrush = ThemeResourceService.GetBrush("CardStrokeColorDefaultBrush");
            sourceContainer.BorderThickness = new Thickness(1);
            sourceContainer.CornerRadius = new CornerRadius(0);
            sourceContainer.Padding = new Thickness(8);
            sourceContainer.Margin = new Thickness(0, 0, 0, 4);
            return;
        }

        surface.BorderThickness = ThemeResourceService.GetResourceOrDefault(
            "FloatingWindowBorderThickness",
            themeRoot,
            new Thickness(0));
        surface.CornerRadius = ThemeResourceService.GetResourceOrDefault(
            "FloatingWindowCornerRadius",
            themeRoot,
            new CornerRadius(0));

        var resolvedAppBg = ThemeResourceService.GetBrush("ApplicationPageBackgroundThemeBrush", themeRoot);
#if DEBUG
        System.Diagnostics.Debug.WriteLine(
            $"[Theme] ApplyFloatingChrome " +
            $"AppTheme={SettingsService.Instance.AppTheme} " +
            $"SystemDark={SystemThemeProbe.IsSystemDark()} " +
            $"ThemeRootRequested={themeRoot?.RequestedTheme} " +
            $"ThemeRootActual={themeRoot?.ActualTheme} " +
            $"DictName={ThemeResourceService.GetThemeDictionaryName(themeRoot)} " +
            $"ResolvedAppBg={(resolvedAppBg as SolidColorBrush)?.Color} " +
            $"SurfaceBgBefore={(surface.Background as SolidColorBrush)?.Color}");
#endif
        surface.Background = resolvedAppBg;
        surface.BorderBrush = ThemeResourceService.GetBrush("FloatingWindowBorderBrush", themeRoot);
        surface.Padding = ThemeResourceService.GetResourceOrDefault(
            "FloatingWindowContentPadding",
            themeRoot,
            new Thickness(8));
        sourceContainer.Background = ThemeResourceService.GetBrush("TextControlBackground", themeRoot);
        sourceContainer.BorderBrush = ThemeResourceService.GetBrush("TextControlBorderBrush", themeRoot);
        sourceContainer.BorderThickness = new Thickness(1);
        sourceContainer.CornerRadius = ThemeResourceService.GetResourceOrDefault(
            "FloatingInputCornerRadius",
            themeRoot,
            new CornerRadius(18));
        sourceContainer.Padding = ThemeResourceService.GetResourceOrDefault(
            "FloatingInputPadding",
            themeRoot,
            new Thickness(6, 4, 6, 4));
        sourceContainer.Margin = ThemeResourceService.GetResourceOrDefault(
            "FloatingInputMargin",
            themeRoot,
            new Thickness(0, 0, 0, 3));
    }

    public static void ApplyAccentIconForeground(
        FontIcon icon,
        ProgressRing? progressRing = null,
        FrameworkElement? themeRoot = null)
    {
        var foreground = IsActive
            ? ThemeResourceService.GetBrush("ButtonForeground", themeRoot)
                ?? ThemeResourceService.GetBrush("TextFillColorPrimaryBrush", themeRoot)
                ?? _transparentBrush
            : ThemeResourceService.GetBrush("AccentTextFillColorPrimaryBrush", themeRoot)
                ?? ThemeResourceService.GetBrush("AccentForegroundBrush", themeRoot)
                ?? ThemeResourceService.GetBrush("ButtonForeground", themeRoot)
                ?? _transparentBrush;

        icon.Foreground = foreground;
        if (progressRing is not null)
        {
            progressRing.Foreground = foreground;
        }
    }
}
