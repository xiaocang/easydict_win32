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
    private bool _handlersRegistered; // Guard to prevent duplicate event handler registration

    public SettingsPage()
    {
        this.InitializeComponent();
        this.Loaded += OnPageLoaded;
    }

    private void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        _isLoading = true;
        LoadSettings();
        if (!_handlersRegistered)
        {
            RegisterChangeHandlers();
            _handlersRegistered = true;
        }
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
        DeepSeekModelCombo.SelectionChanged += OnSettingChanged;
        GroqModelCombo.SelectionChanged += OnSettingChanged;
        ZhipuModelCombo.SelectionChanged += OnSettingChanged;
        GitHubModelsModelCombo.SelectionChanged += OnSettingChanged;
        GeminiModelCombo.SelectionChanged += OnSettingChanged;

        // ToggleSwitch changes
        AutoSelectTargetToggle.Toggled += OnSettingChanged;
        MinimizeToTrayToggle.Toggled += OnSettingChanged;
        ClipboardMonitorToggle.Toggled += OnSettingChanged;
        AlwaysOnTopToggle.Toggled += OnSettingChanged;
        ProxyEnabledToggle.Toggled += OnSettingChanged;
        ProxyBypassLocalToggle.Toggled += OnSettingChanged;

        // TextBox/PasswordBox changes - existing
        DeepLKeyBox.PasswordChanged += OnSettingChanged;
        OpenAIKeyBox.PasswordChanged += OnSettingChanged;
        OpenAIEndpointBox.TextChanged += OnSettingChanged;
        OllamaEndpointBox.TextChanged += OnSettingChanged;
        ProxyUriBox.TextChanged += OnSettingChanged;
        ShowHotkeyBox.TextChanged += OnSettingChanged;
        TranslateHotkeyBox.TextChanged += OnSettingChanged;

        // TextBox/PasswordBox changes - new services
        DeepSeekKeyBox.PasswordChanged += OnSettingChanged;
        GroqKeyBox.PasswordChanged += OnSettingChanged;
        ZhipuKeyBox.PasswordChanged += OnSettingChanged;
        GitHubModelsTokenBox.PasswordChanged += OnSettingChanged;
        GeminiKeyBox.PasswordChanged += OnSettingChanged;
        CustomOpenAIEndpointBox.TextChanged += OnSettingChanged;
        CustomOpenAIKeyBox.PasswordChanged += OnSettingChanged;
        CustomOpenAIModelBox.TextChanged += OnSettingChanged;
        DoubaoKeyBox.PasswordChanged += OnSettingChanged;
        DoubaoEndpointBox.TextChanged += OnSettingChanged;
        DoubaoModelBox.TextChanged += OnSettingChanged;
        CaiyunKeyBox.PasswordChanged += OnSettingChanged;
        NiuTransKeyBox.PasswordChanged += OnSettingChanged;

        // CheckBox changes
        DeepLFreeCheck.Checked += OnSettingChanged;
        DeepLFreeCheck.Unchecked += OnSettingChanged;

        // Service selection checkboxes for each window
        RegisterServiceCheckBoxHandlers(MainWindowServicesPanel);
        RegisterServiceCheckBoxHandlers(MiniWindowServicesPanel);
        RegisterServiceCheckBoxHandlers(FixedWindowServicesPanel);
    }

    /// <summary>
    /// Register change handlers for all checkboxes in a panel.
    /// </summary>
    private void RegisterServiceCheckBoxHandlers(StackPanel panel)
    {
        foreach (var checkBox in panel.Children.OfType<CheckBox>())
        {
            checkBox.Checked += OnSettingChanged;
            checkBox.Unchecked += OnSettingChanged;
        }
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

        // DeepL settings
        DeepLKeyBox.Password = _settings.DeepLApiKey ?? string.Empty;
        DeepLFreeCheck.IsChecked = _settings.DeepLUseFreeApi;

        // OpenAI settings
        OpenAIKeyBox.Password = _settings.OpenAIApiKey ?? string.Empty;
        OpenAIEndpointBox.Text = _settings.OpenAIEndpoint;
        SetEditableComboValue(OpenAIModelCombo, _settings.OpenAIModel);

        // DeepSeek settings
        DeepSeekKeyBox.Password = _settings.DeepSeekApiKey ?? string.Empty;
        SetEditableComboValue(DeepSeekModelCombo, _settings.DeepSeekModel);

        // Groq settings
        GroqKeyBox.Password = _settings.GroqApiKey ?? string.Empty;
        SetEditableComboValue(GroqModelCombo, _settings.GroqModel);

        // Zhipu settings
        ZhipuKeyBox.Password = _settings.ZhipuApiKey ?? string.Empty;
        SetEditableComboValue(ZhipuModelCombo, _settings.ZhipuModel);

        // GitHub Models settings
        GitHubModelsTokenBox.Password = _settings.GitHubModelsToken ?? string.Empty;
        SetEditableComboValue(GitHubModelsModelCombo, _settings.GitHubModelsModel);

        // Gemini settings
        GeminiKeyBox.Password = _settings.GeminiApiKey ?? string.Empty;
        SetEditableComboValue(GeminiModelCombo, _settings.GeminiModel);

        // Custom OpenAI settings
        CustomOpenAIEndpointBox.Text = _settings.CustomOpenAIEndpoint;
        CustomOpenAIKeyBox.Password = _settings.CustomOpenAIApiKey ?? string.Empty;
        CustomOpenAIModelBox.Text = _settings.CustomOpenAIModel;

        // Ollama settings
        OllamaEndpointBox.Text = _settings.OllamaEndpoint;
        OllamaModelCombo.Text = _settings.OllamaModel;

        // Built-in AI settings
        SetEditableComboValue(BuiltInModelCombo, _settings.BuiltInAIModel);

        // Doubao settings
        DoubaoKeyBox.Password = _settings.DoubaoApiKey ?? string.Empty;
        DoubaoEndpointBox.Text = _settings.DoubaoEndpoint;
        DoubaoModelBox.Text = _settings.DoubaoModel;

        // Caiyun settings
        CaiyunKeyBox.Password = _settings.CaiyunApiKey ?? string.Empty;

        // NiuTrans settings
        NiuTransKeyBox.Password = _settings.NiuTransApiKey ?? string.Empty;

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

        // Enabled services for each window
        LoadServiceCheckboxes(MainWindowServicesPanel, _settings.MainWindowEnabledServices);
        LoadServiceCheckboxes(MiniWindowServicesPanel, _settings.MiniWindowEnabledServices);
        LoadServiceCheckboxes(FixedWindowServicesPanel, _settings.FixedWindowEnabledServices);
    }

    /// <summary>
    /// Load enabled services into checkboxes for a window panel.
    /// </summary>
    private static void LoadServiceCheckboxes(StackPanel panel, List<string> enabledServices)
    {
        foreach (var checkBox in panel.Children.OfType<CheckBox>().Where(cb => cb.Tag is string))
        {
            var serviceId = (string)checkBox.Tag!;
            checkBox.IsChecked = enabledServices.Contains(serviceId);
        }
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

    /// <summary>
    /// Gets the value from an editable ComboBox. Returns the typed text if available,
    /// otherwise returns the selected item's tag.
    /// </summary>
    private static string GetEditableComboValue(ComboBox combo, string defaultValue)
    {
        // For editable ComboBox, prefer Text (user-typed value)
        var text = combo.Text?.Trim();
        if (!string.IsNullOrEmpty(text))
        {
            return text;
        }
        // Fall back to selected item's tag
        if (combo.SelectedItem is ComboBoxItem item && item.Tag != null)
        {
            return item.Tag.ToString() ?? defaultValue;
        }
        return defaultValue;
    }

    /// <summary>
    /// Sets the value for an editable ComboBox. If the value matches a dropdown item,
    /// selects it. Otherwise, sets the Text property for custom values.
    /// </summary>
    private static void SetEditableComboValue(ComboBox combo, string value)
    {
        // Try to find matching item in dropdown
        for (int i = 0; i < combo.Items.Count; i++)
        {
            if (combo.Items[i] is ComboBoxItem item && item.Tag?.ToString() == value)
            {
                combo.SelectedIndex = i;
                return;
            }
        }
        // Custom value - set Text directly
        combo.Text = value;
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

        // Save DeepL settings
        var deepLKey = DeepLKeyBox.Password;
        _settings.DeepLApiKey = string.IsNullOrWhiteSpace(deepLKey) ? null : deepLKey;
        _settings.DeepLUseFreeApi = DeepLFreeCheck.IsChecked ?? true;

        // Save OpenAI settings
        var openAIKey = OpenAIKeyBox.Password;
        _settings.OpenAIApiKey = string.IsNullOrWhiteSpace(openAIKey) ? null : openAIKey;
        var openAIEndpoint = OpenAIEndpointBox.Text?.Trim();
        _settings.OpenAIEndpoint = string.IsNullOrWhiteSpace(openAIEndpoint)
            ? "https://api.openai.com/v1/chat/completions"
            : openAIEndpoint;
        _settings.OpenAIModel = GetEditableComboValue(OpenAIModelCombo, "gpt-4o-mini");

        // Save DeepSeek settings
        var deepSeekKey = DeepSeekKeyBox.Password;
        _settings.DeepSeekApiKey = string.IsNullOrWhiteSpace(deepSeekKey) ? null : deepSeekKey;
        _settings.DeepSeekModel = GetEditableComboValue(DeepSeekModelCombo, "deepseek-chat");

        // Save Groq settings
        var groqKey = GroqKeyBox.Password;
        _settings.GroqApiKey = string.IsNullOrWhiteSpace(groqKey) ? null : groqKey;
        _settings.GroqModel = GetEditableComboValue(GroqModelCombo, "llama-3.3-70b-versatile");

        // Save Zhipu settings
        var zhipuKey = ZhipuKeyBox.Password;
        _settings.ZhipuApiKey = string.IsNullOrWhiteSpace(zhipuKey) ? null : zhipuKey;
        _settings.ZhipuModel = GetEditableComboValue(ZhipuModelCombo, "glm-4-flash-250414");

        // Save GitHub Models settings
        var githubToken = GitHubModelsTokenBox.Password;
        _settings.GitHubModelsToken = string.IsNullOrWhiteSpace(githubToken) ? null : githubToken;
        _settings.GitHubModelsModel = GetEditableComboValue(GitHubModelsModelCombo, "gpt-4.1");

        // Save Gemini settings
        var geminiKey = GeminiKeyBox.Password;
        _settings.GeminiApiKey = string.IsNullOrWhiteSpace(geminiKey) ? null : geminiKey;
        _settings.GeminiModel = GetEditableComboValue(GeminiModelCombo, "gemini-2.5-flash");

        // Save Custom OpenAI settings
        var customEndpoint = CustomOpenAIEndpointBox.Text?.Trim() ?? "";
        _settings.CustomOpenAIEndpoint = customEndpoint;
        var customKey = CustomOpenAIKeyBox.Password;
        _settings.CustomOpenAIApiKey = string.IsNullOrWhiteSpace(customKey) ? null : customKey;
        var customModel = CustomOpenAIModelBox.Text?.Trim();
        _settings.CustomOpenAIModel = string.IsNullOrWhiteSpace(customModel) ? "gpt-3.5-turbo" : customModel;

        // Save Ollama settings
        var ollamaEndpoint = OllamaEndpointBox.Text?.Trim();
        _settings.OllamaEndpoint = string.IsNullOrWhiteSpace(ollamaEndpoint)
            ? "http://localhost:11434/v1/chat/completions"
            : ollamaEndpoint;
        _settings.OllamaModel = OllamaModelCombo.Text?.Trim() ?? "llama3.2";

        // Save Built-in AI settings
        _settings.BuiltInAIModel = GetEditableComboValue(BuiltInModelCombo, "llama-3.3-70b-versatile");

        // Save Doubao settings
        var doubaoKey = DoubaoKeyBox.Password;
        _settings.DoubaoApiKey = string.IsNullOrWhiteSpace(doubaoKey) ? null : doubaoKey;
        var doubaoEndpoint = DoubaoEndpointBox.Text?.Trim();
        _settings.DoubaoEndpoint = string.IsNullOrWhiteSpace(doubaoEndpoint)
            ? "https://ark.cn-beijing.volces.com/api/v3/responses"
            : doubaoEndpoint;
        var doubaoModel = DoubaoModelBox.Text?.Trim();
        _settings.DoubaoModel = string.IsNullOrWhiteSpace(doubaoModel)
            ? "doubao-seed-translation-250915"
            : doubaoModel;

        // Save Caiyun settings
        var caiyunKey = CaiyunKeyBox.Password;
        _settings.CaiyunApiKey = string.IsNullOrWhiteSpace(caiyunKey) ? null : caiyunKey;

        // Save NiuTrans settings
        var niutransKey = NiuTransKeyBox.Password;
        _settings.NiuTransApiKey = string.IsNullOrWhiteSpace(niutransKey) ? null : niutransKey;

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

        // Save enabled services for each window
        _settings.MainWindowEnabledServices = GetEnabledServicesFromPanel(MainWindowServicesPanel);
        _settings.MiniWindowEnabledServices = GetEnabledServicesFromPanel(MiniWindowServicesPanel);
        _settings.FixedWindowEnabledServices = GetEnabledServicesFromPanel(FixedWindowServicesPanel);

        // Validate that at least one service is enabled for each window (updates UI too)
        EnsureDefaultServiceEnabled(MainWindowServicesPanel, _settings.MainWindowEnabledServices);
        EnsureDefaultServiceEnabled(MiniWindowServicesPanel, _settings.MiniWindowEnabledServices);
        EnsureDefaultServiceEnabled(FixedWindowServicesPanel, _settings.FixedWindowEnabledServices);

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

    /// <summary>
    /// Get list of enabled services from a panel's checkboxes.
    /// </summary>
    private static List<string> GetEnabledServicesFromPanel(StackPanel panel)
    {
        return panel.Children
            .OfType<CheckBox>()
            .Where(cb => cb.Tag is string && cb.IsChecked == true)
            .Select(cb => (string)cb.Tag!)
            .ToList();
    }

    /// <summary>
    /// Ensure at least Google is checked when the panel has no services selected.
    /// Updates both the settings and the UI checkbox.
    /// </summary>
    private static void EnsureDefaultServiceEnabled(StackPanel panel, List<string> services)
    {
        if (services.Count > 0) return;

        services.Add("google");

        // Also update the UI to reflect this default
        var googleCheckBox = panel.Children
            .OfType<CheckBox>()
            .FirstOrDefault(cb => cb.Tag as string == "google");

        if (googleCheckBox != null)
        {
            googleCheckBox.IsChecked = true;
        }
    }
}
