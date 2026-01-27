using System.Collections.ObjectModel;
using System.Text.Json;
using Easydict.WinUI.Models;
using Easydict.WinUI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Easydict.WinUI.Views;

/// <summary>
/// Represents a navigation section for the floating sidebar.
/// </summary>
internal sealed record NavSection(string Name, string Tooltip, string IconGlyph, FrameworkElement Element);

/// <summary>
/// Settings page for configuring translation services, hotkeys, and behavior.
/// </summary>
public sealed partial class SettingsPage : Page
{
    private readonly SettingsService _settings = SettingsService.Instance;
    private bool _isLoading = true; // Prevent change detection during initial load
    private bool _handlersRegistered; // Guard to prevent duplicate event handler registration

    // Service selection collections for each window (populated from TranslationManager.Services)
    private readonly ObservableCollection<ServiceCheckItem> _mainWindowServices = [];
    private readonly ObservableCollection<ServiceCheckItem> _miniWindowServices = [];
    private readonly ObservableCollection<ServiceCheckItem> _fixedWindowServices = [];

    // Navigation sections for the floating sidebar
    private List<NavSection> _navSections = [];
    private int _currentSectionIndex = -1;

    public SettingsPage()
    {
        this.InitializeComponent();
        this.Loaded += OnPageLoaded;
    }

