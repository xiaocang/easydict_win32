using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Text.Json;
using Easydict.TranslationService;
using Easydict.TranslationService.Models;
using Easydict.TranslationService.Services;
using TranslationLanguage = Easydict.TranslationService.Models.Language;
using Easydict.WinUI.Models;
using Easydict.WinUI.Services;
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
    private static readonly Regex NonServiceIdCharRegex = new("[^a-z0-9-]", RegexOptions.Compiled);
    private readonly SettingsService _settings = SettingsService.Instance;
    private bool _isLoading = true; // Prevent change detection during initial load
    private bool _isInitialized;
    private bool _isUnloaded;
    private bool _isTornDown;
    private bool _changeHandlersRegistered;
    private bool _hasUnsavedChanges; // Track whether any settings have been modified since last save
    private ContentDialog? _currentDialog; // Track open dialog to prevent COMException
    private readonly CancellationTokenSource _lifetimeCts = new();

    // Service selection collections for each window (populated from TranslationManager.Services)
    private readonly ObservableCollection<ServiceCheckItem> _mainWindowServices = [];
    private readonly ObservableCollection<ServiceCheckItem> _miniWindowServices = [];
    private readonly ObservableCollection<ServiceCheckItem> _fixedWindowServices = [];

    // Available language checkbox items for the ItemsRepeater
    private List<LanguageCheckboxItem> _languageItems = [];

    // Snapshot of SelectedLanguages at page load, restored on discard
    private List<string> _originalSelectedLanguages = [];

    // Dynamic UI references for encrypted MDX dictionary credential fields
    private readonly Dictionary<string, (TextBox EmailBox, PasswordBox RegcodeBox)> _mdxCredentialFields = new();

    // Navigation sections for the floating sidebar
    private List<NavSection> _navSections = [];
    private int _currentSectionIndex = -1;


#if DEBUG
    private readonly Stopwatch _perfWatch = new();

    [Conditional("DEBUG")]
    private void PerfLog(string event_)
    {
        Debug.WriteLine($"[SettingsPage] {_perfWatch.ElapsedMilliseconds,5}ms | {event_}");
    }
#endif

    public SettingsPage()
    {
#if DEBUG
        _perfWatch.Start();
        PerfLog("ctor: begin InitializeComponent");
#endif
        this.InitializeComponent();
#if DEBUG
        PerfLog("ctor: end InitializeComponent");
#endif
        this.Loaded += OnPageLoaded;
        this.Unloaded += OnPageUnloaded;
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

        // Enabled Services section
        if (EnabledServicesHeaderText != null)
            EnabledServicesHeaderText.Text = loc.GetString("EnabledServices");
        if (EnabledServicesDescriptionText != null)
            EnabledServicesDescriptionText.Text = loc.GetString("EnabledServicesDescription");

        // International Services toggle
        EnableInternationalServicesHeaderText.Text = loc.GetString("EnableInternationalServices");
        if (EnableInternationalServicesDescriptionText != null)
            EnableInternationalServicesDescriptionText.Text = loc.GetString("EnableInternationalServicesDescription");

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

        // First/Second Language combos are populated dynamically in OnPageLoaded
        // via PopulateSettingsLanguageCombo() — no hardcoded localization needed

        // Available Languages section
        if (AvailableLanguagesHeaderText != null)
            AvailableLanguagesHeaderText.Text = loc.GetString("AvailableLanguages");
        if (AvailableLanguagesDescText != null)
            AvailableLanguagesDescText.Text = loc.GetString("AvailableLanguagesDesc");

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
        BuiltInApiKeyBox.Header = loc.GetString("ApiKeyOptional");
        BuiltInAIHintBar.Title = loc.GetString("Hint");
        BuiltInAIHintBar.Message = loc.GetString("BuiltInAIHint");
        BuiltInDescriptionText.Text = loc.GetString("BuiltInAIDescription");
        DoubaoKeyBox.Header = loc.GetString("ApiKey");
        DoubaoEndpointBox.Header = loc.GetString("EndpointOptional");
        DoubaoModelBox.Header = loc.GetString("Model");
        CaiyunKeyBox.Header = loc.GetString("ApiKey");
        NiuTransKeyBox.Header = loc.GetString("ApiKey");
        YoudaoAppKeyBox.Header = loc.GetString("AppKey");
        YoudaoAppSecretBox.Header = loc.GetString("AppSecret");
        YoudaoUseOfficialApiToggle.Header = loc.GetString("UseOfficialApi");
        YoudaoUseOfficialApiToggle.OnContent = loc.GetString("OfficialApi");
        YoudaoUseOfficialApiToggle.OffContent = loc.GetString("WebFree");

        // Refresh button for Ollama
        RefreshOllamaButton.Content = loc.GetString("Refresh");

        // Test buttons for all services
        var testButtonText = loc.GetString("Test");
        TestDeepLButton.Content = testButtonText;
        TestOpenAIButton.Content = testButtonText;
        TestDeepSeekButton.Content = testButtonText;
        TestGroqButton.Content = testButtonText;
        TestZhipuButton.Content = testButtonText;
        TestGitHubModelsButton.Content = testButtonText;
        TestGeminiButton.Content = testButtonText;
        TestCustomOpenAIButton.Content = testButtonText;
        TestOllamaButton.Content = testButtonText;
        TestBuiltInButton.Content = testButtonText;
        TestDoubaoButton.Content = testButtonText;
        TestCaiyunButton.Content = testButtonText;
        TestNiuTransButton.Content = testButtonText;

        // Free Services section
        if (FreeServicesHeaderText != null)
            FreeServicesHeaderText.Text = loc.GetString("FreeServicesTitle");
        if (FreeServicesDescriptionText != null)
            FreeServicesDescriptionText.Text = loc.GetString("FreeServicesDescription");

        // CJK Font section
        if (CjkFontHeaderText != null)
            CjkFontHeaderText.Text = loc.GetString("CjkFont_Title");
        if (CjkFontDescriptionText != null)
            CjkFontDescriptionText.Text = loc.GetString("CjkFont_Description");
        CjkFontDownloadButton.Content = loc.GetString("CjkFont_Download");
        CjkFontDeleteButton.Content = loc.GetString("CjkFont_Delete");
        if (CjkFontNoteText != null)
            CjkFontNoteText.Text = loc.GetString("CjkFont_Note");

        // Formula Detection section
        if (FormulaDetectionHeaderText != null)
            FormulaDetectionHeaderText.Text = loc.GetString("FormulaDetection_Title");
        if (FormulaDetectionDescriptionText != null)
            FormulaDetectionDescriptionText.Text = loc.GetString("FormulaDetection_Description");
        FormulaFontPatternBox.Header = loc.GetString("FormulaDetection_FontPattern");
        FormulaCharPatternBox.Header = loc.GetString("FormulaDetection_CharPattern");
        if (FormulaDetectionNoteText != null)
            FormulaDetectionNoteText.Text = loc.GetString("FormulaDetection_Note");

        // Translation Cache section
        if (TranslationCacheHeaderText != null)
            TranslationCacheHeaderText.Text = loc.GetString("TranslationCache_Title");
        if (TranslationCacheDescriptionText != null)
            TranslationCacheDescriptionText.Text = loc.GetString("TranslationCache_Description");
        TranslationCacheToggle.Header = loc.GetString("TranslationCache_Title");
        ClearCacheButton.Content = loc.GetString("TranslationCache_Clear");
        if (TranslationCacheNoteText != null)
            TranslationCacheNoteText.Text = loc.GetString("TranslationCache_Note");

        // Custom Prompt section
        if (CustomPromptHeaderText != null)
            CustomPromptHeaderText.Text = loc.GetString("CustomPrompt_Title");
        if (CustomPromptDescriptionText != null)
            CustomPromptDescriptionText.Text = loc.GetString("CustomPrompt_Description");
        CustomPromptBox.Header = loc.GetString("CustomPrompt_Title");
        if (CustomPromptNoteText != null)
            CustomPromptNoteText.Text = loc.GetString("CustomPrompt_Note");

        // HTTP Proxy section
        if (HttpProxyHeaderText != null)
            HttpProxyHeaderText.Text = loc.GetString("HttpProxy");

        ProxyEnabledToggle.Header = loc.GetString("UseHttpProxy");
        ProxyUriBox.Header = loc.GetString("ProxyUrl");
        ProxyBypassLocalToggle.Header = loc.GetString("BypassProxyForLocalhost");

        // Toggle switch On/Off content (override system locale defaults)
        var toggleOn = loc.GetString("ToggleOn");
        var toggleOff = loc.GetString("ToggleOff");
        AutoSelectTargetToggle.OnContent = toggleOn;
        AutoSelectTargetToggle.OffContent = toggleOff;
        EnableInternationalServicesToggle.OnContent = toggleOn;
        EnableInternationalServicesToggle.OffContent = toggleOff;
        ProxyEnabledToggle.OnContent = toggleOn;
        ProxyEnabledToggle.OffContent = toggleOff;
        ProxyBypassLocalToggle.OnContent = toggleOn;
        ProxyBypassLocalToggle.OffContent = toggleOff;
        TranslationCacheToggle.OnContent = toggleOn;
        TranslationCacheToggle.OffContent = toggleOff;
        MinimizeToTrayToggle.OnContent = toggleOn;
        MinimizeToTrayToggle.OffContent = toggleOff;
        MinimizeToTrayOnStartupToggle.OnContent = toggleOn;
        MinimizeToTrayOnStartupToggle.OffContent = toggleOff;
        ClipboardMonitorToggle.OnContent = toggleOn;
        ClipboardMonitorToggle.OffContent = toggleOff;
        MouseSelectionTranslateToggle.OnContent = toggleOn;
        MouseSelectionTranslateToggle.OffContent = toggleOff;
        AlwaysOnTopToggle.OnContent = toggleOn;
        AlwaysOnTopToggle.OffContent = toggleOff;
        LaunchAtStartupToggle.OnContent = toggleOn;
        LaunchAtStartupToggle.OffContent = toggleOff;

        // Hotkeys section
        if (HotkeysHeaderText != null)
            HotkeysHeaderText.Text = loc.GetString("Hotkeys");
        if (HotkeysDescriptionText != null)
            HotkeysDescriptionText.Text = loc.GetString("HotkeysDescription");

        ShowHotkeyBox.Header = loc.GetString("ShowWindow");
        TranslateHotkeyBox.Header = loc.GetString("TranslateSelection");
        ShowMiniHotkeyBox.Header = loc.GetString("ShowMiniWindow");
        ShowFixedHotkeyBox.Header = loc.GetString("ShowFixedWindow");
        OcrTranslateHotkeyBox.Header = loc.GetString("OcrScreenshotTranslate");
        SilentOcrHotkeyBox.Header = loc.GetString("SilentOcr");

        // About section
        if (AboutHeaderText != null)
            AboutHeaderText.Text = loc.GetString("About");
        if (IssueFeedbackLink != null)
            IssueFeedbackLink.Content = loc.GetString("IssueFeedback");

        // Save Settings button
        SaveButton.Content = loc.GetString("SaveSettings");

        // App Theme
        AppThemeCombo.Header = loc.GetString("AppTheme");
        if (AppThemeDescriptionText != null)
            AppThemeDescriptionText.Text = loc.GetString("AppThemeDescription");

        // Localize Theme ComboBox items
        if (AppThemeCombo.Items.Count >= 3)
        {
            ((ComboBoxItem)AppThemeCombo.Items[0]).Content = loc.GetString("ThemeSystem");
            ((ComboBoxItem)AppThemeCombo.Items[1]).Content = loc.GetString("ThemeLight");
            ((ComboBoxItem)AppThemeCombo.Items[2]).Content = loc.GetString("ThemeDark");
        }

        // Tooltips
        ToolTipService.SetToolTip(FloatingBackButton, loc.GetString("Back"));
        ToolTipService.SetToolTip(BackToTopButton, loc.GetString("BackToTop"));

        // Help icon tooltips
        ToolTipService.SetToolTip(EnabledServicesHelpIcon, loc.GetString("EnabledServicesHelpTip"));
        ToolTipService.SetToolTip(ServiceConfigHelpIcon, loc.GetString("ServiceConfigHelpTip"));
        ToolTipService.SetToolTip(HotkeysHelpIcon, loc.GetString("HotkeysHelpTip"));
    }

    private void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        if (_isUnloaded || _isInitialized)
        {
            return;
        }

