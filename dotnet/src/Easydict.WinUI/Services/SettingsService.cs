using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Easydict.TranslationService;
using Easydict.TranslationService.TextActions;
using Easydict.TranslationService.Models;
using Easydict.TranslationService.Services;
using Easydict.TranslationService.Services.AgentCli;
using Easydict.SidecarClient.Protocol;
using Easydict.WindowsAI;
using Easydict.WindowsAI.Services;
using Easydict.WinUI.Models;

namespace Easydict.WinUI.Services;

/// <summary>
/// Manages application settings with persistence using JSON file.
/// Works for both packaged and unpackaged WinUI 3 apps.
/// </summary>
public sealed class SettingsService
{
    private const string GoogleServiceId = "google";
    private const string SettingsDirectoryEnvironmentVariable = "EASYDICT_SETTINGS_DIR";
    private const string DisableLongDocWorkerEnvironmentVariable = "EASYDICT_DISABLE_LONGDOC_WORKER";
    private const string DisableLocalAiWorkerEnvironmentVariable = "EASYDICT_DISABLE_LOCALAI_WORKER";
    private const string DisableOcrWorkerEnvironmentVariable = "EASYDICT_DISABLE_OCR_WORKER";
    private static readonly JsonSerializerOptions SettingsJsonOptions = new() { WriteIndented = true };

    public sealed class ImportedMdxDictionary
    {
        public string ServiceId { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string FilePath { get; set; } = string.Empty;

        /// <summary>
        /// True if the MDX file uses type-2 encryption and requires credentials.
        /// </summary>
        public bool IsEncrypted { get; set; }

        /// <summary>
        /// Base64-encoded registration code for encrypted MDX dictionaries (Encrypted="2").
        /// </summary>
        public string? Regcode { get; set; }

        /// <summary>
        /// Email address or device ID for encrypted MDX dictionaries (RegisterBy="EMail" or "DeviceID").
        /// </summary>
        public string? Email { get; set; }

        /// <summary>
        /// Paths to companion MDD resource files (CSS, images, audio, JS).
        /// </summary>
        public List<string> MddFilePaths { get; set; } = [];
    }

    /// <summary>
    /// One installed Bob plugin instance. Secure option values live in the credential store under
    /// <see cref="BobSecureOptionKey"/>, never in this record.
    /// </summary>
    public sealed class InstalledBobPlugin
    {
        public string ServiceId { get; set; } = string.Empty;
        public string PluginIdentifier { get; set; } = string.Empty;
        public string InstanceId { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string InstallDirectory { get; set; } = string.Empty;
        public string? IconPath { get; set; }

        /// <summary>Bob language codes probed when the plugin was installed. Empty means "no restriction".</summary>
        public List<string> SupportedLanguageCodes { get; set; } = [];

        /// <summary>Non-secure option values, as the plugin's own option identifiers.</summary>
        public Dictionary<string, string> OptionValues { get; set; } = new();

        /// <summary>Identifiers of options whose values are held by the credential store.</summary>
        public List<string> SecureOptionIds { get; set; } = [];

        // Automatic host behavior a plugin only receives once the user opts in, because a plugin
        // can have side effects (billing, rate limits, writes) the host cannot see.
        public bool AllowResultCache { get; set; }
        public bool AllowHostRetry { get; set; }
        public bool AllowPhoneticEnrichment { get; set; }
    }

    private static readonly Lazy<SettingsService> _instance = new(() => new SettingsService());
    public static SettingsService Instance => _instance.Value;

    private readonly string _settingsFilePath;
    private Dictionary<string, object?> _settings = new();
    private volatile bool _needsRegionDetection;
    private readonly HashSet<string> _sensitiveSettingsPendingMigration = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, string?>> _aiProfileSecrets = new(StringComparer.Ordinal);
    private readonly HashSet<string> _aiProfileSecretsCouldNotDecrypt = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _legacyAiSettingsBaseline = new(StringComparer.Ordinal);
    private bool _aiProfilesCanPersist = true;
    private bool _aiProfilesExplicitlyManaged;

    private const string AiServiceProfilesKey = "AiServiceProfiles";
    private const string AiServiceProfilesSchemaVersionKey = "AiServiceProfilesSchemaVersion";
    private const int CurrentAiServiceProfilesSchemaVersion = 1;
    private const string AiProfileSecretKeyPrefix = "AiProfileSecret:";


    private SettingsService()
        : this(ResolveSettingsDirectory())
    {
    }

    internal SettingsService(string settingsDirectory)
    {
        if (string.IsNullOrWhiteSpace(settingsDirectory))
        {
            throw new ArgumentException("A settings directory is required.", nameof(settingsDirectory));
        }

        var easydictPath = Path.GetFullPath(settingsDirectory);
        Directory.CreateDirectory(easydictPath);
        _settingsFilePath = Path.Combine(easydictPath, "settings.json");

        LoadSettings();
    }

    internal static string ResolveSettingsDirectory()
    {
        var settingsDirectory = Environment.GetEnvironmentVariable(SettingsDirectoryEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(settingsDirectory))
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(settingsDirectory));
        }

        var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(appDataPath, "Easydict");
    }

    // AnkiConnect settings (field templates are stored per note type).
    public bool AnkiEnabled { get; set; }
    public bool AnkiQuickAdd { get; set; }
    public bool AnkiRememberLast { get; set; }
    public string AnkiLastDeck { get; set; } = "";
    public string AnkiLastModel { get; set; } = "";
    public string AnkiEndpoint { get; set; } = "http://127.0.0.1:8765/";
    public string AnkiDeck { get; set; } = "";
    public string AnkiModel { get; set; } = "";
    public string AnkiFieldTemplates { get; set; } = "{}";

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
    public bool DeepLUseQualityOptimized { get; set; }

    // OpenAI settings
    public string? OpenAIApiKey { get; set; }
    public string OpenAIEndpoint { get; set; } = OpenAIService.DefaultEndpoint;
    public string OpenAIModel { get; set; } = OpenAIService.DefaultModel;
    public double OpenAITemperature { get; set; } = 0.3;
    /// <summary>
    /// API format override for OpenAI: "Auto" (default, infer from endpoint URL —
    /// <c>/responses</c> → Responses, anything else → Chat Completions),
    /// "Responses", or "ChatCompletions". Persisted as the enum string value of
    /// <c>Easydict.TranslationService.Services.OpenAIApiFormat</c>.
    /// </summary>
    public string OpenAIApiFormatOverride { get; set; } = "Auto";

    // Ollama settings (local LLM)
    public string OllamaEndpoint { get; set; } = "http://localhost:11434/v1/chat/completions";
    public string OllamaModel { get; set; } = "llama3.2";

    /// <summary>
    /// Ollama translation prompt, stored as a <c>TranslationService.Models.OllamaPromptStyle</c> name:
    /// "Standard" (detailed), "Concise" (for small models) or "Custom".
    /// </summary>
    public string OllamaPromptStyle { get; set; } = "Standard";

    /// <summary>System prompt used when <see cref="OllamaPromptStyle"/> is "Custom".</summary>
    public string OllamaCustomPrompt { get; set; } = "";

    // Local AI provider settings. Stored as the enum name.
    // "WindowsAI" is retained as the persisted value for the Phi Silica backend.
    public string LocalAIProvider { get; set; } = "Auto";

    // Foundry Local settings. Empty endpoint means auto-detect from the local CLI.
    public string FoundryLocalEndpoint { get; set; } = "";
    public string FoundryLocalModel { get; set; } = FoundryLocalService.DefaultModel;

    // OpenVINO local NLLB fallback settings.
    // Stored as the enum name ("Auto", "NPU", "GPU", "CPU"); parsed in
    // TranslationManagerService when configuring the service.
    public string OpenVinoDevice { get; set; } = "Auto";

    // DeepSeek settings
    public string? DeepSeekApiKey { get; set; }
    public string DeepSeekModel { get; set; } = "deepseek-chat";

    // Groq settings
    public string? GroqApiKey { get; set; }
    public string GroqModel { get; set; } = "llama-3.3-70b-versatile";

    // Zhipu settings
    public string? ZhipuApiKey { get; set; }
    public string ZhipuModel { get; set; } = "glm-4.5-flash";

    // Kimi settings
    public string? KimiApiKey { get; set; }
    public string KimiModel { get; set; } = "kimi-k2-turbo-preview";

    // GitHub Models settings
    public string? GitHubModelsToken { get; set; }
    public string GitHubModelsModel { get; set; } = "gpt-4.1";

    // Custom OpenAI settings
    public string CustomOpenAIEndpoint { get; set; } = "";
    public string? CustomOpenAIApiKey { get; set; }
    public string CustomOpenAIModel { get; set; } = "gpt-3.5-turbo";

    // OpenRouter settings
    public string? OpenRouterApiKey { get; set; }
    public string OpenRouterModel { get; set; } = "openrouter/free";

    // OrcaRouter settings
    public string? OrcaRouterApiKey { get; set; }
    public string OrcaRouterModel { get; set; } = "orcarouter/free";
    // True once the API key above was obtained via "Sign in with OrcaRouter" (PKCE), rather
    // than pasted in manually. The Settings UI locks the key box while this is set, so the
    // user isn't left wondering whether it's still safe to hand-edit; Disconnect clears both.
    public bool OrcaRouterSignedInViaSso { get; set; }

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

    // Agent CLI settings (Claude Code / Codex). Disabled by default; the
    // settings UI shows a risk acknowledgment before the first enable.
    public bool ClaudeCodeEnabled { get; set; } = false;
    public string ClaudeCodeModel { get; set; } = ClaudeCodeService.DefaultModel;
    public string ClaudeCodeExecutablePath { get; set; } = "";
    public bool CodexEnabled { get; set; } = false;
    public string CodexModel { get; set; } = CodexCliService.DefaultModel;
    public string CodexReasoningEffort { get; set; } = CodexCliService.DefaultReasoningEffort;

    /// <summary>Standalone AI provider profiles. Local AI engine choices remain separate.</summary>
    public List<AiServiceProfile> AiServiceProfiles { get; private set; } = [];

    /// <summary>Non-null when stored profile data is malformed or from a newer schema.</summary>
    public string? AiServiceProfilesLoadError { get; private set; }

    /// <summary>True when a valid persisted profile collection explicitly owns the AI registry.</summary>
    public bool AiServiceProfilesAreAuthoritative { get; private set; }

    // Behavior settings
    public bool MinimizeToTray { get; set; } = true;
    public bool ClipboardMonitoring { get; set; } = false;
    public bool AutoTranslate { get; set; } = false;


    /// <summary>
    /// Whether completed queries are written to local history. Off by default; favorites remain available.
    /// </summary>
    public bool HistoryEnabled { get; set; } = false;

    /// <summary>
    /// Number of days visible history is retained before being pruned.
    /// </summary>
    public int HistoryRetentionDays { get; set; } = 30;
    /// <summary>
    /// When true, long-document translation runs in a child worker process
    /// (Easydict.Workers.LongDoc.exe). Native MuPDF / ONNX heap is reclaimed
    /// on each job by exiting the worker after completion. On by default
    /// with in-process fallback on startup/internal worker failures.
    /// Runtime-only: not persisted to settings.json. Use
    /// EASYDICT_DISABLE_LONGDOC_WORKER=1 for development diagnostics.
    /// </summary>
    public bool UseLongDocWorker { get; set; } = true;

    /// <summary>
    /// When true, local AI translation routes through a child worker process
    /// (Easydict.Workers.LocalAi.exe) instead of the in-proc LocalAITranslationService.
    /// On by default to isolate local model native memory; worker startup failures
    /// fall back to the in-proc LocalAITranslationService.
    /// Runtime-only: not persisted to settings.json. Use
    /// EASYDICT_DISABLE_LOCALAI_WORKER=1 for development diagnostics.
    /// </summary>
    public bool UseLocalAiWorker { get; set; } = true;

    /// <summary>
    /// When true, Windows Native OCR runs in a short-lived child worker process
    /// so WinRT OCR/bitmap resources are reclaimed by process exit. API-based OCR
    /// engines continue to use their existing in-proc HTTP path.
    /// Runtime-only: not persisted to settings.json. Use
    /// EASYDICT_DISABLE_OCR_WORKER=1 for development diagnostics.
    /// </summary>
    public bool UseOcrWorker { get; set; } = true;

    /// <summary>
    /// Enable mouse selection translation: a floating translate button appears
    /// after selecting text in any application. Click the button to translate.
    /// </summary>
    public bool MouseSelectionTranslate { get; set; } = true;

    /// <summary>
    /// Process names excluded from mouse selection translate.
    /// When the foreground app matches any name in this list, the pop button is suppressed.
    /// </summary>
    public List<string> MouseSelectionExcludedApps { get; set; } = ["code"];

    /// <summary>
    /// Lower bound for <see cref="MouseSelectionPopDelayMs"/>: as close to instant as is useful.
    /// Not zero — the source app needs a moment to publish the finished selection before it can be read.
    /// </summary>
    public const int MinMouseSelectionPopDelayMs = 10;

    /// <summary>Upper bound for <see cref="MouseSelectionPopDelayMs"/>.</summary>
    public const int MaxMouseSelectionPopDelayMs = 600;

    /// <summary>Default pop delay: the built-in multi-click settle cap.</summary>
    public const int DefaultMouseSelectionPopDelayMs = MouseHookService.MaxMultiClickWaitMs;

    private int _mouseSelectionPopDelayMs = DefaultMouseSelectionPopDelayMs;

