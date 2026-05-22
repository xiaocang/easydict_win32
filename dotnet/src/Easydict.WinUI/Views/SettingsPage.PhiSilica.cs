using System.Diagnostics;
using Easydict.TranslationService.LocalModels;
using Easydict.WindowsAI;
using Easydict.WindowsAI.Services;
using Easydict.WinUI.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Easydict.WinUI.Views;

/// <summary>
/// Settings UI for Phi Silica via the Windows AI APIs. Surfaces availability
/// state via <see cref="WindowsAIReadyState"/> and offers a one-click "Prepare model"
/// action that triggers <c>LanguageModel.EnsureReadyAsync()</c>.
/// </summary>
public sealed partial class SettingsPage
{
    private const double LocalAIPrimaryTitleFontSize = 14;
    private const double LocalAISecondaryTitleFontSize = 12;

    private bool _phiSilicaPreparing;
    private bool _suppressLocalAIProviderChange;
    private double? _phiSilicaLastProgressPercent;

    private void InitializePhiSilicaPanel()
    {
        PhiSilicaModelPreparationCoordinator.Instance.ProgressChanged -= OnPhiSilicaPreparationProgressChanged;
        PhiSilicaModelPreparationCoordinator.Instance.ProgressChanged += OnPhiSilicaPreparationProgressChanged;

        RefreshPhiSilicaStatus();
        SyncPhiSilicaPreparationProgressFromCoordinator();
    }

    private void TeardownPhiSilicaPanel()
    {
        PhiSilicaModelPreparationCoordinator.Instance.ProgressChanged -= OnPhiSilicaPreparationProgressChanged;
    }

    private void InitializeLocalAIProviderCombo()
    {
        if (LocalAIProviderCombo is null)
        {
            return;
        }

        _suppressLocalAIProviderChange = true;
        try
        {
            SelectComboByTag(LocalAIProviderCombo, _settings.LocalAIProvider);
            if (LocalAIProviderCombo.SelectedItem is null && LocalAIProviderCombo.Items.Count > 0)
            {
                LocalAIProviderCombo.SelectedIndex = 0;
            }
        }
        finally
        {
            _suppressLocalAIProviderChange = false;
        }

        UpdateLocalAIProviderPanels();
    }

