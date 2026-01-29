using System.Diagnostics;
using Easydict.TranslationService.Models;

namespace Easydict.WinUI.Services;

/// <summary>
/// Manages target language selection state for a translation window.
/// Tracks whether the user has manually selected a target language and
/// determines the appropriate target language for each query.
///
/// Once the user manually selects a target language (via combo box or swap),
/// that selection is preserved until <see cref="Reset"/> is called
/// (typically on window close/reopen).
///
/// In all modes (auto, manual, auto-select-disabled), same-language translation
/// is prevented by reversing first↔second language when source == target.
/// </summary>
public sealed class TargetLanguageSelector
{
    private readonly SettingsService _settings;
    private bool _isManualSelection;

    public TargetLanguageSelector(SettingsService settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    /// <summary>
    /// Whether the user has manually selected a target language.
    /// When true, auto-selection is bypassed.
    /// </summary>
    public bool IsManualSelection => _isManualSelection;

    /// <summary>
    /// Mark that the user has manually chosen a target language
    /// (via combo box change or swap button).
    /// </summary>
    public void MarkManualSelection()
    {
        _isManualSelection = true;
        Debug.WriteLine("[TargetLanguageSelector] Manual selection marked");
    }

    /// <summary>
    /// Reset to auto-selection mode. Call on window load / reopen.
    /// </summary>
    public void Reset()
    {
        _isManualSelection = false;
        Debug.WriteLine("[TargetLanguageSelector] Reset to auto-selection");
    }

    /// <summary>
    /// Resolve the target language for a query.
    /// In auto mode, auto-selects the target language via the detection service.
    /// In manual mode or when auto-select is disabled, uses <paramref name="currentTarget"/>.
    /// In all modes, prevents same-language translation by reversing first↔second language.
    /// </summary>
    /// <param name="detectedSource">The detected source language.</param>
    /// <param name="currentTarget">The current target language from the UI combo box.</param>
    /// <param name="detectionService">The language detection service for auto-selection.</param>
    /// <returns>The resolved target language (never the same as source).</returns>
    public Language ResolveTargetLanguage(
        Language detectedSource,
        Language currentTarget,
        LanguageDetectionService detectionService)
    {
        if (detectionService is null)
            throw new ArgumentNullException(nameof(detectionService));

        Language target;

        if (_isManualSelection || !_settings.AutoSelectTargetLanguage)
        {
            target = currentTarget;
            Debug.WriteLine($"[TargetLanguageSelector] Using {(_isManualSelection ? "manual" : "settings")} selection: {target}");
        }
        else
        {
            target = detectionService.GetTargetLanguage(detectedSource);
            Debug.WriteLine($"[TargetLanguageSelector] Auto-selected: {target}");
        }

        // Prevent same-language translation
        if (detectedSource != Language.Auto && target == detectedSource)
        {
            var firstLang = LanguageExtensions.FromCode(_settings.FirstLanguage);
            var secondLang = LanguageExtensions.FromCode(_settings.SecondLanguage);

            if (detectedSource == firstLang)
                target = secondLang;
            else if (detectedSource == secondLang)
                target = firstLang;
            else
                target = firstLang;

            Debug.WriteLine($"[TargetLanguageSelector] Same-language reversal: {detectedSource} -> {target}");
        }

        return target;
    }
}