    /// <summary>
    /// Upper bound, in milliseconds, on how long the pop button waits after a selection gesture
    /// before reading the selected text. Lower values show the translate icon sooner; raising it
    /// leaves more room for a slow triple-click to be recognized before the icon appears.
    /// Clamped to [<see cref="MinMouseSelectionPopDelayMs"/>, <see cref="MaxMouseSelectionPopDelayMs"/>].
    /// </summary>
    public int MouseSelectionPopDelayMs
    {
        get => _mouseSelectionPopDelayMs;
        set => _mouseSelectionPopDelayMs = Math.Clamp(
            value, MinMouseSelectionPopDelayMs, MaxMouseSelectionPopDelayMs);
    }

    /// <summary>
    /// Enable hover word lookup (悬浮取词): hold the trigger key and rest the pointer on a word
    /// to see a small popup with its meaning. Shares <see cref="MouseSelectionExcludedApps"/>.
    /// </summary>
    public bool HoverWordLookupEnabled
    {
        get => _hoverWordLookupEnabled;
        set
        {
            if (_hoverWordLookupEnabled == value) return;
            _hoverWordLookupEnabled = value;
            HoverWordLookupEnabledChanged?.Invoke(this, EventArgs.Empty);
        }
    }
    private bool _hoverWordLookupEnabled;

    public event EventHandler? HoverWordLookupEnabledChanged;

    /// <summary>
    /// Trigger key for hover word lookup, stored as a <see cref="Models.HoverLookupModifier"/> name
    /// ("None", "Ctrl", "Shift", "Alt").
    /// </summary>
    public string HoverWordLookupModifier { get; set; } = "Ctrl";

    public const int MinHoverWordLookupDelayMs = HoverDwellDetector.MinDwellMs;
    public const int MaxHoverWordLookupDelayMs = HoverDwellDetector.MaxDwellMs;
    public const int DefaultHoverWordLookupDelayMs = HoverDwellDetector.DwellMs;

    private int _hoverWordLookupDelayMs = DefaultHoverWordLookupDelayMs;

    /// <summary>Pointer dwell before a lookup starts; excludes extraction and translation time.</summary>
    public int HoverWordLookupDelayMs
    {
        get => _hoverWordLookupDelayMs;
        set => _hoverWordLookupDelayMs = Math.Clamp(value, MinHoverWordLookupDelayMs, MaxHoverWordLookupDelayMs);
    }

    /// <summary>
    /// Use OCR on the pixels around the pointer when the application exposes no accessible text.
    /// </summary>
    public bool HoverWordLookupUseOcrFallback { get; set; } = true;

    /// <summary>
    /// Translation service id used by the hover popup; empty = Auto (first enabled dictionary service).
    /// </summary>
    public string HoverWordLookupServiceId { get; set; } = "";

    /// <summary>
    /// When true, the pop button uses the legacy Win32 SetWindowPos + per-monitor DPI math
    /// to position itself. When false, it uses the WinAppSDK 2.x PopupAnchor / DesktopPopupSiteBridge
    /// path which anchors relative to the source app's hwnd.
    ///
    /// Default true (legacy) until the PopupAnchor migration is verified end-to-end against
    /// multi-monitor + mixed-DPI scenarios. Flip to false to opt in to the new path.
    /// </summary>
    public bool PopButtonUseLegacyPositioning { get; set; } = true;

    /// <summary>
    /// When true and the host's WebView2 Runtime supports it (>= 144.0.3719.11), allow the user
    /// to drag text/HTML/URL/image content out of the WebView2 dictionary results into other apps
    /// (Obsidian, Word, Chrome, …). Disable if it conflicts with text selection in your workflow.
    /// </summary>
    public bool WebView2DragEnabled { get; set; } = true;

