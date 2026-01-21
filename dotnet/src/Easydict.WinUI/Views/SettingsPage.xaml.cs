using System.Text.Json;
using Easydict.WinUI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Easydict.WinUI.Views;

/// <summary>
/// Settings page for configuring translation services, hotkeys, and behavior.
/// </summary>
public sealed partial class SettingsPage : Page
{
    private readonly SettingsService _settings = SettingsService.Instance;
    private bool _isLoading = true; // Prevent change detection during initial load

    public SettingsPage()
    {
        this.InitializeComponent();
        this.Loaded += OnPageLoaded;
    }

    private void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        _isLoading = true;
        LoadSettings();
        RegisterChangeHandlers();
        _isLoading = false;
    }

    /// <summary>
    /// Register event handlers to detect settings changes.
    /// </summary>
    private void RegisterChangeHandlers()
    {
        // ComboBox changes
        ServiceCombo.SelectionChanged += OnSettingChanged;
        TargetLangCombo.SelectionChanged += OnSettingChanged;
        FirstLanguageCombo.SelectionChanged += OnSettingChanged;
        SecondLanguageCombo.SelectionChanged += OnSettingChanged;
        OpenAIModelCombo.SelectionChanged += OnSettingChanged;
        OllamaModelCombo.SelectionChanged += OnSettingChanged;
        BuiltInModelCombo.SelectionChanged += OnSettingChanged;

        // ToggleSwitch changes
        AutoSelectTargetToggle.Toggled += OnSettingChanged;
        MinimizeToTrayToggle.Toggled += OnSettingChanged;
        ClipboardMonitorToggle.Toggled += OnSettingChanged;
        AlwaysOnTopToggle.Toggled += OnSettingChanged;
        ProxyEnabledToggle.Toggled += OnSettingChanged;
        ProxyBypassLocalToggle.Toggled += OnSettingChanged;

        // TextBox/PasswordBox changes
        DeepLKeyBox.PasswordChanged += OnSettingChanged;
        OpenAIKeyBox.PasswordChanged += OnSettingChanged;
        OpenAIEndpointBox.TextChanged += OnSettingChanged;
        OllamaEndpointBox.TextChanged += OnSettingChanged;
        ProxyUriBox.TextChanged += OnSettingChanged;
        ShowHotkeyBox.TextChanged += OnSettingChanged;
        TranslateHotkeyBox.TextChanged += OnSettingChanged;

        // CheckBox changes
        DeepLFreeCheck.Checked += OnSettingChanged;
        DeepLFreeCheck.Unchecked += OnSettingChanged;
    }

    /// <summary>
    /// Show the floating save button when any setting changes.
    /// </summary>
    private void OnSettingChanged(object sender, object e)
    {
        if (_isLoading) return;
        SaveButton.Visibility = Visibility.Visible;
    }

    private void LoadSettings()
    {
        // Translation service
        SelectComboByTag(ServiceCombo, _settings.DefaultService);
        SelectComboByTag(TargetLangCombo, _settings.TargetLanguage);

        // Language preferences
        SelectComboByTag(FirstLanguageCombo, _settings.FirstLanguage);
        SelectComboByTag(SecondLanguageCombo, _settings.SecondLanguage);
        AutoSelectTargetToggle.IsOn = _settings.AutoSelectTargetLanguage;

        // API keys
        DeepLKeyBox.Password = _settings.DeepLApiKey ?? string.Empty;
        DeepLFreeCheck.IsChecked = _settings.DeepLUseFreeApi;

        // OpenAI settings
        OpenAIKeyBox.Password = _settings.OpenAIApiKey ?? string.Empty;
        OpenAIEndpointBox.Text = _settings.OpenAIEndpoint;
        SelectComboByTag(OpenAIModelCombo, _settings.OpenAIModel);

        // Ollama settings
        OllamaEndpointBox.Text = _settings.OllamaEndpoint;
        OllamaModelCombo.Text = _settings.OllamaModel;

        // Built-in AI settings
        SelectComboByTag(BuiltInModelCombo, _settings.BuiltInAIModel);

        // HTTP Proxy settings
        ProxyEnabledToggle.IsOn = _settings.ProxyEnabled;
        ProxyUriBox.Text = _settings.ProxyUri;
        ProxyBypassLocalToggle.IsOn = _settings.ProxyBypassLocal;

        // Behavior
        MinimizeToTrayToggle.IsOn = _settings.MinimizeToTray;
        ClipboardMonitorToggle.IsOn = _settings.ClipboardMonitoring;
        AlwaysOnTopToggle.IsOn = _settings.AlwaysOnTop;

        // Hotkeys
        ShowHotkeyBox.Text = _settings.ShowWindowHotkey;
        TranslateHotkeyBox.Text = _settings.TranslateSelectionHotkey;
    }

    private static void SelectComboByTag(ComboBox combo, string tag)
    {
        for (int i = 0; i < combo.Items.Count; i++)
        {
            if (combo.Items[i] is ComboBoxItem item && item.Tag?.ToString() == tag)
            {
                combo.SelectedIndex = i;
                return;
            }
        }
        // Default to first item
        if (combo.Items.Count > 0)
            combo.SelectedIndex = 0;
    }

    private static string? GetSelectedTag(ComboBox combo)
    {
        if (combo.SelectedItem is ComboBoxItem item)
        {
            return item.Tag?.ToString();
        }
        return null;
    }

    private void OnBackClick(object sender, RoutedEventArgs e)
    {
        if (Frame.CanGoBack)
        {
            Frame.GoBack();
        }
    }

    private async void OnSaveClick(object sender, RoutedEventArgs e)
    {
        // Capture original proxy settings to detect changes
        var originalProxyEnabled = _settings.ProxyEnabled;
        var originalProxyUri = _settings.ProxyUri;
        var originalProxyBypassLocal = _settings.ProxyBypassLocal;

        // Save translation settings
        _settings.DefaultService = GetSelectedTag(ServiceCombo) ?? "google";
        _settings.TargetLanguage = GetSelectedTag(TargetLangCombo) ?? "zh";

        // Save language preferences with validation
        var firstLang = GetSelectedTag(FirstLanguageCombo) ?? "zh";
        var secondLang = GetSelectedTag(SecondLanguageCombo) ?? "en";

        // Validate: FirstLanguage and SecondLanguage cannot be the same
        if (firstLang == secondLang)
        {
            var errorDialog = new ContentDialog
            {
                Title = "Invalid Language Selection",
                Content = "First Language and Second Language cannot be the same. Please choose different languages.",
                CloseButtonText = "OK",
                XamlRoot = this.XamlRoot
            };
            await errorDialog.ShowAsync();
            return;
        }

        _settings.FirstLanguage = firstLang;
        _settings.SecondLanguage = secondLang;
        _settings.AutoSelectTargetLanguage = AutoSelectTargetToggle.IsOn;

        // Save API keys
        var apiKey = DeepLKeyBox.Password;
        _settings.DeepLApiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey;
        _settings.DeepLUseFreeApi = DeepLFreeCheck.IsChecked ?? true;

        // Save OpenAI settings
        var openAIKey = OpenAIKeyBox.Password;
        _settings.OpenAIApiKey = string.IsNullOrWhiteSpace(openAIKey) ? null : openAIKey;
        var openAIEndpoint = OpenAIEndpointBox.Text?.Trim();
        _settings.OpenAIEndpoint = string.IsNullOrWhiteSpace(openAIEndpoint)
            ? "https://api.openai.com/v1/chat/completions"
            : openAIEndpoint;
        _settings.OpenAIModel = GetSelectedTag(OpenAIModelCombo) ?? "gpt-4o-mini";

        // Save Ollama settings
        var ollamaEndpoint = OllamaEndpointBox.Text?.Trim();
        _settings.OllamaEndpoint = string.IsNullOrWhiteSpace(ollamaEndpoint)
            ? "http://localhost:11434/v1/chat/completions"
            : ollamaEndpoint;
        _settings.OllamaModel = OllamaModelCombo.Text?.Trim() ?? "llama3.2";

        // Save Built-in AI settings
        _settings.BuiltInAIModel = GetSelectedTag(BuiltInModelCombo) ?? "llama-3.3-70b-versatile";

        // Save HTTP Proxy settings with validation
        _settings.ProxyEnabled = ProxyEnabledToggle.IsOn;
        _settings.ProxyBypassLocal = ProxyBypassLocalToggle.IsOn;

        var proxyUri = ProxyUriBox.Text?.Trim() ?? "";
        if (_settings.ProxyEnabled && !string.IsNullOrWhiteSpace(proxyUri))
        {
            if (!Uri.TryCreate(proxyUri, UriKind.Absolute, out _))
            {
                var errorDialog = new ContentDialog
                {
                    Title = "Invalid Proxy URL",
                    Content = "The proxy URL is not valid. Please enter a valid URL (e.g., http://127.0.0.1:7890).",
                    CloseButtonText = "OK",
                    XamlRoot = this.XamlRoot
                };
                await errorDialog.ShowAsync();
                return;
            }
        }
        _settings.ProxyUri = proxyUri;

        // Save behavior settings
        _settings.MinimizeToTray = MinimizeToTrayToggle.IsOn;
        _settings.ClipboardMonitoring = ClipboardMonitorToggle.IsOn;
        _settings.AlwaysOnTop = AlwaysOnTopToggle.IsOn;

        // Save hotkey settings
        _settings.ShowWindowHotkey = ShowHotkeyBox.Text;
        _settings.TranslateSelectionHotkey = TranslateHotkeyBox.Text;

        // Persist to storage
        _settings.Save();

        // If proxy settings changed, recreate manager with new proxy (includes service configuration)
        // Otherwise, just reconfigure services with new settings (API keys, models, endpoints)
        var proxyChanged = originalProxyEnabled != _settings.ProxyEnabled ||
                           originalProxyUri != _settings.ProxyUri ||
                           originalProxyBypassLocal != _settings.ProxyBypassLocal;
        if (proxyChanged)
        {
            TranslationManagerService.Instance.ReconfigureProxy();
        }
        else
        {
            TranslationManagerService.Instance.ReconfigureServices();
        }

        // Apply always-on-top setting immediately
        App.ApplyAlwaysOnTop(_settings.AlwaysOnTop);

        // Apply clipboard monitoring immediately
        App.ApplyClipboardMonitoring(_settings.ClipboardMonitoring);

        // Hide the floating save button
        SaveButton.Visibility = Visibility.Collapsed;

        // Show confirmation
        var dialog = new ContentDialog
        {
            Title = "Settings Saved",
            Content = "Your settings have been saved. Hotkey changes require an app restart to take effect.",
            CloseButtonText = "OK",
            XamlRoot = this.XamlRoot
        };
        await dialog.ShowAsync();
    }

    /// <summary>
    /// Refreshes the available Ollama models from the local server.
    /// </summary>
    private async void OnRefreshOllamaModels(object sender, RoutedEventArgs e)
    {
        RefreshOllamaButton.IsEnabled = false;
        try
        {
            // Extract base URL from endpoint
            var endpoint = OllamaEndpointBox.Text?.Trim() ?? "http://localhost:11434/v1/chat/completions";
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
            {
                uri = new Uri("http://localhost:11434");
            }
            var baseUrl = $"{uri.Scheme}://{uri.Host}:{uri.Port}";
            var tagsUrl = $"{baseUrl}/api/tags";

            using var httpClient = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var response = await httpClient.GetStringAsync(tagsUrl);

            // Parse JSON response: {"models": [{"name": "llama3.2"}, ...]}
            using var doc = JsonDocument.Parse(response);
            if (doc.RootElement.TryGetProperty("models", out var models))
            {
                var currentSelection = OllamaModelCombo.Text;
                OllamaModelCombo.Items.Clear();

                foreach (var model in models.EnumerateArray())
                {
                    if (model.TryGetProperty("name", out var nameElement))
                    {
                        var name = nameElement.GetString();
                        if (!string.IsNullOrEmpty(name))
                        {
                            OllamaModelCombo.Items.Add(new ComboBoxItem { Content = name, Tag = name });
                        }
                    }
                }

                // Restore selection or select first item
                if (!string.IsNullOrEmpty(currentSelection))
                {
                    OllamaModelCombo.Text = currentSelection;
                }
                else if (OllamaModelCombo.Items.Count > 0)
                {
                    OllamaModelCombo.SelectedIndex = 0;
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Settings] Failed to refresh Ollama models: {ex.Message}");
            var errorDialog = new ContentDialog
            {
                Title = "Cannot Connect to Ollama",
                Content = $"Failed to fetch models from Ollama server. Make sure Ollama is running.\n\nError: {ex.Message}",
                CloseButtonText = "OK",
                XamlRoot = this.XamlRoot
            };
            await errorDialog.ShowAsync();
        }
        finally
        {
            RefreshOllamaButton.IsEnabled = true;
        }
    }
}
