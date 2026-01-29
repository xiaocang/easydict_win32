using Microsoft.Windows.ApplicationModel.Resources;
using Windows.Globalization;

namespace Easydict.WinUI.Services;

/// <summary>
/// Provides localization services using WindowsAppSDK ResourceManager API.
/// Supports runtime language switching without application restart.
/// Supported languages: English, Chinese (Simplified/Traditional), Japanese, Korean, French, German.
/// </summary>
public sealed class LocalizationService
{
    private static readonly Lazy<LocalizationService> _instance = new(() => new LocalizationService());
    public static LocalizationService Instance => _instance.Value;

    private readonly ResourceManager _resourceManager;
    private ResourceContext _resourceContext;
    private ResourceMap _resourceMap;
    private string _currentLanguage;

    /// <summary>
    /// Supported UI languages.
    /// </summary>
    public static readonly string[] SupportedLanguages =
        { "en-US", "zh-CN", "zh-TW", "ja-JP", "ko-KR", "fr-FR", "de-DE" };

    private LocalizationService()
    {
        System.Diagnostics.Debug.WriteLine("[LocalizationService] Initializing...");

        // Detect if running as packaged (MSIX) or unpackaged
        var isPackaged = IsRunningAsPackaged();
        System.Diagnostics.Debug.WriteLine($"[LocalizationService] Running as packaged: {isPackaged}");

        // Create ResourceManager (new WindowsAppSDK API)
        // This works in both packaged and unpackaged modes if resources.pri exists
        _resourceManager = new ResourceManager();
        _resourceMap = _resourceManager.MainResourceMap;

        // Log resource map info for debugging
        try
        {
            var subtreeCount = _resourceMap.ResourceCount;
            System.Diagnostics.Debug.WriteLine($"[LocalizationService] ResourceMap has {subtreeCount} resources");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[LocalizationService] Warning: Could not enumerate resources: {ex.Message}");
            System.Diagnostics.Debug.WriteLine("[LocalizationService] This may indicate resources.pri is missing or corrupted.");
        }

        // Load current language from settings or system default
        var settings = SettingsService.Instance;
        _currentLanguage = settings.UILanguage;

        if (string.IsNullOrEmpty(_currentLanguage) || !IsSupported(_currentLanguage))
        {
            _currentLanguage = GetSystemLanguage();
            System.Diagnostics.Debug.WriteLine($"[LocalizationService] Using system language: {_currentLanguage}");
        }
        else
        {
            System.Diagnostics.Debug.WriteLine($"[LocalizationService] Loaded language from settings: {_currentLanguage}");
        }

        // Create ResourceContext with the selected language
        _resourceContext = CreateResourceContextForLanguage(_currentLanguage);

        System.Diagnostics.Debug.WriteLine($"[LocalizationService] Initialized with language: {_currentLanguage}");
    }

    /// <summary>
    /// Gets the current UI language code.
    /// </summary>
    public string CurrentLanguage => _currentLanguage;

    /// <summary>
    /// Gets a localized string by key.
    /// </summary>
    /// <param name="key">The resource key.</param>
    /// <returns>The localized string, or the key if not found.</returns>
    public string GetString(string key)
    {
        try
        {
            // Use ResourceMap with custom ResourceContext to get language-specific value
            var resourceCandidate = _resourceMap.GetValue($"Resources/{key}", _resourceContext);
            var value = resourceCandidate?.ValueAsString;

            if (string.IsNullOrEmpty(value))
            {
                System.Diagnostics.Debug.WriteLine($"[LocalizationService] Resource not found: {key}");
                return key;
            }

            return value;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[LocalizationService] Error loading resource '{key}': {ex.Message}");
            return key;
        }
    }

    /// <summary>
    /// Gets a localized string with format arguments.
    /// </summary>
    /// <param name="key">The resource key.</param>
    /// <param name="args">Format arguments.</param>
    /// <returns>The formatted localized string.</returns>
    public string GetString(string key, params object[] args)
    {
        var format = GetString(key);
        try
        {
            return string.Format(format, args);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[LocalizationService] Error formatting string '{key}': {ex.Message}");
            return format;
        }
    }

