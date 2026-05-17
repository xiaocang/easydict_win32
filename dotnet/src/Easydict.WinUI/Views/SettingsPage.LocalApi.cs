using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;
using Easydict.TranslationService;
using Easydict.TranslationService.LocalApi;
using Easydict.WinUI.Models;
using Easydict.WinUI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace Easydict.WinUI.Views;

public sealed partial class SettingsPage
{
    private readonly ObservableCollection<ExposedServiceItem> _localApiServiceItems = new();
    private bool _localApiSuppressEvents;

    private void InitializeLocalApiPanel()
    {
        _localApiSuppressEvents = true;
        try
        {
            var s = SettingsService.Instance;

            LocalApiEnabledToggle.IsOn = s.LocalApiEnabled;
            LocalApiConfigPanel.Visibility = s.LocalApiEnabled ? Visibility.Visible : Visibility.Collapsed;

            LocalApiPortBox.Value = s.LocalApiPort;
            LocalApiTokenBox.Text = string.IsNullOrEmpty(s.LocalApiToken)
                ? "(generated on enable)"
                : s.LocalApiToken;
            LocalApiEndpointBox.Text = $"http://127.0.0.1:{s.LocalApiPort}/v1";

            // Populate exposed-services list from currently configured services.
            _localApiServiceItems.Clear();
            var manager = TranslationManagerService.Instance.Manager;
            var exposedSet = new HashSet<string>(s.LocalApiExposedServices, StringComparer.Ordinal);
            foreach (var svc in manager.Services.Values)
            {
                if (!svc.IsConfigured) continue;
                var item = new ExposedServiceItem
                {
                    ServiceId = svc.ServiceId,
                    DisplayName = svc.DisplayName,
                    SupportsStreaming = svc is IStreamTranslationService,
                    IsExposed = exposedSet.Contains(svc.ServiceId),
                };
                item.PropertyChanged += OnLocalApiServiceItemChanged;
                _localApiServiceItems.Add(item);
            }
            LocalApiServicesList.ItemsSource = _localApiServiceItems;

            // CORS mode
            var allowList = string.Equals(s.LocalApiCorsMode, "AllowList", StringComparison.OrdinalIgnoreCase);
            LocalApiCorsAllowListRadio.IsChecked = allowList;
            LocalApiCorsAnyRadio.IsChecked = !allowList;
            LocalApiCorsAllowListBox.Text = string.Join(", ", s.LocalApiCorsAllowList);
            LocalApiCorsAllowListBox.Visibility = allowList ? Visibility.Visible : Visibility.Collapsed;
            LocalApiCorsAnyWarning.IsOpen = !allowList && s.LocalApiEnabled;

            ApplyLocalApiRunningStatus();
            HookCoordinator();
        }
        finally
        {
            _localApiSuppressEvents = false;
        }
    }

    private LocalApiCoordinator? _localApiCoordinatorRef;

    private void TeardownLocalApiPanel()
    {
        if (_localApiCoordinatorRef is not null)
        {
            _localApiCoordinatorRef.StateChanged -= OnLocalApiStateChanged;
            _localApiCoordinatorRef = null;
        }
        foreach (var item in _localApiServiceItems)
        {
            item.PropertyChanged -= OnLocalApiServiceItemChanged;
        }
        _localApiServiceItems.Clear();
    }

    private void HookCoordinator()
    {
        var coord = App.LocalApiCoordinator;
        if (coord is null || _localApiCoordinatorRef == coord) return;

        if (_localApiCoordinatorRef is not null)
            _localApiCoordinatorRef.StateChanged -= OnLocalApiStateChanged;

        _localApiCoordinatorRef = coord;
        coord.StateChanged += OnLocalApiStateChanged;
    }

