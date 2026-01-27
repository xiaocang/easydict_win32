using Microsoft.Windows.ApplicationModel.Resources;
using System.Linq;
using Windows.Globalization;

namespace Easydict.WinUI.Services;

/// <summary>
/// Represents a supported UI language with its metadata.
/// </summary>
public sealed record LanguageInfo(string Code, string DisplayName, string[] Prefixes);

/// <summary>
/// Provides localization services for the application.
/// Supports English, Chinese (Simplified/Traditional), Japanese, Korean, French, and German.
/// </summary>
public sealed class LocalizationService
{
    private static readonly Lazy<LocalizationService> _instance = new(() => new LocalizationService());
    public static LocalizationService Instance => _instance.Value;

    private readonly ResourceLoader _resourceLoader;
    private string _currentLanguage;

    /// <summary>
    /// Supported UI languages with their metadata.
    /// Order matters: first match wins in system language detection.
    /// </summary>
    private static readonly LanguageInfo[] _languages =
    [
        new("en-US", "English", ["en"]),
        // Chinese: Traditional variants must come before Simplified to match correctly
        // (zh-TW, zh-HK, zh-MO, zh-Hant should match Traditional; plain "zh" defaults to Simplified)
        new("zh-TW", "繁體中文", ["zh-tw", "zh-hk", "zh-mo", "zh-hant"]),
        new("zh-CN", "简体中文", ["zh"]), // Catches zh, zh-CN, zh-SG, zh-Hans, etc.
        new("ja-JP", "日本語", ["ja"]),
        new("ko-KR", "한국어", ["ko"]),
        new("fr-FR", "Français", ["fr"]),
        new("de-DE", "Deutsch", ["de"]),
    ];

    /// <summary>
    /// Supported UI language codes.
    /// </summary>
    public static string[] SupportedLanguages => _languages.Select(l => l.Code).ToArray();

    /// <summary>
    /// Gets all supported languages with their metadata.
    /// </summary>
    public static IReadOnlyList<LanguageInfo> Languages => _languages;

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
            var systemLanguages = ApplicationLanguages.Languages;
            foreach (var lang in systemLanguages)
            {
                var systemLang = lang.ToLowerInvariant();

                // Find matching supported language by prefix
                foreach (var supported in _languages)
                {
                    if (supported.Prefixes.Any(prefix => systemLang.StartsWith(prefix)))
                    {
                        return supported.Code;
                    }
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
        return _languages.FirstOrDefault(l => l.Code == languageCode)?.DisplayName ?? languageCode;
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
