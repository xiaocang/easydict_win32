using System.Diagnostics;
using Easydict.SidecarClient.Protocol;
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

    private void InitializePpOcrV6Settings()
    {
        _ppOcrV6UiLoading = true;
        try
        {
            PpOcrV6ModelCombo.Items.Clear();
            foreach (var model in PpOcrV6ModelCatalog.Models)
            {
                PpOcrV6ModelCombo.Items.Add(new ComboBoxItem
                {
                    Content = model.DisplayName,
                    Tag = model.Id,
                });
            }

            var selectedModel = PpOcrV6ModelCatalog.TryGet(_settings.OcrModel, out _)
                ? _settings.OcrModel
                : PpOcrV6ModelCatalog.SmallId;
            SelectComboByTag(PpOcrV6ModelCombo, selectedModel);
            PpOcrV6ThreadCountBox.Value = Math.Clamp(_settings.PpOcrV6ThreadCount, 1, 16);
            PpOcrV6GpuToggle.IsOn = _settings.PpOcrV6UseGpu;
            PpOcrV6GpuToggle.IsEnabled = true;
            PpOcrV6FallbackToggle.IsOn = _settings.PpOcrV6AllowFallback;
        }
        finally
        {
            _ppOcrV6UiLoading = false;
        }

        UpdatePpOcrV6ModelUi();
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
        var modelId = GetSelectedPpOcrV6ModelId();
        var model = PpOcrV6ModelCatalog.Get(modelId);
        PpOcrV6DownloadSizeText.Text = $"Download: {model.DownloadSizeBytes / 1_000_000d:F1} MB";
        PpOcrV6LanguagesText.Text = $"Languages: {model.Languages.Count}";
        PpOcrV6ModelStatusText.Text = "Checking model...";
        PpOcrV6DownloadButton.Visibility = Visibility.Collapsed;
        var version = ++_ppOcrV6StatusVersion;
        _ = RefreshPpOcrV6ModelStatusAsync(modelId, version);
    }

    private async Task RefreshPpOcrV6ModelStatusAsync(string modelId, int version)
    {
        PpOcrV6ModelState state;
        using var httpClient = OcrServiceFactory.CreateProxyAwareHttpClient(
            _settings.ProxyEnabled,
            _settings.ProxyUri,
            _settings.ProxyBypassLocal);
        using var service = new PpOcrV6ModelDownloadService(httpClient);
        try
        {
            state = await service.Store.ValidateAsync(modelId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            state = PpOcrV6ModelState.Invalid;
            Debug.WriteLine($"[SettingsPage] PP-OCRv6 model validation failed: {ex.Message}");
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            if (version != _ppOcrV6StatusVersion || GetSelectedPpOcrV6ModelId() != modelId)
            {
                return;
            }

            switch (state)
            {
                case PpOcrV6ModelState.Installed:
                    PpOcrV6ModelStatusText.Text = "Downloaded";
                    PpOcrV6DownloadButton.Visibility = Visibility.Collapsed;
                    break;
                case PpOcrV6ModelState.Invalid:
                    PpOcrV6ModelStatusText.Text = "Model invalid — repair required";
                    PpOcrV6DownloadButton.Content = "Repair";
                    PpOcrV6DownloadButton.Visibility = Visibility.Visible;
                    break;
                default:
                    PpOcrV6ModelStatusText.Text = "Not downloaded";
                    PpOcrV6DownloadButton.Content = "Download";
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

    private void OnPpOcrV6ThreadCountChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_ppOcrV6UiLoading)
        {
            return;
        }

        OnSettingChanged(sender, args);
    }

    private void OnPpOcrV6GpuToggled(object sender, RoutedEventArgs e)
    {
        if (_ppOcrV6UiLoading)
        {
            return;
        }

        OnSettingChanged(sender, e);
    }

    private void OnPpOcrV6FallbackToggled(object sender, RoutedEventArgs e)
    {
        if (_ppOcrV6UiLoading)
        {
            return;
        }

        OnSettingChanged(sender, e);
    }

    private async void OnDownloadPpOcrV6ModelClick(object sender, RoutedEventArgs e)
    {
        var modelId = GetSelectedPpOcrV6ModelId();
        PpOcrV6DownloadButton.IsEnabled = false;
        PpOcrV6ModelCombo.IsEnabled = false;
        PpOcrV6DownloadProgress.Visibility = Visibility.Visible;
        PpOcrV6DownloadProgressText.Visibility = Visibility.Visible;
        PpOcrV6ModelStatusText.Text = "Downloading...";

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
                    PpOcrV6DownloadProgress.IsIndeterminate = value.Percentage < 0;
                    if (value.Percentage >= 0)
                    {
                        PpOcrV6DownloadProgress.Value = value.Percentage;
                    }
                    PpOcrV6DownloadProgressText.Text =
                        $"{value.BytesDownloaded / 1_000_000d:F1} / {value.TotalBytes / 1_000_000d:F1} MB";
                });
            });

            var state = await service.DownloadAsync(modelId, progress);
            if (state != PpOcrV6ModelState.Installed)
            {
                throw new InvalidDataException("PP-OCRv6 model did not pass final integrity validation.");
            }

            _settings.OcrModel = modelId;
            _settings.Save();
            UpdatePpOcrV6ModelUi();
        }
        catch (Exception ex)
        {
            PpOcrV6ModelStatusText.Text = "Download failed";
            var dialog = new ContentDialog
            {
                Title = "PP-OCRv6 download failed",
                Content = ex.Message,
                CloseButtonText = "OK",
                XamlRoot = XamlRoot,
            };
            await ShowDialogAsync(dialog);
        }
        finally
        {
            PpOcrV6DownloadButton.IsEnabled = true;
            PpOcrV6ModelCombo.IsEnabled = true;
            PpOcrV6DownloadProgress.Visibility = Visibility.Collapsed;
            PpOcrV6DownloadProgressText.Visibility = Visibility.Collapsed;
        }
    }

    private async void OnTestPpOcrV6Click(object sender, RoutedEventArgs e)
    {
        PpOcrV6TestButton.IsEnabled = false;
        PpOcrV6TestStatusBox.Text = "Select a region on your screen to test PP-OCRv6...";

        try
        {
            var capture = await new ScreenCaptureService().CaptureRegionAsync();
            if (capture is null)
            {
                PpOcrV6TestStatusBox.Text = "Test cancelled.";
                return;
            }

            using (capture)
            using (var ocr = new OcrWorkerClient(
                       SettingsService.Instance,
                       new WindowsOcrService(),
                       OcrEngineType.PpOcrV6,
                       GetSelectedPpOcrV6ModelId(),
                       GetPpOcrV6ThreadCount(),
                       allowFallback: false,
                       useGpu: PpOcrV6GpuToggle.IsOn))
            {
                PpOcrV6TestStatusBox.Text = PpOcrV6GpuToggle.IsOn
                    ? "Processing with PP-OCRv6 GPU..."
                    : "Processing with PP-OCRv6 CPU...";
                var result = await ocr.RecognizeAsync(capture);
                PpOcrV6TestStatusBox.Text = string.IsNullOrWhiteSpace(result.Text)
                    ? "Success: image processed, but no text was recognized."
                    : result.Text;
            }
        }
        catch (Exception ex)
        {
            PpOcrV6TestStatusBox.Text = $"[Error] {ex.Message}";
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
            ? Math.Clamp(_settings.PpOcrV6ThreadCount, 1, 16)
            : Math.Clamp((int)Math.Round(value), 1, 16);
    }

    private void SavePpOcrV6Settings()
    {
        if (PpOcrV6ModelCombo.Items.Count == 0)
        {
            return;
        }

        if (GetSelectedOcrEngine() == OcrEngineType.PpOcrV6)
        {
            _settings.OcrModel = GetSelectedPpOcrV6ModelId();
        }
        _settings.PpOcrV6ThreadCount = GetPpOcrV6ThreadCount();
        _settings.PpOcrV6UseGpu = PpOcrV6GpuToggle.IsOn;
        _settings.PpOcrV6AllowFallback = PpOcrV6FallbackToggle.IsOn;
    }

    private bool PpOcrV6SettingsDifferFromSettings()
    {
        return GetSelectedOcrEngine() == OcrEngineType.PpOcrV6
            && (!string.Equals(GetSelectedPpOcrV6ModelId(), _settings.OcrModel, StringComparison.Ordinal)
                || GetPpOcrV6ThreadCount() != _settings.PpOcrV6ThreadCount
                || PpOcrV6GpuToggle.IsOn != _settings.PpOcrV6UseGpu
                || PpOcrV6FallbackToggle.IsOn != _settings.PpOcrV6AllowFallback);
    }
}
