using System.Diagnostics;
using Easydict.TranslationService;
using Easydict.TranslationService.LocalModels;
using Easydict.TranslationService.Services;
using Easydict.WinUI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Easydict.WinUI.Views;

public sealed partial class SettingsPage
{
    private CancellationTokenSource? _foundryLocalStatusCts;
    private LocalModelStatus? _foundryLocalLastStatus;
    private bool _foundryLocalStarting;

    private void InitializeFoundryLocalPanel()
    {
        _foundryLocalStatusCts?.Cancel();
        _foundryLocalStatusCts?.Dispose();
        _foundryLocalStatusCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);

        UpdateFoundryLocalStatusUi(new LocalModelStatus(
            LocalModelState.Preparing,
            FoundryLocalResources.StatusKeys.Checking));
        _ = RefreshFoundryLocalStatusAsync(_foundryLocalStatusCts.Token);
    }

    private void TeardownFoundryLocalPanel()
    {
        _foundryLocalStatusCts?.Cancel();
        _foundryLocalStatusCts?.Dispose();
        _foundryLocalStatusCts = null;
        _foundryLocalLastStatus = null;
    }

    private async Task RefreshFoundryLocalStatusAsync(CancellationToken cancellationToken)
    {
        try
        {
            var settings = ReadFoundryLocalSettingsFromInputs();
            var status = await TranslationManagerService.Instance
                .GetFoundryLocalStatusAsync(settings.Endpoint, settings.Model, cancellationToken);

            if (_isUnloaded || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            DispatcherQueue.TryEnqueue(() =>
            {
                if (_isUnloaded || cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                UpdateFoundryLocalStatusUi(status);
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Settings] Foundry Local status check failed: {ex.Message}");
            if (_isUnloaded || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            DispatcherQueue.TryEnqueue(() => UpdateFoundryLocalStatusUi(new LocalModelStatus(
                LocalModelState.Failed,
                FoundryLocalResources.StatusKeys.StartFailed,
                DetailMessage: ex.Message)));
        }
    }

    private async void OnStartFoundryLocal(object sender, RoutedEventArgs e)
    {
        if (_foundryLocalStarting)
        {
            return;
        }

        _foundryLocalStarting = true;
        var originalContent = FoundryLocalStartButton.Content;
        FoundryLocalStartButton.IsEnabled = false;
        FoundryLocalStartButton.Content = LocalizationService.Instance.GetString(FoundryLocalResources.StatusKeys.Starting);
        UpdateFoundryLocalStatusUi(new LocalModelStatus(
            LocalModelState.Preparing,
            FoundryLocalResources.StatusKeys.Starting));

        try
        {
            var settings = ReadFoundryLocalSettingsFromInputs();
            var status = await TranslationManagerService.Instance
                .PrepareFoundryLocalAsync(settings.Endpoint, settings.Model, _lifetimeCts.Token);
            if (!_isUnloaded)
            {
                UpdateFoundryLocalStatusUi(status);
            }
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Settings] Foundry Local start failed: {ex.Message}");
            if (!_isUnloaded)
            {
                UpdateFoundryLocalStatusUi(new LocalModelStatus(
                    LocalModelState.Failed,
                    FoundryLocalResources.StatusKeys.StartFailed,
                    DetailMessage: ex.Message));
            }
        }
        finally
        {
            _foundryLocalStarting = false;
            if (!_isUnloaded)
            {
                FoundryLocalStartButton.Content = originalContent;
                FoundryLocalStartButton.IsEnabled = true;
                if (_foundryLocalLastStatus is not null)
                {
                    UpdateFoundryLocalStatusUi(_foundryLocalLastStatus);
                }
                RefreshLocalAIHeaderStatusBadge();
            }
        }
    }

    private void UpdateFoundryLocalStatusUi(LocalModelStatus status)
    {
        _foundryLocalLastStatus = status;
        if (FoundryLocalStatusBar is null)
        {
            return;
        }

        var loc = LocalizationService.Instance;
        FoundryLocalStatusBar.Title = loc.GetString(
            status.State == LocalModelState.Ready
                ? FoundryLocalResources.TitleKeys.Ready
                : FoundryLocalResources.TitleKeys.Unavailable);

        var message = loc.GetString(status.ResourceKey);
        if (!string.IsNullOrWhiteSpace(status.DetailMessage))
        {
            message = $"{message}\n\n{status.DetailMessage.Trim()}";
        }
        FoundryLocalStatusBar.Message = message;

        FoundryLocalStatusBar.Severity = status.State switch
        {
            LocalModelState.Ready => InfoBarSeverity.Success,
            LocalModelState.NeedsPreparation => InfoBarSeverity.Informational,
            LocalModelState.Preparing => InfoBarSeverity.Informational,
            LocalModelState.NotCompatible => InfoBarSeverity.Warning,
            LocalModelState.Failed => InfoBarSeverity.Error,
            _ => InfoBarSeverity.Informational,
        };

        FoundryLocalStartButton.Visibility = status.State is LocalModelState.NeedsPreparation or LocalModelState.Failed
            && !_foundryLocalStarting
            ? Visibility.Visible
            : Visibility.Collapsed;
        FoundryLocalStartButton.IsEnabled = status.State != LocalModelState.Preparing;
        RefreshLocalAIHeaderStatusBadge();
    }

    private (string Endpoint, string Model) ReadFoundryLocalSettingsFromInputs()
    {
        var endpoint = FoundryLocalEndpointBox?.Text?.Trim() ?? "";
        var model = FoundryLocalModelBox?.Text?.Trim();
        return (
            endpoint,
            string.IsNullOrWhiteSpace(model)
                ? FoundryLocalService.DefaultModel
                : model);
    }
}
