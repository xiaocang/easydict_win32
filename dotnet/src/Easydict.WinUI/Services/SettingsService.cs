using System.Text.Json;
using System.Text.Json.Serialization;
using Easydict.TranslationService;

namespace Easydict.WinUI.Services;

/// <summary>
/// Manages application settings with persistence using JSON file.
/// Works for both packaged and unpackaged WinUI 3 apps.
/// </summary>
public sealed class SettingsService
{
    private static readonly Lazy<SettingsService> _instance = new(() => new SettingsService());
    public static SettingsService Instance => _instance.Value;

    private readonly string _settingsFilePath;
    private Dictionary<string, object?> _settings = new();
    private volatile bool _needsRegionDetection;


    private SettingsService()
    {
        // Use AppData\Local\Easydict for settings
        var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var easydictPath = Path.Combine(appDataPath, "Easydict");
        Directory.CreateDirectory(easydictPath);
        _settingsFilePath = Path.Combine(easydictPath, "settings.json");

        LoadSettings();
    }

    // Translation settings
    public string SourceLanguage { get; set; } = "auto";

    // Language preference settings (for automatic target language selection)
    public string FirstLanguage { get; set; } = "zh";   // Simplified Chinese
    public string SecondLanguage { get; set; } = "en";  // English
    public bool AutoSelectTargetLanguage { get; set; } = true;

    /// <summary>
    /// Languages available in source/target pickers across all windows.
    /// Users configure this in Settings → Available Languages.
    /// </summary>
    public List<string> SelectedLanguages { get; set; } = ["zh", "en", "ja", "ko", "fr", "de", "es"];

    // API Keys
    public string? DeepLApiKey { get; set; }
    public bool DeepLUseFreeApi { get; set; } = true;

    // OpenAI settings
    public string? OpenAIApiKey { get; set; }
    public string OpenAIEndpoint { get; set; } = "https://api.openai.com/v1/chat/completions";
    public string OpenAIModel { get; set; } = "gpt-4o-mini";
    public double OpenAITemperature { get; set; } = 0.3;

    // Ollama settings (local LLM)
    public string OllamaEndpoint { get; set; } = "http://localhost:11434/v1/chat/completions";
    public string OllamaModel { get; set; } = "llama3.2";

    // Built-in AI settings
    public string BuiltInAIModel { get; set; } = "llama-3.3-70b-versatile";

    // DeepSeek settings
    public string? DeepSeekApiKey { get; set; }
    public string DeepSeekModel { get; set; } = "deepseek-chat";

    // Groq settings
    public string? GroqApiKey { get; set; }
    public string GroqModel { get; set; } = "llama-3.3-70b-versatile";

    // Zhipu settings
    public string? ZhipuApiKey { get; set; }
    public string ZhipuModel { get; set; } = "glm-4.5-flash";

    // GitHub Models settings
    public string? GitHubModelsToken { get; set; }
    public string GitHubModelsModel { get; set; } = "gpt-4.1";

    // Custom OpenAI settings
    public string CustomOpenAIEndpoint { get; set; } = "";
    public string? CustomOpenAIApiKey { get; set; }
    public string CustomOpenAIModel { get; set; } = "gpt-3.5-turbo";

    // Gemini settings
    public string? GeminiApiKey { get; set; }
    public string GeminiModel { get; set; } = "gemini-2.5-flash";

    // Doubao settings
    public string? DoubaoApiKey { get; set; }
    public string DoubaoEndpoint { get; set; } = "https://ark.cn-beijing.volces.com/api/v3/responses";
    public string DoubaoModel { get; set; } = "doubao-seed-translation-250915";

    // Caiyun settings
    public string? CaiyunApiKey { get; set; }

    // NiuTrans settings
    public string? NiuTransApiKey { get; set; }

    // Youdao settings
    public string? YoudaoAppKey { get; set; }
    public string? YoudaoAppSecret { get; set; }
    public bool YoudaoUseOfficialApi { get; set; } = false;

    // Volcano settings
    public string? VolcanoAccessKeyId { get; set; }
    public string? VolcanoSecretAccessKey { get; set; }

    // Linguee settings (no API key needed)

    // Behavior settings
    public bool MinimizeToTray { get; set; } = true;
    public bool ClipboardMonitoring { get; set; } = false;
    public bool AutoTranslate { get; set; } = false;

    /// <summary>
    /// Enable mouse selection translation: a floating translate button appears
    /// after selecting text in any application. Click the button to translate.
    /// </summary>
    public bool MouseSelectionTranslate { get; set; } = true;