    /// <summary>
    /// Apply localization to all UI elements using LocalizationService.
    /// NOTE: Service names (Google Translate, DeepL, etc.) remain in English.
    /// </summary>
    private void ApplyLocalization()
    {
        var loc = LocalizationService.Instance;

        // Main header
        if (SettingsHeaderText != null)
            SettingsHeaderText.Text = loc.GetString("Settings");

        // Translation Service section
        if (TranslationServiceHeaderText != null)
            TranslationServiceHeaderText.Text = loc.GetString("TranslationService");

        ServiceCombo.Header = loc.GetString("DefaultService");
        TargetLangCombo.Header = loc.GetString("TargetLanguage");

        // Localize Target Language ComboBox items
        if (TargetLangCombo.Items.Count >= 7)
        {
            ((ComboBoxItem)TargetLangCombo.Items[0]).Content = loc.GetString("LangChineseSimplified");
            ((ComboBoxItem)TargetLangCombo.Items[1]).Content = loc.GetString("LangEnglish");
            ((ComboBoxItem)TargetLangCombo.Items[2]).Content = loc.GetString("LangJapanese");
            ((ComboBoxItem)TargetLangCombo.Items[3]).Content = loc.GetString("LangKorean");
            ((ComboBoxItem)TargetLangCombo.Items[4]).Content = loc.GetString("LangFrench");
            ((ComboBoxItem)TargetLangCombo.Items[5]).Content = loc.GetString("LangGerman");
            ((ComboBoxItem)TargetLangCombo.Items[6]).Content = loc.GetString("LangSpanish");
        }

        // NOTE: ServiceCombo items (service names) stay in English - DO NOT translate

        // Enabled Services section
        if (EnabledServicesHeaderText != null)
            EnabledServicesHeaderText.Text = loc.GetString("EnabledServices");
        if (EnabledServicesDescriptionText != null)
            EnabledServicesDescriptionText.Text = loc.GetString("EnabledServicesDescription");

        // Window headers
        if (MainWindowHeaderText != null)
            MainWindowHeaderText.Text = loc.GetString("MainWindow");
        if (MiniWindowHeaderText != null)
            MiniWindowHeaderText.Text = loc.GetString("MiniWindow");
        if (FixedWindowHeaderText != null)
            FixedWindowHeaderText.Text = loc.GetString("FixedWindow");

        // Language Preferences section
        if (LanguagePreferencesHeaderText != null)
            LanguagePreferencesHeaderText.Text = loc.GetString("LanguagePreferences");
        if (LanguagePreferencesDescriptionText != null)
            LanguagePreferencesDescriptionText.Text = loc.GetString("LanguagePreferencesDescription");

        FirstLanguageCombo.Header = loc.GetString("FirstLanguage");
        SecondLanguageCombo.Header = loc.GetString("SecondLanguage");
        AutoSelectTargetToggle.Header = loc.GetString("AutoSelectTargetLanguage");

        // Localize Language ComboBox items (these already have emoji flags)
        // Keep emoji, translate language names
        if (FirstLanguageCombo.Items.Count >= 7)
        {
            ((ComboBoxItem)FirstLanguageCombo.Items[0]).Content = $"🇨🇳 {loc.GetString("LangChineseSimplified")}";
            ((ComboBoxItem)FirstLanguageCombo.Items[1]).Content = $"🇺🇸 {loc.GetString("LangEnglish")}";
            ((ComboBoxItem)FirstLanguageCombo.Items[2]).Content = $"🇯🇵 {loc.GetString("LangJapanese")}";
            ((ComboBoxItem)FirstLanguageCombo.Items[3]).Content = $"🇰🇷 {loc.GetString("LangKorean")}";
            ((ComboBoxItem)FirstLanguageCombo.Items[4]).Content = $"🇫🇷 {loc.GetString("LangFrench")}";
            ((ComboBoxItem)FirstLanguageCombo.Items[5]).Content = $"🇩🇪 {loc.GetString("LangGerman")}";
            ((ComboBoxItem)FirstLanguageCombo.Items[6]).Content = $"🇪🇸 {loc.GetString("LangSpanish")}";
        }

        if (SecondLanguageCombo.Items.Count >= 7)
        {
            ((ComboBoxItem)SecondLanguageCombo.Items[0]).Content = $"🇺🇸 {loc.GetString("LangEnglish")}";
            ((ComboBoxItem)SecondLanguageCombo.Items[1]).Content = $"🇨🇳 {loc.GetString("LangChineseSimplified")}";
            ((ComboBoxItem)SecondLanguageCombo.Items[2]).Content = $"🇯🇵 {loc.GetString("LangJapanese")}";
            ((ComboBoxItem)SecondLanguageCombo.Items[3]).Content = $"🇰🇷 {loc.GetString("LangKorean")}";
            ((ComboBoxItem)SecondLanguageCombo.Items[4]).Content = $"🇫🇷 {loc.GetString("LangFrench")}";
            ((ComboBoxItem)SecondLanguageCombo.Items[5]).Content = $"🇩🇪 {loc.GetString("LangGerman")}";
            ((ComboBoxItem)SecondLanguageCombo.Items[6]).Content = $"🇪🇸 {loc.GetString("LangSpanish")}";
        }

        // Service Configuration section
        if (ServiceConfigurationHeaderText != null)
            ServiceConfigurationHeaderText.Text = loc.GetString("ServiceConfiguration");
        if (ServiceConfigurationDescriptionText != null)
            ServiceConfigurationDescriptionText.Text = loc.GetString("ServiceConfigurationDescription");

        // Service configuration controls (API Keys, Endpoints, Models, etc.)
        // TextBox/PasswordBox headers for each service
        DeepLKeyBox.Header = loc.GetString("ApiKeyOptional");
        OpenAIKeyBox.Header = loc.GetString("ApiKey");
        OpenAIEndpointBox.Header = loc.GetString("EndpointOptional");
        OpenAIModelCombo.Header = loc.GetString("Model");
        DeepSeekKeyBox.Header = loc.GetString("ApiKey");
        DeepSeekModelCombo.Header = loc.GetString("Model");
        GroqKeyBox.Header = loc.GetString("ApiKey");
        GroqModelCombo.Header = loc.GetString("Model");
        ZhipuKeyBox.Header = loc.GetString("ApiKey");
        ZhipuModelCombo.Header = loc.GetString("Model");
        GitHubModelsTokenBox.Header = loc.GetString("ApiKey");
        GitHubModelsModelCombo.Header = loc.GetString("Model");
        GeminiKeyBox.Header = loc.GetString("ApiKey");
        GeminiModelCombo.Header = loc.GetString("Model");
        CustomOpenAIKeyBox.Header = loc.GetString("ApiKeyOptional");
        CustomOpenAIEndpointBox.Header = loc.GetString("EndpointRequired");
        CustomOpenAIModelBox.Header = loc.GetString("Model");
        OllamaEndpointBox.Header = loc.GetString("EndpointOptional");
        OllamaModelCombo.Header = loc.GetString("Model");
        BuiltInModelCombo.Header = loc.GetString("Model");
        DoubaoKeyBox.Header = loc.GetString("ApiKey");
        DoubaoEndpointBox.Header = loc.GetString("EndpointOptional");
        DoubaoModelBox.Header = loc.GetString("Model");
        CaiyunKeyBox.Header = loc.GetString("ApiKey");
        NiuTransKeyBox.Header = loc.GetString("ApiKey");

        // Refresh button for Ollama
        RefreshOllamaButton.Content = loc.GetString("Refresh");

        // Free Services section
        if (FreeServicesHeaderText != null)
            FreeServicesHeaderText.Text = loc.GetString("FreeServicesTitle");
        if (FreeServicesDescriptionText != null)
            FreeServicesDescriptionText.Text = loc.GetString("FreeServicesDescription");

        // HTTP Proxy section
        if (HttpProxyHeaderText != null)
            HttpProxyHeaderText.Text = loc.GetString("HttpProxy");

        ProxyEnabledToggle.Header = loc.GetString("UseHttpProxy");
        ProxyUriBox.Header = loc.GetString("ProxyUrl");
        ProxyBypassLocalToggle.Header = loc.GetString("BypassProxyForLocalhost");

        // Hotkeys section
        if (HotkeysHeaderText != null)
            HotkeysHeaderText.Text = loc.GetString("Hotkeys");
        if (HotkeysDescriptionText != null)
            HotkeysDescriptionText.Text = loc.GetString("HotkeysDescription");

        ShowHotkeyBox.Header = loc.GetString("ShowWindow");
        TranslateHotkeyBox.Header = loc.GetString("TranslateSelection");
        ShowMiniHotkeyBox.Header = loc.GetString("ShowMiniWindow");
        ShowFixedHotkeyBox.Header = loc.GetString("ShowFixedWindow");

        // About section
        if (AboutHeaderText != null)
            AboutHeaderText.Text = loc.GetString("About");

        // Save Settings button
        SaveButton.Content = loc.GetString("SaveSettings");

        // Tooltips
        ToolTipService.SetToolTip(FloatingBackButton, loc.GetString("Back"));
        ToolTipService.SetToolTip(BackToTopButton, loc.GetString("BackToTop"));
    }