#if DEBUG
        MemoryDiagnostics.LogSnapshot("SettingsPage.OnPageLoaded");
#endif
#if DEBUG
        PerfLog("OnPageLoaded: first load, dispatching deferred init");
#endif
        var token = _lifetimeCts.Token;
        // First load: show loading overlay, defer heavy work
        DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () =>
            {
                if (_isUnloaded || _isInitialized || token.IsCancellationRequested)
                {
                    return;
                }

                InitializeSettingsContent(token);
            });
    }

    /// <summary>
    /// Performs all heavy initialization work on first load, then reveals content.
    /// Dispatched at Low priority so the loading overlay renders first.
    /// </summary>
    private void InitializeSettingsContent(CancellationToken cancellationToken)
    {
        if (_isUnloaded || _isInitialized || cancellationToken.IsCancellationRequested)
        {
            return;
        }

#if DEBUG
        PerfLog("InitializeSettingsContent: begin");
#endif
        _isLoading = true;

        // Bind ItemsControls to collections
        MainWindowServicesPanel.ItemsSource = _mainWindowServices;
        MiniWindowServicesPanel.ItemsSource = _miniWindowServices;
        FixedWindowServicesPanel.ItemsSource = _fixedWindowServices;

        // Snapshot original SelectedLanguages for discard/restore
        _originalSelectedLanguages = new List<string>(_settings.SelectedLanguages);

#if DEBUG
        PerfLog("PopulateLanguageCheckboxGrid: begin");
#endif
        // Populate available languages checkbox grid
        PopulateLanguageCheckboxGrid();
#if DEBUG
        PerfLog("PopulateLanguageCheckboxGrid: end");
#endif

        // Populate First/Second Language combos dynamically
        var loc = LocalizationService.Instance;
        PopulateSettingsLanguageCombo(FirstLanguageCombo, loc);
        PopulateSettingsLanguageCombo(SecondLanguageCombo, loc);

#if DEBUG
        PerfLog("LoadSettings: begin");
#endif
        LoadSettings();
#if DEBUG
        PerfLog("LoadSettings: end");
        PerfLog("InitializeNavigation: begin");
#endif
        InitializeNavigation();
#if DEBUG
        PerfLog("InitializeNavigation: end");
        PerfLog("ApplyLocalization: begin");
#endif

        // Apply localization to all UI elements
        ApplyLocalization();
#if DEBUG
        PerfLog("ApplyLocalization: end");
#endif

#if DEBUG
        PerfLog("RegisterChangeHandlers: begin");
#endif
        RegisterChangeHandlers();
#if DEBUG
        PerfLog("RegisterChangeHandlers: end");
#endif
        _isLoading = false;
        _isInitialized = true;

        // Reveal content, hide loading overlay
        LoadingOverlay.Visibility = Visibility.Collapsed;
        MainScrollViewer.Visibility = Visibility.Visible;
        NavSidebar.Visibility = Visibility.Visible;
#if DEBUG
        PerfLog("Content revealed");
        MemoryDiagnostics.LogSnapshot("SettingsPage.InitializeSettingsContent complete");
#endif

        // Defer disk I/O (ONNX model check, SQLite cache) to after content is visible
        DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () =>
            {
                if (_isUnloaded || cancellationToken.IsCancellationRequested)
                {
                    return;
                }

#if DEBUG
                PerfLog("Deferred I/O: begin UpdateOnnxModelStatus");
#endif
                UpdateOnnxModelStatus();
#if DEBUG
                PerfLog("Deferred I/O: end UpdateOnnxModelStatus");
                PerfLog("Deferred I/O: begin UpdateCacheStatusAsync");
#endif
                _ = UpdateCacheStatusAsync(cancellationToken);
#if DEBUG
                PerfLog("Deferred I/O: end (UpdateCacheStatusAsync dispatched)");
#endif
            });
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs e)
    {
#if DEBUG
        MemoryDiagnostics.LogSnapshot("SettingsPage.OnPageUnloaded (before teardown)");
        var baseline = GC.GetTotalMemory(forceFullCollection: true);
#endif
        TeardownOnUnload();
#if DEBUG
        MemoryDiagnostics.LogDelta("SettingsPage.OnPageUnloaded retained after full GC", baseline);
        MemoryDiagnostics.LogSnapshot("SettingsPage.OnPageUnloaded (after teardown)");
#endif
    }

    private void TeardownOnUnload()
    {
        if (_isTornDown)
        {
            return;
        }

        _isTornDown = true;
        _isUnloaded = true;
        _isLoading = true;

        try
        {
            _lifetimeCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Ignore if already disposed by shutdown path.
        }
        _lifetimeCts.Dispose();

        // Disconnect lifecycle handlers to avoid retaining the page.
        this.Loaded -= OnPageLoaded;
        this.Unloaded -= OnPageUnloaded;

        UnregisterChangeHandlers();
        UnregisterLanguageCheckboxHandlers();

        try { _currentDialog?.Hide(); } catch (COMException) { }
        _currentDialog = null;

        MainWindowServicesPanel.ItemsSource = null;
        MiniWindowServicesPanel.ItemsSource = null;
        FixedWindowServicesPanel.ItemsSource = null;
        LanguageCheckboxGrid.ItemsSource = null;

        NavIndicators.Children.Clear();
        _navSections.Clear();

        _mainWindowServices.Clear();
        _miniWindowServices.Clear();
        _fixedWindowServices.Clear();
        _languageItems.Clear();

        _isInitialized = false;
    }

    /// <summary>
    /// Register event handlers to detect settings changes.
    /// </summary>
    private void RegisterChangeHandlers()
    {
        if (_changeHandlersRegistered)
        {
            return;
        }

        // ComboBox changes
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
        MinimizeToTrayOnStartupToggle.Toggled += OnSettingChanged;
        ClipboardMonitorToggle.Toggled += OnSettingChanged;
        MouseSelectionTranslateToggle.Toggled += OnSettingChanged;
        MouseSelectionTranslateToggle.Toggled += OnMouseSelectionTranslateToggled;
        MouseSelectionExcludedAppsBox.TextChanged += OnSettingChanged;
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
        OcrTranslateHotkeyBox.TextChanged += OnSettingChanged;
        SilentOcrHotkeyBox.TextChanged += OnSettingChanged;

        // TextBox/PasswordBox changes - new services
        DeepSeekKeyBox.PasswordChanged += OnSettingChanged;
        GroqKeyBox.PasswordChanged += OnSettingChanged;
        ZhipuKeyBox.PasswordChanged += OnSettingChanged;
        GitHubModelsTokenBox.PasswordChanged += OnSettingChanged;
        GeminiKeyBox.PasswordChanged += OnSettingChanged;
        CustomOpenAIEndpointBox.TextChanged += OnSettingChanged;
        CustomOpenAIKeyBox.PasswordChanged += OnSettingChanged;
        CustomOpenAIModelBox.TextChanged += OnSettingChanged;
        BuiltInApiKeyBox.PasswordChanged += OnSettingChanged;
        DoubaoKeyBox.PasswordChanged += OnSettingChanged;
        DoubaoEndpointBox.TextChanged += OnSettingChanged;
        DoubaoModelBox.TextChanged += OnSettingChanged;
        CaiyunKeyBox.PasswordChanged += OnSettingChanged;
        NiuTransKeyBox.PasswordChanged += OnSettingChanged;
        YoudaoAppKeyBox.PasswordChanged += OnSettingChanged;
        YoudaoAppSecretBox.PasswordChanged += OnSettingChanged;
        YoudaoUseOfficialApiToggle.Toggled += OnSettingChanged;

        // Layout detection changes
        LayoutDetectionModeCombo.SelectionChanged += OnLayoutDetectionModeChanged;
        VisionLayoutServiceCombo.SelectionChanged += OnSettingChanged;

        // CheckBox changes
        DeepLFreeCheck.Checked += OnSettingChanged;
        DeepLFreeCheck.Unchecked += OnSettingChanged;

        // Service selection changes (via PropertyChanged on ServiceCheckItem)
        RegisterServiceCollectionHandlers(_mainWindowServices);
        RegisterServiceCollectionHandlers(_miniWindowServices);
        RegisterServiceCollectionHandlers(_fixedWindowServices);
        _changeHandlersRegistered = true;
    }

    private void UnregisterChangeHandlers()
    {
        if (!_changeHandlersRegistered)
        {
            return;
        }

        FirstLanguageCombo.SelectionChanged -= OnSettingChanged;
        SecondLanguageCombo.SelectionChanged -= OnSettingChanged;
        OpenAIModelCombo.SelectionChanged -= OnSettingChanged;
        OllamaModelCombo.SelectionChanged -= OnSettingChanged;
        BuiltInModelCombo.SelectionChanged -= OnSettingChanged;
        DeepSeekModelCombo.SelectionChanged -= OnSettingChanged;
        GroqModelCombo.SelectionChanged -= OnSettingChanged;
        ZhipuModelCombo.SelectionChanged -= OnSettingChanged;
        GitHubModelsModelCombo.SelectionChanged -= OnSettingChanged;
        GeminiModelCombo.SelectionChanged -= OnSettingChanged;

        AutoSelectTargetToggle.Toggled -= OnSettingChanged;
        MinimizeToTrayToggle.Toggled -= OnSettingChanged;
        MinimizeToTrayOnStartupToggle.Toggled -= OnSettingChanged;
        ClipboardMonitorToggle.Toggled -= OnSettingChanged;
        MouseSelectionTranslateToggle.Toggled -= OnSettingChanged;
        MouseSelectionTranslateToggle.Toggled -= OnMouseSelectionTranslateToggled;
        MouseSelectionExcludedAppsBox.TextChanged -= OnSettingChanged;
        AlwaysOnTopToggle.Toggled -= OnSettingChanged;
        LaunchAtStartupToggle.Toggled -= OnSettingChanged;
        ProxyEnabledToggle.Toggled -= OnSettingChanged;
        ProxyBypassLocalToggle.Toggled -= OnSettingChanged;

        DeepLKeyBox.PasswordChanged -= OnSettingChanged;
        OpenAIKeyBox.PasswordChanged -= OnSettingChanged;
        OpenAIEndpointBox.TextChanged -= OnSettingChanged;
        OllamaEndpointBox.TextChanged -= OnSettingChanged;
        ProxyUriBox.TextChanged -= OnSettingChanged;
        ShowHotkeyBox.TextChanged -= OnSettingChanged;
        TranslateHotkeyBox.TextChanged -= OnSettingChanged;
        ShowMiniHotkeyBox.TextChanged -= OnSettingChanged;
        ShowFixedHotkeyBox.TextChanged -= OnSettingChanged;
        OcrTranslateHotkeyBox.TextChanged -= OnSettingChanged;
        SilentOcrHotkeyBox.TextChanged -= OnSettingChanged;

        DeepSeekKeyBox.PasswordChanged -= OnSettingChanged;
        GroqKeyBox.PasswordChanged -= OnSettingChanged;
        ZhipuKeyBox.PasswordChanged -= OnSettingChanged;
        GitHubModelsTokenBox.PasswordChanged -= OnSettingChanged;
        GeminiKeyBox.PasswordChanged -= OnSettingChanged;
        CustomOpenAIEndpointBox.TextChanged -= OnSettingChanged;
        CustomOpenAIKeyBox.PasswordChanged -= OnSettingChanged;
        CustomOpenAIModelBox.TextChanged -= OnSettingChanged;
        BuiltInApiKeyBox.PasswordChanged -= OnSettingChanged;
        DoubaoKeyBox.PasswordChanged -= OnSettingChanged;
        DoubaoEndpointBox.TextChanged -= OnSettingChanged;
        DoubaoModelBox.TextChanged -= OnSettingChanged;
        CaiyunKeyBox.PasswordChanged -= OnSettingChanged;
        NiuTransKeyBox.PasswordChanged -= OnSettingChanged;
        YoudaoAppKeyBox.PasswordChanged -= OnSettingChanged;
        YoudaoAppSecretBox.PasswordChanged -= OnSettingChanged;
        YoudaoUseOfficialApiToggle.Toggled -= OnSettingChanged;

        LayoutDetectionModeCombo.SelectionChanged -= OnLayoutDetectionModeChanged;
        VisionLayoutServiceCombo.SelectionChanged -= OnSettingChanged;

        DeepLFreeCheck.Checked -= OnSettingChanged;
        DeepLFreeCheck.Unchecked -= OnSettingChanged;

        UnregisterServiceCollectionHandlers(_mainWindowServices);
        UnregisterServiceCollectionHandlers(_miniWindowServices);
        UnregisterServiceCollectionHandlers(_fixedWindowServices);

        _changeHandlersRegistered = false;
    }

    /// <summary>
    /// Named handler for ServiceCheckItem.PropertyChanged, replacing the lambda
    /// so it can be properly unregistered to prevent memory leaks.
    /// </summary>
    private void OnServiceItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        OnSettingChanged(sender!, e);
    }

    private void RegisterServiceCollectionHandlers(ObservableCollection<ServiceCheckItem> collection)
    {
        foreach (var item in collection)
        {
            item.PropertyChanged -= OnServiceItemPropertyChanged;
            item.PropertyChanged += OnServiceItemPropertyChanged;
        }
    }

    private void UnregisterServiceCollectionHandlers(ObservableCollection<ServiceCheckItem> collection)
    {
        foreach (var item in collection)
        {
            item.PropertyChanged -= OnServiceItemPropertyChanged;
        }
    }

    /// <summary>
    /// Apply localized On/Off content to service ToggleSwitches in DataTemplates.
    /// Called when each ToggleSwitch is loaded.
    /// </summary>
    private void OnServiceToggleSwitchLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleSwitch toggle)
        {
            var loc = LocalizationService.Instance;
            toggle.OnContent = loc.GetString("Auto");
            toggle.OffContent = loc.GetString("Manual");
        }
    }

    /// <summary>
    /// Show the floating save button when any setting changes.
    /// </summary>
    private void OnSettingChanged(object sender, object e)
    {
        if (_isLoading) return;
        _hasUnsavedChanges = true;
        SaveButton.Visibility = Visibility.Visible;
    }

    private void LoadSettings()
    {
        // International services toggle
        EnableInternationalServicesToggle.IsOn = _settings.EnableInternationalServices;

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
        BuiltInApiKeyBox.Password = _settings.BuiltInAIApiKey ?? string.Empty;

        // Doubao settings
        DoubaoKeyBox.Password = _settings.DoubaoApiKey ?? string.Empty;
        DoubaoEndpointBox.Text = _settings.DoubaoEndpoint;
        DoubaoModelBox.Text = _settings.DoubaoModel;

        // Caiyun settings
        CaiyunKeyBox.Password = _settings.CaiyunApiKey ?? string.Empty;

        // NiuTrans settings
        NiuTransKeyBox.Password = _settings.NiuTransApiKey ?? string.Empty;

        // Youdao settings
        YoudaoAppKeyBox.Password = _settings.YoudaoAppKey ?? string.Empty;
        YoudaoAppSecretBox.Password = _settings.YoudaoAppSecret ?? string.Empty;
        YoudaoUseOfficialApiToggle.IsOn = _settings.YoudaoUseOfficialApi;

        // HTTP Proxy settings
        ProxyEnabledToggle.IsOn = _settings.ProxyEnabled;
        ProxyUriBox.Text = _settings.ProxyUri;
        ProxyBypassLocalToggle.IsOn = _settings.ProxyBypassLocal;

        // Layout Detection settings
        SelectComboByTag(LayoutDetectionModeCombo, _settings.LayoutDetectionMode);
        SelectComboByTag(VisionLayoutServiceCombo, _settings.VisionLayoutServiceId);
        UpdateLayoutDetectionUI();

        // Formula Detection
        FormulaFontPatternBox.Text = _settings.FormulaFontPattern;
        FormulaCharPatternBox.Text = _settings.FormulaCharPattern;

        // Translation Cache
        TranslationCacheToggle.IsOn = _settings.EnableTranslationCache;

        // Custom Prompt
        CustomPromptBox.Text = _settings.LongDocCustomPrompt;

        // Behavior
        // App Theme - select based on current setting
        SelectComboByTag(AppThemeCombo, _settings.AppTheme);

        // UI Language - select based on current setting or system default
        var currentLanguage = LocalizationService.Instance.CurrentLanguage;
        SelectComboByTag(UILanguageCombo, currentLanguage);

        MinimizeToTrayToggle.IsOn = _settings.MinimizeToTray;
        MinimizeToTrayOnStartupToggle.IsOn = _settings.MinimizeToTrayOnStartup;
        ClipboardMonitorToggle.IsOn = _settings.ClipboardMonitoring;
        MouseSelectionTranslateToggle.IsOn = _settings.MouseSelectionTranslate;
        MouseSelectionExcludedAppsBox.Text = string.Join(", ", _settings.MouseSelectionExcludedApps);
        MouseSelectionExcludedAppsPanel.Visibility = _settings.MouseSelectionTranslate
            ? Visibility.Visible : Visibility.Collapsed;
        AlwaysOnTopToggle.IsOn = _settings.AlwaysOnTop;
        LaunchAtStartupToggle.IsOn = _settings.LaunchAtStartup;

        // Hotkeys
        ShowHotkeyBox.Text = _settings.ShowWindowHotkey;
        TranslateHotkeyBox.Text = _settings.TranslateSelectionHotkey;
        ShowMiniHotkeyBox.Text = _settings.ShowMiniWindowHotkey;
        ShowFixedHotkeyBox.Text = _settings.ShowFixedWindowHotkey;
        OcrTranslateHotkeyBox.Text = _settings.OcrTranslateHotkey;
        SilentOcrHotkeyBox.Text = _settings.SilentOcrHotkey;

        // Enabled services for each window (populate from TranslationManager.Services)
        // Acquire handle once for all three collections to avoid repeated handle acquisition
        UnregisterServiceCollectionHandlers(_mainWindowServices);
        UnregisterServiceCollectionHandlers(_miniWindowServices);
        UnregisterServiceCollectionHandlers(_fixedWindowServices);
        using (var handle = TranslationManagerService.Instance.AcquireHandle())
        {
            var manager = handle.Manager;
            PopulateServiceCollection(_mainWindowServices, _settings.MainWindowEnabledServices, _settings.MainWindowServiceEnabledQuery, manager);
            PopulateServiceCollection(_miniWindowServices, _settings.MiniWindowEnabledServices, _settings.MiniWindowServiceEnabledQuery, manager);
            PopulateServiceCollection(_fixedWindowServices, _settings.FixedWindowEnabledServices, _settings.FixedWindowServiceEnabledQuery, manager);
        }
        if (_changeHandlersRegistered)
        {
            RegisterServiceCollectionHandlers(_mainWindowServices);
            RegisterServiceCollectionHandlers(_miniWindowServices);
            RegisterServiceCollectionHandlers(_fixedWindowServices);
        }

        // Set version from assembly metadata
        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = $"Version {version?.ToString(3) ?? "Unknown"}";

        // Restore test status indicators
        RestoreTestStatusIndicators();
    }

    /// <summary>
    /// Restores test success indicators based on persisted ServiceTestStatus.
    /// </summary>
    private void RestoreTestStatusIndicators()
    {
        var statusMap = new Dictionary<string, TextBlock>
        {
            ["deepl"] = DeepLStatusText,
            ["openai"] = OpenAIStatusText,
            ["deepseek"] = DeepSeekStatusText,
            ["groq"] = GroqStatusText,
            ["zhipu"] = ZhipuStatusText,
            ["github"] = GitHubModelsStatusText,
            ["gemini"] = GeminiStatusText,
            ["custom-openai"] = CustomOpenAIStatusText,
            ["ollama"] = OllamaStatusText,
            ["builtin"] = BuiltInStatusText,
            ["doubao"] = DoubaoStatusText,
            ["caiyun"] = CaiyunStatusText,
            ["niutrans"] = NiuTransStatusText
        };

        foreach (var (serviceId, indicator) in statusMap)
        {
            if (_settings.ServiceTestStatus.TryGetValue(serviceId, out var passed) && passed)
            {
                indicator.Visibility = Visibility.Visible;
            }
        }

        UpdateImportedMdxSummary();
        BuildImportedMdxConfigUI();
    }

    private void UpdateImportedMdxSummary()
    {
        ImportedMdxSummaryText.Text = _settings.ImportedMdxDictionaries.Count switch
        {
            0 => "No MDX dictionaries imported",
            1 => "1 MDX dictionary imported",
            var c => $"{c} MDX dictionaries imported"
        };
    }

    /// <summary>
    /// Dynamically builds Expander config UI for each imported MDX dictionary.
    /// Encrypted dictionaries show email/regcode fields + test button.
    /// Non-encrypted dictionaries show a "ready to use" status.
    /// All dictionaries show file path and a delete button.
    /// </summary>
    private void BuildImportedMdxConfigUI()
    {
        ImportedMdxConfigPanel.Children.Clear();
        _mdxCredentialFields.Clear();

        var allDicts = _settings.ImportedMdxDictionaries;
        if (allDicts.Count == 0) return;

        // Sync IsEncrypted from live services (handles stale settings data)
        using var handle = TranslationManagerService.Instance.AcquireHandle();
        bool settingsChanged = false;
        foreach (var d in allDicts)
        {
            if (handle.Manager.Services.TryGetValue(d.ServiceId, out var svc)
                && svc is MdxDictionaryTranslationService mdx
                && mdx.IsEncrypted != d.IsEncrypted)
            {
                d.IsEncrypted = mdx.IsEncrypted;
                settingsChanged = true;
            }
        }
        if (settingsChanged) _settings.Save();

        var loc = LocalizationService.Instance;

        foreach (var dict in allDicts)
        {
            var expander = new Expander
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch
            };

            // Header with name (icon varies by encryption status) and status indicator
            var headerGrid = new Grid();
            // Strip any existing emoji prefix from DisplayName (import hardcodes 📚)
            var rawName = dict.DisplayName;
            if (rawName.StartsWith("\U0001f4da ")) rawName = rawName["\U0001f4da ".Length..];
            if (rawName.StartsWith("\U0001f512 ")) rawName = rawName["\U0001f512 ".Length..];
            var icon = dict.IsEncrypted ? "\U0001f512" : "\U0001f4da";
            var nameText = new TextBlock
            {
                Text = $"{icon} {rawName}",
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Left
            };
            var statusText = new TextBlock
            {
                Text = "\u2705",
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.Green),
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 0, 8, 0),
                Visibility = _settings.ServiceTestStatus.TryGetValue(dict.ServiceId, out var passed) && passed
                    ? Visibility.Visible : Visibility.Collapsed
            };
            headerGrid.Children.Add(nameText);
            headerGrid.Children.Add(statusText);
            expander.Header = headerGrid;

            // Content panel
            var contentPanel = new StackPanel { Spacing = 12, Padding = new Thickness(0, 8, 0, 0) };

            // File path (read-only, selectable)
            var filePathBox = new TextBox
            {
                Header = loc.GetString("MdxFilePath"),
                Text = dict.FilePath ?? string.Empty,
                IsReadOnly = true,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            contentPanel.Children.Add(filePathBox);

            if (dict.IsEncrypted)
            {
                // Encrypted: email + regcode + help text + test button
                var emailBox = new TextBox
                {
                    Header = "Email",
                    Width = 350,
                    PlaceholderText = loc.GetString("MdxEmailPlaceholder"),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Text = dict.Email ?? string.Empty
                };
                contentPanel.Children.Add(emailBox);

                var regcodeBox = new PasswordBox
                {
                    Header = loc.GetString("MdxRegistrationCode"),
                    Width = 350,
                    PlaceholderText = loc.GetString("MdxRegistrationCode"),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Password = dict.Regcode ?? string.Empty
                };
                contentPanel.Children.Add(regcodeBox);

                var helpText = new TextBlock
                {
                    Text = loc.GetString("MdxEncryptedHelpText"),
                    FontSize = 12,
                    Foreground = (SolidColorBrush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                    TextWrapping = TextWrapping.Wrap
                };
                contentPanel.Children.Add(helpText);

                var testButton = new Button { Content = loc.GetString("Test"), Padding = new Thickness(8, 4, 8, 4) };

                // Capture variables for the closure
                var capturedDict = dict;
                var capturedEmailBox = emailBox;
                var capturedRegcodeBox = regcodeBox;
                var capturedStatusText = statusText;

                testButton.Click += async (s, args) =>
                {
                    await TestEncryptedMdxAsync(capturedDict, capturedEmailBox, capturedRegcodeBox, (Button)s!, capturedStatusText);
                };
                contentPanel.Children.Add(testButton);

                // Register for Save button credential persistence
                _mdxCredentialFields[dict.ServiceId] = (emailBox, regcodeBox);
            }
            else
            {
                // Non-encrypted: ready to use info
                var readyText = new TextBlock
                {
                    Text = loc.GetString("MdxDictionaryReady"),
                    FontSize = 12,
                    Foreground = (SolidColorBrush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                    TextWrapping = TextWrapping.Wrap
                };
                contentPanel.Children.Add(readyText);
            }

            // MDD Resource Files section
            var mddLabel = new TextBlock
            {
                Text = loc.GetString("MddAssociatedFiles") ?? "Resource Files",
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                FontSize = 13,
                Margin = new Thickness(0, 4, 0, 2)
            };
            contentPanel.Children.Add(mddLabel);

            if (dict.MddFilePaths.Count > 0)
            {
                foreach (var mddPath in dict.MddFilePaths)
                {
                    var mddFileText = new TextBlock
                    {
                        Text = Path.GetFileName(mddPath),
                        FontSize = 12,
                        Foreground = (SolidColorBrush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                        Margin = new Thickness(8, 0, 0, 0)
                    };
                    contentPanel.Children.Add(mddFileText);
                }
            }
            else
            {
                var noMddText = new TextBlock
                {
                    Text = loc.GetString("MddNoFilesDetected") != null
                        ? string.Format(loc.GetString("MddNoFilesDetected")!, string.Empty).Trim()
                        : "No companion MDD resource files found.",
                    FontSize = 12,
                    Foreground = (SolidColorBrush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                    Margin = new Thickness(8, 0, 0, 0)
                };
                contentPanel.Children.Add(noMddText);
            }

            // MDD button row: Add MDD + Re-scan
            var mddButtonPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Margin = new Thickness(0, 4, 0, 4)
            };

            var capturedDictForMdd = dict;
            var addMddButton = new Button
            {
                Content = loc.GetString("MddAddFile") ?? "Add MDD File...",
                Padding = new Thickness(8, 4, 8, 4)
            };
            addMddButton.Click += async (s, args) =>
            {
                try
                {
                    var picker = new Windows.Storage.Pickers.FileOpenPicker();
                    var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
                    WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
                    picker.FileTypeFilter.Add(".mdd");

                    var file = await picker.PickSingleFileAsync();
                    if (file == null || string.IsNullOrWhiteSpace(file.Path))
                        return;

                    if (!capturedDictForMdd.MddFilePaths.Contains(file.Path, StringComparer.OrdinalIgnoreCase))
                    {
                        capturedDictForMdd.MddFilePaths.Add(file.Path);
                        _settings.Save();

                        // Load into live service
                        using var h = TranslationManagerService.Instance.AcquireHandle();
                        if (h.Manager.Services.TryGetValue(capturedDictForMdd.ServiceId, out var svc)
                            && svc is MdxDictionaryTranslationService mdxSvc)
                        {
                            mdxSvc.LoadMddFiles([file.Path]);
                        }

                        BuildImportedMdxConfigUI();
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[SettingsPage] Failed to add MDD file: {ex.Message}");
                }
            };
            mddButtonPanel.Children.Add(addMddButton);

            var rescanMddButton = new Button
            {
                Content = loc.GetString("MddRescanFiles") ?? "Re-scan MDD Files",
                Padding = new Thickness(8, 4, 8, 4)
            };
            var capturedDictForRescan = dict;
            rescanMddButton.Click += (s, args) =>
            {
                var discovered = MdxDictionaryTranslationService.DiscoverMddFiles(capturedDictForRescan.FilePath ?? string.Empty);
                capturedDictForRescan.MddFilePaths = discovered;
                _settings.Save();

                // Reconfigure services to reload MDD files
                TranslationManagerService.Instance.ReconfigureServices();
                BuildImportedMdxConfigUI();
            };
            mddButtonPanel.Children.Add(rescanMddButton);
            contentPanel.Children.Add(mddButtonPanel);

            // Button row: re-detect encryption + delete
            var buttonPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Margin = new Thickness(0, 4, 0, 0)
            };

            var redetectButton = new Button
            {
                Content = loc.GetString("MdxRedetectEncryption"),
                Padding = new Thickness(8, 4, 8, 4)
            };
            var capturedDictForRedetect = dict;
            redetectButton.Click += (s, args) =>
            {
                var btn = (Button)s!;
                btn.IsEnabled = false;
                try
                {
                    // Reconfigure services to re-detect encryption via constructor
                    TranslationManagerService.Instance.ReconfigureServices();

                    // Sync IsEncrypted from live services back to settings
                    foreach (var d in _settings.ImportedMdxDictionaries)
                    {
                        if (TranslationManagerService.Instance.Manager.Services.TryGetValue(d.ServiceId, out var svc)
                            && svc is MdxDictionaryTranslationService mdxSvc)
                        {
                            d.IsEncrypted = mdxSvc.IsEncrypted;
                        }
                    }
                    _settings.Save();

                    // Rebuild UI
                    BuildImportedMdxConfigUI();
                }
                finally
                {
                    btn.IsEnabled = true;
                }
            };
            buttonPanel.Children.Add(redetectButton);

            var capturedDictForDelete = dict;
            var deleteButton = new Button
            {
                Content = loc.GetString("MdxDeleteDictionary"),
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.Red),
                Padding = new Thickness(8, 4, 8, 4)
            };
            deleteButton.Click += async (s, args) =>
            {
                await DeleteMdxDictionaryAsync(capturedDictForDelete);
            };
            buttonPanel.Children.Add(deleteButton);

            contentPanel.Children.Add(buttonPanel);

            expander.Content = contentPanel;
            ImportedMdxConfigPanel.Children.Add(expander);
        }
    }

    /// <summary>
    /// Deletes an imported MDX dictionary after user confirmation.
    /// Removes from settings, service lists, test status, and TranslationManager.
    /// </summary>
    private async Task DeleteMdxDictionaryAsync(SettingsService.ImportedMdxDictionary dict)
    {
        var loc = LocalizationService.Instance;

        var confirmDialog = new ContentDialog
        {
            Title = loc.GetString("MdxDeleteConfirmTitle"),
            Content = string.Format(loc.GetString("MdxDeleteConfirmMessage"), dict.DisplayName),
            PrimaryButtonText = loc.GetString("MdxDeleteDictionary"),
            CloseButtonText = loc.GetString("Cancel"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = this.XamlRoot
        };

        var result = await ShowDialogAsync(confirmDialog);
        if (result != ContentDialogResult.Primary) return;

        var serviceId = dict.ServiceId;

        // Remove from imported dictionaries list
        _settings.ImportedMdxDictionaries.Remove(dict);

        // Remove from enabled services lists
        _settings.MainWindowEnabledServices.Remove(serviceId);
        _settings.MiniWindowEnabledServices.Remove(serviceId);
        _settings.FixedWindowEnabledServices.Remove(serviceId);

        // Remove from test status
        _settings.ServiceTestStatus.Remove(serviceId);

        // Remove from credential fields cache
        _mdxCredentialFields.Remove(serviceId);

        _settings.Save();

        // Unregister from TranslationManager
        TranslationManagerService.Instance.UnregisterMdxDictionary(serviceId);

        // Rebuild UI
        LoadSettings();
    }

    /// <summary>
    /// Persists encrypted MDX credential values from the dynamic UI into settings.
    /// Called by SaveSettingsAsync when the user clicks Save.
    /// </summary>
    private void SaveEncryptedMdxCredentials()
    {
        foreach (var dict in _settings.ImportedMdxDictionaries.Where(d => d.IsEncrypted))
        {
            if (_mdxCredentialFields.TryGetValue(dict.ServiceId, out var fields))
            {
                var email = fields.EmailBox.Text?.Trim();
                var regcode = fields.RegcodeBox.Password?.Trim();
                dict.Email = string.IsNullOrEmpty(email) ? null : email;
                dict.Regcode = string.IsNullOrEmpty(regcode) ? null : regcode;
            }
        }
    }

    /// <summary>
    /// Tests encrypted MDX dictionary credentials by attempting to open the dictionary
    /// with the provided email and registration code.
    /// </summary>
    private async Task TestEncryptedMdxAsync(
        SettingsService.ImportedMdxDictionary dict,
        TextBox emailBox,
        PasswordBox regcodeBox,
        Button testButton,
        TextBlock statusIndicator)
    {
        var loc = LocalizationService.Instance;
        var originalContent = testButton.Content;
        testButton.IsEnabled = false;
        testButton.Content = loc.GetString("Testing");

        try
        {
            var email = emailBox.Text?.Trim();
            var regcode = regcodeBox.Password?.Trim();

            if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(regcode))
            {
                await ShowSimpleDialogAsync(loc.GetString("TestFailedTitle"),
                    "Please enter both email and registration code.");
                return;
            }

            // Try to open the dictionary with the provided credentials
            var testService = new MdxDictionaryTranslationService(
                dict.ServiceId, dict.DisplayName, dict.FilePath,
                regcode, email);

            if (!testService.IsConfigured)
            {
                throw new TranslationException("Invalid credentials — dictionary could not be decrypted.");
            }

            // Test a lookup to verify the dictionary is readable
            var request = new TranslationRequest
            {
                Text = "test",
                FromLanguage = TranslationLanguage.English,
                ToLanguage = TranslationLanguage.SimplifiedChinese
            };
            await testService.TranslateAsync(request);

            // Success — save credentials and reconfigure the live service
            dict.Regcode = regcode;
            dict.Email = email;
            _settings.ServiceTestStatus[dict.ServiceId] = true;
            _settings.Save();

            statusIndicator.Visibility = Visibility.Visible;

            // Reconfigure the live service in TranslationManager
            TranslationManagerService.Instance.ReconfigureServices();

            var successDialog = new ContentDialog
            {
                Title = loc.GetString("TestSuccessTitle"),
                Content = "Dictionary credentials verified successfully. The dictionary is now ready to use.",
                CloseButtonText = loc.GetString("OK"),
                XamlRoot = this.XamlRoot
            };
            await ShowDialogAsync(successDialog);
        }
        catch (Exception ex)
        {
            _settings.ServiceTestStatus.Remove(dict.ServiceId);
            _settings.Save();
            statusIndicator.Visibility = Visibility.Collapsed;

            var errorDialog = new ContentDialog
            {
                Title = loc.GetString("TestFailedTitle"),
                Content = $"Credential verification failed: {ex.Message}",
                CloseButtonText = loc.GetString("OK"),
                XamlRoot = this.XamlRoot
            };
            await ShowDialogAsync(errorDialog);
        }
        finally
        {
            testButton.Content = originalContent;
            testButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// Populate a service collection from TranslationManager.Services with enabled state and EnabledQuery settings.
    /// </summary>
    private static void PopulateServiceCollection(
        ObservableCollection<ServiceCheckItem> collection,
        List<string> enabledServices,
        Dictionary<string, bool> enabledQuerySettings,
        TranslationManager? sharedManager = null)
    {
        collection.Clear();

        var settings = SettingsService.Instance;
        var internationalEnabled = settings.EnableInternationalServices;

        var handle = sharedManager == null ? TranslationManagerService.Instance.AcquireHandle() : null;
        var manager = sharedManager ?? handle!.Manager;

        try
        {
            foreach (var (serviceId, service) in manager.Services)
            {
                // Default EnabledQuery is true (auto-query); use stored setting if available
                var enabledQuery = enabledQuerySettings.TryGetValue(serviceId, out var stored) ? stored : true;

                var isInternationalOnly = SettingsService.InternationalOnlyServices.Contains(serviceId);
                var isAvailable = internationalEnabled || !isInternationalOnly;

                var item = new ServiceCheckItem
                {
                    ServiceId = serviceId,
                    DisplayName = service.DisplayName,
                    IsChecked = isAvailable && enabledServices.Contains(serviceId),
                    EnabledQuery = enabledQuery,
                    IsAvailable = isAvailable,
                    IsUnconfigured = service.RequiresApiKey && !service.IsConfigured
                };

                collection.Add(item);
            }
        }
        finally
        {
            handle?.Dispose();
        }
    }

    private async void OnImportMdxDictionaryClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            picker.FileTypeFilter.Add(".mdx");

            var file = await picker.PickSingleFileAsync();
            if (file == null || string.IsNullOrWhiteSpace(file.Path))
            {
                return;
            }

            var displayName = Path.GetFileNameWithoutExtension(file.Path);
            var serviceId = BuildMdxServiceId(displayName, file.Path);

            // Discover companion MDD resource files
            var mddFiles = MdxDictionaryTranslationService.DiscoverMddFiles(file.Path);

            var imported = new SettingsService.ImportedMdxDictionary
            {
                ServiceId = serviceId,
                DisplayName = $"📚 {displayName}",
                FilePath = file.Path,
                MddFilePaths = mddFiles
            };

            if (!TranslationManagerService.Instance.TryRegisterMdxDictionary(imported, out var error))
            {
                await ShowSimpleDialogAsync("Import failed", $"Unable to load MDX dictionary: {error}");
                return;
            }

            // Load MDD files into the registered service
            using var handle = TranslationManagerService.Instance.AcquireHandle();
            if (handle.Manager.Services.TryGetValue(serviceId, out var service) &&
                service is MdxDictionaryTranslationService mdxService)
            {
                if (mdxService.IsEncrypted)
                    imported.IsEncrypted = true;

                if (mddFiles.Count > 0)
                    mdxService.LoadMddFiles(mddFiles);
            }

            _settings.ImportedMdxDictionaries.RemoveAll(d => string.Equals(d.FilePath, file.Path, StringComparison.OrdinalIgnoreCase));
            _settings.ImportedMdxDictionaries.Add(imported);

            // Enable imported service by default in all windows.
            if (!_settings.MainWindowEnabledServices.Contains(serviceId)) _settings.MainWindowEnabledServices.Add(serviceId);
            if (!_settings.MiniWindowEnabledServices.Contains(serviceId)) _settings.MiniWindowEnabledServices.Add(serviceId);
            if (!_settings.FixedWindowEnabledServices.Contains(serviceId)) _settings.FixedWindowEnabledServices.Add(serviceId);

            _settings.Save();
            LoadSettings();

            var loc = LocalizationService.Instance;
            if (imported.IsEncrypted)
            {
                await ShowSimpleDialogAsync("Encrypted Dictionary",
                    "This dictionary is encrypted. Please configure your credentials (Email and Registration Code) " +
                    "in the Service Configuration section below.");
            }
            else if (mddFiles.Count > 0)
            {
                var msg = string.Format(loc.GetString("MddFilesDetected") ?? "Imported '{0}' with {1} companion resource file(s).",
                    displayName, mddFiles.Count);
                await ShowSimpleDialogAsync("Import Successful", msg);
            }
            else
            {
                var msg = string.Format(loc.GetString("MddNoFilesDetected") ?? "Imported '{0}'. No companion MDD resource files found.",
                    displayName);
                await ShowSimpleDialogAsync("Import Successful", msg);
            }
        }
        catch (Exception ex)
        {
            await ShowSimpleDialogAsync("Import failed", ex.Message);
        }
    }

    internal static string BuildMdxServiceId(string displayName, string filePath)
    {
        var stableHash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(filePath))).ToLowerInvariant()[..8];
        var normalizedName = displayName.Trim().ToLowerInvariant().Replace(' ', '-');
        normalizedName = NonServiceIdCharRegex.Replace(normalizedName, "-");
        normalizedName = Regex.Replace(normalizedName, "-+", "-").Trim('-');
        if (string.IsNullOrWhiteSpace(normalizedName))
        {
            normalizedName = "dictionary";
        }
        return $"mdx::{normalizedName}-{stableHash}";
    }

    private async Task ShowSimpleDialogAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = this.XamlRoot,
            Title = title,
            Content = message,
            CloseButtonText = "OK"
        };
        await ShowDialogAsync(dialog);
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
            // If text matches a dropdown item's Content, return its Tag (programmatic value).
            // This handles cases where Content differs from Tag (e.g., "glm-4-flash-250414 (GLM)" → "glm-4-flash-250414").
            for (int i = 0; i < combo.Items.Count; i++)
            {
                if (combo.Items[i] is ComboBoxItem item && item.Content?.ToString() == text && item.Tag != null)
                {
                    return item.Tag.ToString() ?? defaultValue;
                }
            }
            // Custom user-typed value
            return text;
        }
        // Fall back to selected item's tag
        if (combo.SelectedItem is ComboBoxItem selectedItem && selectedItem.Tag != null)
        {
            return selectedItem.Tag.ToString() ?? defaultValue;
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

    private async void OnBackClick(object sender, RoutedEventArgs e)
    {
        if (_hasUnsavedChanges)
        {
            var loc = LocalizationService.Instance;
            var dialog = new ContentDialog
            {
                Title = loc.GetString("UnsavedChangesTitle"),
                Content = loc.GetString("UnsavedChangesMessage"),
                PrimaryButtonText = loc.GetString("SaveSettings"),
                SecondaryButtonText = loc.GetString("DontSave"),
                CloseButtonText = loc.GetString("Cancel"),
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = this.XamlRoot
            };

            var result = await ShowDialogAsync(dialog);

            if (result == ContentDialogResult.Primary)
            {
                // Save and then go back
                var saved = await SaveSettingsAsync();
                if (!saved) return; // Validation failed, stay on page
            }
            else if (result == ContentDialogResult.Secondary)
            {
                // Discard changes — restore SelectedLanguages to pre-edit snapshot
                _settings.SelectedLanguages = _originalSelectedLanguages;
                _hasUnsavedChanges = false;
            }
            else
            {
                // Cancel - stay on settings page
                return;
            }
        }

        if (Frame.CanGoBack)
        {
            Frame.GoBack();
        }
    }

    private async void OnSaveClick(object sender, RoutedEventArgs e)
    {
        var saved = await SaveSettingsAsync();
        if (!saved) return;

        // Show confirmation
        var loc = LocalizationService.Instance;
        var dialog = new ContentDialog
        {
            Title = loc.GetString("SettingsSaved"),
            Content = loc.GetString("SettingsSavedMessage"),
            CloseButtonText = loc.GetString("OK"),
            XamlRoot = this.XamlRoot
        };
        await ShowDialogAsync(dialog);
    }

    /// <summary>
    /// Validates and saves all settings. Returns true if save was successful, false if validation failed.
    /// </summary>
    private async Task<bool> SaveSettingsAsync()
    {
        // Get localization service instance once for the entire method
        var loc = LocalizationService.Instance;

        // Capture original proxy settings to detect changes
        var originalProxyEnabled = _settings.ProxyEnabled;
        var originalProxyUri = _settings.ProxyUri;
        var originalProxyBypassLocal = _settings.ProxyBypassLocal;

        // === Validate all inputs before modifying any settings ===

        // Validate language preferences
        var firstLang = GetSelectedTag(FirstLanguageCombo) ?? "zh";
        var secondLang = GetSelectedTag(SecondLanguageCombo) ?? "en";

        if (firstLang == secondLang)
        {
            var errorDialog = new ContentDialog
            {
                Title = loc.GetString("InvalidLanguageSelection"),
                Content = loc.GetString("InvalidLanguageSelectionMessage"),
                CloseButtonText = loc.GetString("OK"),
                XamlRoot = this.XamlRoot
            };
            await ShowDialogAsync(errorDialog);
            return false;
        }

        // Validate proxy URI
        var proxyUri = ProxyUriBox.Text?.Trim() ?? "";
        if (ProxyEnabledToggle.IsOn && string.IsNullOrWhiteSpace(proxyUri))
        {
            var errorDialog = new ContentDialog
            {
                Title = loc.GetString("InvalidProxyUrl"),
                Content = loc.GetString("InvalidProxyUrlMessage"),
                CloseButtonText = loc.GetString("OK"),
                XamlRoot = this.XamlRoot
            };
            await ShowDialogAsync(errorDialog);
            return false;
        }
        if (ProxyEnabledToggle.IsOn && !string.IsNullOrWhiteSpace(proxyUri))
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
                await ShowDialogAsync(errorDialog);
                return false;
            }
        }

        // === All validations passed — apply settings ===

        // Save international services setting
        _settings.EnableInternationalServices = EnableInternationalServicesToggle.IsOn;
        _settings.HasUserConfiguredServices = true;

        _settings.FirstLanguage = firstLang;
        _settings.SecondLanguage = secondLang;
        _settings.AutoSelectTargetLanguage = AutoSelectTargetToggle.IsOn;

        // Save selected languages from checkbox grid
        _settings.SelectedLanguages = _languageItems
            .Where(item => item.IsSelected)
            .Select(item => item.Tag)
            .ToList();

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
        _settings.ZhipuModel = GetEditableComboValue(ZhipuModelCombo, "glm-4.5-flash");

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
        _settings.BuiltInAIModel = GetEditableComboValue(BuiltInModelCombo, "glm-4-flash-250414");
        var builtInKey = BuiltInApiKeyBox.Password;
        _settings.BuiltInAIApiKey = string.IsNullOrWhiteSpace(builtInKey) ? null : builtInKey;

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

        // Save Youdao settings
        var youdaoAppKey = YoudaoAppKeyBox.Password;
        _settings.YoudaoAppKey = string.IsNullOrWhiteSpace(youdaoAppKey) ? null : youdaoAppKey;
        var youdaoAppSecret = YoudaoAppSecretBox.Password;
        _settings.YoudaoAppSecret = string.IsNullOrWhiteSpace(youdaoAppSecret) ? null : youdaoAppSecret;
        _settings.YoudaoUseOfficialApi = YoudaoUseOfficialApiToggle.IsOn;

        // Save encrypted MDX dictionary credentials from dynamic UI
        SaveEncryptedMdxCredentials();

        // Save HTTP Proxy settings (already validated above)
        _settings.ProxyEnabled = ProxyEnabledToggle.IsOn;
        _settings.ProxyBypassLocal = ProxyBypassLocalToggle.IsOn;
        _settings.ProxyUri = proxyUri;

        // Save behavior settings
        _settings.MinimizeToTray = MinimizeToTrayToggle.IsOn;
        _settings.MinimizeToTrayOnStartup = MinimizeToTrayOnStartupToggle.IsOn;
        _settings.ClipboardMonitoring = ClipboardMonitorToggle.IsOn;
        _settings.MouseSelectionTranslate = MouseSelectionTranslateToggle.IsOn;
        _settings.MouseSelectionExcludedApps = MouseSelectionExcludedAppsBox.Text
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => s.Length > 0)
            .ToList();
        _settings.AlwaysOnTop = AlwaysOnTopToggle.IsOn;
        _settings.LaunchAtStartup = LaunchAtStartupToggle.IsOn;

        // Apply startup setting to Windows registry
        StartupService.SetEnabled(_settings.LaunchAtStartup);

        // Save hotkey settings
        _settings.ShowWindowHotkey = ShowHotkeyBox.Text;
        _settings.TranslateSelectionHotkey = TranslateHotkeyBox.Text;
        _settings.ShowMiniWindowHotkey = ShowMiniHotkeyBox.Text;
        _settings.ShowFixedWindowHotkey = ShowFixedHotkeyBox.Text;
        _settings.OcrTranslateHotkey = OcrTranslateHotkeyBox.Text;
        _settings.SilentOcrHotkey = SilentOcrHotkeyBox.Text;

        // Save enabled services for each window (from collections)
        _settings.MainWindowEnabledServices = GetEnabledServicesFromCollection(_mainWindowServices);
        _settings.MiniWindowEnabledServices = GetEnabledServicesFromCollection(_miniWindowServices);
        _settings.FixedWindowEnabledServices = GetEnabledServicesFromCollection(_fixedWindowServices);

        // Layout Detection settings
        _settings.LayoutDetectionMode = GetSelectedTag(LayoutDetectionModeCombo) ?? "Auto";
        _settings.VisionLayoutServiceId = GetSelectedTag(VisionLayoutServiceCombo) ?? "gemini";

        // Formula Detection
        _settings.FormulaFontPattern = FormulaFontPatternBox.Text?.Trim() ?? "";
        _settings.FormulaCharPattern = FormulaCharPatternBox.Text?.Trim() ?? "";

        // Translation Cache
        _settings.EnableTranslationCache = TranslationCacheToggle.IsOn;

        // Custom Prompt
        _settings.LongDocCustomPrompt = CustomPromptBox.Text?.Trim() ?? "";

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

        // Refresh language combos in open windows to pick up SelectedLanguages changes
        MiniWindowService.Instance.RefreshLanguageCombos();
        FixedWindowService.Instance.RefreshLanguageCombos();

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
        App.ApplyMouseSelectionTranslate(_settings.MouseSelectionTranslate);

        // Hide the floating save button and reset unsaved changes flag
        _hasUnsavedChanges = false;
        SaveButton.Visibility = Visibility.Collapsed;

        return true;
    }

    private void OnMouseSelectionTranslateToggled(object sender, RoutedEventArgs e)
    {
        MouseSelectionExcludedAppsPanel.Visibility = MouseSelectionTranslateToggle.IsOn
            ? Visibility.Visible : Visibility.Collapsed;
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
            await ShowDialogAsync(errorDialog);
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
    /// Ensure at least one service is checked when the collection has no services selected.
    /// Uses region-appropriate default (bing for China, google for international),
    /// falling back to the first available service if the default is unavailable.
    /// Updates both the settings and the collection item.
    /// </summary>
    private static void EnsureDefaultServiceEnabled(ObservableCollection<ServiceCheckItem> collection, List<string> services)
    {
        if (services.Count > 0) return;

        var defaultServiceId = SettingsService.GetRegionDefaultServiceId();

        // If the region default is not available, pick the first available service
        var defaultItem = collection.FirstOrDefault(item => item.ServiceId == defaultServiceId && item.IsAvailable)
                       ?? collection.FirstOrDefault(item => item.IsAvailable);

        if (defaultItem != null)
        {
            services.Add(defaultItem.ServiceId);
            defaultItem.IsChecked = true;
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
            new NavSection("LanguagePreferencesSection", "Language Preferences", "\uE774", LanguagePreferencesSection), // Globe
            new NavSection("EnabledServicesSection", "Enabled Services", "\uE73E", EnabledServicesSection),           // Checkmark
            new NavSection("ServiceConfigurationSection", "Service Configuration", "\uE90F", ServiceConfigurationSection), // Key
            new NavSection("LayoutDetectionSection", "Layout Detection", "\uE8A1", LayoutDetectionSection),  // Page
            new NavSection("CjkFontSection", "CJK Font", "\uE8D2", CjkFontSection),  // Font
            new NavSection("FormulaDetectionSection", "Formula Detection", "\uE8EF", FormulaDetectionSection),  // Calculator
            new NavSection("TranslationCacheSection", "Translation Cache", "\uE74E", TranslationCacheSection),  // Save
            new NavSection("CustomPromptSection", "Custom Prompt", "\uE8BD", CustomPromptSection),  // Comment
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
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
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
                    // Inactive icon: smaller + secondary color
                    icon.FontSize = 14;
                    icon.Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
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
    /// Handle Enable International Services toggle change.
    /// Updates IsAvailable on all service items and unchecks unavailable services.
    /// </summary>
    private void OnEnableInternationalServicesToggled(object sender, RoutedEventArgs e)
    {
        if (_isLoading) return;

        var enabled = EnableInternationalServicesToggle.IsOn;

        UpdateServiceAvailability(_mainWindowServices, enabled);
        UpdateServiceAvailability(_miniWindowServices, enabled);
        UpdateServiceAvailability(_fixedWindowServices, enabled);

        // Ensure at least one available service is still enabled after unchecking unavailable ones
        var mainServices = GetEnabledServicesFromCollection(_mainWindowServices);
        var miniServices = GetEnabledServicesFromCollection(_miniWindowServices);
        var fixedServices = GetEnabledServicesFromCollection(_fixedWindowServices);
        EnsureDefaultServiceEnabled(_mainWindowServices, mainServices);
        EnsureDefaultServiceEnabled(_miniWindowServices, miniServices);
        EnsureDefaultServiceEnabled(_fixedWindowServices, fixedServices);

        SaveButton.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Update IsAvailable and uncheck unavailable services in a collection.
    /// </summary>
    private static void UpdateServiceAvailability(ObservableCollection<ServiceCheckItem> collection, bool internationalEnabled)
    {
        foreach (var item in collection)
        {
            var isInternationalOnly = SettingsService.InternationalOnlyServices.Contains(item.ServiceId);
            item.IsAvailable = internationalEnabled || !isInternationalOnly;

            // Uncheck unavailable services
            if (!item.IsAvailable && item.IsChecked)
            {
                item.IsChecked = false;
            }
        }

        // Sort: available items first, unavailable items last (preserve relative order within each group)
        var sorted = collection.OrderBy(item => item.IsAvailable ? 0 : 1).ToList();
        for (int i = 0; i < sorted.Count; i++)
        {
            var currentIndex = collection.IndexOf(sorted[i]);
            if (currentIndex != i)
                collection.Move(currentIndex, i);
        }
    }

    /// <summary>
    /// Handle app theme selection change.
    /// </summary>
    private void OnAppThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoading) return;

        var selectedTag = GetSelectedTag(AppThemeCombo);
        if (string.IsNullOrEmpty(selectedTag)) return;

        System.Diagnostics.Debug.WriteLine($"[SettingsPage] User selected theme: {selectedTag}");

        // Save the theme setting
        _settings.AppTheme = selectedTag;
        _settings.Save();

        // Apply theme immediately
        App.ApplyTheme(selectedTag);

        // Show the save button (in case other settings were changed)
        SaveButton.Visibility = Visibility.Visible;
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
            await ShowDialogAsync(dialog);
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
            await ShowDialogAsync(errorDialog);
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

    #region Service Test Handlers

    /// <summary>
    /// Creates a fresh, isolated service instance for testing.
    /// This ensures the test uses current UI values without modifying the shared service.
    /// </summary>
    private static ITranslationService? CreateFreshService(string serviceId, HttpClient httpClient)
    {
        return serviceId switch
        {
            "deepl" => new DeepLService(httpClient),
            "openai" => new OpenAIService(httpClient),
            "ollama" => new OllamaService(httpClient),
            "deepseek" => new DeepSeekService(httpClient),
            "groq" => new GroqService(httpClient),
            "zhipu" => new ZhipuService(httpClient),
            "github" => new GitHubModelsService(httpClient),
            "gemini" => new GeminiService(httpClient),
            "custom-openai" => new CustomOpenAIService(httpClient),
            "builtin" => new BuiltInAIService(httpClient),
            "doubao" => new DoubaoService(httpClient),
            "caiyun" => new CaiyunService(httpClient),
            "niutrans" => new NiuTransService(httpClient),
            _ => null
        };
    }

    /// <summary>
    /// Test a translation service with current UI configuration.
    /// </summary>
    /// <param name="serviceId">The service ID to test.</param>
    /// <param name="configureAction">Action to configure the service with current UI values.</param>
    /// <param name="testButton">The test button to disable during testing.</param>
    /// <param name="statusIndicator">Optional TextBlock to show success indicator.</param>
    private async Task TestServiceAsync(string serviceId, Action<ITranslationService> configureAction, Button testButton, TextBlock? statusIndicator = null)
    {
        var loc = LocalizationService.Instance;
        var originalContent = testButton.Content;

        testButton.IsEnabled = false;
        testButton.Content = loc.GetString("Testing");

        try
        {
            // Create an isolated service instance so the test uses UI values
            // and doesn't modify the shared service in TranslationManager.
            using var handle = TranslationManagerService.Instance.AcquireHandle();
            var service = CreateFreshService(serviceId, handle.Manager.SharedHttpClient);
            if (service == null)
            {
                throw new TranslationException($"Service '{serviceId}' not found");
            }

            // Configure the fresh instance with current UI values
            configureAction(service);

            // Check if configured
            if (!service.IsConfigured)
            {
                var notConfiguredDialog = new ContentDialog
                {
                    Title = loc.GetString("TestFailedTitle"),
                    Content = loc.GetString("TestNotConfigured"),
                    CloseButtonText = loc.GetString("OK"),
                    XamlRoot = this.XamlRoot
                };
                await ShowDialogAsync(notConfiguredDialog);
                return;
            }

            // Create a simple test request
            var request = new TranslationRequest
            {
                Text = "hello",
                FromLanguage = TranslationLanguage.English,
                ToLanguage = TranslationLanguage.SimplifiedChinese
            };

            // Run the test translation
            var result = await service.TranslateAsync(request);

            // Show success indicator on expander header
            if (statusIndicator != null)
            {
                statusIndicator.Visibility = Visibility.Visible;
            }

            // Save test success status
            _settings.ServiceTestStatus[serviceId] = true;
            _settings.Save();

            // Show success
            var successDialog = new ContentDialog
            {
                Title = loc.GetString("TestSuccessTitle"),
                Content = string.Format(loc.GetString("TestSuccessMessage"), result.TimingMs),
                CloseButtonText = loc.GetString("OK"),
                XamlRoot = this.XamlRoot
            };
            await ShowDialogAsync(successDialog);
        }
        catch (TranslationException ex)
        {
            // Clear test passed status and hide indicator
            _settings.ServiceTestStatus.Remove(serviceId);
            _settings.Save();
            if (statusIndicator != null)
            {
                statusIndicator.Visibility = Visibility.Collapsed;
            }

            var errorDialog = new ContentDialog
            {
                Title = loc.GetString("TestFailedTitle"),
                Content = string.Format(loc.GetString("TestFailedMessage"), ex.Message),
                CloseButtonText = loc.GetString("OK"),
                XamlRoot = this.XamlRoot
            };
            await ShowDialogAsync(errorDialog);
        }
        catch (Exception ex)
        {
            // Clear test passed status and hide indicator
            _settings.ServiceTestStatus.Remove(serviceId);
            _settings.Save();
            if (statusIndicator != null)
            {
                statusIndicator.Visibility = Visibility.Collapsed;
            }

            var errorDialog = new ContentDialog
            {
                Title = loc.GetString("TestFailedTitle"),
                Content = string.Format(loc.GetString("TestFailedMessage"), ex.Message),
                CloseButtonText = loc.GetString("OK"),
                XamlRoot = this.XamlRoot
            };
            await ShowDialogAsync(errorDialog);
        }
        finally
        {
            testButton.Content = originalContent;
            testButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// Test DeepL configuration.
    /// </summary>
    private async void OnTestDeepL(object sender, RoutedEventArgs e)
    {
        await TestServiceAsync("deepl", service =>
        {
            if (service is DeepLService deepl)
            {
                var apiKey = DeepLKeyBox.Password;
                deepl.Configure(
                    string.IsNullOrWhiteSpace(apiKey) ? null : apiKey,
                    useWebFirst: DeepLFreeCheck.IsChecked ?? true);
            }
        }, TestDeepLButton, DeepLStatusText);
    }

    /// <summary>
    /// Test OpenAI configuration.
    /// </summary>
    private async void OnTestOpenAI(object sender, RoutedEventArgs e)
    {
        await TestServiceAsync("openai", service =>
        {
            if (service is OpenAIService openai)
            {
                var apiKey = OpenAIKeyBox.Password;
                var endpoint = OpenAIEndpointBox.Text?.Trim();
                var model = GetEditableComboValue(OpenAIModelCombo, "gpt-4o-mini");

                openai.Configure(
                    string.IsNullOrWhiteSpace(apiKey) ? "" : apiKey,
                    string.IsNullOrWhiteSpace(endpoint) ? "https://api.openai.com/v1/chat/completions" : endpoint,
                    model);
            }
        }, TestOpenAIButton, OpenAIStatusText);
    }

    /// <summary>
    /// Test DeepSeek configuration.
    /// </summary>
    private async void OnTestDeepSeek(object sender, RoutedEventArgs e)
    {
        await TestServiceAsync("deepseek", service =>
        {
            if (service is DeepSeekService deepseek)
            {
                var apiKey = DeepSeekKeyBox.Password;
                var model = GetEditableComboValue(DeepSeekModelCombo, "deepseek-chat");
                deepseek.Configure(string.IsNullOrWhiteSpace(apiKey) ? "" : apiKey, model: model);
            }
        }, TestDeepSeekButton, DeepSeekStatusText);
    }

    /// <summary>
    /// Test Groq configuration.
    /// </summary>
    private async void OnTestGroq(object sender, RoutedEventArgs e)
    {
        await TestServiceAsync("groq", service =>
        {
            if (service is GroqService groq)
            {
                var apiKey = GroqKeyBox.Password;
                var model = GetEditableComboValue(GroqModelCombo, "llama-3.3-70b-versatile");
                groq.Configure(string.IsNullOrWhiteSpace(apiKey) ? "" : apiKey, model: model);
            }
        }, TestGroqButton, GroqStatusText);
    }

    /// <summary>
    /// Test Zhipu configuration.
    /// </summary>
    private async void OnTestZhipu(object sender, RoutedEventArgs e)
    {
        await TestServiceAsync("zhipu", service =>
        {
            if (service is ZhipuService zhipu)
            {
                var apiKey = ZhipuKeyBox.Password;
                var model = GetEditableComboValue(ZhipuModelCombo, "glm-4.5-flash");
                zhipu.Configure(string.IsNullOrWhiteSpace(apiKey) ? "" : apiKey, model: model);
            }
        }, TestZhipuButton, ZhipuStatusText);
    }

    /// <summary>
    /// Test GitHub Models configuration.
    /// </summary>
    private async void OnTestGitHubModels(object sender, RoutedEventArgs e)
    {
        await TestServiceAsync("github", service =>
        {
            if (service is GitHubModelsService github)
            {
                var token = GitHubModelsTokenBox.Password;
                var model = GetEditableComboValue(GitHubModelsModelCombo, "gpt-4.1");
                github.Configure(string.IsNullOrWhiteSpace(token) ? "" : token, model: model);
            }
        }, TestGitHubModelsButton, GitHubModelsStatusText);
    }

    /// <summary>
    /// Test Gemini configuration.
    /// </summary>
    private async void OnTestGemini(object sender, RoutedEventArgs e)
    {
        await TestServiceAsync("gemini", service =>
        {
            if (service is GeminiService gemini)
            {
                var apiKey = GeminiKeyBox.Password;
                var model = GetEditableComboValue(GeminiModelCombo, "gemini-2.5-flash");
                gemini.Configure(string.IsNullOrWhiteSpace(apiKey) ? "" : apiKey, model);
            }
        }, TestGeminiButton, GeminiStatusText);
    }

    /// <summary>
    /// Test Custom OpenAI configuration.
    /// </summary>
    private async void OnTestCustomOpenAI(object sender, RoutedEventArgs e)
    {
        await TestServiceAsync("custom-openai", service =>
        {
            if (service is CustomOpenAIService customOpenai)
            {
                var endpoint = CustomOpenAIEndpointBox.Text?.Trim() ?? "";
                var apiKey = CustomOpenAIKeyBox.Password;
                var model = CustomOpenAIModelBox.Text?.Trim();
                customOpenai.Configure(
                    endpoint,
                    string.IsNullOrWhiteSpace(apiKey) ? null : apiKey,
                    string.IsNullOrWhiteSpace(model) ? "gpt-3.5-turbo" : model);
            }
        }, TestCustomOpenAIButton, CustomOpenAIStatusText);
    }

    /// <summary>
    /// Test Ollama configuration.
    /// </summary>
    private async void OnTestOllama(object sender, RoutedEventArgs e)
    {
        await TestServiceAsync("ollama", service =>
        {
            if (service is OllamaService ollama)
            {
                var endpoint = OllamaEndpointBox.Text?.Trim();
                var model = OllamaModelCombo.Text?.Trim() ?? "llama3.2";
                ollama.Configure(
                    string.IsNullOrWhiteSpace(endpoint) ? "http://localhost:11434/v1/chat/completions" : endpoint,
                    model);
            }
        }, TestOllamaButton, OllamaStatusText);
    }

    /// <summary>
    /// Test Built-in AI configuration.
    /// </summary>
    private async void OnTestBuiltIn(object sender, RoutedEventArgs e)
    {
        await TestServiceAsync("builtin", service =>
        {
            if (service is BuiltInAIService builtin)
            {
                var model = GetEditableComboValue(BuiltInModelCombo, "glm-4-flash-250414");
                var apiKey = BuiltInApiKeyBox.Password;
                var deviceId = SettingsService.Instance.DeviceId;
                builtin.Configure(model, string.IsNullOrWhiteSpace(apiKey) ? null : apiKey, deviceId);
            }
        }, TestBuiltInButton, BuiltInStatusText);
    }

    /// <summary>
    /// Test Doubao configuration.
    /// </summary>
    private async void OnTestDoubao(object sender, RoutedEventArgs e)
    {
        await TestServiceAsync("doubao", service =>
        {
            if (service is DoubaoService doubao)
            {
                var apiKey = DoubaoKeyBox.Password;
                var endpoint = DoubaoEndpointBox.Text?.Trim();
                var model = DoubaoModelBox.Text?.Trim();
                doubao.Configure(
                    string.IsNullOrWhiteSpace(apiKey) ? "" : apiKey,
                    string.IsNullOrWhiteSpace(endpoint) ? "https://ark.cn-beijing.volces.com/api/v3/responses" : endpoint,
                    string.IsNullOrWhiteSpace(model) ? "doubao-seed-translation-250915" : model);
            }
        }, TestDoubaoButton, DoubaoStatusText);
    }

    /// <summary>
    /// Test Caiyun configuration.
    /// </summary>
    private async void OnTestCaiyun(object sender, RoutedEventArgs e)
    {
        await TestServiceAsync("caiyun", service =>
        {
            if (service is CaiyunService caiyun)
            {
                var apiKey = CaiyunKeyBox.Password;
                caiyun.Configure(string.IsNullOrWhiteSpace(apiKey) ? "" : apiKey);
            }
        }, TestCaiyunButton, CaiyunStatusText);
    }

    /// <summary>
    /// Test NiuTrans configuration.
    /// </summary>
    private async void OnTestNiuTrans(object sender, RoutedEventArgs e)
    {
        await TestServiceAsync("niutrans", service =>
        {
            if (service is NiuTransService niutrans)
            {
                var apiKey = NiuTransKeyBox.Password;
                niutrans.Configure(string.IsNullOrWhiteSpace(apiKey) ? "" : apiKey);
            }
        }, TestNiuTransButton, NiuTransStatusText);
    }

    #region Layout Detection

    private void OnLayoutDetectionModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoading) return;
        OnSettingChanged(sender, e);
        UpdateLayoutDetectionUI();
        UpdateOnnxModelStatus();
    }

    private void UpdateLayoutDetectionUI()
    {
        var mode = GetSelectedTag(LayoutDetectionModeCombo) ?? "Auto";

        // Show/hide ONNX model panel for Auto and OnnxLocal modes
        var showOnnx = mode is "Auto" or "OnnxLocal";
        OnnxModelPanel.Visibility = showOnnx ? Visibility.Visible : Visibility.Collapsed;

        // Show/hide Vision LLM panel for VisionLLM mode
        VisionLLMPanel.Visibility = mode == "VisionLLM" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateOnnxModelStatus()
    {
        using var downloadService = new LayoutModelDownloadService();
        var loc = LocalizationService.Instance;

        if (downloadService.IsReady)
        {
            OnnxModelStatusText.Text = loc.GetString("LayoutDetection_Downloaded");
            OnnxModelStatusIcon.Visibility = Visibility.Visible;
            DownloadOnnxModelButton.Visibility = Visibility.Collapsed;
            DeleteOnnxModelButton.Visibility = Visibility.Visible;
            _settings.OnnxModelDownloaded = true;
        }
        else
        {
            OnnxModelStatusText.Text = loc.GetString("LayoutDetection_NotDownloaded");
            OnnxModelStatusIcon.Visibility = Visibility.Collapsed;
            DownloadOnnxModelButton.Visibility = Visibility.Visible;
            DeleteOnnxModelButton.Visibility = Visibility.Collapsed;
            _settings.OnnxModelDownloaded = false;
        }
    }

    private async void OnDownloadOnnxModelClick(object sender, RoutedEventArgs e)
    {
        DownloadOnnxModelButton.IsEnabled = false;
        OnnxDownloadProgress.Visibility = Visibility.Visible;
        OnnxDownloadProgressText.Visibility = Visibility.Visible;

        try
        {
            using var downloadService = new LayoutModelDownloadService();
            var progress = new Progress<ModelDownloadProgress>(p =>
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (p.Percentage >= 0)
                    {
                        OnnxDownloadProgress.IsIndeterminate = false;
                        OnnxDownloadProgress.Value = p.Percentage;
                    }
                    else
                    {
                        OnnxDownloadProgress.IsIndeterminate = true;
                    }

                    var stageName = p.Stage == "runtime" ? "ONNX Runtime" : "Model";
                    var mb = p.BytesDownloaded / (1024.0 * 1024.0);
                    var totalMb = p.TotalBytes > 0 ? p.TotalBytes / (1024.0 * 1024.0) : 0;
                    OnnxDownloadProgressText.Text = totalMb > 0
                        ? $"{stageName}: {mb:F1} / {totalMb:F1} MB"
                        : $"{stageName}: {mb:F1} MB";
                });
            });

            await downloadService.EnsureAvailableAsync(progress);

            _settings.OnnxModelDownloaded = true;
            _settings.Save();
            UpdateOnnxModelStatus();
        }
        catch (Exception ex)
        {
            var loc = LocalizationService.Instance;
            var dialog = new ContentDialog
            {
                Title = loc.GetString("LayoutDetection_DownloadFailed"),
                Content = ex.Message,
                CloseButtonText = loc.GetString("OK"),
                XamlRoot = this.XamlRoot
            };
            await ShowDialogAsync(dialog);
        }
        finally
        {
            DownloadOnnxModelButton.IsEnabled = true;
            OnnxDownloadProgress.Visibility = Visibility.Collapsed;
            OnnxDownloadProgressText.Visibility = Visibility.Collapsed;
        }
    }

    private void OnDeleteOnnxModelClick(object sender, RoutedEventArgs e)
    {
        using var downloadService = new LayoutModelDownloadService();
        downloadService.DeleteAll();
        _settings.OnnxModelDownloaded = false;
        _settings.Save();
        UpdateOnnxModelStatus();
    }

    #endregion

    #region Formula Detection

    private void OnFormulaPatternChanged(object sender, TextChangedEventArgs e)
    {
        if (_isLoading) return;
        OnSettingChanged(sender, new RoutedEventArgs());
    }

    #endregion

    #region Translation Cache

    private void OnTranslationCacheToggled(object sender, RoutedEventArgs e)
    {
        if (_isLoading) return;
        OnSettingChanged(sender, new RoutedEventArgs());
    }

    private async void OnClearCacheClick(object sender, RoutedEventArgs e)
    {
        ClearCacheButton.IsEnabled = false;
        try
        {
            using var cacheService = new TranslationCacheService();
            await cacheService.ClearAsync();
            CacheStatusText.Text = "Cache cleared.";
        }
        catch (Exception ex)
        {
            CacheStatusText.Text = $"Error: {ex.Message}";
        }
        finally
        {
            ClearCacheButton.IsEnabled = true;
        }
    }

    private async Task UpdateCacheStatusAsync(CancellationToken cancellationToken = default)
    {
        if (_isUnloaded || cancellationToken.IsCancellationRequested)
        {
            return;
        }

        try
        {
            using var cacheService = new TranslationCacheService();
            var count = await cacheService.GetEntryCountAsync();
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

                CacheStatusText.Text = $"{count} cached entries";
            });
        }
        catch (OperationCanceledException)
        {
            // Ignore when teardown has canceled background work.
        }
        catch
        {
            // Ignore if cache DB doesn't exist yet
        }
    }

    #endregion

    #region Custom Prompt

    private void OnCustomPromptChanged(object sender, TextChangedEventArgs e)
    {
        if (_isLoading) return;
        OnSettingChanged(sender, new RoutedEventArgs());
    }

    #endregion

    #region CJK Font

    private async void OnCjkFontDownloadClick(object sender, RoutedEventArgs e)
    {
        CjkFontDownloadButton.IsEnabled = false;
        CjkFontProgressBar.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
        CjkFontProgressBar.Value = 0;
        CjkFontStatusText.Text = "Downloading...";

        try
        {
            using var fontService = new FontDownloadService();
            var progress = new Progress<ModelDownloadProgress>(p =>
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    CjkFontProgressBar.Value = p.Percentage;
                    CjkFontStatusText.Text = $"Downloading {p.Stage}... {p.Percentage:F0}%";
                });
            });

            // Download fonts for all CJK languages
            var languages = new[]
            {
                Easydict.TranslationService.Models.Language.SimplifiedChinese,
                Easydict.TranslationService.Models.Language.TraditionalChinese,
                Easydict.TranslationService.Models.Language.Japanese,
                Easydict.TranslationService.Models.Language.Korean,
            };

            foreach (var lang in languages)
            {
                await fontService.EnsureFontAsync(lang, progress);
            }

            CjkFontStatusText.Text = $"CJK fonts downloaded ({fontService.GetTotalFontSizeBytes() / 1024 / 1024}MB).";
        }
        catch (Exception ex)
        {
            CjkFontStatusText.Text = $"Download failed: {ex.Message}";
        }
        finally
        {
            CjkFontDownloadButton.IsEnabled = true;
            CjkFontProgressBar.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
        }
    }

    private void OnCjkFontDeleteClick(object sender, RoutedEventArgs e)
    {
        using var fontService = new FontDownloadService();
        fontService.DeleteAllFonts();
        CjkFontStatusText.Text = "CJK fonts deleted.";
    }

    #endregion

    /// <summary>
    /// Shows a ContentDialog, hiding any currently-open dialog first.
    /// WinUI 3 allows only one ContentDialog open at a time per XamlRoot.
    /// </summary>
    private async Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog)
    {
        if (_isUnloaded || _lifetimeCts.IsCancellationRequested)
        {
            return ContentDialogResult.None;
        }

        try { _currentDialog?.Hide(); } catch (COMException) { }
        _currentDialog = dialog;

        try
        {
            return await dialog.ShowAsync();
        }
        finally
        {
            if (_currentDialog == dialog)
            {
                _currentDialog = null;
            }
        }
    }

    #endregion

    #region Available Languages Checkbox

    /// <summary>
    /// Build the language checkbox items from AllLanguages, sorted by group
    /// with the user's FirstLanguage group shown first.
    /// </summary>
    private void PopulateLanguageCheckboxGrid()
    {
        UnregisterLanguageCheckboxHandlers();

        var loc = LocalizationService.Instance;
        var selectedSet = new HashSet<string>(_settings.SelectedLanguages, StringComparer.OrdinalIgnoreCase);

        // Determine which group the user's FirstLanguage belongs to
        var firstLangGroup = LanguageComboHelper.AllLanguages
            .FirstOrDefault(e => string.Equals(e.Tag, _settings.FirstLanguage, StringComparison.OrdinalIgnoreCase))
            .GroupOrder;

        _languageItems = LanguageComboHelper.AllLanguages
            .OrderBy(e => e.GroupOrder == firstLangGroup ? 0 : 1) // User's group first
            .ThenBy(e => e.GroupOrder) // Then by group order
            .Select(entry =>
            {
                var emoji = entry.Language.GetFlagEmoji();
                var name = loc.GetString(entry.LocalizationKey);
                var isEnglish = entry.Tag == "en";

                var item = new LanguageCheckboxItem
                {
                    Language = entry.Language,
                    Tag = entry.Tag,
                    DisplayText = $"{emoji} {name}",
                    IsEnabled = !isEnglish, // English is always selected and disabled
                    IsSelected = isEnglish || selectedSet.Contains(entry.Tag)
                };

                item.PropertyChanged += OnLanguageCheckboxChanged;
                return item;
            })
            .ToList();

        // Set up the ItemTemplate programmatically since LanguageCheckboxItem is a private inner class
        LanguageCheckboxGrid.ItemTemplate = CreateLanguageCheckboxTemplate();
        LanguageCheckboxGrid.ItemsSource = _languageItems;
    }

    private void UnregisterLanguageCheckboxHandlers()
    {
        foreach (var item in _languageItems)
        {
            item.PropertyChanged -= OnLanguageCheckboxChanged;
        }
    }

    /// <summary>
    /// Creates a DataTemplate for language checkbox items using XamlReader.
    /// Uses Binding (not x:Bind) since the model type is private.
    /// Cached to avoid repeated XamlReader.Load() calls.
    /// </summary>
    private static Microsoft.UI.Xaml.DataTemplate? _cachedLanguageCheckboxTemplate;
    private static Microsoft.UI.Xaml.DataTemplate CreateLanguageCheckboxTemplate()
    {
        if (_cachedLanguageCheckboxTemplate != null)
            return _cachedLanguageCheckboxTemplate;

        var xaml = """
            <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
                <CheckBox Content="{Binding DisplayText}"
                          IsChecked="{Binding IsSelected, Mode=TwoWay}"
                          IsEnabled="{Binding IsEnabled}"
                          MinWidth="160" Padding="4,0" />
            </DataTemplate>
            """;
        _cachedLanguageCheckboxTemplate = (Microsoft.UI.Xaml.DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load(xaml);
        return _cachedLanguageCheckboxTemplate;
    }

    /// <summary>
    /// Handle checkbox changes in the Available Languages grid.
    /// Validates minimum selection and updates dependent combos.
    /// </summary>
    private void OnLanguageCheckboxChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_isLoading || e.PropertyName != nameof(LanguageCheckboxItem.IsSelected)) return;
        if (sender is not LanguageCheckboxItem changedItem) return;

        // Count currently selected languages
        var selectedCount = _languageItems.Count(item => item.IsSelected);

        // Enforce minimum of 2 selected languages
        if (selectedCount < 2 && !changedItem.IsSelected)
        {
            // Revert the uncheck
            _isLoading = true;
            changedItem.IsSelected = true;
            _isLoading = false;
            return;
        }

        // Build new selected languages list
        var newSelectedLanguages = _languageItems
            .Where(item => item.IsSelected)
            .Select(item => item.Tag)
            .ToList();

        // Check if First/Second language was unchecked — reset if so
        var firstLang = GetSelectedTag(FirstLanguageCombo) ?? _settings.FirstLanguage;
        var secondLang = GetSelectedTag(SecondLanguageCombo) ?? _settings.SecondLanguage;

        if (!newSelectedLanguages.Contains(firstLang, StringComparer.OrdinalIgnoreCase))
        {
            // Reset First Language to the first available selected language (prefer "zh")
            firstLang = newSelectedLanguages.Contains("zh") ? "zh" : newSelectedLanguages[0];
        }

        if (!newSelectedLanguages.Contains(secondLang, StringComparer.OrdinalIgnoreCase))
        {
            // Reset Second Language to a still-selected language that differs from First
            secondLang = newSelectedLanguages.FirstOrDefault(t =>
                !string.Equals(t, firstLang, StringComparison.OrdinalIgnoreCase)) ?? newSelectedLanguages[0];
        }

        // Update settings temporarily for combo helpers to pick up
        _settings.SelectedLanguages = newSelectedLanguages;

        // Always rebuild First/Second Language combos since available languages changed
        _isLoading = true;
        try
        {
            var loc = LocalizationService.Instance;
            PopulateSettingsLanguageCombo(FirstLanguageCombo, loc);
            PopulateSettingsLanguageCombo(SecondLanguageCombo, loc);
            SelectComboByTag(FirstLanguageCombo, firstLang);
            SelectComboByTag(SecondLanguageCombo, secondLang);
        }
        finally
        {
            _isLoading = false;
        }

        OnSettingChanged(null!, null!);
    }

    /// <summary>
    /// Populate a settings First/Second language combo with active languages (flag emoji + name).
    /// Unlike the window combos, these don't have Auto Detect.
    /// </summary>
    private static void PopulateSettingsLanguageCombo(ComboBox combo, LocalizationService loc)
    {
        combo.Items.Clear();
        foreach (var entry in LanguageComboHelper.SelectableLanguages)
        {
            var emoji = entry.Language.GetFlagEmoji();
            var name = loc.GetString(entry.LocalizationKey);
            combo.Items.Add(new ComboBoxItem
            {
                Content = $"{emoji} {name}",
                Tag = entry.Tag
            });
        }
    }

    /// <summary>
    /// Model class for language checkbox items in the Available Languages grid.
    /// </summary>
    private sealed class LanguageCheckboxItem : INotifyPropertyChanged
    {
        public TranslationLanguage Language { get; init; }
        public string Tag { get; init; } = string.Empty;
        public string DisplayText { get; init; } = string.Empty;
        public bool IsEnabled { get; init; } = true;

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected != value)
                {
                    _isSelected = value;
                    OnPropertyChanged();
                }
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    #endregion
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