    // Hotkey settings (stored as string like "Ctrl+Alt+T")
    public string ShowWindowHotkey { get; set; } = "Ctrl+Alt+T";
    public string TranslateSelectionHotkey { get; set; } = "Ctrl+Alt+D";

    // UI settings
    public bool AlwaysOnTop { get; set; } = false;

    // UI Language (for localization)
    public string UILanguage { get; set; } = ""; // Empty means system default

    // App Theme (System, Light, Dark)
    public string AppTheme { get; set; } = "System"; // Default to system theme

    // Startup settings
    public bool LaunchAtStartup { get; set; } = false;

    // Mini window settings
    public string ShowMiniWindowHotkey { get; set; } = "Ctrl+Alt+M";
    public bool MiniWindowAutoClose { get; set; } = true;
    public double MiniWindowXDips { get; set; } = 0;
    public double MiniWindowYDips { get; set; } = 0;
    public double MiniWindowWidthDips { get; set; } = 320;
    public double MiniWindowHeightDips { get; set; } = 200;
    public bool MiniWindowIsPinned { get; set; } = false;

    /// <summary>
    /// List of enabled translation services for MiniWindow.
    /// Each service result is displayed in a collapsible panel.
    /// </summary>
    public List<string> MiniWindowEnabledServices { get; set; } = ["google"];

    /// <summary>
    /// List of enabled translation services for MainWindow.
    /// Each service result is displayed in a collapsible panel.
    /// </summary>
    public List<string> MainWindowEnabledServices { get; set; } = ["google"];

    // Fixed window settings
    public string ShowFixedWindowHotkey { get; set; } = "Ctrl+Alt+F";
    public double FixedWindowXDips { get; set; } = 0;
    public double FixedWindowYDips { get; set; } = 0;
    public double FixedWindowWidthDips { get; set; } = 320;
    public double FixedWindowHeightDips { get; set; } = 280;

    /// <summary>
    /// List of enabled translation services for FixedWindow.
    /// Each service result is displayed in a collapsible panel.
    /// </summary>
    public List<string> FixedWindowEnabledServices { get; set; } = ["google"];

    /// <summary>
    /// Per-service EnabledQuery setting for MainWindow.
    /// When true (default), service auto-queries and expands. When false, starts collapsed and queries on demand.
    /// </summary>
    public Dictionary<string, bool> MainWindowServiceEnabledQuery { get; set; } = new();

    /// <summary>
    /// Per-service EnabledQuery setting for MiniWindow.
    /// When true (default), service auto-queries and expands. When false, starts collapsed and queries on demand.
    /// </summary>
    public Dictionary<string, bool> MiniWindowServiceEnabledQuery { get; set; } = new();

    /// <summary>
    /// Per-service EnabledQuery setting for FixedWindow.
    /// When true (default), service auto-queries and expands. When false, starts collapsed and queries on demand.
    /// </summary>
    public Dictionary<string, bool> FixedWindowServiceEnabledQuery { get; set; } = new();

    /// <summary>
    /// Tracks whether each service passed its last configuration test.
    /// Persisted to show test success indicators on settings page.
    /// </summary>
    public Dictionary<string, bool> ServiceTestStatus { get; set; } = new();

    /// <summary>
    /// Enable international services that may not be accessible in all regions.
    /// Auto-detected from system region; persisted value is used once saved.
    /// </summary>
    private bool _enableInternationalServices = true; // Optimistic default; corrected by InitializeRegionDefaultsAsync
    public bool EnableInternationalServices
    {
        get => _enableInternationalServices;
        set
        {
            if (_enableInternationalServices != value)
            {
                _enableInternationalServices = value;
                EnableInternationalServicesChanged?.Invoke(this, value);
            }
        }
    }

    /// <summary>
    /// Raised when <see cref="EnableInternationalServices"/> changes.
    /// </summary>
    public event EventHandler<bool>? EnableInternationalServicesChanged;

    /// <summary>
    /// True once the user has explicitly saved settings from the Settings page.
    /// Only set by the Settings page save handler — NOT by automatic Save() calls
    /// (window move, theme change, etc.). Used by <see cref="NotifyInternationalServiceFailed"/>
    /// to avoid overriding the user's explicit service choices.
    /// </summary>
    public bool HasUserConfiguredServices { get; set; }

    // HTTP Proxy settings
    /// <summary>
    /// Enable HTTP proxy for translation network requests.
    /// </summary>
    public bool ProxyEnabled { get; set; } = false;

