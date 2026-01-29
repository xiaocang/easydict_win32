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
    /// Returns the auto-selected language when in auto mode, or null when
    /// the caller should use the current combo box value (manual mode).
    /// </summary>
    /// <param name="detectedSource">The detected source language.</param>
    /// <param name="detectionService">The language detection service for auto-selection.</param>
    /// <returns>
    /// The auto-selected target language, or <c>null</c> if the caller should
    /// use the current UI selection (manual mode or auto-select disabled).
    /// </returns>
    public Language? ResolveTargetLanguage(
        Language detectedSource,
        LanguageDetectionService detectionService)
    {
        if (detectionService is null)
            throw new ArgumentNullException(nameof(detectionService));

        if (_isManualSelection)
        {
            Debug.WriteLine("[TargetLanguageSelector] Using manual selection");
            return null;
        }

        if (!_settings.AutoSelectTargetLanguage)
        {
            Debug.WriteLine("[TargetLanguageSelector] Auto-select disabled, using current selection");
            return null;
        }

        var target = detectionService.GetTargetLanguage(detectedSource);
        Debug.WriteLine($"[TargetLanguageSelector] Auto-selected: {target}");
        return target;
    }
}
