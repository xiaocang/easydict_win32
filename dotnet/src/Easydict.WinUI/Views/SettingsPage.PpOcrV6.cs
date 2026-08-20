using System.Diagnostics;
using Easydict.SidecarClient.Protocol;
using Easydict.WinUI;
using Easydict.WinUI.Models;
using Easydict.WinUI.Services;
using Easydict.WinUI.Services.Workers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Easydict.WinUI.Views;

public sealed partial class SettingsPage
{
    private bool _ppOcrV6UiLoading;
    private int _ppOcrV6StatusVersion;
    private CancellationTokenSource? _ppOcrV6DownloadCts;

    private void InitializePpOcrV6Settings()
    {
        _ppOcrV6UiLoading = true;
        try
        {
            var loc = LocalizationService.Instance;
            PpOcrV6ModelCombo.Items.Clear();
            foreach (var model in PpOcrV6ModelCatalog.Models)
            {
                PpOcrV6ModelCombo.Items.Add(new ComboBoxItem
                {
                    Content = model.Id switch
                    {
                        PpOcrV6ModelCatalog.TinyId => loc.GetString("PpOcrV6ModelTiny"),
                        PpOcrV6ModelCatalog.SmallId => loc.GetString("PpOcrV6ModelSmall"),
                        PpOcrV6ModelCatalog.MediumId => loc.GetString("PpOcrV6ModelMedium"),
                        _ => model.DisplayName,
                    },
                    Tag = model.Id,
                });
            }

            var selectedModel = PpOcrV6ModelCatalog.TryGet(_settings.PpOcrV6ModelId, out _)
                ? _settings.PpOcrV6ModelId
                : PpOcrV6ModelCatalog.SmallId;
            SelectComboByTag(PpOcrV6ModelCombo, selectedModel);
            PpOcrV6ThreadCountBox.Value = Math.Clamp(
                _settings.PpOcrV6ThreadCount,
                PpOcrV6ModelCatalog.MinThreadCount,
                PpOcrV6ModelCatalog.MaxThreadCount);
            PpOcrV6GpuToggle.IsOn = _settings.PpOcrV6UseGpu;
            PpOcrV6GpuToggle.IsEnabled = true;
            PpOcrV6FallbackToggle.IsOn = _settings.PpOcrV6AllowFallback;
        }
        finally
        {
            _ppOcrV6UiLoading = false;
        }

        PpOcrV6ThreadCountBox.Minimum = PpOcrV6ModelCatalog.MinThreadCount;
        PpOcrV6ThreadCountBox.Maximum = PpOcrV6ModelCatalog.MaxThreadCount;
    }

    private string GetSelectedPpOcrV6ModelId()
    {
        return GetSelectedTag(PpOcrV6ModelCombo) ?? PpOcrV6ModelCatalog.SmallId;
    }

    private void UpdatePpOcrV6ModelUi()
    {
        if (GetSelectedOcrEngine() != OcrEngineType.PpOcrV6)
        {
            PpOcrV6Panel.Visibility = Visibility.Collapsed;
            return;
        }

        PpOcrV6Panel.Visibility = Visibility.Visible;
        var loc = LocalizationService.Instance;
        var modelId = GetSelectedPpOcrV6ModelId();
        var model = PpOcrV6ModelCatalog.Get(modelId);
        PpOcrV6DownloadSizeText.Text = loc.GetString(
            "PpOcrV6DownloadSize",
            model.DownloadSizeBytes / 1_000_000d);
        PpOcrV6LanguagesText.Text = loc.GetString("PpOcrV6LanguageCount", model.Languages.Count);
        PpOcrV6ModelStatusText.Text = loc.GetString("PpOcrV6StatusChecking");
        PpOcrV6DownloadButton.Visibility = Visibility.Collapsed;
        var version = ++_ppOcrV6StatusVersion;
        _ = RefreshPpOcrV6ModelStatusAsync(modelId, version);
    }

