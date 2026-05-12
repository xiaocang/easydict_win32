using System.Diagnostics;
using Easydict.WindowsAI;
using Easydict.WindowsAI.Services;
using Easydict.WinUI.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Easydict.WinUI.Views;

/// <summary>
/// Settings UI for the Windows Local AI provider (Phi Silica). Surfaces availability
/// state via <see cref="WindowsAIReadyState"/> and offers a one-click "Prepare model"
/// action that triggers <c>LanguageModel.EnsureReadyAsync()</c>.
/// </summary>
public sealed partial class SettingsPage
{
    private bool _windowsLocalAIPreparing;

    private void RefreshWindowsLocalAIStatus()
    {
        var state = WindowsLocalAIAvailability.GetReadyState();
        UpdateWindowsLocalAIStatusUi(state);
    }

    private void UpdateWindowsLocalAIStatusUi(WindowsAIReadyState state)
    {
        if (WindowsLocalAIStatusBar == null)
        {
            return;
        }

        var loc = LocalizationService.Instance;

        var titleKey = state == WindowsAIReadyState.Ready
            ? "WindowsLocalAI_Title_Ready"
            : "WindowsLocalAI_Title_Unavailable";
        WindowsLocalAIStatusBar.Title = loc.GetString(titleKey);

        var messageKey = WindowsLocalAIAvailability.GetStatusResourceKey(state);
        WindowsLocalAIStatusBar.Message = loc.GetString(messageKey);

        WindowsLocalAIStatusBar.Severity = state switch
        {
            WindowsAIReadyState.Ready => InfoBarSeverity.Success,
            WindowsAIReadyState.NotReady => InfoBarSeverity.Informational,
            _ => InfoBarSeverity.Warning,
        };

        // Plain Unicode glyphs (✓ = ✓, ⚠ = ⚠) instead of the colored
        // ✅/⚠ emoji so the foreground brush actually applies. Brushes resolved
        // from theme resources so the badge follows light/dark/high-contrast.
        WindowsLocalAIStatusBadge.Text = state == WindowsAIReadyState.Ready ? "✓" : "⚠";
        WindowsLocalAIStatusBadge.Foreground = GetLocalAiStatusBrush(state == WindowsAIReadyState.Ready);
        WindowsLocalAIStatusBadge.Visibility = Visibility.Visible;

        WindowsLocalAIPrepareButton.Visibility = state == WindowsAIReadyState.NotReady && !_windowsLocalAIPreparing
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private async void OnPrepareWindowsLocalAIModel(object sender, RoutedEventArgs e)
    {
        if (_windowsLocalAIPreparing)
        {
            return;
        }

        _windowsLocalAIPreparing = true;
        var loc = LocalizationService.Instance;
        var originalContent = WindowsLocalAIPrepareButton.Content;
        WindowsLocalAIPrepareButton.IsEnabled = false;
        WindowsLocalAIPrepareButton.Content = loc.GetString("WindowsLocalAI_Preparing");

        try
        {
            var newState = await WindowsLocalAIAvailability.Client.EnsureReadyAsync(CancellationToken.None);
            UpdateWindowsLocalAIStatusUi(newState);
        }
        catch (OperationCanceledException)
        {
            UpdateWindowsLocalAIStatusUi(WindowsLocalAIAvailability.GetReadyState());
        }
        catch (Exception ex)
        {
            // Distinct from the "user hasn't tried yet" path: show the dedicated
            // PrepareFailed message + Error severity so the user sees that the
            // attempt failed and knows to retry / investigate. The raw exception
            // message is forwarded as the InfoBar's secondary content so common
            // diagnostics (offline, AV blocking, quota) are visible.
            Debug.WriteLine($"[Settings] Windows Local AI prepare failed: {ex.Message}");
            ShowWindowsLocalAIPrepareFailure(ex.Message);
        }
        finally
        {
            _windowsLocalAIPreparing = false;
            WindowsLocalAIPrepareButton.Content = originalContent;
            WindowsLocalAIPrepareButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// Renders the failure path of <see cref="OnPrepareWindowsLocalAIModel"/> —
    /// distinct from the normal "NotReady, click to prepare" state. Uses the
    /// dedicated <c>WindowsLocalAI_Status_PrepareFailed</c> resource key (not
    /// <c>WindowsLocalAI_Status_NotReady</c>) so the user can tell the system
    /// tried and failed. Leaves the Prepare button visible so retry is one click.
    /// </summary>
    private void ShowWindowsLocalAIPrepareFailure(string? detailMessage)
    {
        if (WindowsLocalAIStatusBar is null) return;

        var loc = LocalizationService.Instance;
        WindowsLocalAIStatusBar.Title = loc.GetString("WindowsLocalAI_Title_Unavailable");
        WindowsLocalAIStatusBar.Message = loc.GetString("WindowsLocalAI_Status_PrepareFailed");
        WindowsLocalAIStatusBar.Severity = InfoBarSeverity.Error;
        WindowsLocalAIStatusBar.IsOpen = true;

        WindowsLocalAIStatusBadge.Text = "⚠";
        WindowsLocalAIStatusBadge.Foreground = GetLocalAiStatusBrush(isReady: false);
        WindowsLocalAIStatusBadge.Visibility = Visibility.Visible;

        // Allow retry — failure isn't a permanent state.
        WindowsLocalAIPrepareButton.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Resolves the appropriate Fluent system brush for a local-model status
    /// badge. Shared between the WindowsLocalAI and OpenVINO partials. Uses
    /// theme resources so the badge tracks light/dark/high-contrast instead of
    /// hard-coded ARGB.
    /// </summary>
    private static Brush GetLocalAiStatusBrush(bool isReady)
    {
        var key = isReady ? "SystemFillColorSuccessBrush" : "SystemFillColorCautionBrush";
        if (Application.Current.Resources.TryGetValue(key, out var value) && value is Brush brush)
        {
            return brush;
        }
        // Fall back to the foreground brush so the glyph remains visible even
        // if the theme resource isn't available (e.g. unloaded ResourceDictionary).
        return Application.Current.Resources.TryGetValue("TextFillColorPrimaryBrush", out var fb)
                && fb is Brush fallback
            ? fallback
            : new SolidColorBrush(Colors.Gray);
    }
}
