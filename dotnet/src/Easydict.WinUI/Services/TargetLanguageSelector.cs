using System.Diagnostics;
using Easydict.TranslationService.Models;

namespace Easydict.WinUI.Services;

/// <summary>
/// Manages target language selection state for a translation window.
/// Tracks whether the user has manually selected a target language and
/// determines the appropriate language route for each query.
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
    /// Resolve the language route for a quick query.
    /// </summary>
    public QuickQueryLanguageResolution ResolveQueryLanguage(
        Language selectedSource,
        Language selectedTarget,
        Language effectiveSource,
        bool grammarCorrectionAvailable)
    {
        var isTargetAuto = selectedTarget == Language.Auto;
        var target = isTargetAuto
            ? ResolveAutoTargetLanguage(effectiveSource)
            : selectedTarget;

        var grammarRequested = effectiveSource != Language.Auto
            && !isTargetAuto
            && target == effectiveSource;

        if (grammarRequested && grammarCorrectionAvailable)
        {
            Debug.WriteLine($"[TargetLanguageSelector] Same-language route resolved as grammar correction: {effectiveSource}");
            return new QuickQueryLanguageResolution(
                selectedSource,
                selectedTarget,
                effectiveSource,
                effectiveSource,
                QueryMode.GrammarCorrection,
                isTargetAuto,
                GrammarCorrectionRequested: true,
                GrammarCorrectionFallback: false);
        }

        var fallback = false;
        if (grammarRequested)
        {
            target = ResolveDifferentTargetLanguage(effectiveSource);
            fallback = target != Language.Auto && target != effectiveSource;
            Debug.WriteLine($"[TargetLanguageSelector] Grammar correction unavailable, fallback target: {target}");
        }

        return new QuickQueryLanguageResolution(
            selectedSource,
            selectedTarget,
            effectiveSource,
            target,
            QueryMode.Translation,
            isTargetAuto,
            grammarRequested,
            fallback);
    }

    /// <summary>
    /// Resolve an automatic target language using Easydict macOS's first/second-language rule.
    /// </summary>
    public Language ResolveAutoTargetLanguage(Language source)
    {
        var firstLang = LanguageExtensions.FromCode(_settings.FirstLanguage);
        var secondLang = LanguageExtensions.FromCode(_settings.SecondLanguage);

        var target = firstLang;
        if (source == firstLang)
        {
            target = secondLang;
        }

        if (target == source)
        {
            target = ResolveDifferentTargetLanguage(source);
        }

        Debug.WriteLine($"[TargetLanguageSelector] Auto target resolved: source={source}, target={target}");
        return target;
    }

    /// <summary>
    /// Resolve a normal translation fallback target that differs from the source.
    /// </summary>
    public Language ResolveDifferentTargetLanguage(Language source)
    {
        var firstLang = LanguageExtensions.FromCode(_settings.FirstLanguage);
        var secondLang = LanguageExtensions.FromCode(_settings.SecondLanguage);

        if (source != firstLang && firstLang != Language.Auto)
            return firstLang;

        if (source != secondLang && secondLang != Language.Auto)
            return secondLang;

        foreach (var entry in LanguageComboHelper.SelectableLanguages)
        {
            if (entry.Language != source)
                return entry.Language;
        }

        if (source != Language.English)
            return Language.English;

        if (source != Language.SimplifiedChinese)
            return Language.SimplifiedChinese;

        return Language.Auto;
    }
}

public sealed record QuickQueryLanguageResolution(
    Language SelectedSourceLanguage,
    Language SelectedTargetLanguage,
    Language EffectiveSourceLanguage,
    Language EffectiveTargetLanguage,
    QueryMode EffectiveMode,
    bool IsTargetAuto,
    bool GrammarCorrectionRequested,
    bool GrammarCorrectionFallback);
