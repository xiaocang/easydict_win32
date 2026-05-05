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

        WindowsLocalAIStatusBadge.Text = state == WindowsAIReadyState.Ready ? "✅" : "⚠";
        WindowsLocalAIStatusBadge.Foreground = state == WindowsAIReadyState.Ready
            ? new SolidColorBrush(Colors.Green)
            : new SolidColorBrush(Colors.DarkOrange);
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
            Debug.WriteLine($"[Settings] Windows Local AI prepare failed: {ex.Message}");
            UpdateWindowsLocalAIStatusUi(WindowsLocalAIAvailability.GetReadyState());
        }
        finally
        {
            _windowsLocalAIPreparing = false;
            WindowsLocalAIPrepareButton.Content = originalContent;
            WindowsLocalAIPrepareButton.IsEnabled = true;
        }
    }
}