    /// <summary>
    /// HTTP proxy URI (e.g., "http://127.0.0.1:7890").
    /// </summary>
    public string ProxyUri { get; set; } = "";

    /// <summary>
    /// Bypass proxy for localhost addresses (important for Ollama).
    /// </summary>
    public bool ProxyBypassLocal { get; set; } = true;

    /// <summary>
    /// Enable DPI-aware window positioning and scaling.
    /// Set to false to revert to legacy behavior if issues occur.
    /// </summary>
    public bool EnableDpiAwareness { get; set; } = true;

    /// <summary>
    /// Window width stored in DIPs (Device-Independent Pixels).
    /// Ensures consistent sizing across different DPI monitors.
    /// </summary>
    public double WindowWidthDips { get; set; } = 430;

    /// <summary>
    /// Window height stored in DIPs (Device-Independent Pixels).
    /// Ensures consistent sizing across different DPI monitors.
    /// </summary>
    public double WindowHeightDips { get; set; } = 503;

    /// <summary>
    /// Legacy property for backwards compatibility.
    /// Maps to WindowWidthDips for migration from older settings files.
    /// </summary>
    [JsonIgnore]
    public double WindowWidth
    {
        get => WindowWidthDips;
        set => WindowWidthDips = value;
    }

    /// <summary>
    /// Legacy property for backwards compatibility.
    /// Maps to WindowHeightDips for migration from older settings files.
    /// </summary>
    [JsonIgnore]
    public double WindowHeight
    {
        get => WindowHeightDips;
        set => WindowHeightDips = value;
    }