    private async Task RefreshPpOcrV6ModelStatusAsync(string modelId, int version)
    {
        PpOcrV6ModelState state;
        try
        {
            state = await Task.Run(() => _ppOcrV6ModelStore.GetStateBySize(modelId)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            state = PpOcrV6ModelState.Invalid;
            Debug.WriteLine($"[SettingsPage] PP-OCRv6 model validation failed: {ex.Message}");
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            if (_ppOcrV6DownloadCts is not null
                || version != _ppOcrV6StatusVersion
                || GetSelectedPpOcrV6ModelId() != modelId)
            {
                return;
            }

            var loc = LocalizationService.Instance;
            switch (state)
            {
                case PpOcrV6ModelState.Installed:
                    PpOcrV6ModelStatusText.Text = loc.GetString("PpOcrV6StatusDownloaded");
                    PpOcrV6DownloadButton.Content = loc.GetString("PpOcrV6ActionRemove");
                    PpOcrV6DownloadButton.Visibility = Visibility.Visible;
                    break;
                case PpOcrV6ModelState.Invalid:
                    PpOcrV6ModelStatusText.Text = loc.GetString("PpOcrV6StatusInvalid");
                    PpOcrV6DownloadButton.Content = loc.GetString("PpOcrV6ActionRepair");
                    PpOcrV6DownloadButton.Visibility = Visibility.Visible;
                    break;
                default:
                    PpOcrV6ModelStatusText.Text = loc.GetString("PpOcrV6StatusNotDownloaded");
                    PpOcrV6DownloadButton.Content = loc.GetString("PpOcrV6ActionDownload");
                    PpOcrV6DownloadButton.Visibility = Visibility.Visible;
                    break;
            }
        });
    }