/// <summary>
/// Converts a boolean value to opacity.
/// True = 1.0 (fully opaque), False = 0.4 (grayed out).
/// </summary>
public class BoolToOpacityConverter : Microsoft.UI.Xaml.Data.IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is bool boolValue)
        {
            return boolValue ? 1.0 : 0.4;
        }
        return 1.0;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        if (value is double opacity)
        {
            return opacity > 0.5;
        }
        return true;
    }
}

/// <summary>
/// Converts a boolean (IsAvailable) to margin for compact layout.
/// True = normal spacing, False = reduced spacing for unavailable items.
/// </summary>
public class BoolToCompactMarginConverter : Microsoft.UI.Xaml.Data.IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is true ? new Thickness(0, 4, 0, 0) : new Thickness(0, 1, 0, 0);

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}

/// <summary>
/// Converts a boolean (IsAvailable) to font size for compact layout.
/// True = normal size (14), False = smaller size (12) for unavailable items.
/// </summary>
public class BoolToCompactFontSizeConverter : Microsoft.UI.Xaml.Data.IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is true ? 14.0 : 12.0;

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}

/// <summary>
/// Converts a boolean (IsUnconfigured) to FontStyle.
/// True = Italic (needs configuration), False = Normal.
/// </summary>
public class BoolToItalicFontStyleConverter : Microsoft.UI.Xaml.Data.IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is true ? Windows.UI.Text.FontStyle.Italic : Windows.UI.Text.FontStyle.Normal;

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}