    private void LoadSettings()
    {
        try
        {
            if (File.Exists(_settingsFilePath))
            {
                var json = File.ReadAllText(_settingsFilePath);
                _settings = JsonSerializer.Deserialize<Dictionary<string, object?>>(json) ?? new();
            }
        }
        catch
        {
            _settings = new();
        }

        SourceLanguage = GetValue(nameof(SourceLanguage), "auto");

        // Load language preferences
        FirstLanguage = GetValue(nameof(FirstLanguage), "zh");
        SecondLanguage = GetValue(nameof(SecondLanguage), "en");
        AutoSelectTargetLanguage = GetValue(nameof(AutoSelectTargetLanguage), true);
        SelectedLanguages = GetStringList(nameof(SelectedLanguages), ["zh", "en", "ja", "ko", "fr", "de", "es"]);

        // Migration: Import old TargetLanguage if FirstLanguage not explicitly set
        if (_settings.ContainsKey("TargetLanguage") && !_settings.ContainsKey("FirstLanguage"))
        {
            var oldTarget = GetValue("TargetLanguage", "zh");
            FirstLanguage = oldTarget;
            // Set SecondLanguage as English ↔ Chinese fallback
            SecondLanguage = (oldTarget == "en") ? "zh" : "en";
            System.Diagnostics.Debug.WriteLine($"[Settings] Migrated TargetLanguage '{oldTarget}' to FirstLanguage");
        }
        // Note: TargetLanguage will NOT be written back on Save()

        // Validate: FirstLanguage and SecondLanguage cannot be the same
        if (FirstLanguage == SecondLanguage)
        {
            System.Diagnostics.Debug.WriteLine("[Settings] FirstLanguage == SecondLanguage, resetting to defaults");
            FirstLanguage = "zh";
            SecondLanguage = "en";
        }

        DeepLApiKey = GetValue<string?>(nameof(DeepLApiKey), null);
        DeepLUseFreeApi = GetValue(nameof(DeepLUseFreeApi), true);

        // OpenAI settings
        OpenAIApiKey = GetValue<string?>(nameof(OpenAIApiKey), null);
        OpenAIEndpoint = GetValue(nameof(OpenAIEndpoint), "https://api.openai.com/v1/chat/completions");
        OpenAIModel = GetValue(nameof(OpenAIModel), "gpt-4o-mini");
        OpenAITemperature = GetValue(nameof(OpenAITemperature), 0.3);

        // Ollama settings
        OllamaEndpoint = GetValue(nameof(OllamaEndpoint), "http://localhost:11434/v1/chat/completions");
        OllamaModel = GetValue(nameof(OllamaModel), "llama3.2");

        // Built-in AI settings
        BuiltInAIModel = GetValue(nameof(BuiltInAIModel), "llama-3.3-70b-versatile");

        // DeepSeek settings
        DeepSeekApiKey = GetValue<string?>(nameof(DeepSeekApiKey), null);
        DeepSeekModel = GetValue(nameof(DeepSeekModel), "deepseek-chat");

        // Groq settings
        GroqApiKey = GetValue<string?>(nameof(GroqApiKey), null);
        GroqModel = GetValue(nameof(GroqModel), "llama-3.3-70b-versatile");

        // Zhipu settings
        ZhipuApiKey = GetValue<string?>(nameof(ZhipuApiKey), null);
        ZhipuModel = GetValue(nameof(ZhipuModel), "glm-4.5-flash");

        // GitHub Models settings
        GitHubModelsToken = GetValue<string?>(nameof(GitHubModelsToken), null);
        GitHubModelsModel = GetValue(nameof(GitHubModelsModel), "gpt-4.1");

        // Custom OpenAI settings
        CustomOpenAIEndpoint = GetValue(nameof(CustomOpenAIEndpoint), "");
        CustomOpenAIApiKey = GetValue<string?>(nameof(CustomOpenAIApiKey), null);
        CustomOpenAIModel = GetValue(nameof(CustomOpenAIModel), "gpt-3.5-turbo");

        // Gemini settings
        GeminiApiKey = GetValue<string?>(nameof(GeminiApiKey), null);
        GeminiModel = GetValue(nameof(GeminiModel), "gemini-2.5-flash");

        // Doubao settings
        DoubaoApiKey = GetValue<string?>(nameof(DoubaoApiKey), null);
        DoubaoEndpoint = GetValue(nameof(DoubaoEndpoint), "https://ark.cn-beijing.volces.com/api/v3/responses");
        DoubaoModel = GetValue(nameof(DoubaoModel), "doubao-seed-translation-250915");

        // Caiyun settings
        CaiyunApiKey = GetValue<string?>(nameof(CaiyunApiKey), null);

        // NiuTrans settings
        NiuTransApiKey = GetValue<string?>(nameof(NiuTransApiKey), null);

        // Volcano settings
        VolcanoAccessKeyId = GetValue<string?>(nameof(VolcanoAccessKeyId), null);
        VolcanoSecretAccessKey = GetValue<string?>(nameof(VolcanoSecretAccessKey), null);

        MinimizeToTray = GetValue(nameof(MinimizeToTray), true);
        ClipboardMonitoring = GetValue(nameof(ClipboardMonitoring), false);
        AutoTranslate = GetValue(nameof(AutoTranslate), false);
        MouseSelectionTranslate = GetValue(nameof(MouseSelectionTranslate), true);
        ShowWindowHotkey = GetValue(nameof(ShowWindowHotkey), "Ctrl+Alt+T");
        TranslateSelectionHotkey = GetValue(nameof(TranslateSelectionHotkey), "Ctrl+Alt+D");
        AlwaysOnTop = GetValue(nameof(AlwaysOnTop), false);
        UILanguage = GetValue(nameof(UILanguage), "");
        AppTheme = GetValue(nameof(AppTheme), "System");
        LaunchAtStartup = GetValue(nameof(LaunchAtStartup), false);
        EnableDpiAwareness = GetValue(nameof(EnableDpiAwareness), true);

        // Try to load WindowWidthDips first (new format), fallback to WindowWidth (legacy)
        WindowWidthDips = GetValue(nameof(WindowWidthDips), GetValue(nameof(WindowWidth), 600.0));
        WindowHeightDips = GetValue(nameof(WindowHeightDips), GetValue(nameof(WindowHeight), 700.0));

        // Mini window settings
        ShowMiniWindowHotkey = GetValue(nameof(ShowMiniWindowHotkey), "Ctrl+Alt+M");
        MiniWindowAutoClose = GetValue(nameof(MiniWindowAutoClose), true);
        MiniWindowXDips = GetValue(nameof(MiniWindowXDips), 0.0);
        MiniWindowYDips = GetValue(nameof(MiniWindowYDips), 0.0);
        MiniWindowWidthDips = GetValue(nameof(MiniWindowWidthDips), 320.0);
        MiniWindowHeightDips = GetValue(nameof(MiniWindowHeightDips), 200.0);
        MiniWindowIsPinned = GetValue(nameof(MiniWindowIsPinned), false);
        MiniWindowEnabledServices = GetStringList(nameof(MiniWindowEnabledServices), ["google"]);
        MainWindowEnabledServices = GetStringList(nameof(MainWindowEnabledServices), ["google"]);

        // Fixed window settings
        ShowFixedWindowHotkey = GetValue(nameof(ShowFixedWindowHotkey), "Ctrl+Alt+F");
        FixedWindowXDips = GetValue(nameof(FixedWindowXDips), 0.0);
        FixedWindowYDips = GetValue(nameof(FixedWindowYDips), 0.0);
        FixedWindowWidthDips = GetValue(nameof(FixedWindowWidthDips), 320.0);
        FixedWindowHeightDips = GetValue(nameof(FixedWindowHeightDips), 280.0);
        FixedWindowEnabledServices = GetStringList(nameof(FixedWindowEnabledServices), ["google"]);

        // EnabledQuery settings per window (which services auto-query vs. query on demand)
        MainWindowServiceEnabledQuery = GetStringBoolDictionary(nameof(MainWindowServiceEnabledQuery));
        MiniWindowServiceEnabledQuery = GetStringBoolDictionary(nameof(MiniWindowServiceEnabledQuery));
        FixedWindowServiceEnabledQuery = GetStringBoolDictionary(nameof(FixedWindowServiceEnabledQuery));

        // Service test status
        ServiceTestStatus = GetStringBoolDictionary(nameof(ServiceTestStatus));

        // International services: use optimistic default (true) during sync construction.
        // Actual region detection runs asynchronously via InitializeRegionDefaultsAsync().
        if (_settings.ContainsKey(nameof(EnableInternationalServices)))
        {
            EnableInternationalServices = GetValue(nameof(EnableInternationalServices), true);
        }
        else
        {
            EnableInternationalServices = true;
            _needsRegionDetection = true;
        }

        // Flag: true once user has explicitly saved settings from the Settings page.
        // Used by NotifyInternationalServiceFailed to avoid overriding user choices.
        HasUserConfiguredServices = GetValue(nameof(HasUserConfiguredServices), false);

        // HTTP Proxy settings
        ProxyEnabled = GetValue(nameof(ProxyEnabled), false);
        ProxyUri = GetValue(nameof(ProxyUri), "");
        ProxyBypassLocal = GetValue(nameof(ProxyBypassLocal), true);
    }

