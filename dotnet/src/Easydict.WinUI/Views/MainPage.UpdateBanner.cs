#if PORTABLE_UPDATE_CHECK
using System.Diagnostics;
using Easydict.WinUI.Services;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace Easydict.WinUI.Views;

public partial class MainPage
{
    private readonly GitHubReleaseUpdateService _githubReleaseUpdateService = new();
    private CancellationTokenSource? _updateCheckCts;
    private Button? _updateBannerButton;
    private TextBlock? _updateBannerText;
    private GitHubReleaseUpdate? _availableRelease;
    private bool _updateCheckStarted;
#if DEBUG
    private const bool IncludeLatestStableReleaseInDebug = true;
#else
    private const bool IncludeLatestStableReleaseInDebug = false;
#endif


    private void InitializePortableUpdateBanner()
    {
        foreach (var child in RootGrid.Children)
        {
            if (child is FrameworkElement element)
            {
                Grid.SetRow(element, Grid.GetRow(element) + 1);
            }
        }

        RootGrid.RowDefinitions.Insert(0, new RowDefinition { Height = GridLength.Auto });

        var transparentBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        var icon = new FontIcon
        {
            Glyph = "\uE895",
            FontSize = 10,
            VerticalAlignment = VerticalAlignment.Center
        };
        _updateBannerText = new TextBlock
        {
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        var content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center
        };
        content.Children.Add(icon);
        content.Children.Add(_updateBannerText);

        _updateBannerButton = new Button
        {
            Content = content,
            Background = transparentBrush,
            BorderBrush = transparentBrush,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            Height = 26,
            MinHeight = 26,
            Padding = new Thickness(12, 0, 12, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            VerticalContentAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
            Opacity = 0.72
        };
        _updateBannerButton.Resources["ButtonBackground"] = transparentBrush;
        _updateBannerButton.Resources["ButtonBackgroundPointerOver"] = transparentBrush;
        _updateBannerButton.Resources["ButtonBackgroundPressed"] = transparentBrush;
        _updateBannerButton.Resources["ButtonBackgroundDisabled"] = transparentBrush;
        _updateBannerButton.Resources["ButtonBorderBrush"] = transparentBrush;
        _updateBannerButton.Resources["ButtonBorderBrushPointerOver"] = transparentBrush;
        _updateBannerButton.Resources["ButtonBorderBrushPressed"] = transparentBrush;
        _updateBannerButton.Click += OnUpdateBannerClicked;
        AutomationProperties.SetAutomationId(_updateBannerButton, "UpdateAvailableBanner");

        Grid.SetRow(_updateBannerButton, 0);
        RootGrid.Children.Add(_updateBannerButton);
        RefreshPortableUpdateBannerTheme();
    }

    private void StartPortableUpdateCheck()
    {
        if (_availableRelease is not null)
        {
            ShowPortableUpdateBanner(_availableRelease);
            return;
        }

        if (_updateCheckStarted)
        {
            return;
        }

        _updateCheckStarted = true;
        _updateCheckCts?.Dispose();
        _updateCheckCts = new CancellationTokenSource();
        _ = CheckForPortableUpdateAsync(_updateCheckCts.Token);
    }

    private async Task CheckForPortableUpdateAsync(CancellationToken cancellationToken)
    {
        try
        {
            var release = await _githubReleaseUpdateService.CheckForUpdateAsync(
                GitHubReleaseUpdateService.GetCurrentApplicationVersion(),
                cancellationToken,
                includeLatestStableRelease: IncludeLatestStableReleaseInDebug);
            if (release is null || cancellationToken.IsCancellationRequested || !_isLoaded)
            {
                return;
            }

            _availableRelease = release;
            ShowPortableUpdateBanner(release);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[UpdateCheck] GitHub release check failed: {ex.Message}");
        }
    }

    private void StopPortableUpdateCheck()
    {
        _updateCheckCts?.Cancel();
        _updateCheckCts?.Dispose();
        _updateCheckCts = null;
        if (_availableRelease is null)
        {
            _updateCheckStarted = false;
        }
    }

    private void ShowPortableUpdateBanner(GitHubReleaseUpdate release)
    {
        if (_updateBannerButton is null || _updateBannerText is null)
        {
            return;
        }

#if DEBUG
        const string resourceKey = "PortableUpdateBanner_Debug";
#else
        const string resourceKey = "PortableUpdateBanner_Available";
#endif
        _updateBannerText.Text = LocalizationService.Instance.GetString(resourceKey, release.TagName);
        AutomationProperties.SetName(_updateBannerButton, _updateBannerText.Text);
        RefreshPortableUpdateBannerTheme();
        _updateBannerButton.Visibility = Visibility.Visible;
    }

    private void RefreshPortableUpdateBannerTheme()
    {
        if (_updateBannerButton is null)
        {
            return;
        }

        _updateBannerButton.Foreground =
            ThemeResourceService.GetBrush("EasydictTertiaryTextBrush", this)
            ?? ThemeResourceService.GetBrush("EasydictSecondaryTextBrush", this);
    }

    private async void OnUpdateBannerClicked(object sender, RoutedEventArgs e)
    {
        if (_availableRelease is null)
        {
            return;
        }

        try
        {
            await Launcher.LaunchUriAsync(_availableRelease.ReleaseUri);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[UpdateCheck] Could not open release page: {ex.Message}");
        }
    }
}
#endif
