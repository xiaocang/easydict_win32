using Microsoft.Windows.ApplicationModel.Resources;
using Windows.Globalization;

namespace Easydict.WinUI.Services;

/// <summary>
/// Provides localization services for the application.
/// Supports: English, Chinese (Simplified/Traditional), Japanese, Korean, French, and German.
/// </summary>
public sealed class LocalizationService
{
    private static readonly Lazy<LocalizationService> _instance = new(() => new LocalizationService());
    public static LocalizationService Instance => _instance.Value;

    private readonly ResourceLoader _resourceLoader;
    private string _currentLanguage;

    /// <summary>
    /// Supported UI languages.
    /// </summary>
    public static readonly string[] SupportedLanguages = { "en-US", "zh-CN", "zh-TW", "ja-JP", "ko-KR", "fr-FR", "de-DE" };

    private LocalizationService()
    {
        // Initialize based on settings or system default
        var settings = SettingsService.Instance;
        _currentLanguage = settings.UILanguage;

        // If no language is set, use system default
        if (string.IsNullOrEmpty(_currentLanguage))
        {
            _currentLanguage = GetSystemLanguage();
        }

        // Apply the language override
        ApplicationLanguages.PrimaryLanguageOverride = _currentLanguage;

        // Create resource loader
        _resourceLoader = new ResourceLoader();
    }

    /// <summary>
    /// Gets the current UI language.
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
            var value = _resourceLoader.GetString(key);
            return string.IsNullOrEmpty(value) ? key : value;
        }
        catch
        {
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
        catch
        {
            return format;
        }
    }

    /// <summary>
    /// Sets the UI language and saves to settings.
    /// Note: Requires app restart to take full effect.
    /// </summary>
    /// <param name="languageCode">Language code (e.g., "en-US", "zh-CN").</param>
    public void SetLanguage(string languageCode)
    {
        if (string.IsNullOrEmpty(languageCode))
        {
            languageCode = GetSystemLanguage();
        }

        _currentLanguage = languageCode;
        ApplicationLanguages.PrimaryLanguageOverride = languageCode;

        // Save to settings
        var settings = SettingsService.Instance;
        settings.UILanguage = languageCode;
        settings.Save();
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
                if (systemLang.StartsWith("ja"))
                {
                    return "ja-JP";
                }
                if (systemLang.StartsWith("ko"))
                {
                    return "ko-KR";
                }
                if (systemLang.StartsWith("fr"))
                {
                    return "fr-FR";
                }
                if (systemLang.StartsWith("de"))
                {
                    return "de-DE";
                }
                if (systemLang.StartsWith("en"))
                {
                    return "en-US";
                }
            }
        }
        catch
        {
            // Ignore errors
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