    public void Save()
    {
        _settings[nameof(SourceLanguage)] = SourceLanguage;

        // Save language preferences
        _settings[nameof(FirstLanguage)] = FirstLanguage;
        _settings[nameof(SecondLanguage)] = SecondLanguage;
        _settings[nameof(AutoSelectTargetLanguage)] = AutoSelectTargetLanguage;
        _settings[nameof(SelectedLanguages)] = SelectedLanguages;

        _settings[nameof(DeepLApiKey)] = DeepLApiKey ?? string.Empty;
        _settings[nameof(DeepLUseFreeApi)] = DeepLUseFreeApi;

        // OpenAI settings
        _settings[nameof(OpenAIApiKey)] = OpenAIApiKey ?? string.Empty;
        _settings[nameof(OpenAIEndpoint)] = OpenAIEndpoint;
        _settings[nameof(OpenAIModel)] = OpenAIModel;
        _settings[nameof(OpenAITemperature)] = OpenAITemperature;

        // Ollama settings
        _settings[nameof(OllamaEndpoint)] = OllamaEndpoint;
        _settings[nameof(OllamaModel)] = OllamaModel;

        // Built-in AI settings
        _settings[nameof(BuiltInAIModel)] = BuiltInAIModel;

        // DeepSeek settings
        _settings[nameof(DeepSeekApiKey)] = DeepSeekApiKey ?? string.Empty;
        _settings[nameof(DeepSeekModel)] = DeepSeekModel;

        // Groq settings
        _settings[nameof(GroqApiKey)] = GroqApiKey ?? string.Empty;
        _settings[nameof(GroqModel)] = GroqModel;

        // Zhipu settings
        _settings[nameof(ZhipuApiKey)] = ZhipuApiKey ?? string.Empty;
        _settings[nameof(ZhipuModel)] = ZhipuModel;

        // GitHub Models settings
        _settings[nameof(GitHubModelsToken)] = GitHubModelsToken ?? string.Empty;
        _settings[nameof(GitHubModelsModel)] = GitHubModelsModel;

        // Custom OpenAI settings
        _settings[nameof(CustomOpenAIEndpoint)] = CustomOpenAIEndpoint;
        _settings[nameof(CustomOpenAIApiKey)] = CustomOpenAIApiKey ?? string.Empty;
        _settings[nameof(CustomOpenAIModel)] = CustomOpenAIModel;

        // Gemini settings
        _settings[nameof(GeminiApiKey)] = GeminiApiKey ?? string.Empty;
        _settings[nameof(GeminiModel)] = GeminiModel;

        // Doubao settings
        _settings[nameof(DoubaoApiKey)] = DoubaoApiKey ?? string.Empty;
        _settings[nameof(DoubaoEndpoint)] = DoubaoEndpoint;
        _settings[nameof(DoubaoModel)] = DoubaoModel;

        // Caiyun settings
        _settings[nameof(CaiyunApiKey)] = CaiyunApiKey ?? string.Empty;

        // NiuTrans settings
        _settings[nameof(NiuTransApiKey)] = NiuTransApiKey ?? string.Empty;

        // Volcano settings
        _settings[nameof(VolcanoAccessKeyId)] = VolcanoAccessKeyId ?? string.Empty;
        _settings[nameof(VolcanoSecretAccessKey)] = VolcanoSecretAccessKey ?? string.Empty;

        _settings[nameof(MinimizeToTray)] = MinimizeToTray;
        _settings[nameof(ClipboardMonitoring)] = ClipboardMonitoring;
        _settings[nameof(AutoTranslate)] = AutoTranslate;
        _settings[nameof(MouseSelectionTranslate)] = MouseSelectionTranslate;
        _settings[nameof(ShowWindowHotkey)] = ShowWindowHotkey;
        _settings[nameof(TranslateSelectionHotkey)] = TranslateSelectionHotkey;
        _settings[nameof(AlwaysOnTop)] = AlwaysOnTop;
        _settings[nameof(UILanguage)] = UILanguage;
        _settings[nameof(AppTheme)] = AppTheme;
        _settings[nameof(LaunchAtStartup)] = LaunchAtStartup;
        _settings[nameof(EnableDpiAwareness)] = EnableDpiAwareness;
        _settings[nameof(WindowWidthDips)] = WindowWidthDips;
        _settings[nameof(WindowHeightDips)] = WindowHeightDips;

        // Mini window settings
        _settings[nameof(ShowMiniWindowHotkey)] = ShowMiniWindowHotkey;
        _settings[nameof(MiniWindowAutoClose)] = MiniWindowAutoClose;
        _settings[nameof(MiniWindowXDips)] = MiniWindowXDips;
        _settings[nameof(MiniWindowYDips)] = MiniWindowYDips;
        _settings[nameof(MiniWindowWidthDips)] = MiniWindowWidthDips;
        _settings[nameof(MiniWindowHeightDips)] = MiniWindowHeightDips;
        _settings[nameof(MiniWindowIsPinned)] = MiniWindowIsPinned;
        _settings[nameof(MiniWindowEnabledServices)] = MiniWindowEnabledServices;
        _settings[nameof(MainWindowEnabledServices)] = MainWindowEnabledServices;

        // Fixed window settings
        _settings[nameof(ShowFixedWindowHotkey)] = ShowFixedWindowHotkey;
        _settings[nameof(FixedWindowXDips)] = FixedWindowXDips;
        _settings[nameof(FixedWindowYDips)] = FixedWindowYDips;
        _settings[nameof(FixedWindowWidthDips)] = FixedWindowWidthDips;
        _settings[nameof(FixedWindowHeightDips)] = FixedWindowHeightDips;
        _settings[nameof(FixedWindowEnabledServices)] = FixedWindowEnabledServices;

        // EnabledQuery settings per window
        _settings[nameof(MainWindowServiceEnabledQuery)] = MainWindowServiceEnabledQuery;
        _settings[nameof(MiniWindowServiceEnabledQuery)] = MiniWindowServiceEnabledQuery;
        _settings[nameof(FixedWindowServiceEnabledQuery)] = FixedWindowServiceEnabledQuery;

        // Service test status
        _settings[nameof(ServiceTestStatus)] = ServiceTestStatus;

        // International services setting
        _settings[nameof(EnableInternationalServices)] = EnableInternationalServices;
        _settings[nameof(HasUserConfiguredServices)] = HasUserConfiguredServices;

        // HTTP Proxy settings
        _settings[nameof(ProxyEnabled)] = ProxyEnabled;
        _settings[nameof(ProxyUri)] = ProxyUri;
        _settings[nameof(ProxyBypassLocal)] = ProxyBypassLocal;

        try
        {
            var json = JsonSerializer.Serialize(_settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_settingsFilePath, json);

            // Verify the file was written successfully
            System.Diagnostics.Debug.WriteLine($"[SettingsService] Settings saved successfully to: {_settingsFilePath}");
            System.Diagnostics.Debug.WriteLine($"[SettingsService] UILanguage saved as: {UILanguage}");
        }
        catch (Exception ex)
        {
            // Log the error for debugging
            System.Diagnostics.Debug.WriteLine($"[SettingsService] ERROR: Failed to save settings: {ex.Message}");
            System.Diagnostics.Debug.WriteLine($"[SettingsService] Settings file path: {_settingsFilePath}");
            System.Diagnostics.Debug.WriteLine($"[SettingsService] Exception: {ex}");

            // Re-throw the exception so callers know save failed
            throw;
        }
    }

