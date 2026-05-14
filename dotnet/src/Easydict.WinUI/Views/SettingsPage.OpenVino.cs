using System.Diagnostics;
using Easydict.OpenVINO.Services;
using Easydict.TranslationService.LocalModels;
using Easydict.WinUI.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Easydict.WinUI.Views;

/// <summary>
/// Settings UI for the OpenVINO + NLLB-200 provider. Subscribes to
/// <see cref="ILocalModelProvider.StatusChanged"/> so download progress drives
/// both the InfoBar message and the ProgressBar without polling.
/// </summary>
public sealed partial class SettingsPage
{
    private OpenVINOTranslationService? _openVinoServiceCached;
    private bool _openVinoSubscribed;

    private OpenVINOTranslationService? GetOpenVinoService()
    {
        if (_openVinoServiceCached is not null)
        {
            return _openVinoServiceCached;
        }

        _openVinoServiceCached = TranslationManagerService.Instance.OpenVinoService;
        return _openVinoServiceCached;
    }

    private void InitializeOpenVinoPanel()
    {
        var svc = GetOpenVinoService();
        if (svc is null) return;

        if (!_openVinoSubscribed)
        {
            svc.StatusChanged += OnOpenVinoStatusChanged;
            _openVinoSubscribed = true;
        }

        UpdateOpenVinoStatusUi(svc.GetStatus());
    }

    private void OnOpenVinoStatusChanged(object? sender, LocalModelStatus status)
    {
        // StatusChanged may fire on a worker thread (download progress); marshal
        // to the UI dispatcher.
        DispatcherQueue.TryEnqueue(() => UpdateOpenVinoStatusUi(status));
    }

    private void UpdateOpenVinoStatusUi(LocalModelStatus status)
    {
        if (OpenVinoStatusBar is null) return;

        var loc = LocalizationService.Instance;
        OpenVinoStatusBar.Message = loc.GetString(status.ResourceKey);
        OpenVinoStatusBar.Title = loc.GetString(
            status.State == LocalModelState.Ready
                ? "OpenVINO_Title_Ready"
                : "OpenVINO_Title_Unavailable");

        OpenVinoStatusBar.Severity = status.State switch
        {
            LocalModelState.Ready => InfoBarSeverity.Success,
            LocalModelState.NeedsPreparation => InfoBarSeverity.Informational,
            LocalModelState.Preparing => InfoBarSeverity.Informational,
            LocalModelState.NotCompatible => InfoBarSeverity.Warning,
            LocalModelState.Failed => InfoBarSeverity.Error,
            _ => InfoBarSeverity.Informational,
        };

        // Status badge in the header. Plain Unicode glyphs (✓/⚠) so the
        // foreground brush actually applies; brush comes from theme resources
        // so light/dark/high-contrast all render correctly.
        var isReady = status.State == LocalModelState.Ready;
        OpenVinoStatusBadge.Text = isReady ? "✓" : "⚠";
        OpenVinoStatusBadge.Foreground = GetLocalAiStatusBrush(isReady);
        OpenVinoStatusBadge.Visibility = Visibility.Visible;
        RefreshLocalAIHeaderStatusBadge();

        // Progress bar — only meaningful during download.
        if (status.State == LocalModelState.Preparing && status.ProgressPercent is double pct)
        {
            OpenVinoDownloadProgress.IsIndeterminate = false;
            OpenVinoDownloadProgress.Value = pct;
            OpenVinoDownloadProgress.Visibility = Visibility.Visible;
        }
        else if (status.State == LocalModelState.Preparing)
        {
            OpenVinoDownloadProgress.IsIndeterminate = true;
            OpenVinoDownloadProgress.Visibility = Visibility.Visible;
        }
        else
        {
            OpenVinoDownloadProgress.Visibility = Visibility.Collapsed;
        }

        // Download button — only when not yet prepared and not already preparing.
        OpenVinoDownloadButton.Visibility = status.State == LocalModelState.NeedsPreparation
            || status.State == LocalModelState.Failed
            ? Visibility.Visible
            : Visibility.Collapsed;

        OpenVinoDownloadButton.IsEnabled = status.State != LocalModelState.Preparing;
    }

    private async void OnDownloadOpenVinoModel(object sender, RoutedEventArgs e)
    {
        var svc = GetOpenVinoService();
        if (svc is null) return;

        OpenVinoDownloadButton.IsEnabled = false;
        try
        {
            await svc.PrepareAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Settings] OpenVINO download failed: {ex.Message}");
            UpdateOpenVinoStatusUi(svc.GetStatus());
        }
        finally
        {
            // Status update from PrepareAsync re-enables the button if needed.
        }
    }

    /// <summary>
    /// Detach the StatusChanged subscription so the singleton
    /// <see cref="OpenVINOTranslationService"/> doesn't retain this page after
    /// navigation/unload (which would also keep enqueuing UI updates to a dead
    /// dispatcher). Called from <c>SettingsPage.TeardownOnUnload</c>.
    /// </summary>
    private void TeardownOpenVinoPanel()
    {
        if (!_openVinoSubscribed) return;

        var svc = _openVinoServiceCached ?? GetOpenVinoService();
        if (svc is not null)
        {
            svc.StatusChanged -= OnOpenVinoStatusChanged;
        }
        _openVinoSubscribed = false;
        _openVinoServiceCached = null;
    }
}