    private void OnLocalAIProviderChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressLocalAIProviderChange || !_isInitialized)
        {
            return;
        }

        if (LocalAIProviderCombo.SelectedItem is not ComboBoxItem)
        {
            return;
        }

        UpdateLocalAIProviderPanels();
        OnSettingChanged(sender, e);
    }

    private void UpdateLocalAIProviderPanels()
    {
        var mode = GetSelectedLocalAIProviderMode();
        UpdateLocalAIProviderDescription(mode);

        if (WindowsLocalAIConfigPanel is not null)
        {
            WindowsLocalAIConfigPanel.Visibility = mode == LocalAIProviderMode.Auto || mode == LocalAIProviderMode.WindowsAI
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        if (FoundryLocalConfigPanel is not null)
        {
            FoundryLocalConfigPanel.Visibility = mode == LocalAIProviderMode.Auto || mode == LocalAIProviderMode.FoundryLocal
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        if (OpenVinoConfigPanel is not null)
        {
            var showOpenVino = mode == LocalAIProviderMode.Auto || mode == LocalAIProviderMode.OpenVINO;
            OpenVinoConfigPanel.Visibility = showOpenVino
                ? Visibility.Visible
                : Visibility.Collapsed;

            if (showOpenVino)
            {
                InitializeOpenVinoPanel();
            }
        }

        UpdateLocalAIProviderPanelEmphasis(mode);
        RefreshLocalAIHeaderStatusBadge();
        SyncPhiSilicaPreparationProgressFromCoordinator();
    }

    private void UpdateLocalAIProviderPanelEmphasis(LocalAIProviderMode mode)
    {
        var highlightedMode = mode == LocalAIProviderMode.Auto
            ? GetFirstAvailableLocalAIProviderMode()
            : mode;

        SetLocalAIProviderPanelEmphasis(
            WindowsLocalAISectionTitleText,
            WindowsLocalAISectionRatingText,
            highlightedMode == LocalAIProviderMode.WindowsAI);
        SetLocalAIProviderPanelEmphasis(
            FoundryLocalTitleText,
            FoundryLocalRatingText,
            highlightedMode == LocalAIProviderMode.FoundryLocal);
        SetLocalAIProviderPanelEmphasis(
            OpenVinoTitleText,
            OpenVinoRatingText,
            highlightedMode == LocalAIProviderMode.OpenVINO);
    }

    private LocalAIProviderMode? GetFirstAvailableLocalAIProviderMode()
    {
        if (GetPhiSilicaLocalModelStatus().State == LocalModelState.Ready)
        {
            return LocalAIProviderMode.WindowsAI;
        }

        if (IsFoundryLocalConfigured())
        {
            return LocalAIProviderMode.FoundryLocal;
        }

        if (GetOpenVinoService()?.GetStatus().State == LocalModelState.Ready)
        {
            return LocalAIProviderMode.OpenVINO;
        }

        return null;
    }

    private static void SetLocalAIProviderPanelEmphasis(TextBlock? title, TextBlock? rating, bool isPrimary)
    {
        var fontSize = isPrimary ? LocalAIPrimaryTitleFontSize : LocalAISecondaryTitleFontSize;
        if (title is not null)
        {
            title.FontSize = fontSize;
        }

        if (rating is not null)
        {
            rating.FontSize = fontSize;
        }
    }

    private LocalAIProviderMode GetSelectedLocalAIProviderMode()
    {
        return LocalAIProviderModeExtensions.Parse(GetSelectedTag(LocalAIProviderCombo) ?? _settings.LocalAIProvider);
    }

    private void UpdateLocalAIProviderDescription(LocalAIProviderMode? selectedMode = null)
    {
        if (WindowsLocalAIDescriptionText is null)
        {
            return;
        }

        var key = (selectedMode ?? GetSelectedLocalAIProviderMode()) switch
        {
            LocalAIProviderMode.WindowsAI => LocalAIResources.DescriptionKeys.WindowsAI,
            LocalAIProviderMode.FoundryLocal => LocalAIResources.DescriptionKeys.FoundryLocal,
            LocalAIProviderMode.OpenVINO => LocalAIResources.DescriptionKeys.OpenVINO,
            _ => LocalAIResources.DescriptionKeys.Auto,
        };

        WindowsLocalAIDescriptionText.Text = LocalizationService.Instance.GetString(key);
    }

    private void RefreshLocalAIHeaderStatusBadge()
    {
        if (WindowsLocalAIStatusBadge is null)
        {
            return;
        }

        var mode = GetSelectedLocalAIProviderMode();
        var isReady = mode switch
        {
            LocalAIProviderMode.WindowsAI => GetPhiSilicaLocalModelStatus().State == LocalModelState.Ready,
            LocalAIProviderMode.FoundryLocal => IsFoundryLocalConfigured(),
            LocalAIProviderMode.OpenVINO => GetOpenVinoService()?.GetStatus().State == LocalModelState.Ready,
            _ => GetPhiSilicaLocalModelStatus().State == LocalModelState.Ready
                || IsFoundryLocalConfigured()
                || GetOpenVinoService()?.GetStatus().State == LocalModelState.Ready,
        };

        WindowsLocalAIStatusBadge.Text = isReady ? "✓" : "⚠";
        WindowsLocalAIStatusBadge.Foreground = GetLocalAiStatusBrush(isReady);
        WindowsLocalAIStatusBadge.Visibility = Visibility.Visible;
    }

    private bool IsFoundryLocalConfigured()
    {
        if (_foundryLocalLastStatus is not null)
        {
            return _foundryLocalLastStatus.State == LocalModelState.Ready;
        }

        if (FoundryLocalModelBox is null)
        {
            return !string.IsNullOrWhiteSpace(_settings.FoundryLocalModel);
        }

        return !string.IsNullOrWhiteSpace(FoundryLocalModelBox.Text);
    }

    private void RefreshPhiSilicaStatus()
    {
        UpdatePhiSilicaStatusUi(GetPhiSilicaLocalModelStatus());
    }

    private void UpdatePhiSilicaStatusUi(WindowsAIReadyState state)
    {
        UpdatePhiSilicaStatusUi(
            state == WindowsAIReadyState.Ready
                ? GetPhiSilicaLocalModelStatus()
                : PhiSilicaTranslationService.MapReadyStateToStatus(state));
    }

    private void UpdatePhiSilicaStatusUi(LocalModelStatus status)
    {
        if (WindowsLocalAIStatusBar == null)
        {
            return;
        }

        var loc = LocalizationService.Instance;

        var isReady = status.State == LocalModelState.Ready;
        var titleKey = isReady
            ? PhiSilicaResources.TitleKeys.Ready
            : PhiSilicaResources.TitleKeys.Unavailable;
        WindowsLocalAIStatusBar.Title = loc.GetString(titleKey);

        var message = loc.GetString(status.ResourceKey);
        if (!string.IsNullOrWhiteSpace(status.DetailMessage))
        {
            message = $"{message}\n\n{status.DetailMessage.Trim()}";
        }
        WindowsLocalAIStatusBar.Message = message;

        WindowsLocalAIStatusBar.Severity = status.State switch
        {
            LocalModelState.Ready => InfoBarSeverity.Success,
            LocalModelState.NeedsPreparation => InfoBarSeverity.Informational,
            LocalModelState.Failed => InfoBarSeverity.Error,
            _ => InfoBarSeverity.Warning,
        };

        RefreshLocalAIHeaderStatusBadge();

        WindowsLocalAIPrepareButton.Visibility = status.State is LocalModelState.NeedsPreparation or LocalModelState.Failed
            && !_phiSilicaPreparing
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private static LocalModelStatus GetPhiSilicaLocalModelStatus()
    {
        return PhiSilicaBackendHealthMonitor.Shared.GetStatus(PhiSilicaAvailability.Client);
    }

    private async void OnPreparePhiSilicaModel(object sender, RoutedEventArgs e)
    {
        if (_phiSilicaPreparing)
        {
            return;
        }

        _phiSilicaPreparing = true;
        _phiSilicaLastProgressPercent = null;
        var loc = LocalizationService.Instance;
        var originalContent = WindowsLocalAIPrepareButton.Content;
        WindowsLocalAIPrepareButton.IsEnabled = false;
        WindowsLocalAIPrepareButton.Content = loc.GetString(PhiSilicaResources.UiKeys.Preparing);
        var shouldRefreshStatusAfterPreparing = false;
        WindowsAIReadyState? completedState = null;

        try
        {
            ShowPhiSilicaPrepareProgress(PhiSilicaResources.ProgressKeys.Checking);
            await Task.Yield();
            ShowPhiSilicaPrepareProgress(PhiSilicaResources.ProgressKeys.Requesting);
            await Task.Yield();
            ShowPhiSilicaPrepareProgress(PhiSilicaResources.ProgressKeys.Waiting);
            var newState = await PhiSilicaModelPreparationCoordinator.Instance.EnsureReadyAsync(
                ShowPhiSilicaPrepareProgress,
                _lifetimeCts.Token);
            ShowPhiSilicaPrepareProgress(PhiSilicaResources.ProgressKeys.Finalizing);
            UpdatePhiSilicaStatusUi(newState);
            shouldRefreshStatusAfterPreparing = true;
            completedState = newState;
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
            // Page navigation cancels this UI wait only. The shared coordinator
            // keeps the Windows-managed download/preparation alive.
        }
        catch (OperationCanceledException)
        {
            UpdatePhiSilicaStatusUi(PhiSilicaAvailability.GetReadyState());
            shouldRefreshStatusAfterPreparing = true;
        }
        catch (Exception ex)
        {
            // Distinct from the "user hasn't tried yet" path: show the dedicated
            // PrepareFailed message + Error severity so the user sees that the
            // attempt failed and knows to retry / investigate. The raw exception
            // message is forwarded as the InfoBar's secondary content so common
            // diagnostics (offline, AV blocking, quota) are visible.
            Debug.WriteLine($"[Settings] Phi Silica prepare failed: {ex.Message}");
            ShowPhiSilicaPrepareFailure(ex.Message);
        }
        finally
        {
            _phiSilicaPreparing = false;
            WindowsLocalAIPrepareButton.Content = originalContent;
            WindowsLocalAIPrepareButton.IsEnabled = true;
            HidePhiSilicaPrepareProgress();
            if (!_isUnloaded && shouldRefreshStatusAfterPreparing)
            {
                var state = completedState == WindowsAIReadyState.Ready
                    ? WindowsAIReadyState.Ready
                    : PhiSilicaAvailability.GetReadyState();
                UpdatePhiSilicaStatusUi(state);
            }
        }
    }

    private void OnPhiSilicaPreparationProgressChanged(
        object? sender,
        PhiSilicaModelPreparationSnapshot snapshot)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_isUnloaded)
            {
                return;
            }

            _phiSilicaPreparing = snapshot.IsPreparing;
            if (snapshot.IsPreparing)
            {
                ShowPhiSilicaPrepareProgress(snapshot);
                UpdatePhiSilicaStatusUi(PhiSilicaAvailability.GetReadyState());
                return;
            }

            HidePhiSilicaPrepareProgress();
            UpdatePhiSilicaStatusUi(snapshot.ReadyState ?? PhiSilicaAvailability.GetReadyState());
        });
    }

    private void SyncPhiSilicaPreparationProgressFromCoordinator()
    {
        var snapshot = PhiSilicaModelPreparationCoordinator.Instance.CurrentSnapshot;
        _phiSilicaPreparing = snapshot.IsPreparing;
        if (snapshot.IsPreparing)
        {
            ShowPhiSilicaPrepareProgress(snapshot);
            return;
        }

        HidePhiSilicaPrepareProgress();
    }

    private void ShowPhiSilicaPrepareProgress(string resourceKey)
    {
        ShowPhiSilicaPrepareProgress(
            PhiSilicaModelPreparationCoordinator.Instance.CreatePreparingSnapshot(resourceKey));
    }

    private void ShowPhiSilicaPrepareProgress(PhiSilicaModelPreparationSnapshot snapshot)
    {
        if (WindowsLocalAIPrepareProgressPanel is null
            || WindowsLocalAIPrepareProgressText is null
            || WindowsLocalAIPrepareProgressBar is null)
        {
            return;
        }

        WindowsLocalAIPrepareProgressText.Text = PhiSilicaModelPreparationProgressFormatter.FormatText(snapshot);
        _phiSilicaLastProgressPercent = PhiSilicaModelPreparationProgressFormatter.MergeProgressPercent(
            _phiSilicaLastProgressPercent,
            snapshot.ProgressPercent);
        if (_phiSilicaLastProgressPercent is { } percent)
        {
            WindowsLocalAIPrepareProgressBar.IsIndeterminate = false;
            WindowsLocalAIPrepareProgressBar.Value = percent;
        }
        else
        {
            WindowsLocalAIPrepareProgressBar.IsIndeterminate = true;
        }
        WindowsLocalAIPrepareProgressPanel.Visibility = Visibility.Visible;
    }

    private void HidePhiSilicaPrepareProgress()
    {
        if (WindowsLocalAIPrepareProgressPanel is null
            || WindowsLocalAIPrepareProgressText is null)
        {
            return;
        }

        WindowsLocalAIPrepareProgressPanel.Visibility = Visibility.Collapsed;
        WindowsLocalAIPrepareProgressText.Text = string.Empty;
        _phiSilicaLastProgressPercent = null;
    }

    /// <summary>
    /// Renders the failure path of <see cref="OnPreparePhiSilicaModel"/> —
    /// distinct from the normal "NotReady, click to prepare" state. Baseline
    /// diagnostics get a dedicated resource key so users can tell Windows AI is
    /// unavailable on the current image rather than assuming translation broke.
    /// </summary>
    private void ShowPhiSilicaPrepareFailure(string? detailMessage)
    {
        if (WindowsLocalAIStatusBar is null) return;

        var status = PhiSilicaTranslationService.CreatePreparationFailureStatus(
            detailMessage,
            TryGetPhiSilicaHealthFingerprint());

        UpdatePhiSilicaStatusUi(status);
        WindowsLocalAIStatusBar.IsOpen = true;
    }

    private static WindowsAIHealthFingerprint? TryGetPhiSilicaHealthFingerprint()
    {
        try
        {
            return PhiSilicaAvailability.Client.GetHealthFingerprint();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Resolves the appropriate Fluent system brush for a local-model status
    /// badge. Shared between the Phi Silica and OpenVINO partials. Uses
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