    private void OnLocalApiStateChanged(object? sender, LocalApiStateChangedEventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            // The token may have been auto-generated on first enable; refresh display.
            UpdateLocalApiTokenDisplay();
            ApplyLocalApiRunningStatus(e);
        });
    }

    private void ApplyLocalApiRunningStatus(LocalApiStateChangedEventArgs? evt = null)
    {
        if (LocalApiStatusInfo is null) return;

        var coord = _localApiCoordinatorRef ?? App.LocalApiCoordinator;
        var running = coord?.IsRunning ?? false;
        var url = coord?.CurrentBaseUrl;

        if (evt is { LastChangeSucceeded: false, LastErrorMessage: { } err })
        {
            LocalApiStatusInfo.Severity = InfoBarSeverity.Error;
            LocalApiStatusInfo.Title = "Failed to start";
            LocalApiStatusInfo.Message = err;
            LocalApiStatusInfo.IsOpen = true;
        }
        else if (running && !string.IsNullOrEmpty(url))
        {
            LocalApiStatusInfo.Severity = InfoBarSeverity.Success;
            LocalApiStatusInfo.Title = "Running";
            LocalApiStatusInfo.Message = url + "/v1";
            LocalApiStatusInfo.IsOpen = true;
            LocalApiEndpointBox.Text = url + "/v1";
        }
        else if (SettingsService.Instance.LocalApiEnabled)
        {
            LocalApiStatusInfo.Severity = InfoBarSeverity.Informational;
            LocalApiStatusInfo.Title = "Starting";
            LocalApiStatusInfo.Message = string.Empty;
            LocalApiStatusInfo.IsOpen = true;
        }
        else
        {
            LocalApiStatusInfo.IsOpen = false;
        }
    }

    // ---------------- event handlers ----------------

    private void OnLocalApiEnabledToggled(object sender, RoutedEventArgs e)
    {
        if (_localApiSuppressEvents) return;
        var on = LocalApiEnabledToggle.IsOn;
        LocalApiConfigPanel.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        SettingsService.Instance.LocalApiEnabled = on;
        SettingsService.Instance.Save();
        UpdateLocalApiTokenDisplay();
        App.LocalApiCoordinator?.NotifySettingsChanged();
        LocalApiCorsAnyWarning.IsOpen = on && string.Equals(
            SettingsService.Instance.LocalApiCorsMode, "Any", StringComparison.OrdinalIgnoreCase);
    }

    private void OnLocalApiPortChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_localApiSuppressEvents) return;
        if (double.IsNaN(args.NewValue)) return;
        var port = (int)args.NewValue;
        if (port < 1024 || port > 65535) return;
        SettingsService.Instance.LocalApiPort = port;
        SettingsService.Instance.Save();
        LocalApiEndpointBox.Text = $"http://127.0.0.1:{port}/v1";
        App.LocalApiCoordinator?.NotifySettingsChanged();
    }

    private void OnLocalApiCopyTokenClick(object sender, RoutedEventArgs e)
    {
        var token = SettingsService.Instance.LocalApiToken;
        if (string.IsNullOrEmpty(token)) return;
        CopyToClipboard(token);
    }

    private void OnLocalApiRegenTokenClick(object sender, RoutedEventArgs e)
    {
        SettingsService.Instance.LocalApiToken = LocalApiTokenGenerator.Generate();
        SettingsService.Instance.Save();
        UpdateLocalApiTokenDisplay();
        App.LocalApiCoordinator?.NotifySettingsChanged();
    }

    private void OnLocalApiCopyEndpointClick(object sender, RoutedEventArgs e)
    {
        CopyToClipboard(LocalApiEndpointBox.Text);
    }

    private void OnLocalApiServiceItemChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_localApiSuppressEvents) return;
        if (e.PropertyName != nameof(ExposedServiceItem.IsExposed)) return;

        SettingsService.Instance.LocalApiExposedServices = _localApiServiceItems
            .Where(i => i.IsExposed)
            .Select(i => i.ServiceId)
            .ToList();
        SettingsService.Instance.Save();
        App.LocalApiCoordinator?.NotifySettingsChanged();
    }

    private void OnLocalApiCorsModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_localApiSuppressEvents) return;
        if (sender is not RadioButtons radios) return;
        var tag = (radios.SelectedItem as FrameworkElement)?.Tag as string;
        if (tag is null) return;
        var allowList = string.Equals(tag, "AllowList", StringComparison.OrdinalIgnoreCase);
        SettingsService.Instance.LocalApiCorsMode = allowList ? "AllowList" : "Any";
        SettingsService.Instance.Save();
        LocalApiCorsAllowListBox.Visibility = allowList ? Visibility.Visible : Visibility.Collapsed;
        LocalApiCorsAnyWarning.IsOpen = !allowList && LocalApiEnabledToggle.IsOn;
        App.LocalApiCoordinator?.NotifySettingsChanged();
    }

    private void OnLocalApiCorsAllowListLostFocus(object sender, RoutedEventArgs e)
    {
        if (_localApiSuppressEvents) return;
        var raw = LocalApiCorsAllowListBox.Text ?? string.Empty;
        var origins = raw.Split(new[] { ',', ';', '\n', '\r', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(o => o.Trim().TrimEnd('/'))
            .Where(o => o.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        SettingsService.Instance.LocalApiCorsAllowList = origins;
        SettingsService.Instance.Save();
        App.LocalApiCoordinator?.NotifySettingsChanged();
    }

    private void OnLocalApiCopyKissHookClick(object sender, RoutedEventArgs e)
    {
        var s = SettingsService.Instance;
        var endpoint = $"http://127.0.0.1:{s.LocalApiPort}/v1/chat/completions";
        var anyModel = _localApiServiceItems.FirstOrDefault(i => i.IsExposed);
        var modelId = LocalApiServer.ModelIdPrefix + (anyModel?.ServiceId ?? "openai");

        var sb = new StringBuilder();
        sb.AppendLine("=== Easydict for KISS Translator ===");
        sb.AppendLine($"Interface URL: {endpoint}");
        sb.AppendLine($"API Key:       {s.LocalApiToken}");
        sb.AppendLine($"Model:         {modelId}");
        sb.AppendLine();
        sb.AppendLine("Request Hook (paste into KISS custom interface → Hook):");
        sb.AppendLine("(text, from, to, url, key) => [url, {");
        sb.AppendLine("  method: \"POST\",");
        sb.AppendLine("  headers: {");
        sb.AppendLine("    \"Content-Type\": \"application/json\",");
        sb.AppendLine("    \"Authorization\": `Bearer ${key}`");
        sb.AppendLine("  },");
        sb.AppendLine("  body: {");
        sb.AppendLine($"    model: \"{modelId}\",");
        sb.AppendLine("    stream: true,");
        sb.AppendLine("    messages: [");
        sb.AppendLine("      { role: \"user\", content: text }");
        sb.AppendLine("    ],");
        sb.AppendLine("    extra_body: { easydict: { source_language: from, target_language: to } }");
        sb.AppendLine("  }");
        sb.AppendLine("}]");
        sb.AppendLine();
        sb.AppendLine("Response Hook: use the default OpenAI hook (Easydict emits standard OpenAI SSE).");

        CopyToClipboard(sb.ToString());
    }

    private async void OnLocalApiOpenKissSettingsClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await Launcher.LaunchUriAsync(new Uri("https://fishjar.github.io/kiss-translator/options.html"));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SettingsPage] open KISS options failed: {ex.Message}");
        }
    }

    private void UpdateLocalApiTokenDisplay()
    {
        var token = SettingsService.Instance.LocalApiToken;
        LocalApiTokenBox.Text = string.IsNullOrEmpty(token)
            ? "(generated on enable)"
            : token;
    }

    private static void CopyToClipboard(string text)
    {
        try
        {
            var pkg = new DataPackage();
            pkg.SetText(text);
            Clipboard.SetContent(pkg);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SettingsPage] clipboard copy failed: {ex.Message}");
        }
    }
}