    /// <summary>
    /// Asynchronously detects the system region and applies appropriate defaults.
    /// Must be called once after application startup completes.
    /// On first launch (no saved EnableInternationalServices), detects whether the system
    /// is in China and switches default services from Google to Bing if so.
    /// For returning users with saved settings, this is a no-op.
    /// </summary>
    public async Task InitializeRegionDefaultsAsync()
    {
        if (!_needsRegionDetection)
            return;

        var isChinaRegion = await Task.Run(() => IsChinaRegion());

        System.Diagnostics.Debug.WriteLine(
            $"[SettingsService] Async region detection complete: IsChinaRegion={isChinaRegion}");

        if (isChinaRegion)
        {
            EnableInternationalServices = false;
            ReplaceInList(MiniWindowEnabledServices, "google", "bing");
            ReplaceInList(MainWindowEnabledServices, "google", "bing");
            ReplaceInList(FixedWindowEnabledServices, "google", "bing");
            Save();
        }

        _needsRegionDetection = false;
    }

    /// <summary>
    /// Service IDs that require international network access (may not be available in all regions).
    /// </summary>
    public static readonly HashSet<string> InternationalOnlyServices = new(StringComparer.OrdinalIgnoreCase)
    {
        "google", "google_web", "deepl", "openai", "gemini",
        "groq", "github", "builtin", "linguee"
    };

