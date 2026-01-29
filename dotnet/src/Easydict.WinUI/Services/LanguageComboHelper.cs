using Microsoft.UI.Xaml.Controls;
using TranslationLanguage = Easydict.TranslationService.Models.Language;

namespace Easydict.WinUI.Services;

/// <summary>
/// Shared helper for language combo box operations across all window types.
/// Provides consistent language lists, Tag-based selection reading, and
/// target combo rebuilding with mutual exclusion.
/// </summary>
public static class LanguageComboHelper
{
    /// <summary>
    /// Canonical list of selectable languages (excluding Auto).
    /// Order: En, Zh-CN, Zh-TW, Ja, Ko, Fr, De, Es.
    /// </summary>
    public static readonly (TranslationLanguage Language, string Tag, string LocalizationKey)[] SelectableLanguages =
    [
        (TranslationLanguage.English, "en", "LangEnglish"),
        (TranslationLanguage.SimplifiedChinese, "zh", "LangChinese"),
        (TranslationLanguage.TraditionalChinese, "zh-tw", "LangChineseTraditional"),
        (TranslationLanguage.Japanese, "ja", "LangJapanese"),
        (TranslationLanguage.Korean, "ko", "LangKorean"),
        (TranslationLanguage.French, "fr", "LangFrench"),
        (TranslationLanguage.German, "de", "LangGerman"),
        (TranslationLanguage.Spanish, "es", "LangSpanish"),
    ];

    /// <summary>
    /// Read the selected language from a combo box using the Tag property.
    /// Returns <see cref="TranslationLanguage.Auto"/> if the selected item
    /// has Tag "auto" or no recognized Tag.
    /// </summary>
    public static TranslationLanguage GetSelectedLanguage(ComboBox combo)
    {
        if (combo.SelectedItem is not ComboBoxItem item)
            return TranslationLanguage.Auto;

        var tag = item.Tag as string;
        if (string.IsNullOrEmpty(tag) || tag == "auto")
            return TranslationLanguage.Auto;

        foreach (var entry in SelectableLanguages)
        {
            if (entry.Tag == tag)
                return entry.Language;
        }

        return TranslationLanguage.Auto;
    }

    /// <summary>
    /// Find the index of a language in a combo box by its Tag.
    /// Returns -1 if not found.
    /// </summary>
    public static int FindLanguageIndex(ComboBox combo, TranslationLanguage language)
    {
        string? targetTag = null;
        foreach (var entry in SelectableLanguages)
        {
            if (entry.Language == language)
            {
                targetTag = entry.Tag;
                break;
            }
        }

        if (targetTag == null)
            return -1;

        for (int i = 0; i < combo.Items.Count; i++)
        {
            if (combo.Items[i] is ComboBoxItem item && item.Tag as string == targetTag)
                return i;
        }

        return -1;
    }

    /// <summary>
    /// Rebuild a target language combo box, excluding the specified source language.
    /// Preserves the current target selection if possible; applies first-second
    /// reversal if the current target was removed.
    /// </summary>
    /// <param name="targetCombo">The target language combo box to rebuild.</param>
    /// <param name="sourceLanguage">The current source language to exclude (Auto means include all).</param>
    /// <param name="currentTarget">The currently selected target language.</param>
    /// <param name="loc">Localization service for display names.</param>
    /// <param name="newTarget">The resolved target language after rebuilding.</param>
    public static void RebuildTargetCombo(
        ComboBox targetCombo,
        TranslationLanguage sourceLanguage,
        TranslationLanguage currentTarget,
        LocalizationService loc,
        out TranslationLanguage newTarget)
    {
        targetCombo.Items.Clear();

        var targetWasRemoved = false;
        newTarget = currentTarget;

        foreach (var entry in SelectableLanguages)
        {
            // Skip the source language (unless source is Auto)
            if (sourceLanguage != TranslationLanguage.Auto && entry.Language == sourceLanguage)
            {
                if (currentTarget == entry.Language)
                    targetWasRemoved = true;
                continue;
            }

            targetCombo.Items.Add(new ComboBoxItem
            {
                Content = loc.GetString(entry.LocalizationKey),
                Tag = entry.Tag
            });
        }

        // If the current target was removed (same as source), apply reversal
        if (targetWasRemoved)
        {
            // Use first↔second language reversal
            var settings = SettingsService.Instance;
            var firstLang = Easydict.TranslationService.Models.LanguageExtensions.FromCode(settings.FirstLanguage);
            var secondLang = Easydict.TranslationService.Models.LanguageExtensions.FromCode(settings.SecondLanguage);

            if (sourceLanguage == firstLang)
                newTarget = secondLang;
            else if (sourceLanguage == secondLang)
                newTarget = firstLang;
            else
                newTarget = firstLang;
        }

        // Select the resolved target in the combo
        var idx = FindLanguageIndex(targetCombo, newTarget);
        if (idx >= 0)
        {
            targetCombo.SelectedIndex = idx;
        }
        else if (targetCombo.Items.Count > 0)
        {
            targetCombo.SelectedIndex = 0;
            // Read back what we actually selected
            newTarget = GetSelectedLanguage(targetCombo);
        }
    }
}