    /// <summary>
    /// Sets the UI language and updates the ResourceContext.
    /// Language change takes effect immediately without application restart.
    /// </summary>
    /// <param name="languageCode">Language code (e.g., "en-US", "zh-CN").</param>
    public void SetLanguage(string languageCode)
    {
        if (string.IsNullOrEmpty(languageCode) || !IsSupported(languageCode))
        {
            System.Diagnostics.Debug.WriteLine($"[LocalizationService] Invalid language code: {languageCode}");
            languageCode = GetSystemLanguage();
        }

        if (_currentLanguage == languageCode)
        {
            System.Diagnostics.Debug.WriteLine($"[LocalizationService] Language already set to: {languageCode}");
            return;
        }

        System.Diagnostics.Debug.WriteLine($"[LocalizationService] Changing language from {_currentLanguage} to {languageCode}");

        _currentLanguage = languageCode;

        // Create new ResourceContext with the new language
        _resourceContext = CreateResourceContextForLanguage(languageCode);

        // Save to settings
        var settings = SettingsService.Instance;
        settings.UILanguage = languageCode;
        settings.Save();

        System.Diagnostics.Debug.WriteLine($"[LocalizationService] Language changed successfully to: {languageCode}");
        System.Diagnostics.Debug.WriteLine($"[LocalizationService] NOTE: UI must be manually refreshed to show new language");
    }

    /// <summary>
    /// Creates a ResourceContext configured for the specified language.
    /// </summary>
    private ResourceContext CreateResourceContextForLanguage(string languageCode)
    {
        var context = _resourceManager.CreateResourceContext();
        context.QualifierValues["Language"] = languageCode;
        System.Diagnostics.Debug.WriteLine($"[LocalizationService] Created ResourceContext for language: {languageCode}");
        return context;
    }

    private static bool IsSupported(string lang) =>
        SupportedLanguages.Contains(lang, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Detects if the application is running as a packaged (MSIX) app.
    /// </summary>
    private static bool IsRunningAsPackaged()
    {
        try
        {
            // Windows.ApplicationModel.Package.Current throws if not packaged
            var package = Windows.ApplicationModel.Package.Current;
            return package != null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Gets the system's preferred language, mapped to our supported languages.
    /// Falls back to English if the system language is not supported.
    /// </summary>
    private static string GetSystemLanguage()
    {
        try
        {
            // Get system language preferences (ordered by user preference)
            var languages = ApplicationLanguages.Languages;
            foreach (var lang in languages)
            {
                var systemLang = lang.ToLowerInvariant();

                // Map to supported languages
                // Chinese: distinguish between Simplified and Traditional
                if (systemLang.StartsWith("zh"))
                {
                    if (systemLang.Contains("tw") || systemLang.Contains("hant") ||
                        systemLang.Contains("hk") || systemLang.Contains("mo"))
                    {
                        return "zh-TW";
                    }
                    return "zh-CN";
                }
                if (systemLang.StartsWith("ja")) return "ja-JP";
                if (systemLang.StartsWith("ko")) return "ko-KR";
                if (systemLang.StartsWith("fr")) return "fr-FR";
                if (systemLang.StartsWith("de")) return "de-DE";
                if (systemLang.StartsWith("en")) return "en-US";
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[LocalizationService] GetSystemLanguage error: {ex.Message}");
        }

        // Default to English if no supported language is found
        return "en-US";
    }

    /// <summary>
    /// Gets the display name for a language code.
    /// </summary>
    /// <param name="languageCode">The language code.</param>
    /// <returns>The display name.</returns>
    public static string GetLanguageDisplayName(string languageCode)
    {
        return languageCode switch
        {
            "en-US" => "English",
            "zh-CN" => "简体中文",
            "zh-TW" => "繁體中文",
            "ja-JP" => "日本語",
            "ko-KR" => "한국어",
            "fr-FR" => "Français",
            "de-DE" => "Deutsch",
            _ => languageCode
        };
    }
}

/// <summary>
/// Extension methods for easy access to localized strings.
/// </summary>
public static class LocalizationExtensions
{
    /// <summary>
    /// Gets a localized string.
    /// </summary>
    public static string Localize(this string key)
    {
        return LocalizationService.Instance.GetString(key);
    }

    /// <summary>
    /// Gets a localized string with format arguments.
    /// </summary>
    public static string Localize(this string key, params object[] args)
    {
        return LocalizationService.Instance.GetString(key, args);
    }
}