    private void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        _isLoading = true;

        // Bind ItemsControls to collections
        MainWindowServicesPanel.ItemsSource = _mainWindowServices;
        MiniWindowServicesPanel.ItemsSource = _miniWindowServices;
        FixedWindowServicesPanel.ItemsSource = _fixedWindowServices;

        LoadSettings();
        InitializeNavigation();

        // Apply localization to all UI elements
        ApplyLocalization();

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
        LaunchAtStartupToggle.Toggled += OnSettingChanged;
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
        ShowMiniHotkeyBox.TextChanged += OnSettingChanged;
        ShowFixedHotkeyBox.TextChanged += OnSettingChanged;

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

        // Service selection changes (via PropertyChanged on ServiceCheckItem)
        RegisterServiceCollectionHandlers(_mainWindowServices);
        RegisterServiceCollectionHandlers(_miniWindowServices);
        RegisterServiceCollectionHandlers(_fixedWindowServices);
    }

    /// <summary>
    /// Register PropertyChanged handlers for service check items in a collection.
    /// </summary>
    private void RegisterServiceCollectionHandlers(ObservableCollection<ServiceCheckItem> collection)
    {
        foreach (var item in collection)
        {
            item.PropertyChanged += (_, _) => OnSettingChanged(null!, null!);
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
        // UI Language - select based on current setting or system default
        var currentLanguage = LocalizationService.Instance.CurrentLanguage;
        SelectComboByTag(UILanguageCombo, currentLanguage);

        MinimizeToTrayToggle.IsOn = _settings.MinimizeToTray;
        ClipboardMonitorToggle.IsOn = _settings.ClipboardMonitoring;
        AlwaysOnTopToggle.IsOn = _settings.AlwaysOnTop;
        LaunchAtStartupToggle.IsOn = _settings.LaunchAtStartup;

        // Hotkeys
        ShowHotkeyBox.Text = _settings.ShowWindowHotkey;
        TranslateHotkeyBox.Text = _settings.TranslateSelectionHotkey;
        ShowMiniHotkeyBox.Text = _settings.ShowMiniWindowHotkey;
        ShowFixedHotkeyBox.Text = _settings.ShowFixedWindowHotkey;

        // Enabled services for each window (populate from TranslationManager.Services)
        PopulateServiceCollection(_mainWindowServices, _settings.MainWindowEnabledServices, _settings.MainWindowServiceEnabledQuery);
        PopulateServiceCollection(_miniWindowServices, _settings.MiniWindowEnabledServices, _settings.MiniWindowServiceEnabledQuery);
        PopulateServiceCollection(_fixedWindowServices, _settings.FixedWindowEnabledServices, _settings.FixedWindowServiceEnabledQuery);

        // Set version from assembly metadata
        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = $"Version {version?.ToString(3) ?? "Unknown"}";
    }

    /// <summary>
    /// Populate a service collection from TranslationManager.Services with enabled state and EnabledQuery settings.
    /// </summary>
    private static void PopulateServiceCollection(
        ObservableCollection<ServiceCheckItem> collection,
        List<string> enabledServices,
        Dictionary<string, bool> enabledQuerySettings)
    {
        collection.Clear();

        using var handle = TranslationManagerService.Instance.AcquireHandle();
        var manager = handle.Manager;

        foreach (var (serviceId, service) in manager.Services)
        {
            // Default EnabledQuery is true (auto-query); use stored setting if available
            var enabledQuery = enabledQuerySettings.TryGetValue(serviceId, out var stored) ? stored : true;

            collection.Add(new ServiceCheckItem
            {
                ServiceId = serviceId,
                DisplayName = service.DisplayName,
                IsChecked = enabledServices.Contains(serviceId),
                EnabledQuery = enabledQuery
            });
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
        // Get localization service instance once for the entire method
        var loc = LocalizationService.Instance;

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
                Title = loc.GetString("InvalidLanguageSelection"),
                Content = loc.GetString("InvalidLanguageSelectionMessage"),
                CloseButtonText = loc.GetString("OK"),
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
                    Title = loc.GetString("InvalidProxyUrl"),
                    Content = loc.GetString("InvalidProxyUrlMessage"),
                    CloseButtonText = loc.GetString("OK"),
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
        _settings.LaunchAtStartup = LaunchAtStartupToggle.IsOn;

        // Apply startup setting to Windows registry
        StartupService.SetEnabled(_settings.LaunchAtStartup);

        // Save hotkey settings
        _settings.ShowWindowHotkey = ShowHotkeyBox.Text;
        _settings.TranslateSelectionHotkey = TranslateHotkeyBox.Text;
        _settings.ShowMiniWindowHotkey = ShowMiniHotkeyBox.Text;
        _settings.ShowFixedWindowHotkey = ShowFixedHotkeyBox.Text;

        // Save enabled services for each window (from collections)
        _settings.MainWindowEnabledServices = GetEnabledServicesFromCollection(_mainWindowServices);
        _settings.MiniWindowEnabledServices = GetEnabledServicesFromCollection(_miniWindowServices);
        _settings.FixedWindowEnabledServices = GetEnabledServicesFromCollection(_fixedWindowServices);

        // Save EnabledQuery settings for each window
        _settings.MainWindowServiceEnabledQuery = GetEnabledQueryFromCollection(_mainWindowServices);
        _settings.MiniWindowServiceEnabledQuery = GetEnabledQueryFromCollection(_miniWindowServices);
        _settings.FixedWindowServiceEnabledQuery = GetEnabledQueryFromCollection(_fixedWindowServices);

        // Validate that at least one service is enabled for each window (updates collection too)
        EnsureDefaultServiceEnabled(_mainWindowServices, _settings.MainWindowEnabledServices);
        EnsureDefaultServiceEnabled(_miniWindowServices, _settings.MiniWindowEnabledServices);
        EnsureDefaultServiceEnabled(_fixedWindowServices, _settings.FixedWindowEnabledServices);

        // Persist to storage
        _settings.Save();

        // Refresh window service results to pick up new EnabledQuery settings
        MiniWindowService.Instance.RefreshServiceResults();
        FixedWindowService.Instance.RefreshServiceResults();

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
            Title = loc.GetString("SettingsSaved"),
            Content = loc.GetString("SettingsSavedMessage"),
            CloseButtonText = loc.GetString("OK"),
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
            var loc = LocalizationService.Instance;
            var errorDialog = new ContentDialog
            {
                Title = loc.GetString("CannotConnectToOllama"),
                Content = loc.GetString("CannotConnectToOllamaMessage"),
                CloseButtonText = loc.GetString("OK"),
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
    /// Get list of enabled services from a collection.
    /// </summary>
    private static List<string> GetEnabledServicesFromCollection(ObservableCollection<ServiceCheckItem> collection)
    {
        return collection
            .Where(item => item.IsChecked)
            .Select(item => item.ServiceId)
            .ToList();
    }

    /// <summary>
    /// Ensure at least Google is checked when the collection has no services selected.
    /// Updates both the settings and the collection item.
    /// </summary>
    private static void EnsureDefaultServiceEnabled(ObservableCollection<ServiceCheckItem> collection, List<string> services)
    {
        if (services.Count > 0) return;

        services.Add("google");

        // Also update the collection item to reflect this default
        var googleItem = collection.FirstOrDefault(item => item.ServiceId == "google");
        if (googleItem != null)
        {
            googleItem.IsChecked = true;
        }
    }

    /// <summary>
    /// Initialize the floating navigation sidebar with section dots.
    /// </summary>
    private void InitializeNavigation()
    {
        // Define navigation sections
        // Define navigation sections with icons (Segoe Fluent Icons)
        _navSections =
        [
            new NavSection("HeaderSection", "Settings", "\uE713", HeaderSection),              // Settings gear
            new NavSection("TranslationServiceSection", "Translation Service", "\uE8C1", TranslationServiceSection),  // Translate
            new NavSection("EnabledServicesSection", "Enabled Services", "\uE73E", EnabledServicesSection),           // Checkmark
            new NavSection("LanguagePreferencesSection", "Language Preferences", "\uE774", LanguagePreferencesSection), // Globe
            new NavSection("ServiceConfigurationSection", "Service Configuration", "\uE90F", ServiceConfigurationSection), // Key
            new NavSection("HttpProxySection", "HTTP Proxy", "\uE968", HttpProxySection),      // Network
            new NavSection("BehaviorSection", "Behavior", "\uE771", BehaviorSection),          // Touch
            new NavSection("HotkeysSection", "Hotkeys", "\uE765", HotkeysSection),             // Keyboard
            new NavSection("AboutSection", "About", "\uE946", AboutSection)                    // Info
        ];

        // Clear existing icons and create new ones
        NavIndicators.Children.Clear();

        for (int i = 0; i < _navSections.Count; i++)
        {
            var section = _navSections[i];
            var icon = new FontIcon
            {
                Glyph = section.IconGlyph,
                FontSize = 14,
                Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
                Tag = i
            };

            // Add tooltip
            ToolTipService.SetToolTip(icon, section.Tooltip);

            // Add click handler
            icon.PointerPressed += OnNavIconClick;
            icon.PointerEntered += (s, e) => { if (s is FontIcon fi) fi.Opacity = 0.7; };
            icon.PointerExited += (s, e) => { if (s is FontIcon fi) fi.Opacity = 1.0; };

            NavIndicators.Children.Add(icon);
        }

        // Set initial active icon
        UpdateActiveNavIcon(0);
    }

    /// <summary>
    /// Handle scroll view changes to detect current section and show/hide back-to-top button.
    /// </summary>
    private void OnScrollViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
    {
        if (_navSections.Count == 0) return;

        var scrollViewer = MainScrollViewer;
        var verticalOffset = scrollViewer.VerticalOffset;

        // Show/hide floating back button (show after 60px scroll - when header is out of view)
        FloatingBackButton.Visibility = verticalOffset > 60 ? Visibility.Visible : Visibility.Collapsed;

        // Show/hide back-to-top button (show after 200px scroll)
        BackToTopButton.Visibility = verticalOffset > 200 ? Visibility.Visible : Visibility.Collapsed;

        // Find current section by checking element positions relative to viewport
        int currentIndex = 0;
        double viewportTop = verticalOffset + 50; // Add small offset for better UX

        for (int i = 0; i < _navSections.Count; i++)
        {
            var section = _navSections[i];
            var transform = section.Element.TransformToVisual(scrollViewer);
            var position = transform.TransformPoint(new Windows.Foundation.Point(0, 0));

            // Element position relative to scroll content
            var elementTop = position.Y + verticalOffset;

            if (elementTop <= viewportTop)
            {
                currentIndex = i;
            }
        }

        // Update active nav icon if section changed
        if (currentIndex != _currentSectionIndex)
        {
            UpdateActiveNavIcon(currentIndex);
        }
    }

    /// <summary>
    /// Update the active navigation icon styling.
    /// </summary>
    private void UpdateActiveNavIcon(int activeIndex)
    {
        _currentSectionIndex = activeIndex;

        for (int i = 0; i < NavIndicators.Children.Count; i++)
        {
            if (NavIndicators.Children[i] is FontIcon icon)
            {
                if (i == activeIndex)
                {
                    // Active icon: larger + accent color
                    icon.FontSize = 16;
                    icon.Foreground = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
                }
                else
                {
                    // Inactive icon: smaller + tertiary color
                    icon.FontSize = 14;
                    icon.Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"];
                }
            }
        }
    }

    /// <summary>
    /// Handle navigation icon click to scroll to the corresponding section.
    /// </summary>
    private void OnNavIconClick(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (sender is not FontIcon icon || icon.Tag is not int index) return;
        if (index < 0 || index >= _navSections.Count) return;

        var section = _navSections[index];
        ScrollToElement(section.Element);
    }

    /// <summary>
    /// Scroll to a specific element with smooth animation.
    /// </summary>
    private void ScrollToElement(FrameworkElement element)
    {
        var transform = element.TransformToVisual(MainScrollViewer);
        var position = transform.TransformPoint(new Windows.Foundation.Point(0, 0));
        var targetOffset = MainScrollViewer.VerticalOffset + position.Y - 24; // 24px padding

        // Ensure we don't scroll past the content
        targetOffset = Math.Max(0, Math.Min(targetOffset, MainScrollViewer.ScrollableHeight));

        // Use ChangeView for smooth scrolling animation
        MainScrollViewer.ChangeView(null, targetOffset, null, disableAnimation: false);
    }

    /// <summary>
    /// Handle back-to-top button click.
    /// </summary>
    private void OnBackToTopClick(object sender, RoutedEventArgs e)
    {
        MainScrollViewer.ChangeView(null, 0, null, disableAnimation: false);
    }

    /// <summary>
    /// Handle UI language selection change.
    /// </summary>
    private async void OnUILanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoading) return;

        var selectedTag = GetSelectedTag(UILanguageCombo);
        if (string.IsNullOrEmpty(selectedTag)) return;

        try
        {
            System.Diagnostics.Debug.WriteLine($"[SettingsPage] User selected language: {selectedTag}");

            // Set the language (this also saves to settings)
            LocalizationService.Instance.SetLanguage(selectedTag);

            System.Diagnostics.Debug.WriteLine($"[SettingsPage] Language set and saved successfully");

            // Show restart required message
            var loc = LocalizationService.Instance;
            var dialog = new ContentDialog
            {
                Title = loc.GetString("RestartRequired"),
                Content = loc.GetString("RestartRequiredMessage"),
                CloseButtonText = loc.GetString("OK"),
                XamlRoot = this.XamlRoot
            };
            await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SettingsPage] ERROR: Failed to save language: {ex.Message}");

            // Show error dialog to user
            var loc = LocalizationService.Instance;
            var errorDialog = new ContentDialog
            {
                Title = loc.GetString("StatusError"),
                Content = $"Failed to save language setting: {ex.Message}\n\nPlease check if the application has write permissions.",
                CloseButtonText = loc.GetString("OK"),
                XamlRoot = this.XamlRoot
            };
            await errorDialog.ShowAsync();
        }
    }

    /// <summary>
    /// Get EnabledQuery dictionary from a service collection.
    /// Only includes services that are checked (enabled).
    /// </summary>
    private static Dictionary<string, bool> GetEnabledQueryFromCollection(ObservableCollection<ServiceCheckItem> collection)
    {
        var dict = new Dictionary<string, bool>();
        foreach (var item in collection)
        {
            if (item.IsChecked)
            {
                dict[item.ServiceId] = item.EnabledQuery;
            }
        }
        return dict;
    }
}

/// <summary>
/// Converts a boolean value to Visibility.
/// True = Visible, False = Collapsed.
/// </summary>
public class BoolToVisibilityConverter : Microsoft.UI.Xaml.Data.IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is bool boolValue)
        {
            return boolValue ? Visibility.Visible : Visibility.Collapsed;
        }
        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        if (value is Visibility visibility)
        {
            return visibility == Visibility.Visible;
        }
        return false;
    }
}