    /// <summary>
    /// Detects whether the system is configured for China mainland based on locale/region settings.
    /// This is a synchronous, locale-only check used for default property initialization.
    /// For devices with non-Chinese locale in China, see <see cref="NotifyInternationalServiceFailed"/>
    /// which combines timezone detection with actual translation failure as a lazy probe.
    /// </summary>
    public static bool IsChinaRegion()
    {
        try
        {
            var region = System.Globalization.RegionInfo.CurrentRegion;
            if (region.TwoLetterISORegionName.Equals("CN", StringComparison.OrdinalIgnoreCase))
                return true;

            // Also check system culture (only zh-CN and zh-Hans-CN are mainland China;
            // zh-Hans alone is ambiguous and could be Singapore zh-Hans-SG, etc.)
            var culture = System.Globalization.CultureInfo.CurrentUICulture;
            var name = culture.Name.ToLowerInvariant();
            if (name == "zh-cn" || name == "zh-hans-cn")
                return true;
        }
        catch
        {
            // Ignore errors in region detection
        }
        return false;
    }

    /// <summary>
    /// Checks whether the system timezone is set to China Standard Time (UTC+8 Beijing/Shanghai).
    /// Note: This timezone is shared by other regions (HK, Singapore, Malaysia, etc.),
    /// so it must NOT be used alone as a China indicator.
    /// </summary>
    public static bool IsChineseTimezone()
    {
        try
        {
            var tz = TimeZoneInfo.Local;
            return tz.Id.Equals("China Standard Time", StringComparison.OrdinalIgnoreCase) ||
                   tz.Id.Equals("Asia/Shanghai", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Returns the default service ID appropriate for the current region.
    /// </summary>
    public static string GetRegionDefaultServiceId()
    {
        return IsChinaRegion() ? "bing" : "google";
    }

    /// <summary>
    /// Checks if a service ID belongs to the international-only set.
    /// </summary>
    public static bool IsInternationalOnlyService(string serviceId)
    {
        return InternationalOnlyServices.Contains(serviceId);
    }

    /// <summary>
    /// Called when an international-only service fails with a network error during translation.
    /// The translation failure itself serves as the network probe — no extra HTTP request needed.
    /// Migrates default services from Google to Bing when:
    ///   1. Locale didn't already detect China (those users already get Bing)
    ///   2. User hasn't explicitly configured services yet (first launch defaults)
    ///   3. Timezone is China Standard Time (narrows scope to UTC+8 region)
    /// This avoids false positives: Singapore/HK users won't trigger this because their
    /// international services work fine and never produce network errors.
    /// </summary>
    public void NotifyInternationalServiceFailed(string serviceId, TranslationErrorCode errorCode)
    {
        // Only act on network-related failures
        if (errorCode is not (TranslationErrorCode.NetworkError or TranslationErrorCode.Timeout))
            return;

        // Only act on international-only services
        if (!IsInternationalOnlyService(serviceId))
            return;

        // Skip if locale already detected China — defaults are already Bing
        if (IsChinaRegion())
            return;

        // Skip if user has explicitly saved settings from the Settings page.
        // This flag is only set by the Settings page save handler, NOT by
        // automatic Save() calls (window move, theme change, etc.).
        if (HasUserConfiguredServices)
            return;

        // Skip if timezone is not Chinese — no reason to suspect restricted network
        if (!IsChineseTimezone())
            return;

        System.Diagnostics.Debug.WriteLine(
            $"[SettingsService] International service '{serviceId}' failed with {errorCode} " +
            "in Chinese timezone → applying China defaults");

        // Switch Google → Bing in all window enabled services
        ReplaceInList(MiniWindowEnabledServices, "google", "bing");
        ReplaceInList(MainWindowEnabledServices, "google", "bing");
        ReplaceInList(FixedWindowEnabledServices, "google", "bing");
        EnableInternationalServices = false;
        Save();
    }

    /// <summary>
    /// Replaces all occurrences of <paramref name="oldValue"/> in <paramref name="list"/>
    /// with <paramref name="newValue"/>. If <paramref name="newValue"/> already exists,
    /// removes <paramref name="oldValue"/> instead to avoid duplicates.
    /// </summary>
    private static void ReplaceInList(List<string> list, string oldValue, string newValue)
    {
        var hasNewValue = list.Contains(newValue);
        for (var i = list.Count - 1; i >= 0; i--)
        {
            if (list[i] == oldValue)
            {
                if (hasNewValue)
                    list.RemoveAt(i);     // bing already present → just remove google
                else
                {
                    list[i] = newValue;   // replace first google → bing
                    hasNewValue = true;   // subsequent googles should be removed
                }
            }
        }
    }

    private T GetValue<T>(string key, T defaultValue)
    {
        if (_settings.TryGetValue(key, out var value) && value != null)
        {
            try
            {
                if (value is JsonElement jsonElement)
                {
                    if (typeof(T) == typeof(string))
                        return (T)(object)jsonElement.GetString()!;
                    if (typeof(T) == typeof(bool))
                        return (T)(object)jsonElement.GetBoolean();
                    if (typeof(T) == typeof(double))
                        return (T)(object)jsonElement.GetDouble();
                    if (typeof(T) == typeof(int))
                        return (T)(object)jsonElement.GetInt32();
                }

                if (value is T typedValue)
                    return typedValue;

                // Handle type conversion
                if (typeof(T) == typeof(double) && value is long longVal)
                    return (T)(object)(double)longVal;
                if (typeof(T) == typeof(bool) && value is bool boolVal)
                    return (T)(object)boolVal;
            }
            catch { }
        }
        return defaultValue;
    }

    private List<string> GetStringList(string key, List<string> defaultValue)
    {
        if (_settings.TryGetValue(key, out var value) && value != null)
        {
            try
            {
                if (value is JsonElement jsonElement && jsonElement.ValueKind == JsonValueKind.Array)
                {
                    var list = new List<string>();
                    foreach (var item in jsonElement.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.String)
                        {
                            list.Add(item.GetString()!);
                        }
                    }
                    return list.Count > 0 ? list : defaultValue;
                }

                if (value is List<string> stringList)
                {
                    return stringList;
                }
            }
            catch { }
        }
        return defaultValue;
    }

    private Dictionary<string, bool> GetStringBoolDictionary(string key)
    {
        if (_settings.TryGetValue(key, out var value) && value != null)
        {
            try
            {
                if (value is JsonElement jsonElement && jsonElement.ValueKind == JsonValueKind.Object)
                {
                    var dict = new Dictionary<string, bool>();
                    foreach (var prop in jsonElement.EnumerateObject())
                    {
                        if (prop.Value.ValueKind == JsonValueKind.True || prop.Value.ValueKind == JsonValueKind.False)
                        {
                            dict[prop.Name] = prop.Value.GetBoolean();
                        }
                    }
                    return dict;
                }

                if (value is Dictionary<string, bool> boolDict)
                {
                    return boolDict;
                }
            }
            catch { }
        }
        return new Dictionary<string, bool>();
    }

    /// <summary>
    /// Clears the test passed status for a service when translation fails.
    /// This ensures the success indicator is removed if the service stops working.
    /// </summary>
    /// <param name="serviceId">The service ID whose test status should be cleared</param>
    public void ClearServiceTestStatus(string serviceId)
    {
        if (ServiceTestStatus.Remove(serviceId))
        {
            Save();
        }
    }
}