    /// <summary>
    /// Checks if a process name is in the mouse selection excluded apps list.
    /// </summary>
    public bool IsMouseSelectionExcluded(string? processName)
    {
        if (string.IsNullOrEmpty(processName) || MouseSelectionExcludedApps.Count == 0)
            return false;
        return MouseSelectionExcludedApps.Exists(
            app => string.Equals(app.Trim(), processName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Register "OCR 截图翻译" in the Windows Shell context menu (File Explorer / Desktop).
    /// The entry launches --ocr-translate which signals the running app via a named event.
    /// </summary>
    public bool ShellContextMenu { get; set; } = false;

    // Hotkey settings (stored as string like "Ctrl+Alt+T")
    public string ShowWindowHotkey { get; set; } = "Ctrl+Alt+T";
    public string TranslateSelectionHotkey { get; set; } = "Ctrl+Alt+D";

    // OCR settings
    public string OcrTranslateHotkey { get; set; } = "Ctrl+Alt+S";
    public string SilentOcrHotkey { get; set; } = "Ctrl+Alt+Shift+S";

    // Per-hotkey enable flags. When false, the corresponding RegisterHotKey call is skipped at startup.
    public bool EnableShowWindowHotkey { get; set; } = true;
    public bool EnableTranslateSelectionHotkey { get; set; } = true;
    public bool EnableShowMiniWindowHotkey { get; set; } = true;
    public bool EnableShowFixedWindowHotkey { get; set; } = true;
    public bool EnableOcrTranslateHotkey { get; set; } = true;
    public bool EnableSilentOcrHotkey { get; set; } = true;

    private bool _hideEmptyServiceResults = true;

    /// <summary>
    /// When true, ServiceResultItem rows for services that returned no result are demoted
    /// (grayed out, moved to the bottom of the list, and not expandable).
    /// </summary>
    public bool HideEmptyServiceResults
    {
        get => _hideEmptyServiceResults;
        set
        {
            if (_hideEmptyServiceResults == value) return;
            _hideEmptyServiceResults = value;
            HideEmptyServiceResultsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Raised when <see cref="HideEmptyServiceResults"/> changes. Subscribers should refresh any
    /// UI that depends on the demotion state of no-result service rows.
    /// </summary>
    public event EventHandler? HideEmptyServiceResultsChanged;

    /// <summary>
    /// Preferred OCR recognition language. "auto" uses the system profile languages.
    /// Otherwise a BCP-47 tag like "zh-Hans-CN", "en-US", "ja".
    /// </summary>
    public string OcrLanguage { get; set; } = "auto";

    // OCR Engine settings
    public OcrEngineType OcrEngine { get; set; } = OcrEngineType.WindowsNative;
    public string? OcrApiKey { get; set; }
    public string OcrEndpoint { get; set; } = OcrServiceOptions.DefaultEndpoint;
    public string OcrModel { get; set; } = OcrServiceOptions.DefaultModel;
    public string PpOcrV6ModelId { get; set; } = PpOcrV6ModelCatalog.SmallId;
    public string OcrSystemPrompt { get; set; } = "Extract all the text from this image perfectly. Output ONLY the extracted text, without any conversational filler, markdown formatting, or introductory words.";

    /// <summary>
    /// Whether the OCR model may think before answering. Off by default: reasoning adds
    /// substantial latency without improving text recognition. Only has an effect on
    /// models that support thinking.
    /// </summary>
    public bool OcrEnableThinking { get; set; }

    /// <summary>CPU inference threads for PP-OCRv6. Range: 1-16. Default: 4.</summary>
    public int PpOcrV6ThreadCount { get; set; } = PpOcrV6ModelCatalog.DefaultThreadCount;

    /// <summary>Use the DirectML GPU provider for PP-OCRv6 when available.</summary>
    public bool PpOcrV6UseGpu { get; set; }

    /// <summary>Fall back to Windows OCR when PP-OCRv6 is unavailable or fails.</summary>
    public bool PpOcrV6AllowFallback { get; set; } = true;

    // Layout detection settings (long document translation)

    /// <summary>
    /// Layout detection mode for long document translation.
    /// Values: "Auto", "OnnxLocal", "VisionLLM", "Heuristic".
    /// Auto = prefer local ONNX → fallback to heuristic.
    /// </summary>
    public string LayoutDetectionMode { get; set; } = "Auto";

    /// <summary>
    /// Service ID for vision LLM layout detection (e.g., "openai", "gemini").
    /// </summary>
    public string VisionLayoutServiceId { get; set; } = "gemini";

    /// <summary>
    /// Whether the ONNX model has been downloaded.
    /// </summary>
    public bool OnnxModelDownloaded { get; set; }

    /// <summary>
    /// Whether the Microsoft Table Transformer (TATR) model has been downloaded.
    /// Used for stage-2 cell-level structure recognition inside detected tables.
    /// </summary>
    public bool TatrModelDownloaded { get; set; }

    /// <summary>
    /// Kill switch for TATR stage-2 table structure recognition. When false,
    /// tables are preserved as single blocks (pre-TATR behavior) even if the
    /// model is downloaded. Default: true. Exposed as a kill switch so users
    /// can A/B compare or work around regressions without deleting the model.
    /// </summary>
    public bool EnableTatrTableStructure { get; set; } = true;

    /// <summary>
    /// Document output mode for long document translation.
    /// Values: "Monolingual", "Bilingual", "Both".
    /// Monolingual = translated-only (default).
    /// Bilingual = original + translated interleaved.
    /// Both = generates both monolingual and bilingual outputs.
    /// </summary>
    public string DocumentOutputMode { get; set; } = "Monolingual";

    /// <summary>
    /// Maximum concurrent translation requests for long document translation.
    /// Range: 1-16. Default: 4.
    /// </summary>
    public int LongDocMaxConcurrency { get; set; } = 4;

    /// <summary>
    /// When enabled, long-document translation runs a Pass 1 LLM read of the
    /// document (page-by-page, parallel, gated by <see cref="LongDocMaxConcurrency"/>)
    /// to extract a glossary, summary, and preservation hints, then prepends
    /// them to every Pass 2 translation prompt for terminology consistency.
    /// Default: true.
    /// </summary>
    public bool LongDocEnableDocumentContextPass { get; set; } = true;

    /// <summary>
    /// Custom regex pattern for font-based formula detection (Level 2).
    /// Empty uses the built-in math font regex.
    /// </summary>
    public string FormulaFontPattern { get; set; } = "";

    /// <summary>
    /// Custom regex pattern for character-based formula detection (Level 3).
    /// Empty uses the built-in Unicode math character regex.
    /// </summary>
    public string FormulaCharPattern { get; set; } = "";

    /// <summary>
    /// Enable persistent translation cache for long document translation.
    /// Caches translated segments in a local SQLite database for reuse.
    /// </summary>
    public bool EnableTranslationCache { get; set; } = true;

    /// <summary>
    /// Page range for long document translation. Empty = all pages.
    /// Supports formats: "1-3,5,7-10", "1-5", "3".
    /// Not persisted - starts empty each session so default is translating all pages.
    /// </summary>
    public string LongDocPageRange { get; set; } = "";

    /// <summary>
    /// Custom LLM prompt for long document translation.
    /// Appended to the system prompt for LLM-based translation services.
    /// </summary>
    public string LongDocCustomPrompt { get; set; } = "";

    // UI settings
    public bool AlwaysOnTop { get; set; } = false;

    // TTS settings
    public double TtsSpeed { get; set; } = 1.0;
    public string SelectedTtsVoiceId { get; set; } = string.Empty;
    public bool AutoPlayTranslation { get; set; } = false;

    // UI Language (for localization)
    public string UILanguage { get; set; } = ""; // Empty means system default

    // App Theme (System, Light, Dark, Minimal)
    public string AppTheme { get; set; } = "System"; // Default to system theme

    // Appearance / personalization (issue #172)
    /// <summary>
    /// Font-size multiplier for translation result text across all windows.
    /// 1.0 = default; clamped to [0.85, 1.4] when applied via AppearanceService.
    /// </summary>
    public double ResultFontScale { get; set; } = 1.0;

    /// <summary>Hide secondary chrome and optional helper actions for a tighter translation surface.</summary>
    public bool CompactMode { get; set; } = false;

    /// <summary>Show the OCR quick-action button in the translation windows.</summary>
    public bool ShowOcrButton { get; set; } = true;

    /// <summary>Show the pin / always-on-top toggle button.</summary>
    public bool ShowPinButton { get; set; } = true;

    /// <summary>Show the play-source-text (TTS) button.</summary>
    public bool ShowSourcePlayButton { get; set; } = true;

    /// <summary>Show the swap-languages button.</summary>
    public bool ShowSwapButton { get; set; } = true;

    /// <summary>
    /// Keep the fixed window always on top. Default true (fixed window has always been topmost);
    /// exposed so users can turn it off via the pin toggle.
    /// </summary>
    public bool FixedWindowIsPinned { get; set; } = true;

    // Startup settings
    public bool LaunchAtStartup { get; set; } = false;

    /// <summary>
    /// When enabled, the app starts minimized to the system tray without showing the main window.
    /// Requires MinimizeToTray to be enabled.
    /// </summary>
    public bool MinimizeToTrayOnStartup { get; set; } = false;

    /// <summary>
    /// Built-in system tray icon id. <see cref="TrayIconCatalog.DefaultId"/> keeps the current icon.
    /// The other ids use artwork shipped under Assets/Branding/TrayIcons.
    /// </summary>
    public string TrayIconId { get; set; } = TrayIconCatalog.DefaultId;

    // Mini window settings
    public string ShowMiniWindowHotkey { get; set; } = "Ctrl+Alt+M";
    public bool MiniWindowAutoClose { get; set; } = true;
    public double MiniWindowXDips { get; set; } = 0;
    public double MiniWindowYDips { get; set; } = 0;
    // Distinguishes "never saved" from "saved at (0, 0)" or saved to a monitor whose
    // virtual-screen coordinates are negative (monitor placed left/above primary).
    public bool MiniWindowPositionSaved { get; set; } = false;
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

    /// <summary>
    /// Imported MDX dictionaries. Each entry is exposed as a selectable translation service.
    /// </summary>
    public List<ImportedMdxDictionary> ImportedMdxDictionaries { get; set; } = [];

    /// <summary>
    /// Installed Bob plugins. Each entry is exposed as a manual-query translation service.
    /// </summary>
    public List<InstalledBobPlugin> InstalledBobPlugins { get; set; } = [];

    /// <summary>
    /// User-configurable text actions (selection pop-up buttons and each window's Actions menu).
    /// Loaded as saved (user removals are respected); defaults are seeded on first run.
    /// </summary>
    public List<TextAction> TextActions { get; set; } = TextActionCatalog.Normalize(null);

    // Fixed window settings
    public string ShowFixedWindowHotkey { get; set; } = "Ctrl+Alt+F";
    public double FixedWindowXDips { get; set; } = 0;
    public double FixedWindowYDips { get; set; } = 0;
    // See MiniWindowPositionSaved.
    public bool FixedWindowPositionSaved { get; set; } = false;
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
    /// Bypass proxy for services hosted in mainland China (*.cn, Youdao, DeepSeek, cn.bing.com, ...).
    /// </summary>
    public bool ProxyBypassChina { get; set; }

    /// <summary>
    /// Enable DPI-aware window positioning and scaling.
    /// Set to false to revert to legacy behavior if issues occur.
    /// </summary>
    public bool EnableDpiAwareness { get; set; } = true;

    /// <summary>
    /// Whether grammar correction results should include explanations of changes.
    /// </summary>
    public bool GrammarIncludeExplanations { get; set; } = true;

    /// <summary>
    /// Whether local MDX dictionaries should provide input suggestions in the main page.
    /// </summary>
    public bool EnableLocalDictionarySuggestions { get; set; } = false;

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
        catch (Exception ex)
        {
            Debug.WriteLine($"[SettingsService] Failed to load settings: {ex.Message}");
            _settings = new();
        }

        AnkiEnabled = GetValue(nameof(AnkiEnabled), false);
        AnkiQuickAdd = GetValue(nameof(AnkiQuickAdd), false);
        AnkiRememberLast = GetValue(nameof(AnkiRememberLast), false);
        AnkiLastDeck = GetValue(nameof(AnkiLastDeck), "");
        AnkiLastModel = GetValue(nameof(AnkiLastModel), "");
        AnkiEndpoint = GetValue(nameof(AnkiEndpoint), "http://127.0.0.1:8765/");
        AnkiDeck = GetValue(nameof(AnkiDeck), "");
        AnkiModel = GetValue(nameof(AnkiModel), "");
        AnkiFieldTemplates = GetValue(nameof(AnkiFieldTemplates), "{}");
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

        DeepLApiKey = GetSensitiveSetting(nameof(DeepLApiKey));
        DeepLUseFreeApi = GetValue(nameof(DeepLUseFreeApi), true);
        DeepLUseQualityOptimized = GetValue(nameof(DeepLUseQualityOptimized), false);
        NormalizeDeepLModeSettings();

        // OpenAI settings
        OpenAIApiKey = GetSensitiveSetting(nameof(OpenAIApiKey));
        OpenAIEndpoint = GetValue(nameof(OpenAIEndpoint), OpenAIService.DefaultEndpoint);
        OpenAIModel = GetValue(nameof(OpenAIModel), OpenAIService.DefaultModel);
        OpenAITemperature = GetValue(nameof(OpenAITemperature), 0.3);
        OpenAIApiFormatOverride = GetValue(nameof(OpenAIApiFormatOverride), "Auto");

        // Ollama settings
        OllamaEndpoint = GetValue(nameof(OllamaEndpoint), "http://localhost:11434/v1/chat/completions");
        OllamaModel = GetValue(nameof(OllamaModel), "llama3.2");
        OllamaPromptStyle = GetValue(nameof(OllamaPromptStyle), "Standard");
        OllamaCustomPrompt = GetValue(nameof(OllamaCustomPrompt), "");

        // Local AI settings
        LocalAIProvider = GetValue(nameof(LocalAIProvider), "Auto");
        FoundryLocalEndpoint = GetValue(nameof(FoundryLocalEndpoint), "");
        FoundryLocalModel = GetValue(nameof(FoundryLocalModel), FoundryLocalService.DefaultModel);

        // OpenVINO local NLLB fallback settings
        OpenVinoDevice = GetValue(nameof(OpenVinoDevice), "Auto");

        // DeepSeek settings
        DeepSeekApiKey = GetSensitiveSetting(nameof(DeepSeekApiKey));
        DeepSeekModel = GetValue(nameof(DeepSeekModel), "deepseek-chat");

        // Groq settings
        GroqApiKey = GetSensitiveSetting(nameof(GroqApiKey));
        GroqModel = GetValue(nameof(GroqModel), "llama-3.3-70b-versatile");

        // Zhipu settings
        ZhipuApiKey = GetSensitiveSetting(nameof(ZhipuApiKey));
        ZhipuModel = GetValue(nameof(ZhipuModel), "glm-4.5-flash");

        // Kimi settings
        KimiApiKey = GetSensitiveSetting(nameof(KimiApiKey));
        KimiModel = GetValue(nameof(KimiModel), "kimi-k2-turbo-preview");

        // GitHub Models settings
        GitHubModelsToken = GetSensitiveSetting(nameof(GitHubModelsToken));
        GitHubModelsModel = GetValue(nameof(GitHubModelsModel), "gpt-4.1");

        // Custom OpenAI settings
        CustomOpenAIEndpoint = GetValue(nameof(CustomOpenAIEndpoint), "");
        CustomOpenAIApiKey = GetSensitiveSetting(nameof(CustomOpenAIApiKey));
        CustomOpenAIModel = GetValue(nameof(CustomOpenAIModel), "gpt-3.5-turbo");

        // OpenRouter settings
        OpenRouterApiKey = GetSensitiveSetting(nameof(OpenRouterApiKey));
        OpenRouterModel = GetValue(nameof(OpenRouterModel), "openrouter/free");

        // OrcaRouter settings
        OrcaRouterApiKey = GetSensitiveSetting(nameof(OrcaRouterApiKey));
        OrcaRouterModel = GetValue(nameof(OrcaRouterModel), "orcarouter/free");
        OrcaRouterSignedInViaSso = GetValue(nameof(OrcaRouterSignedInViaSso), false);

        // Gemini settings
        GeminiApiKey = GetSensitiveSetting(nameof(GeminiApiKey));
        GeminiModel = GetValue(nameof(GeminiModel), "gemini-2.5-flash");

        // Doubao settings
        DoubaoApiKey = GetSensitiveSetting(nameof(DoubaoApiKey));
        DoubaoEndpoint = GetValue(nameof(DoubaoEndpoint), "https://ark.cn-beijing.volces.com/api/v3/responses");
        DoubaoModel = GetValue(nameof(DoubaoModel), "doubao-seed-translation-250915");

        // Caiyun settings
        CaiyunApiKey = GetSensitiveSetting(nameof(CaiyunApiKey));

        // NiuTrans settings
        NiuTransApiKey = GetSensitiveSetting(nameof(NiuTransApiKey));

        // Youdao settings
        YoudaoAppKey = GetSensitiveSetting(nameof(YoudaoAppKey));
        YoudaoAppSecret = GetSensitiveSetting(nameof(YoudaoAppSecret));
        YoudaoUseOfficialApi = GetValue(nameof(YoudaoUseOfficialApi), false);

        // Volcano settings
        VolcanoAccessKeyId = GetSensitiveSetting(nameof(VolcanoAccessKeyId));
        VolcanoSecretAccessKey = GetSensitiveSetting(nameof(VolcanoSecretAccessKey));

        // Agent CLI settings
        ClaudeCodeEnabled = GetValue(nameof(ClaudeCodeEnabled), false);
        ClaudeCodeModel = GetValue(nameof(ClaudeCodeModel), ClaudeCodeService.DefaultModel);
        ClaudeCodeExecutablePath = GetValue(nameof(ClaudeCodeExecutablePath), "");
        CodexEnabled = GetValue(nameof(CodexEnabled), false);
        CodexModel = NormalizeCodexModel(GetValue(nameof(CodexModel), CodexCliService.DefaultModel));
        CodexReasoningEffort = GetValue(
            nameof(CodexReasoningEffort),
            CodexCliService.DefaultReasoningEffort);

        MinimizeToTray = GetValue(nameof(MinimizeToTray), true);
        TrayIconId = TrayIconCatalog.Normalize(GetValue(nameof(TrayIconId), TrayIconCatalog.DefaultId));
        ClipboardMonitoring = GetValue(nameof(ClipboardMonitoring), false);
        AutoTranslate = GetValue(nameof(AutoTranslate), false);
        UseLongDocWorker = ResolveWorkerIsolationSetting(nameof(UseLongDocWorker), DisableLongDocWorkerEnvironmentVariable);
        UseLocalAiWorker = ResolveWorkerIsolationSetting(nameof(UseLocalAiWorker), DisableLocalAiWorkerEnvironmentVariable);
        UseOcrWorker = ResolveWorkerIsolationSetting(nameof(UseOcrWorker), DisableOcrWorkerEnvironmentVariable);
        MouseSelectionTranslate = GetValue(nameof(MouseSelectionTranslate), true);
        MouseSelectionExcludedApps = GetStringList(nameof(MouseSelectionExcludedApps), ["code"]);
        HoverWordLookupEnabled = GetValue(nameof(HoverWordLookupEnabled), false);
        HoverWordLookupModifier = Models.HoverLookupModifierExtensions
            .Parse(GetValue(nameof(HoverWordLookupModifier), "Ctrl"))
            .ToString();
        HoverWordLookupUseOcrFallback = GetValue(nameof(HoverWordLookupUseOcrFallback), true);
        HoverWordLookupDelayMs = GetValue(nameof(HoverWordLookupDelayMs), DefaultHoverWordLookupDelayMs);
        HoverWordLookupServiceId = GetValue(nameof(HoverWordLookupServiceId), "") ?? "";
        MouseSelectionPopDelayMs = GetValue(nameof(MouseSelectionPopDelayMs), DefaultMouseSelectionPopDelayMs);
        ShellContextMenu = GetValue(nameof(ShellContextMenu), false);
        HistoryEnabled = GetValue(nameof(HistoryEnabled), false);
        HistoryRetentionDays = Math.Clamp(GetValue(nameof(HistoryRetentionDays), 30), 1, 3650);
        ShowWindowHotkey = GetValue(nameof(ShowWindowHotkey), "Ctrl+Alt+T");
        TranslateSelectionHotkey = GetValue(nameof(TranslateSelectionHotkey), "Ctrl+Alt+D");
        OcrTranslateHotkey = GetValue(nameof(OcrTranslateHotkey), "Ctrl+Alt+S");
        SilentOcrHotkey = GetValue(nameof(SilentOcrHotkey), "Ctrl+Alt+Shift+S");
        EnableShowWindowHotkey = GetValue(nameof(EnableShowWindowHotkey), true);
        EnableTranslateSelectionHotkey = GetValue(nameof(EnableTranslateSelectionHotkey), true);
        EnableShowMiniWindowHotkey = GetValue(nameof(EnableShowMiniWindowHotkey), true);
        EnableShowFixedWindowHotkey = GetValue(nameof(EnableShowFixedWindowHotkey), true);
        EnableOcrTranslateHotkey = GetValue(nameof(EnableOcrTranslateHotkey), true);
        EnableSilentOcrHotkey = GetValue(nameof(EnableSilentOcrHotkey), true);
        HideEmptyServiceResults = GetValue(nameof(HideEmptyServiceResults), true);
        OcrLanguage = GetValue(nameof(OcrLanguage), "auto");

        // Load OCR Engine settings
        OcrEngine = (OcrEngineType)GetValue(nameof(OcrEngine), (int)OcrEngineType.WindowsNative);
        OcrApiKey = GetSensitiveSetting(nameof(OcrApiKey));
        OcrEndpoint = GetValue(nameof(OcrEndpoint), OcrServiceOptions.DefaultEndpoint);
        OcrModel = GetValue(nameof(OcrModel), OcrServiceOptions.DefaultModel);
        var legacyPpOcrV6Model = PpOcrV6ModelCatalog.TryGet(OcrModel, out _)
            ? OcrModel
            : PpOcrV6ModelCatalog.SmallId;
        PpOcrV6ModelId = GetValue(nameof(PpOcrV6ModelId), legacyPpOcrV6Model);
        if (!PpOcrV6ModelCatalog.TryGet(PpOcrV6ModelId, out _))
        {
            PpOcrV6ModelId = PpOcrV6ModelCatalog.SmallId;
        }
        if (PpOcrV6ModelCatalog.TryGet(OcrModel, out _))
        {
            OcrModel = OcrServiceOptions.DefaultModel;
        }
        OcrSystemPrompt = GetValue(nameof(OcrSystemPrompt), "Extract all the text from this image perfectly. Output ONLY the extracted text, without any conversational filler, markdown formatting, or introductory words.");
        OcrEnableThinking = GetValue(nameof(OcrEnableThinking), false);
        PpOcrV6ThreadCount = Math.Clamp(
            GetValue(nameof(PpOcrV6ThreadCount), PpOcrV6ModelCatalog.DefaultThreadCount),
            PpOcrV6ModelCatalog.MinThreadCount,
            PpOcrV6ModelCatalog.MaxThreadCount);
        PpOcrV6UseGpu = GetValue(nameof(PpOcrV6UseGpu), false);
        PpOcrV6AllowFallback = GetValue(nameof(PpOcrV6AllowFallback), true);

        AlwaysOnTop = GetValue(nameof(AlwaysOnTop), false);
        TtsSpeed = GetValue(nameof(TtsSpeed), 1.0);
        SelectedTtsVoiceId = GetValue(nameof(SelectedTtsVoiceId), string.Empty);
        AutoPlayTranslation = GetValue(nameof(AutoPlayTranslation), false);
        UILanguage = GetValue(nameof(UILanguage), "");
        AppTheme = GetValue(nameof(AppTheme), "System");

        // Appearance / personalization (issue #172)
        ResultFontScale = GetValue(nameof(ResultFontScale), 1.0);
        CompactMode = GetValue(nameof(CompactMode), false);
        ShowOcrButton = GetValue(nameof(ShowOcrButton), true);
        ShowPinButton = GetValue(nameof(ShowPinButton), true);
        ShowSourcePlayButton = GetValue(nameof(ShowSourcePlayButton), true);
        ShowSwapButton = GetValue(nameof(ShowSwapButton), true);
        FixedWindowIsPinned = GetValue(nameof(FixedWindowIsPinned), true);

        LaunchAtStartup = GetValue(nameof(LaunchAtStartup), false);
        MinimizeToTrayOnStartup = GetValue(nameof(MinimizeToTrayOnStartup), false);
        EnableDpiAwareness = GetValue(nameof(EnableDpiAwareness), true);

        // Try to load WindowWidthDips first (new format), fallback to WindowWidth (legacy)
        WindowWidthDips = GetValue(nameof(WindowWidthDips), GetValue(nameof(WindowWidth), 600.0));
        WindowHeightDips = GetValue(nameof(WindowHeightDips), GetValue(nameof(WindowHeight), 700.0));

        // Mini window settings
        ShowMiniWindowHotkey = GetValue(nameof(ShowMiniWindowHotkey), "Ctrl+Alt+M");
        MiniWindowAutoClose = GetValue(nameof(MiniWindowAutoClose), true);
        MiniWindowXDips = GetValue(nameof(MiniWindowXDips), 0.0);
        MiniWindowYDips = GetValue(nameof(MiniWindowYDips), 0.0);
        // Migration: pre-flag versions only stored coords. If either is non-zero, treat
        // as previously saved so the user's position survives the upgrade.
        MiniWindowPositionSaved = GetValue(
            nameof(MiniWindowPositionSaved),
            MiniWindowXDips != 0.0 || MiniWindowYDips != 0.0);
        MiniWindowWidthDips = GetValue(nameof(MiniWindowWidthDips), 320.0);
        MiniWindowHeightDips = GetValue(nameof(MiniWindowHeightDips), 200.0);
        MiniWindowIsPinned = GetValue(nameof(MiniWindowIsPinned), false);
        var hasSavedEnabledServiceSettings = _settings.ContainsKey(nameof(MiniWindowEnabledServices))
            || _settings.ContainsKey(nameof(MainWindowEnabledServices))
            || _settings.ContainsKey(nameof(FixedWindowEnabledServices));
        var hasUserConfiguredServices = GetValue(nameof(HasUserConfiguredServices), false);
        var defaultEnabledServices = GetDefaultEnabledServicesForProfile(
            hasSavedEnabledServiceSettings,
            hasUserConfiguredServices,
            PhiSilicaAvailability.GetReadyState);
        MiniWindowEnabledServices = GetStringList(nameof(MiniWindowEnabledServices), defaultEnabledServices);
        MainWindowEnabledServices = GetStringList(nameof(MainWindowEnabledServices), defaultEnabledServices);
        ImportedMdxDictionaries = GetImportedMdxDictionaries();
        InstalledBobPlugins = GetInstalledBobPlugins();
        TextActions = GetTextActions();

        // Fixed window settings
        ShowFixedWindowHotkey = GetValue(nameof(ShowFixedWindowHotkey), "Ctrl+Alt+F");
        FixedWindowXDips = GetValue(nameof(FixedWindowXDips), 0.0);
        FixedWindowYDips = GetValue(nameof(FixedWindowYDips), 0.0);
        // See MiniWindowPositionSaved migration note.
        FixedWindowPositionSaved = GetValue(
            nameof(FixedWindowPositionSaved),
            FixedWindowXDips != 0.0 || FixedWindowYDips != 0.0);
        FixedWindowWidthDips = GetValue(nameof(FixedWindowWidthDips), 320.0);
        FixedWindowHeightDips = GetValue(nameof(FixedWindowHeightDips), 280.0);
        FixedWindowEnabledServices = GetStringList(nameof(FixedWindowEnabledServices), defaultEnabledServices);

        // EnabledQuery settings per window (which services auto-query vs. query on demand)
        MainWindowServiceEnabledQuery = GetStringBoolDictionary(nameof(MainWindowServiceEnabledQuery));
        MiniWindowServiceEnabledQuery = GetStringBoolDictionary(nameof(MiniWindowServiceEnabledQuery));
        FixedWindowServiceEnabledQuery = GetStringBoolDictionary(nameof(FixedWindowServiceEnabledQuery));
        MigrateStandaloneOpenVinoService();

        // Service test status
        ServiceTestStatus = GetStringBoolDictionary(nameof(ServiceTestStatus));
        PruneRetiredService("builtin");

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
        HasUserConfiguredServices = hasUserConfiguredServices;

        // HTTP Proxy settings
        ProxyEnabled = GetValue(nameof(ProxyEnabled), false);
        ProxyUri = GetValue(nameof(ProxyUri), "");
        ProxyBypassLocal = GetValue(nameof(ProxyBypassLocal), true);
        ProxyBypassChina = GetValue(nameof(ProxyBypassChina), false);
        GrammarIncludeExplanations = GetValue(nameof(GrammarIncludeExplanations), true);
        EnableLocalDictionarySuggestions = GetValue(nameof(EnableLocalDictionarySuggestions), false);

        // Formula detection patterns
        FormulaFontPattern = GetValue(nameof(FormulaFontPattern), "");
        FormulaCharPattern = GetValue(nameof(FormulaCharPattern), "");

        // Translation cache
        EnableTranslationCache = GetValue(nameof(EnableTranslationCache), true);

        // Custom prompt (page range is intentionally not persisted - starts empty each session)
        LongDocCustomPrompt = GetValue(nameof(LongDocCustomPrompt), "");

        // Layout detection settings
        LayoutDetectionMode = GetValue(nameof(LayoutDetectionMode), "Auto");
        VisionLayoutServiceId = GetValue(nameof(VisionLayoutServiceId), "gemini");
        OnnxModelDownloaded = GetValue(nameof(OnnxModelDownloaded), false);
        TatrModelDownloaded = GetValue(nameof(TatrModelDownloaded), false);
        EnableTatrTableStructure = GetValue(nameof(EnableTatrTableStructure), true);
        DocumentOutputMode = GetValue(nameof(DocumentOutputMode), "Monolingual");
        LongDocMaxConcurrency = GetValue(nameof(LongDocMaxConcurrency), 4);
        LongDocEnableDocumentContextPass = GetValue(nameof(LongDocEnableDocumentContextPass), true);

        CaptureLegacyAiSettingsBaseline();
        LoadAiServiceProfiles();
        MigrateSensitiveSettingsIfNeeded();
    }

    private void LoadAiServiceProfiles()
    {
        var hasProfiles = _settings.ContainsKey(AiServiceProfilesKey);
        var hasVersion = _settings.ContainsKey(AiServiceProfilesSchemaVersionKey);
        if (hasProfiles || hasVersion)
        {
            try
            {
                if (!hasProfiles || !hasVersion
                    || !TryGetStoredInt(AiServiceProfilesSchemaVersionKey, out var version)
                    || version != CurrentAiServiceProfilesSchemaVersion
                    || !_settings.TryGetValue(AiServiceProfilesKey, out var rawProfiles)
                    || rawProfiles is not JsonElement { ValueKind: JsonValueKind.Array } profileArray)
                {
                    throw new InvalidDataException("AI profile schema header or collection is invalid or unsupported.");
                }

                var profiles = JsonSerializer.Deserialize<List<AiServiceProfile>>(profileArray.GetRawText());
                if (profiles is null)
                {
                    throw new InvalidDataException("AI profile collection is null.");
                }

                if (!ValidateAiServiceProfiles(profiles, out var validationError))
                {
                    throw new InvalidDataException(validationError ?? "AI profile collection is invalid.");
                }

                AiServiceProfiles = profiles;
                AiServiceProfilesAreAuthoritative = true;
                _aiProfilesExplicitlyManaged = true;
                foreach (var profile in profiles)
                {
                    if (!AiProviderCatalog.TryGet(profile.ProviderType, out var provider))
                    {
                        throw new InvalidDataException($"Unknown AI provider type '{profile.ProviderType}'.");
                    }

                    foreach (var secret in provider.SecretFields)
                    {
                        var key = AiProfileSecretKey(profile.ProfileId, secret.Id);
                        var value = GetSensitiveSetting(key, out var decryptFailed);
                        if (decryptFailed)
                        {
                            _aiProfileSecretsCouldNotDecrypt.Add(key);
                            continue;
                        }

                        if (value is not null)
                        {
                            SetLoadedAiProfileSecret(profile.ProfileId, secret.Id, value);
                        }
                    }
                }

                return;
            }
            catch (Exception ex)
            {
                // Keep the original JSON value in _settings. SaveCore intentionally skips the
                // profile keys while this flag is false, so a normal settings save cannot erase
                // malformed or newer-version data.
                _aiProfilesCanPersist = false;
                AiServiceProfilesLoadError = ex.Message;
                AiServiceProfiles = [];
                _aiProfileSecrets.Clear();
                Debug.WriteLine($"[SettingsService] AI profiles were not loaded: {ex.Message}");
                return;
            }
        }

        AiServiceProfiles = BuildLegacyAiServiceProfiles();
        if (AiServiceProfiles.Count > 0)
        {
            try
            {
                // Legacy values remain in place for recovery. Persist a profile snapshot and
                // separately protected credentials before the first profile-aware runtime uses it.
                SaveCore(preserveUnmigratedSensitiveSettings: true);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SettingsService] AI profile migration will retry on the next save: {ex.Message}");
            }
        }
    }

    private List<AiServiceProfile> BuildLegacyAiServiceProfiles()
    {
        var profiles = new List<AiServiceProfile>();
        foreach (var provider in AiProviderCatalog.All)
        {
            if (!provider.HasLegacyConfiguration) continue;
            var configuration = new Dictionary<string, string>(StringComparer.Ordinal);
            var secrets = new Dictionary<string, string?>(StringComparer.Ordinal);
            var hasNonDefaultConfiguration = false;

            foreach (var field in provider.Fields)
            {
                var value = GetLegacyAiSettingValue(field.LegacySettingName);
                if (field.IsSecret)
                {
                    secrets[field.Id] = value;
                    hasNonDefaultConfiguration |= !string.IsNullOrWhiteSpace(value);
                }
                else
                {
                    configuration[field.Id] = value;
                    hasNonDefaultConfiguration |= !string.Equals(value, field.DefaultValue, StringComparison.OrdinalIgnoreCase);
                }
            }

            var hasEnabledToggle = provider.LegacyEnabledSettingName is not null;
            var enabled = !hasEnabledToggle
                || bool.TryParse(GetLegacyAiSettingValue(provider.LegacyEnabledSettingName!), out var legacyEnabled) && legacyEnabled;
            var hasLegacyReference = IsLegacyAiServiceReferenced(provider.LegacyServiceId);
            var explicitlyEnabled = hasEnabledToggle && enabled;
            if (!hasNonDefaultConfiguration && !explicitlyEnabled && !hasLegacyReference)
            {
                continue;
            }

            var profileId = $"legacy-{provider.Id}";
            profiles.Add(new AiServiceProfile
            {
                ProfileId = profileId,
                ServiceId = provider.LegacyServiceId,
                ProviderType = provider.Id,
                Name = provider.DisplayName,
                IsEnabled = enabled,
                Configuration = configuration
            });

            if (secrets.Values.Any(value => !string.IsNullOrEmpty(value)))
            {
                _aiProfileSecrets[profileId] = secrets;
            }
        }

        return profiles;
    }

    internal bool HasLegacyAiProviderConfiguration(string providerType)
    {
        if (!AiProviderCatalog.TryGet(providerType, out var provider)) return false;
        if (!provider.HasLegacyConfiguration) return false;
        foreach (var field in provider.Fields)
        {
            var value = GetLegacyAiSettingValue(field.LegacySettingName);
            if (field.IsSecret
                ? !string.IsNullOrWhiteSpace(value)
                : !string.Equals(value, field.DefaultValue, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        var explicitlyEnabled = provider.LegacyEnabledSettingName is not null
            && bool.TryParse(GetLegacyAiSettingValue(provider.LegacyEnabledSettingName), out var enabled)
            && enabled;
        return explicitlyEnabled || IsLegacyAiServiceReferenced(provider.LegacyServiceId);
    }

    private string GetLegacyAiSettingValue(string settingName)
    {
        if (string.IsNullOrWhiteSpace(settingName))
        {
            return string.Empty;
        }

        var property = GetType().GetProperty(settingName);
        if (property is null)
        {
            throw new InvalidOperationException($"Missing legacy AI setting '{settingName}'.");
        }

        var value = property.GetValue(this);
        return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private void SyncLegacyAiProfilesFromSettings()
    {
        // While unknown/corrupt profile data is present, avoid changing the profile collection:
        // SaveCore will preserve the raw JSON and continue saving ordinary settings.
        if (AiServiceProfilesLoadError is not null)
        {
            return;
        }

        foreach (var provider in AiProviderCatalog.All)
        {
            if (!provider.HasLegacyConfiguration) continue;
            var changedFields = provider.Fields
                .Where(field => HasLegacyAiSettingChanged(field.LegacySettingName))
                .ToArray();
            var enabledChanged = provider.LegacyEnabledSettingName is not null
                && HasLegacyAiSettingChanged(provider.LegacyEnabledSettingName);
            if (changedFields.Length == 0 && !enabledChanged)
            {
                continue;
            }

            var profile = AiServiceProfiles.FirstOrDefault(candidate =>
                candidate.ProviderType == provider.Id && candidate.ServiceId == provider.LegacyServiceId);
            if (profile is null)
            {
                var configuration = new Dictionary<string, string>(StringComparer.Ordinal);
                var secrets = new Dictionary<string, string?>(StringComparer.Ordinal);
                foreach (var field in provider.Fields)
                {
                    var value = GetLegacyAiSettingValue(field.LegacySettingName);
                    if (field.IsSecret)
                    {
                        secrets[field.Id] = value;
                    }
                    else
                    {
                        configuration[field.Id] = value;
                    }
                }

                var enabled = provider.LegacyEnabledSettingName is null
                    || bool.TryParse(GetLegacyAiSettingValue(provider.LegacyEnabledSettingName), out var legacyEnabled) && legacyEnabled;
                var profileId = $"legacy-{provider.Id}";
                profile = new AiServiceProfile
                {
                    ProfileId = profileId,
                    ServiceId = provider.LegacyServiceId,
                    ProviderType = provider.Id,
                    Name = provider.DisplayName,
                    IsEnabled = enabled,
                    Configuration = configuration
                };
                AiServiceProfiles.Add(profile);
                if (secrets.Values.Any(value => !string.IsNullOrEmpty(value)))
                {
                    _aiProfileSecrets[profileId] = secrets;
                }
            }

            var updatedConfiguration = new Dictionary<string, string>(profile.Configuration, StringComparer.Ordinal);
            var configurationChanged = false;
            foreach (var field in changedFields)
            {
                var value = GetLegacyAiSettingValue(field.LegacySettingName);
                if (field.IsSecret)
                {
                    if (!string.Equals(GetAiServiceProfileSecret(profile.ProfileId, field.Id) ?? string.Empty, value, StringComparison.Ordinal))
                    {
                        SetAiServiceProfileSecret(profile.ProfileId, field.Id, value);
                    }
                }
                else if (!updatedConfiguration.TryGetValue(field.Id, out var currentValue)
                    || !string.Equals(currentValue, value, StringComparison.Ordinal))
                {
                    updatedConfiguration[field.Id] = value;
                    configurationChanged = true;
                }
            }

            if (configurationChanged)
            {
                profile = UpdateAiServiceProfileConfiguration(profile.ProfileId, updatedConfiguration);
            }

            if (enabledChanged && provider.LegacyEnabledSettingName is not null)
            {
                var enabled = bool.TryParse(GetLegacyAiSettingValue(provider.LegacyEnabledSettingName), out var legacyEnabled)
                    && legacyEnabled;
                if (profile.IsEnabled != enabled)
                {
                    UpdateAiServiceProfileDetails(profile.ProfileId, profile.Name, enabled);
                }
            }
        }
    }

    private void CaptureLegacyAiSettingsBaseline()
    {
        _legacyAiSettingsBaseline.Clear();
        foreach (var provider in AiProviderCatalog.All)
        {
            if (!provider.HasLegacyConfiguration) continue;
            foreach (var field in provider.Fields)
            {
                _legacyAiSettingsBaseline[field.LegacySettingName] = GetLegacyAiSettingValue(field.LegacySettingName);
            }

            if (provider.LegacyEnabledSettingName is { } enabledSetting)
            {
                _legacyAiSettingsBaseline[enabledSetting] = GetLegacyAiSettingValue(enabledSetting);
            }
        }
    }

    private bool HasLegacyAiSettingChanged(string settingName)
        => !string.IsNullOrWhiteSpace(settingName)
            && (!_legacyAiSettingsBaseline.TryGetValue(settingName, out var baseline)
                || !string.Equals(GetLegacyAiSettingValue(settingName), baseline, StringComparison.Ordinal));

    private bool IsLegacyAiServiceReferenced(string serviceId)
    {
        return MiniWindowEnabledServices.Contains(serviceId, StringComparer.Ordinal)
            || MainWindowEnabledServices.Contains(serviceId, StringComparer.Ordinal)
            || FixedWindowEnabledServices.Contains(serviceId, StringComparer.Ordinal)
            || MainWindowServiceEnabledQuery.ContainsKey(serviceId)
            || MiniWindowServiceEnabledQuery.ContainsKey(serviceId)
            || FixedWindowServiceEnabledQuery.ContainsKey(serviceId)
            || ServiceTestStatus.ContainsKey(serviceId)
            || string.Equals(HoverWordLookupServiceId, serviceId, StringComparison.Ordinal)
            || string.Equals(VisionLayoutServiceId, serviceId, StringComparison.Ordinal)
            || TextActions.Any(action => string.Equals(action.ServiceId, serviceId, StringComparison.Ordinal));
    }

    private static bool ValidateAiServiceProfiles(IReadOnlyList<AiServiceProfile?> profiles, out string? error)
    {
        var profileIds = new HashSet<string>(StringComparer.Ordinal);
        var serviceIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var profile in profiles)
        {
            if (profile is null
                || string.IsNullOrWhiteSpace(profile.ProfileId)
                || string.IsNullOrWhiteSpace(profile.ServiceId)
                || string.IsNullOrWhiteSpace(profile.ProviderType)
                || string.IsNullOrWhiteSpace(profile.Name)
                || profile.ConfigurationRevision < 0
                || profile.Configuration is null
                || profile.Configuration.Any(pair => pair.Key is null || pair.Value is null))
            {
                error = "AI profile entry is missing a required identity or has invalid configuration.";
                return false;
            }

            if (!AiProviderCatalog.TryGet(profile.ProviderType, out var provider))
            {
                error = $"Unknown AI provider type '{profile.ProviderType}'.";
                return false;
            }

            if (profile.ServiceId != provider.LegacyServiceId
                && profile.ServiceId != $"ai-profile:{profile.ProfileId}")
            {
                error = $"AI profile '{profile.ProfileId}' has an invalid runtime service identity.";
                return false;
            }

            if (profile.ExtensionData is { Count: > 0 })
            {
                error = $"AI profile '{profile.ProfileId}' contains fields this app version does not understand.";
                return false;
            }

            var allowedConfigurationFields = provider.Fields
                .Where(field => !field.IsSecret)
                .Select(field => field.Id)
                .ToHashSet(StringComparer.Ordinal);
            if (profile.Configuration.Keys.Any(key => !allowedConfigurationFields.Contains(key)))
            {
                error = $"AI profile '{profile.ProfileId}' contains an unknown or secret configuration field.";
                return false;
            }

            if (!profileIds.Add(profile.ProfileId) || !serviceIds.Add(profile.ServiceId))
            {
                error = "AI profile IDs and runtime service IDs must be unique.";
                return false;
            }
        }

        error = null;
        return true;
    }

    private bool TryGetStoredInt(string key, out int value)
    {
        value = default;
        if (!_settings.TryGetValue(key, out var raw))
        {
            return false;
        }

        if (raw is JsonElement { ValueKind: JsonValueKind.Number } element)
        {
            return element.TryGetInt32(out value);
        }

        if (raw is int integer)
        {
            value = integer;
            return true;
        }

        return false;
    }

    private static string AiProfileSecretKey(string profileId, string fieldId)
        => $"{AiProfileSecretKeyPrefix}{profileId}:{fieldId}";

    private void SetLoadedAiProfileSecret(string profileId, string fieldId, string? value)
    {
        if (!_aiProfileSecrets.TryGetValue(profileId, out var secrets))
        {
            secrets = new Dictionary<string, string?>(StringComparer.Ordinal);
            _aiProfileSecrets.Add(profileId, secrets);
        }

        secrets[fieldId] = value;
    }

    /// <summary>Read an AI profile credential from the protected local settings store.</summary>
    public string? GetAiServiceProfileSecret(string profileId, string fieldId)
        => _aiProfileSecrets.TryGetValue(profileId, out var secrets)
            && secrets.TryGetValue(fieldId, out var value)
                ? value
                : null;

    public AiServiceProfile AddAiServiceProfile(
        string providerType,
        string? name = null,
        bool isAutoConnected = false,
        IReadOnlySet<string>? excludedProfileIds = null)
    {
        if (!AiProviderCatalog.TryGet(providerType, out var provider))
        {
            throw new ArgumentException("Unknown AI provider type.", nameof(providerType));
        }

        if (isAutoConnected)
        {
            var existingAutoProfile = AiServiceProfiles.FirstOrDefault(profile =>
                profile.IsAutoConnected
                && profile.ProviderType == provider.Id
                && !(excludedProfileIds?.Contains(profile.ProfileId) ?? false));
            if (existingAutoProfile is not null)
            {
                return existingAutoProfile;
            }
        }

        _aiProfilesExplicitlyManaged = true;
        AiServiceProfilesAreAuthoritative = true;
        var profileId = Guid.NewGuid().ToString("N");
        var profile = new AiServiceProfile
        {
            ProfileId = profileId,
            ServiceId = $"ai-profile:{profileId}",
            ProviderType = provider.Id,
            Name = string.IsNullOrWhiteSpace(name) ? provider.DisplayName : name.Trim(),
            IsEnabled = true,
            IsAutoConnected = isAutoConnected,
            Configuration = provider.Fields
                .Where(field => !field.IsSecret)
                .ToDictionary(field => field.Id, field => field.DefaultValue, StringComparer.Ordinal)
        };

        AiServiceProfiles.Add(profile);
        return profile;
    }

    public bool RemoveAiServiceProfile(string profileId, string? replacementServiceId = null)
    {
        var index = AiServiceProfiles.FindIndex(profile => profile.ProfileId == profileId);
        if (index < 0)
        {
            return false;
        }

        var removedProfile = AiServiceProfiles[index];
        _aiProfilesExplicitlyManaged = true;
        AiServiceProfilesAreAuthoritative = true;
        replacementServiceId = AiServiceProfiles.FirstOrDefault(candidate =>
            candidate.ServiceId == replacementServiceId
            && candidate.ProfileId != profileId
            && candidate.ProviderType == removedProfile.ProviderType)?.ServiceId;
        RedirectOrRemoveAiProfileReferences(removedProfile.ServiceId, replacementServiceId);
        AiServiceProfiles.RemoveAt(index);
        _aiProfileSecrets.Remove(profileId);
        foreach (var provider in AiProviderCatalog.All)
        {
            foreach (var secret in provider.SecretFields)
            {
                _aiProfileSecretsCouldNotDecrypt.Remove(AiProfileSecretKey(profileId, secret.Id));
            }
        }
        return true;
    }

    private void RedirectOrRemoveAiProfileReferences(string removedServiceId, string? replacementServiceId)
    {
        RedirectOrRemoveInList(MiniWindowEnabledServices, removedServiceId, replacementServiceId);
        RedirectOrRemoveInList(MainWindowEnabledServices, removedServiceId, replacementServiceId);
        RedirectOrRemoveInList(FixedWindowEnabledServices, removedServiceId, replacementServiceId);
        RedirectOrRemoveDictionaryKey(MiniWindowServiceEnabledQuery, removedServiceId, replacementServiceId);
        RedirectOrRemoveDictionaryKey(MainWindowServiceEnabledQuery, removedServiceId, replacementServiceId);
        RedirectOrRemoveDictionaryKey(FixedWindowServiceEnabledQuery, removedServiceId, replacementServiceId);
        ServiceTestStatus.Remove(removedServiceId);

        if (string.Equals(HoverWordLookupServiceId, removedServiceId, StringComparison.Ordinal))
        {
            HoverWordLookupServiceId = replacementServiceId ?? string.Empty;
        }
        if (string.Equals(VisionLayoutServiceId, removedServiceId, StringComparison.Ordinal))
        {
            VisionLayoutServiceId = replacementServiceId ?? string.Empty;
        }

        if (replacementServiceId is null)
        {
            TextActions.RemoveAll(action => string.Equals(action.ServiceId, removedServiceId, StringComparison.Ordinal));
        }
        else
        {
            for (var index = 0; index < TextActions.Count; index++)
            {
                var action = TextActions[index];
                if (string.Equals(action.ServiceId, removedServiceId, StringComparison.Ordinal))
                {
                    TextActions[index] = action with { ServiceId = replacementServiceId };
                }
            }
        }
    }

    internal void RedirectLegacyAiServiceReferences(string providerType, string replacementServiceId)
    {
        if (!AiProviderCatalog.TryGet(providerType, out var provider)) return;
        RedirectOrRemoveAiProfileReferences(provider.LegacyServiceId, replacementServiceId);
    }

    private static void RedirectOrRemoveInList(List<string> values, string removed, string? replacement)
    {
        if (replacement is null)
        {
            RemoveFromList(values, removed);
        }
        else
        {
            ReplaceInList(values, removed, replacement);
        }
    }

    private static void RedirectOrRemoveDictionaryKey(
        Dictionary<string, bool> values,
        string removed,
        string? replacement)
    {
        if (!values.Remove(removed, out var removedValue) || replacement is null)
        {
            return;
        }
        values.TryAdd(replacement, removedValue);
    }

    public AiServiceProfile UpdateAiServiceProfileConfiguration(
        string profileId,
        IReadOnlyDictionary<string, string> configuration)
    {
        var index = AiServiceProfiles.FindIndex(profile => profile.ProfileId == profileId);
        if (index < 0)
        {
            throw new ArgumentException("AI profile was not found.", nameof(profileId));
        }

        var current = AiServiceProfiles[index];
        if (!AiProviderCatalog.TryGet(current.ProviderType, out var provider))
        {
            throw new InvalidOperationException($"Unknown AI provider type '{current.ProviderType}'.");
        }

        var allowedFields = provider.Fields.Where(field => !field.IsSecret).Select(field => field.Id).ToHashSet(StringComparer.Ordinal);
        if (configuration.Keys.Any(key => !allowedFields.Contains(key)))
        {
            throw new ArgumentException("Configuration contains a field not supported by this provider.", nameof(configuration));
        }

        var updated = current.CopyWithConfiguration(
            configuration.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));
        _aiProfilesExplicitlyManaged = true;
        AiServiceProfilesAreAuthoritative = true;
        AiServiceProfiles[index] = updated;
        return updated;
    }

    public void UpdateAiServiceProfileDetails(string profileId, string name, bool isEnabled)
    {
        var index = AiServiceProfiles.FindIndex(profile => profile.ProfileId == profileId);
        if (index < 0)
        {
            throw new ArgumentException("AI profile was not found.", nameof(profileId));
        }

        var profile = AiServiceProfiles[index];
        var normalizedName = string.IsNullOrWhiteSpace(name)
            ? throw new ArgumentException("A profile name is required.", nameof(name))
            : name.Trim();
        if (!string.Equals(profile.Name, normalizedName, StringComparison.Ordinal)
            || profile.IsEnabled != isEnabled)
        {
            _aiProfilesExplicitlyManaged = true;
            AiServiceProfilesAreAuthoritative = true;
            profile.ConfigurationRevision = checked(profile.ConfigurationRevision + 1);
            profile.Name = normalizedName;
            profile.IsEnabled = isEnabled;
        }
    }

    /// <summary>Update a profile credential in memory. The caller persists it through Save().</summary>
    public void SetAiServiceProfileSecret(string profileId, string fieldId, string? value)
    {
        var profile = AiServiceProfiles.FirstOrDefault(candidate => candidate.ProfileId == profileId)
            ?? throw new ArgumentException("AI profile was not found.", nameof(profileId));
        if (!AiProviderCatalog.TryGet(profile.ProviderType, out var provider)
            || !provider.SecretFields.Any(field => field.Id == fieldId))
        {
            throw new ArgumentException("The requested field is not a secret for this provider.", nameof(fieldId));
        }

        var secretKey = AiProfileSecretKey(profileId, fieldId);
        if (_aiProfileSecretsCouldNotDecrypt.Contains(secretKey)
            || !string.Equals(GetAiServiceProfileSecret(profileId, fieldId), value, StringComparison.Ordinal))
        {
            profile.ConfigurationRevision = checked(profile.ConfigurationRevision + 1);
        }

        _aiProfileSecretsCouldNotDecrypt.Remove(secretKey);
        _aiProfilesExplicitlyManaged = true;
        AiServiceProfilesAreAuthoritative = true;
        SetLoadedAiProfileSecret(profileId, fieldId, value);
    }

    private void NormalizeDeepLModeSettings()
    {
        if (DeepLUseQualityOptimized)
        {
            DeepLUseFreeApi = false;
        }
    }

    internal static string NormalizeCodexModel(string model)
    {
        return model switch
        {
            "luna" => "gpt-5.6-luna",
            "terra" => "gpt-5.6-terra",
            "sol" => "gpt-5.6-sol",
            _ => model,
        };
    }

    public void Save()
        => SaveCore(preserveUnmigratedSensitiveSettings: false);

    private void SaveCore(bool preserveUnmigratedSensitiveSettings)
    {
        NormalizeDeepLModeSettings();
        RemoveRuntimeOnlyWorkerIsolationSettings();
        SyncLegacyAiProfilesFromSettings();

        _settings[nameof(AnkiEnabled)] = AnkiEnabled;
        _settings[nameof(AnkiQuickAdd)] = AnkiQuickAdd;
        _settings[nameof(AnkiRememberLast)] = AnkiRememberLast;
        _settings[nameof(AnkiLastDeck)] = AnkiLastDeck;
        _settings[nameof(AnkiLastModel)] = AnkiLastModel;
        _settings[nameof(AnkiEndpoint)] = AnkiEndpoint;
        _settings[nameof(AnkiDeck)] = AnkiDeck;
        _settings[nameof(AnkiModel)] = AnkiModel;
        _settings[nameof(AnkiFieldTemplates)] = AnkiFieldTemplates;
        _settings[nameof(SourceLanguage)] = SourceLanguage;

        // Save language preferences
        _settings[nameof(FirstLanguage)] = FirstLanguage;
        _settings[nameof(SecondLanguage)] = SecondLanguage;
        _settings[nameof(AutoSelectTargetLanguage)] = AutoSelectTargetLanguage;
        _settings[nameof(SelectedLanguages)] = SelectedLanguages;

        SaveSensitiveSetting(nameof(DeepLApiKey), DeepLApiKey, preserveUnmigratedSensitiveSettings);
        _settings[nameof(DeepLUseFreeApi)] = DeepLUseFreeApi;
        _settings[nameof(DeepLUseQualityOptimized)] = DeepLUseQualityOptimized;

        // OpenAI settings
        SaveSensitiveSetting(nameof(OpenAIApiKey), OpenAIApiKey, preserveUnmigratedSensitiveSettings);
        _settings[nameof(OpenAIEndpoint)] = OpenAIEndpoint;
        _settings[nameof(OpenAIModel)] = OpenAIModel;
        _settings[nameof(OpenAIApiFormatOverride)] = OpenAIApiFormatOverride;
        _settings[nameof(OpenAITemperature)] = OpenAITemperature;

        // Ollama settings
        _settings[nameof(OllamaEndpoint)] = OllamaEndpoint;
        _settings[nameof(OllamaModel)] = OllamaModel;
        _settings[nameof(OllamaPromptStyle)] = OllamaPromptStyle;
        _settings[nameof(OllamaCustomPrompt)] = OllamaCustomPrompt;

        // Local AI settings
        _settings[nameof(LocalAIProvider)] = LocalAIProvider;
        _settings[nameof(FoundryLocalEndpoint)] = FoundryLocalEndpoint;
        _settings[nameof(FoundryLocalModel)] = FoundryLocalModel;

        // OpenVINO local NLLB fallback settings
        _settings[nameof(OpenVinoDevice)] = OpenVinoDevice;

        // DeepSeek settings
        SaveSensitiveSetting(nameof(DeepSeekApiKey), DeepSeekApiKey, preserveUnmigratedSensitiveSettings);
        _settings[nameof(DeepSeekModel)] = DeepSeekModel;

        // Groq settings
        SaveSensitiveSetting(nameof(GroqApiKey), GroqApiKey, preserveUnmigratedSensitiveSettings);
        _settings[nameof(GroqModel)] = GroqModel;

        // Zhipu settings
        SaveSensitiveSetting(nameof(ZhipuApiKey), ZhipuApiKey, preserveUnmigratedSensitiveSettings);
        _settings[nameof(ZhipuModel)] = ZhipuModel;

        // Kimi settings
        SaveSensitiveSetting(nameof(KimiApiKey), KimiApiKey, preserveUnmigratedSensitiveSettings);
        _settings[nameof(KimiModel)] = KimiModel;

        // GitHub Models settings
        SaveSensitiveSetting(nameof(GitHubModelsToken), GitHubModelsToken, preserveUnmigratedSensitiveSettings);
        _settings[nameof(GitHubModelsModel)] = GitHubModelsModel;

        // Custom OpenAI settings
        _settings[nameof(CustomOpenAIEndpoint)] = CustomOpenAIEndpoint;
        SaveSensitiveSetting(nameof(CustomOpenAIApiKey), CustomOpenAIApiKey, preserveUnmigratedSensitiveSettings);
        _settings[nameof(CustomOpenAIModel)] = CustomOpenAIModel;

        // OpenRouter settings
        SaveSensitiveSetting(nameof(OpenRouterApiKey), OpenRouterApiKey, preserveUnmigratedSensitiveSettings);
        _settings[nameof(OpenRouterModel)] = OpenRouterModel;

        // OrcaRouter settings
        SaveSensitiveSetting(nameof(OrcaRouterApiKey), OrcaRouterApiKey, preserveUnmigratedSensitiveSettings);
        _settings[nameof(OrcaRouterModel)] = OrcaRouterModel;
        _settings[nameof(OrcaRouterSignedInViaSso)] = OrcaRouterSignedInViaSso;

        // Gemini settings
        SaveSensitiveSetting(nameof(GeminiApiKey), GeminiApiKey, preserveUnmigratedSensitiveSettings);
        _settings[nameof(GeminiModel)] = GeminiModel;

        // Doubao settings
        SaveSensitiveSetting(nameof(DoubaoApiKey), DoubaoApiKey, preserveUnmigratedSensitiveSettings);
        _settings[nameof(DoubaoEndpoint)] = DoubaoEndpoint;
        _settings[nameof(DoubaoModel)] = DoubaoModel;

        // Caiyun settings
        SaveSensitiveSetting(nameof(CaiyunApiKey), CaiyunApiKey, preserveUnmigratedSensitiveSettings);

        // NiuTrans settings
        SaveSensitiveSetting(nameof(NiuTransApiKey), NiuTransApiKey, preserveUnmigratedSensitiveSettings);

        // Youdao settings
        SaveSensitiveSetting(nameof(YoudaoAppKey), YoudaoAppKey, preserveUnmigratedSensitiveSettings);
        SaveSensitiveSetting(nameof(YoudaoAppSecret), YoudaoAppSecret, preserveUnmigratedSensitiveSettings);
        _settings[nameof(YoudaoUseOfficialApi)] = YoudaoUseOfficialApi;

        // Volcano settings
        SaveSensitiveSetting(nameof(VolcanoAccessKeyId), VolcanoAccessKeyId, preserveUnmigratedSensitiveSettings);
        SaveSensitiveSetting(nameof(VolcanoSecretAccessKey), VolcanoSecretAccessKey, preserveUnmigratedSensitiveSettings);

        // Agent CLI settings
        _settings[nameof(ClaudeCodeEnabled)] = ClaudeCodeEnabled;
        _settings[nameof(ClaudeCodeModel)] = ClaudeCodeModel;
        _settings[nameof(ClaudeCodeExecutablePath)] = ClaudeCodeExecutablePath;
        _settings[nameof(CodexEnabled)] = CodexEnabled;
        _settings[nameof(CodexModel)] = CodexModel;
        _settings[nameof(CodexReasoningEffort)] = CodexReasoningEffort;

        PersistAiServiceProfiles(preserveUnmigratedSensitiveSettings);

        _settings[nameof(MinimizeToTray)] = MinimizeToTray;
        _settings[nameof(TrayIconId)] = TrayIconCatalog.Normalize(TrayIconId);
        _settings[nameof(ClipboardMonitoring)] = ClipboardMonitoring;
        _settings[nameof(AutoTranslate)] = AutoTranslate;
        HistoryRetentionDays = Math.Clamp(HistoryRetentionDays, 1, 3650);
        _settings[nameof(HistoryEnabled)] = HistoryEnabled;
        _settings[nameof(HistoryRetentionDays)] = HistoryRetentionDays;
        _settings[nameof(MouseSelectionTranslate)] = MouseSelectionTranslate;
        _settings[nameof(MouseSelectionExcludedApps)] = MouseSelectionExcludedApps;
        _settings[nameof(HoverWordLookupEnabled)] = HoverWordLookupEnabled;
        _settings[nameof(HoverWordLookupModifier)] = HoverWordLookupModifier;
        _settings[nameof(HoverWordLookupUseOcrFallback)] = HoverWordLookupUseOcrFallback;
        _settings[nameof(HoverWordLookupDelayMs)] = HoverWordLookupDelayMs;
        _settings[nameof(HoverWordLookupServiceId)] = HoverWordLookupServiceId;
        _settings[nameof(MouseSelectionPopDelayMs)] = MouseSelectionPopDelayMs;
        _settings[nameof(ShellContextMenu)] = ShellContextMenu;
        _settings[nameof(ShowWindowHotkey)] = ShowWindowHotkey;
        _settings[nameof(TranslateSelectionHotkey)] = TranslateSelectionHotkey;
        _settings[nameof(OcrTranslateHotkey)] = OcrTranslateHotkey;
        _settings[nameof(SilentOcrHotkey)] = SilentOcrHotkey;
        _settings[nameof(EnableShowWindowHotkey)] = EnableShowWindowHotkey;
        _settings[nameof(EnableTranslateSelectionHotkey)] = EnableTranslateSelectionHotkey;
        _settings[nameof(EnableShowMiniWindowHotkey)] = EnableShowMiniWindowHotkey;
        _settings[nameof(EnableShowFixedWindowHotkey)] = EnableShowFixedWindowHotkey;
        _settings[nameof(EnableOcrTranslateHotkey)] = EnableOcrTranslateHotkey;
        _settings[nameof(EnableSilentOcrHotkey)] = EnableSilentOcrHotkey;
        _settings[nameof(HideEmptyServiceResults)] = HideEmptyServiceResults;
        _settings[nameof(OcrLanguage)] = OcrLanguage;

        // Save OCR Engine settings
        _settings[nameof(OcrEngine)] = (int)OcrEngine;
        SaveSensitiveSetting(nameof(OcrApiKey), OcrApiKey, preserveUnmigratedSensitiveSettings);
        _settings[nameof(OcrEndpoint)] = OcrEndpoint;
        _settings[nameof(OcrModel)] = OcrModel;
        _settings[nameof(PpOcrV6ModelId)] = PpOcrV6ModelCatalog.TryGet(PpOcrV6ModelId, out _)
            ? PpOcrV6ModelId
            : PpOcrV6ModelCatalog.SmallId;
        _settings[nameof(OcrSystemPrompt)] = OcrSystemPrompt;
        _settings[nameof(OcrEnableThinking)] = OcrEnableThinking;
        _settings[nameof(PpOcrV6ThreadCount)] = Math.Clamp(
            PpOcrV6ThreadCount,
            PpOcrV6ModelCatalog.MinThreadCount,
            PpOcrV6ModelCatalog.MaxThreadCount);
        _settings[nameof(PpOcrV6UseGpu)] = PpOcrV6UseGpu;
        _settings[nameof(PpOcrV6AllowFallback)] = PpOcrV6AllowFallback;

        _settings[nameof(AlwaysOnTop)] = AlwaysOnTop;
        _settings[nameof(TtsSpeed)] = TtsSpeed;
        _settings[nameof(SelectedTtsVoiceId)] = SelectedTtsVoiceId;
        _settings[nameof(AutoPlayTranslation)] = AutoPlayTranslation;
        _settings[nameof(UILanguage)] = UILanguage;
        _settings[nameof(AppTheme)] = AppTheme;

        // Appearance / personalization (issue #172)
        _settings[nameof(ResultFontScale)] = ResultFontScale;
        _settings[nameof(CompactMode)] = CompactMode;
        _settings[nameof(ShowOcrButton)] = ShowOcrButton;
        _settings[nameof(ShowPinButton)] = ShowPinButton;
        _settings[nameof(ShowSourcePlayButton)] = ShowSourcePlayButton;
        _settings[nameof(ShowSwapButton)] = ShowSwapButton;
        _settings[nameof(FixedWindowIsPinned)] = FixedWindowIsPinned;

        _settings[nameof(LaunchAtStartup)] = LaunchAtStartup;
        _settings[nameof(MinimizeToTrayOnStartup)] = MinimizeToTrayOnStartup;
        _settings[nameof(EnableDpiAwareness)] = EnableDpiAwareness;
        _settings[nameof(WindowWidthDips)] = WindowWidthDips;
        _settings[nameof(WindowHeightDips)] = WindowHeightDips;

        // Mini window settings
        _settings[nameof(ShowMiniWindowHotkey)] = ShowMiniWindowHotkey;
        _settings[nameof(MiniWindowAutoClose)] = MiniWindowAutoClose;
        _settings[nameof(MiniWindowXDips)] = MiniWindowXDips;
        _settings[nameof(MiniWindowYDips)] = MiniWindowYDips;
        _settings[nameof(MiniWindowPositionSaved)] = MiniWindowPositionSaved;
        _settings[nameof(MiniWindowWidthDips)] = MiniWindowWidthDips;
        _settings[nameof(MiniWindowHeightDips)] = MiniWindowHeightDips;
        _settings[nameof(MiniWindowIsPinned)] = MiniWindowIsPinned;
        _settings[nameof(MiniWindowEnabledServices)] = MiniWindowEnabledServices;
        _settings[nameof(MainWindowEnabledServices)] = MainWindowEnabledServices;
        _settings[nameof(ImportedMdxDictionaries)] = ImportedMdxDictionaries;
        _settings[nameof(InstalledBobPlugins)] = InstalledBobPlugins;
        _settings[nameof(TextActions)] = TextActions;

        // Fixed window settings
        _settings[nameof(ShowFixedWindowHotkey)] = ShowFixedWindowHotkey;
        _settings[nameof(FixedWindowXDips)] = FixedWindowXDips;
        _settings[nameof(FixedWindowYDips)] = FixedWindowYDips;
        _settings[nameof(FixedWindowPositionSaved)] = FixedWindowPositionSaved;
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
        _settings[nameof(ProxyBypassChina)] = ProxyBypassChina;
        _settings[nameof(GrammarIncludeExplanations)] = GrammarIncludeExplanations;
        _settings[nameof(EnableLocalDictionarySuggestions)] = EnableLocalDictionarySuggestions;

        // Formula detection patterns
        _settings[nameof(FormulaFontPattern)] = FormulaFontPattern;
        _settings[nameof(FormulaCharPattern)] = FormulaCharPattern;

        // Translation cache
        _settings[nameof(EnableTranslationCache)] = EnableTranslationCache;

        // Custom prompt (page range is intentionally not persisted - starts empty each session)
        _settings[nameof(LongDocCustomPrompt)] = LongDocCustomPrompt;

        // Layout detection settings
        _settings[nameof(LayoutDetectionMode)] = LayoutDetectionMode;
        _settings[nameof(VisionLayoutServiceId)] = VisionLayoutServiceId;
        _settings[nameof(OnnxModelDownloaded)] = OnnxModelDownloaded;
        _settings[nameof(TatrModelDownloaded)] = TatrModelDownloaded;
        _settings[nameof(EnableTatrTableStructure)] = EnableTatrTableStructure;
        _settings[nameof(DocumentOutputMode)] = DocumentOutputMode;
        _settings[nameof(LongDocMaxConcurrency)] = LongDocMaxConcurrency;
        _settings[nameof(LongDocEnableDocumentContextPass)] = LongDocEnableDocumentContextPass;

        try
        {
            var json = JsonSerializer.Serialize(_settings, SettingsJsonOptions);
            // Compare with the file, not a snapshot of _settings. SetBobPluginSecureOption
            // and other callers update the dictionary before Save, so a snapshot taken
            // at the start of this method already contains the new value and would
            // skip writing a real change.
            if (SettingsFileHasSameContent(json))
            {
                _sensitiveSettingsPendingMigration.Clear();
                CaptureLegacyAiSettingsBaseline();
                System.Diagnostics.Debug.WriteLine($"[SettingsService] Settings unchanged; save skipped for: {_settingsFilePath}");
                return;
            }

            File.WriteAllText(_settingsFilePath, json);
            _sensitiveSettingsPendingMigration.Clear();
            CaptureLegacyAiSettingsBaseline();

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
    /// True when <paramref name="json"/> already matches the settings file.
    /// A read failure is treated as a mismatch so Save still attempts to write.
    /// </summary>
    private bool SettingsFileHasSameContent(string json)
    {
        try
        {
            return File.Exists(_settingsFilePath)
                && string.Equals(File.ReadAllText(_settingsFilePath), json, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[SettingsService] Could not read settings before save: {ex.Message}");
            return false;
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
        "groq", "github", "openrouter", "orcarouter",
#if ENABLE_LINGUEE_SERVICE
        "linguee",
#endif
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
    /// Default enabled services for a fresh profile. Phi Silica is enabled only
    /// when the OS reports that the on-device language model is supported on
    /// this PC; <see cref="WindowsAIReadyState.NotReady"/> still counts because
    /// the model can be prepared on first use after user confirmation.
    /// </summary>
    internal static List<string> GetDefaultEnabledServices(WindowsAIReadyState phiSilicaReadyState)
    {
        var services = new List<string> { GoogleServiceId };
        if (IsPhiSilicaSupportedForDefaultEnable(phiSilicaReadyState))
        {
            services.Add(LocalAITranslationService.ServiceIdValue);
        }

        return services;
    }

    internal static List<string> GetDefaultEnabledServicesForProfile(
        bool hasSavedEnabledServiceSettings,
        WindowsAIReadyState phiSilicaReadyState)
    {
        return GetDefaultEnabledServicesForProfile(
            hasSavedEnabledServiceSettings,
            hasUserConfiguredServices: false,
            () => phiSilicaReadyState);
    }

    internal static List<string> GetDefaultEnabledServicesForProfile(
        bool hasSavedEnabledServiceSettings,
        bool hasUserConfiguredServices,
        Func<WindowsAIReadyState> getPhiSilicaReadyState)
    {
        return hasSavedEnabledServiceSettings || hasUserConfiguredServices
            ? [GoogleServiceId]
            : GetDefaultEnabledServices(getPhiSilicaReadyState());
    }

    internal static bool IsPhiSilicaSupportedForDefaultEnable(WindowsAIReadyState phiSilicaReadyState)
    {
        return phiSilicaReadyState is WindowsAIReadyState.Ready or WindowsAIReadyState.NotReady;
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
        var hasNewValue = ContainsInList(list, newValue);
        for (var i = list.Count - 1; i >= 0; i--)
        {
            if (string.Equals(list[i], oldValue, StringComparison.OrdinalIgnoreCase))
            {
                if (hasNewValue)
                {
                    list.RemoveAt(i);
                }
                else
                {
                    list[i] = newValue;
                    hasNewValue = true;
                }
            }
        }
    }

    internal void DisablePhiSilicaService()
    {
        DisableServiceEverywhere(LocalAITranslationService.ServiceIdValue);
    }

    internal void DisableServiceEverywhere(string serviceId)
    {
        RemoveFromList(MiniWindowEnabledServices, serviceId);
        RemoveFromList(MainWindowEnabledServices, serviceId);
        RemoveFromList(FixedWindowEnabledServices, serviceId);

        RemoveDictionaryKey(MiniWindowServiceEnabledQuery, serviceId);
        RemoveDictionaryKey(MainWindowServiceEnabledQuery, serviceId);
        RemoveDictionaryKey(FixedWindowServiceEnabledQuery, serviceId);

        HasUserConfiguredServices = true;
        Save();
    }

    private static void RemoveFromList(List<string> list, string value)
    {
        for (var i = list.Count - 1; i >= 0; i--)
        {
            if (string.Equals(list[i], value, StringComparison.OrdinalIgnoreCase))
            {
                list.RemoveAt(i);
            }
        }
    }

    /// <summary>
    /// Strips a retired service id from every enabled-service list, auto-query dictionary,
    /// and test-status entry, so a stale id from an old settings.json (e.g. "builtin" after
    /// its removal) can never reach the live <see cref="TranslationManager"/> registry lookup.
    /// Idempotent and silent — does not force a save; the next natural Save() call persists it.
    /// </summary>
    private void PruneRetiredService(string serviceId)
    {
        RemoveFromList(MiniWindowEnabledServices, serviceId);
        RemoveFromList(MainWindowEnabledServices, serviceId);
        RemoveFromList(FixedWindowEnabledServices, serviceId);

        RemoveDictionaryKey(MiniWindowServiceEnabledQuery, serviceId);
        RemoveDictionaryKey(MainWindowServiceEnabledQuery, serviceId);
        RemoveDictionaryKey(FixedWindowServiceEnabledQuery, serviceId);
        RemoveDictionaryKey(ServiceTestStatus, serviceId);
    }

    private void MigrateStandaloneOpenVinoService()
    {
        var hadOpenVino = ContainsInList(MiniWindowEnabledServices, LocalAITranslationService.LegacyOpenVinoServiceId)
            || ContainsInList(MainWindowEnabledServices, LocalAITranslationService.LegacyOpenVinoServiceId)
            || ContainsInList(FixedWindowEnabledServices, LocalAITranslationService.LegacyOpenVinoServiceId);
        var hadPhiSilicaService = ContainsInList(MiniWindowEnabledServices, LocalAITranslationService.ServiceIdValue)
            || ContainsInList(MainWindowEnabledServices, LocalAITranslationService.ServiceIdValue)
            || ContainsInList(FixedWindowEnabledServices, LocalAITranslationService.ServiceIdValue);

        if (hadOpenVino && !hadPhiSilicaService && !_settings.ContainsKey(nameof(LocalAIProvider)))
        {
            LocalAIProvider = "OpenVINO";
        }

        ReplaceInList(
            MiniWindowEnabledServices,
            LocalAITranslationService.LegacyOpenVinoServiceId,
            LocalAITranslationService.ServiceIdValue);
        ReplaceInList(
            MainWindowEnabledServices,
            LocalAITranslationService.LegacyOpenVinoServiceId,
            LocalAITranslationService.ServiceIdValue);
        ReplaceInList(
            FixedWindowEnabledServices,
            LocalAITranslationService.LegacyOpenVinoServiceId,
            LocalAITranslationService.ServiceIdValue);

        MoveDictionaryKey(
            MiniWindowServiceEnabledQuery,
            LocalAITranslationService.LegacyOpenVinoServiceId,
            LocalAITranslationService.ServiceIdValue);
        MoveDictionaryKey(
            MainWindowServiceEnabledQuery,
            LocalAITranslationService.LegacyOpenVinoServiceId,
            LocalAITranslationService.ServiceIdValue);
        MoveDictionaryKey(
            FixedWindowServiceEnabledQuery,
            LocalAITranslationService.LegacyOpenVinoServiceId,
            LocalAITranslationService.ServiceIdValue);
    }

    private static void MoveDictionaryKey(Dictionary<string, bool> dictionary, string oldKey, string newKey)
    {
        if (!TryGetDictionaryEntry(dictionary, oldKey, out var existingKey, out var value))
        {
            return;
        }

        dictionary.Remove(existingKey);
        if (!ContainsDictionaryKey(dictionary, newKey))
        {
            dictionary[newKey] = value;
        }
    }

    private static void RemoveDictionaryKey(Dictionary<string, bool> dictionary, string key)
    {
        if (TryGetDictionaryEntry(dictionary, key, out var existingKey, out _))
        {
            dictionary.Remove(existingKey);
        }
    }

    private static bool ContainsInList(IEnumerable<string> list, string value)
    {
        return list.Any(item => string.Equals(item, value, StringComparison.OrdinalIgnoreCase));
    }

    private static bool ContainsDictionaryKey(Dictionary<string, bool> dictionary, string key)
    {
        return TryGetDictionaryEntry(dictionary, key, out _, out _);
    }

    private static bool TryGetDictionaryEntry(
        Dictionary<string, bool> dictionary,
        string key,
        out string existingKey,
        out bool value)
    {
        if (dictionary.TryGetValue(key, out value))
        {
            existingKey = key;
            return true;
        }

        foreach (var candidate in dictionary.Keys)
        {
            if (string.Equals(candidate, key, StringComparison.OrdinalIgnoreCase))
            {
                existingKey = candidate;
                value = dictionary[candidate];
                return true;
            }
        }

        existingKey = string.Empty;
        value = false;
        return false;
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

    private bool ResolveWorkerIsolationSetting(string key, string disableEnvironmentVariable)
    {
        if (IsEnvironmentFlagEnabled(disableEnvironmentVariable))
        {
            Debug.WriteLine($"[SettingsService] Worker isolation disabled by {disableEnvironmentVariable}=1 for {key}.");
            return false;
        }

        // These flags are internal safety switches. Older/debug settings files may
        // contain false; do not let that silently route memory-heavy work back into
        // the UI process. Use the environment variable above for explicit opt-out.
        if (!GetValue(key, true))
        {
            Debug.WriteLine(
                $"[SettingsService] Ignoring persisted {key}=false; worker isolation remains enabled. " +
                $"Set {disableEnvironmentVariable}=1 to disable it for diagnostics.");
        }

        return true;
    }

    private void RemoveRuntimeOnlyWorkerIsolationSettings()
    {
        _settings.Remove(nameof(UseLongDocWorker));
        _settings.Remove(nameof(UseLocalAiWorker));
        _settings.Remove(nameof(UseOcrWorker));
    }

    private static bool IsEnvironmentFlagEnabled(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.Equals(value, "1", StringComparison.Ordinal)
            || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "on", StringComparison.OrdinalIgnoreCase);
    }

    private string? GetSensitiveSetting(string key)
        => GetSensitiveSetting(key, out _);

    private string? GetSensitiveSetting(string key, out bool decryptFailed)
    {
        var storedValue = GetValue<string?>(key, null);
        var plaintext = LocalCredentialProtector.UnprotectOrReturnPlaintext(
            storedValue,
            out var needsMigration,
            out decryptFailed);
        if (needsMigration)
        {
            _sensitiveSettingsPendingMigration.Add(key);
        }

        if (decryptFailed)
        {
            Debug.WriteLine($"[SettingsService] Failed to decrypt protected setting: {key}");
        }

        return plaintext;
    }

    private static string ProtectSensitiveSetting(string? plaintext)
    {
        return string.IsNullOrEmpty(plaintext)
            ? string.Empty
            : LocalCredentialProtector.Protect(plaintext);
    }

    private void SaveSensitiveSetting(
        string key,
        string? plaintext,
        bool preserveUnmigratedSensitiveSettings)
    {
        if (preserveUnmigratedSensitiveSettings
            && !_sensitiveSettingsPendingMigration.Contains(key)
            && TryGetStoredSensitiveSetting(key, out var storedValue)
            && LocalCredentialProtector.IsProtected(storedValue))
        {
            _settings[key] = storedValue;
            return;
        }

        if (!_sensitiveSettingsPendingMigration.Contains(key)
            && TryGetStoredSensitiveSetting(key, out storedValue)
            && LocalCredentialProtector.IsProtected(storedValue)
            && LocalCredentialProtector.TryUnprotect(storedValue, out var storedPlaintext)
            && string.Equals(storedPlaintext, plaintext ?? string.Empty, StringComparison.Ordinal))
        {
            _settings[key] = storedValue;
            return;
        }

        _settings[key] = ProtectSensitiveSetting(plaintext);
    }

    private void PersistAiServiceProfiles(bool preserveUnmigratedSensitiveSettings)
    {
        if (!_aiProfilesCanPersist)
        {
            return;
        }

        // Do not turn an untouched legacy installation with no AI profiles into
        // an explicitly empty allowlist when an unrelated setting is saved.
        if (AiServiceProfiles.Count == 0 && !_aiProfilesExplicitlyManaged)
        {
            return;
        }

        if (!ValidateAiServiceProfiles(AiServiceProfiles, out var validationError))
        {
            _aiProfilesCanPersist = false;
            AiServiceProfilesLoadError = validationError;
            return;
        }

        _settings[AiServiceProfilesSchemaVersionKey] = CurrentAiServiceProfilesSchemaVersion;
        _settings[AiServiceProfilesKey] = AiServiceProfiles;
        AiServiceProfilesAreAuthoritative = true;

        var activeSecretKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var profile in AiServiceProfiles)
        {
            if (!AiProviderCatalog.TryGet(profile.ProviderType, out var provider))
            {
                throw new InvalidOperationException($"Unknown AI provider type '{profile.ProviderType}'.");
            }

            foreach (var secret in provider.SecretFields)
            {
                var key = AiProfileSecretKey(profile.ProfileId, secret.Id);
                if (_aiProfileSecretsCouldNotDecrypt.Contains(key)
                    && (!_aiProfileSecrets.TryGetValue(profile.ProfileId, out var loadedSecrets)
                        || !loadedSecrets.ContainsKey(secret.Id)))
                {
                    // Preserve the opaque protected value byte-for-byte until the user explicitly
                    // supplies or clears this credential through SetAiServiceProfileSecret.
                    activeSecretKeys.Add(key);
                    continue;
                }

                if (!_aiProfileSecrets.TryGetValue(profile.ProfileId, out var secrets)
                    || !secrets.TryGetValue(secret.Id, out var plaintext))
                {
                    if (_settings.ContainsKey(key))
                    {
                        SaveSensitiveSetting(key, null, preserveUnmigratedSensitiveSettings);
                        activeSecretKeys.Add(key);
                    }

                    continue;
                }

                SaveSensitiveSetting(key, plaintext, preserveUnmigratedSensitiveSettings);
                activeSecretKeys.Add(key);
            }
        }

        foreach (var staleKey in _settings.Keys
            .Where(key => key.StartsWith(AiProfileSecretKeyPrefix, StringComparison.Ordinal)
                && !activeSecretKeys.Contains(key))
            .ToList())
        {
            _settings.Remove(staleKey);
            _sensitiveSettingsPendingMigration.Remove(staleKey);
            _aiProfileSecretsCouldNotDecrypt.Remove(staleKey);
        }
    }

    private bool TryGetStoredSensitiveSetting(string key, out string storedValue)
    {
        storedValue = string.Empty;
        if (!_settings.TryGetValue(key, out var value) || value is null)
        {
            return false;
        }

        if (value is string stringValue)
        {
            storedValue = stringValue;
            return true;
        }

        if (value is JsonElement { ValueKind: JsonValueKind.String } jsonElement)
        {
            storedValue = jsonElement.GetString() ?? string.Empty;
            return true;
        }

        return false;
    }

    private void MigrateSensitiveSettingsIfNeeded()
    {
        if (_sensitiveSettingsPendingMigration.Count == 0)
        {
            return;
        }

        try
        {
            var migratedCount = _sensitiveSettingsPendingMigration.Count;
            SaveCore(preserveUnmigratedSensitiveSettings: true);
            Debug.WriteLine(
                $"[SettingsService] Migrated {migratedCount} plaintext or legacy sensitive settings to protected storage.");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SettingsService] Failed to migrate sensitive settings: {ex.Message}");
        }
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
            catch (Exception ex) { Debug.WriteLine($"[SettingsService] Failed to deserialize service test status: {ex.Message}"); }
        }
        return new Dictionary<string, bool>();
    }

    private List<TextAction> GetTextActions()
    {
        if (_settings.TryGetValue(nameof(TextActions), out var value) && value != null)
        {
            try
            {
                if (value is JsonElement element && element.ValueKind == JsonValueKind.Array)
                {
                    return TextActionCatalog.Normalize(JsonSerializer.Deserialize<List<TextAction>>(element.GetRawText()));
                }

                if (value is List<TextAction> typed)
                {
                    return TextActionCatalog.Normalize(typed);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SettingsService] Failed to deserialize text actions: {ex.Message}");
            }
        }

        return TextActionCatalog.Normalize(null);
    }

    /// <summary>Insert or replace a text action by id (does not save).</summary>
    public void AddOrReplaceTextAction(TextAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var index = TextActions.FindIndex(a => string.Equals(a.Id, action.Id, StringComparison.Ordinal));
        if (index >= 0)
        {
            TextActions[index] = action;
        }
        else
        {
            TextActions.Add(action);
        }
    }

    /// <summary>
    /// Remove actions generated by a subsystem (for example every action a removed plugin created).
    /// Returns the number of removed actions (does not save).
    /// </summary>
    public int RemoveTextActionsBySource(string source, string? serviceId = null)
    {
        return TextActions.RemoveAll(a =>
            string.Equals(a.Source, source, StringComparison.Ordinal)
            && (serviceId is null || string.Equals(a.ServiceId, serviceId, StringComparison.Ordinal)));
    }

    private List<ImportedMdxDictionary> GetImportedMdxDictionaries()
    {
        if (_settings.TryGetValue(nameof(ImportedMdxDictionaries), out var value) && value != null)
        {
            try
            {
                if (value is JsonElement jsonElement && jsonElement.ValueKind == JsonValueKind.Array)
                {
                    var list = new List<ImportedMdxDictionary>();
                    foreach (var item in jsonElement.EnumerateArray())
                    {
                        var serviceId = item.TryGetProperty(nameof(ImportedMdxDictionary.ServiceId), out var sid)
                            ? sid.GetString() ?? string.Empty
                            : string.Empty;
                        var displayName = item.TryGetProperty(nameof(ImportedMdxDictionary.DisplayName), out var dn)
                            ? dn.GetString() ?? string.Empty
                            : string.Empty;
                        var filePath = item.TryGetProperty(nameof(ImportedMdxDictionary.FilePath), out var fp)
                            ? fp.GetString() ?? string.Empty
                            : string.Empty;
                        var isEncrypted = item.TryGetProperty(nameof(ImportedMdxDictionary.IsEncrypted), out var enc)
                            && enc.ValueKind == JsonValueKind.True;
                        var regcode = item.TryGetProperty(nameof(ImportedMdxDictionary.Regcode), out var rc)
                            ? rc.GetString()
                            : null;
                        var email = item.TryGetProperty(nameof(ImportedMdxDictionary.Email), out var em)
                            ? em.GetString()
                            : null;
                        var mddFilePaths = new List<string>();
                        if (item.TryGetProperty(nameof(ImportedMdxDictionary.MddFilePaths), out var mddArr)
                            && mddArr.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var mddItem in mddArr.EnumerateArray())
                            {
                                var path = mddItem.GetString();
                                if (!string.IsNullOrWhiteSpace(path))
                                    mddFilePaths.Add(path);
                            }
                        }

                        if (!string.IsNullOrWhiteSpace(serviceId) && !string.IsNullOrWhiteSpace(filePath))
                        {
                            list.Add(new ImportedMdxDictionary
                            {
                                ServiceId = serviceId,
                                DisplayName = string.IsNullOrWhiteSpace(displayName) ? serviceId : displayName,
                                FilePath = filePath,
                                IsEncrypted = isEncrypted,
                                Regcode = regcode,
                                Email = email,
                                MddFilePaths = mddFilePaths
                            });
                        }
                    }

                    return list;
                }

                if (value is List<ImportedMdxDictionary> typed)
                {
                    return typed;
                }
            }
            catch (Exception ex) { Debug.WriteLine($"[SettingsService] Failed to deserialize MDX dictionaries: {ex.Message}"); }
        }

        return [];
    }

    private List<InstalledBobPlugin> GetInstalledBobPlugins()
    {
        if (_settings.TryGetValue(nameof(InstalledBobPlugins), out var value) && value != null)
        {
            try
            {
                if (value is JsonElement element && element.ValueKind == JsonValueKind.Array)
                {
                    var list = JsonSerializer.Deserialize<List<InstalledBobPlugin>>(element.GetRawText()) ?? [];
                    return list
                        .Where(p => !string.IsNullOrWhiteSpace(p.ServiceId) && !string.IsNullOrWhiteSpace(p.InstallDirectory))
                        .ToList();
                }

                if (value is List<InstalledBobPlugin> typed)
                {
                    return typed;
                }
            }
            catch (Exception ex) { Debug.WriteLine($"[SettingsService] Failed to deserialize Bob plugins: {ex.Message}"); }
        }

        return [];
    }

    /// <summary>Settings key holding one plugin option's protected value.</summary>
    internal static string BobSecureOptionKey(string serviceId, string optionId) => $"BobOption:{serviceId}:{optionId}";

    /// <summary>Read a plugin option that is held by the credential store.</summary>
    public string? GetBobPluginSecureOption(string serviceId, string optionId)
        => GetSensitiveSetting(BobSecureOptionKey(serviceId, optionId));

    /// <summary>
    /// Store a plugin option in the credential store. An empty value removes it. Does not save.
    /// </summary>
    public void SetBobPluginSecureOption(string serviceId, string optionId, string? value)
    {
        var key = BobSecureOptionKey(serviceId, optionId);
        if (string.IsNullOrEmpty(value))
        {
            _settings.Remove(key);
            _sensitiveSettingsPendingMigration.Remove(key);
            return;
        }

        // Keeps the stored ciphertext when the value is unchanged. Protect() salts every
        // call, so re-protecting would make settings.json differ on every Settings save
        // and defeat Save()'s unchanged-content skip.
        SaveSensitiveSetting(key, value, preserveUnmigratedSensitiveSettings: false);
        _sensitiveSettingsPendingMigration.Remove(key);
    }

    /// <summary>Forget every stored option of a plugin instance (does not save).</summary>
    public void RemoveBobPluginSecureOptions(string serviceId)
    {
        var prefix = $"BobOption:{serviceId}:";
        foreach (var key in _settings.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
        {
            _settings.Remove(key);
            _sensitiveSettingsPendingMigration.Remove(key);
        }
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