    private void OnPpOcrV6ModelChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_ppOcrV6UiLoading)
        {
            return;
        }

        UpdatePpOcrV6ModelUi();
        OnSettingChanged(sender, e);
    }

    private void OnPpOcrV6SettingChanged(object sender, object e)
    {
        if (!_ppOcrV6UiLoading)
        {
            OnSettingChanged(sender, e);
        }
    }

    private async void OnDownloadPpOcrV6ModelClick(object sender, RoutedEventArgs e)
    {
        if (_ppOcrV6DownloadCts is not null)
        {
            _ppOcrV6DownloadCts.Cancel();
            return;
        }

        var modelId = GetSelectedPpOcrV6ModelId();
        if (_ppOcrV6ModelStore.GetStateBySize(modelId) == PpOcrV6ModelState.Installed)
        {
            await RemovePpOcrV6ModelAsync(modelId);
            return;
        }

        await DownloadPpOcrV6ModelAsync(modelId, sender, e);
    }

    private async Task DownloadPpOcrV6ModelAsync(string modelId, object sender, object e)
    {
        _ppOcrV6DownloadCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
        PpOcrV6DownloadButton.Content = LocalizationService.Instance.GetString("Cancel");
        PpOcrV6DownloadButton.IsEnabled = true;
        PpOcrV6ModelCombo.IsEnabled = false;
        PpOcrV6DownloadProgress.Visibility = Visibility.Visible;
        PpOcrV6DownloadProgressText.Visibility = Visibility.Visible;
        PpOcrV6ModelStatusText.Text = LocalizationService.Instance.GetString("PpOcrV6StatusDownloading");

        try
        {
            using var httpClient = OcrServiceFactory.CreateProxyAwareHttpClient(
                _settings.ProxyEnabled,
                _settings.ProxyUri,
                _settings.ProxyBypassLocal,
                TimeSpan.FromMinutes(30));
            using var service = new PpOcrV6ModelDownloadService(httpClient);
            var progress = new Progress<PpOcrV6DownloadProgress>(value =>
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (_ppOcrV6DownloadCts?.IsCancellationRequested != false || _isUnloaded)
                    {
                        return;
                    }

                    PpOcrV6DownloadProgress.IsIndeterminate = value.Percentage < 0;
                    if (value.Percentage >= 0)
                    {
                        PpOcrV6DownloadProgress.Value = value.Percentage;
                    }
                    PpOcrV6DownloadProgressText.Text = LocalizationService.Instance.GetString(
                        "PpOcrV6DownloadProgress",
                        value.BytesDownloaded / 1_000_000d,
                        value.TotalBytes / 1_000_000d);
                });
            });

            var state = await service.DownloadAsync(modelId, progress, _ppOcrV6DownloadCts.Token);
            if (state != PpOcrV6ModelState.Installed)
            {
                throw new InvalidDataException(
                    LocalizationService.Instance.GetString("PpOcrV6IntegrityValidationFailed"));
            }

            OnSettingChanged(sender, e);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            var loc = LocalizationService.Instance;
            var dialog = new ContentDialog
            {
                Title = loc.GetString("PpOcrV6DownloadFailedTitle"),
                Content = ex.Message,
                CloseButtonText = loc.GetString("OK"),
                XamlRoot = XamlRoot,
            };
            await ShowDialogAsync(dialog);
        }
        finally
        {
            _ppOcrV6DownloadCts.Dispose();
            _ppOcrV6DownloadCts = null;
            PpOcrV6ModelCombo.IsEnabled = true;
            PpOcrV6DownloadProgress.Visibility = Visibility.Collapsed;
            PpOcrV6DownloadProgressText.Visibility = Visibility.Collapsed;
            UpdatePpOcrV6ModelUi();
        }
    }

    private async Task RemovePpOcrV6ModelAsync(string modelId)
    {
        var loc = LocalizationService.Instance;
        var confirmDialog = new ContentDialog
        {
            Title = loc.GetString("PpOcrV6RemoveTitle"),
            Content = loc.GetString("PpOcrV6RemoveMessage", modelId),
            PrimaryButtonText = loc.GetString("PpOcrV6ActionRemove"),
            CloseButtonText = loc.GetString("Cancel"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        if (await ShowDialogAsync(confirmDialog) != ContentDialogResult.Primary)
        {
            return;
        }

        PpOcrV6DownloadButton.IsEnabled = false;
        try
        {
            await App.ReleasePpOcrV6ModelAsync(modelId).ConfigureAwait(true);
            using var service = new PpOcrV6ModelDownloadService();
            await service.RemoveAsync(modelId, _lifetimeCts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            var dialog = new ContentDialog
            {
                Title = loc.GetString("PpOcrV6RemoveFailedTitle"),
                Content = ex.Message,
                CloseButtonText = loc.GetString("OK"),
                XamlRoot = XamlRoot,
            };
            await ShowDialogAsync(dialog);
        }
        finally
        {
            PpOcrV6DownloadButton.IsEnabled = true;
            UpdatePpOcrV6ModelUi();
        }
    }

    private async void OnTestPpOcrV6Click(object sender, RoutedEventArgs e)
    {
        PpOcrV6TestButton.IsEnabled = false;
        var loc = LocalizationService.Instance;
        PpOcrV6TestStatusBox.Text = loc.GetString("PpOcrV6TestSelectRegion");

        try
        {
            var capture = await new ScreenCaptureService().CaptureRegionAsync();
            if (capture is null)
            {
                PpOcrV6TestStatusBox.Text = loc.GetString("PpOcrV6TestCancelled");
                return;
            }

            using (capture)
            await using (var ocr = new OcrWorkerClient(
                       SettingsService.Instance,
                       new WindowsOcrService(),
                       OcrEngineType.PpOcrV6,
                       GetSelectedPpOcrV6ModelId(),
                       GetPpOcrV6ThreadCount(),
                       allowFallback: false,
                       useGpu: PpOcrV6GpuToggle.IsOn))
            {
                PpOcrV6TestStatusBox.Text = loc.GetString(
                    PpOcrV6GpuToggle.IsOn
                        ? "PpOcrV6TestProcessingGpu"
                        : "PpOcrV6TestProcessingCpu");
                var result = await ocr.RecognizeAsync(capture);
                PpOcrV6TestStatusBox.Text = string.IsNullOrWhiteSpace(result.Text)
                    ? loc.GetString("PpOcrV6TestNoText")
                    : result.Text;
            }
        }
        catch (Exception ex)
        {
            PpOcrV6TestStatusBox.Text = loc.GetString("PpOcrV6TestError", ex.Message);
        }
        finally
        {
            PpOcrV6TestButton.IsEnabled = true;
        }
    }

    private int GetPpOcrV6ThreadCount()
    {
        var value = PpOcrV6ThreadCountBox.Value;
        return double.IsNaN(value)
            ? Math.Clamp(_settings.PpOcrV6ThreadCount, PpOcrV6ModelCatalog.MinThreadCount, PpOcrV6ModelCatalog.MaxThreadCount)
            : Math.Clamp((int)Math.Round(value), PpOcrV6ModelCatalog.MinThreadCount, PpOcrV6ModelCatalog.MaxThreadCount);
    }

    private void SavePpOcrV6Settings()
    {
        if (PpOcrV6ModelCombo.Items.Count == 0)
        {
            return;
        }

        if (GetSelectedOcrEngine() == OcrEngineType.PpOcrV6)
        {
            var modelId = GetSelectedPpOcrV6ModelId();
            if (_ppOcrV6ModelStore.GetStateBySize(modelId) == PpOcrV6ModelState.Installed)
            {
                _settings.PpOcrV6ModelId = modelId;
            }
        }
        _settings.PpOcrV6ThreadCount = GetPpOcrV6ThreadCount();
        _settings.PpOcrV6UseGpu = PpOcrV6GpuToggle.IsOn;
        _settings.PpOcrV6AllowFallback = PpOcrV6FallbackToggle.IsOn;
    }

    private bool PpOcrV6SettingsDifferFromSettings()
    {
        return GetSelectedOcrEngine() == OcrEngineType.PpOcrV6
            && (!string.Equals(GetSelectedPpOcrV6ModelId(), _settings.PpOcrV6ModelId, StringComparison.Ordinal)
                || GetPpOcrV6ThreadCount() != _settings.PpOcrV6ThreadCount
                || PpOcrV6GpuToggle.IsOn != _settings.PpOcrV6UseGpu
                || PpOcrV6FallbackToggle.IsOn != _settings.PpOcrV6AllowFallback);
    }
}
