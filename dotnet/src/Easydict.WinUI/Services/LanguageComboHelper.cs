using Easydict.TranslationService.Models;
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
    /// Complete list of all languages with their tags, localization keys, and group ordering.
    /// Grouped by language family for display in the Available Languages checkbox grid.
    /// </summary>
    public static readonly (TranslationLanguage Language, string Tag, string LocalizationKey, int GroupOrder)[] AllLanguages =
    [
        // East Asian (group 0)
        (TranslationLanguage.SimplifiedChinese, "zh", "LangChineseSimplified", 0),
        (TranslationLanguage.TraditionalChinese, "zh-tw", "LangChineseTraditional", 0),
        (TranslationLanguage.Japanese, "ja", "LangJapanese", 0),
        (TranslationLanguage.Korean, "ko", "LangKorean", 0),
        (TranslationLanguage.ClassicalChinese, "zh-classical", "LangClassicalChinese", 0),

        // Germanic (group 1)
        (TranslationLanguage.English, "en", "LangEnglish", 1),
        (TranslationLanguage.German, "de", "LangGerman", 1),
        (TranslationLanguage.Dutch, "nl", "LangDutch", 1),
        (TranslationLanguage.Swedish, "sv", "LangSwedish", 1),
        (TranslationLanguage.Norwegian, "no", "LangNorwegian", 1),
        (TranslationLanguage.Danish, "da", "LangDanish", 1),

        // Romance (group 2)
        (TranslationLanguage.French, "fr", "LangFrench", 2),
        (TranslationLanguage.Spanish, "es", "LangSpanish", 2),
        (TranslationLanguage.Portuguese, "pt", "LangPortuguese", 2),
        (TranslationLanguage.Italian, "it", "LangItalian", 2),
        (TranslationLanguage.Romanian, "ro", "LangRomanian", 2),

        // Slavic (group 3)
        (TranslationLanguage.Russian, "ru", "LangRussian", 3),
        (TranslationLanguage.Polish, "pl", "LangPolish", 3),
        (TranslationLanguage.Czech, "cs", "LangCzech", 3),
        (TranslationLanguage.Ukrainian, "uk", "LangUkrainian", 3),
        (TranslationLanguage.Bulgarian, "bg", "LangBulgarian", 3),
        (TranslationLanguage.Slovak, "sk", "LangSlovak", 3),
        (TranslationLanguage.Slovenian, "sl", "LangSlovenian", 3),

        // Baltic (group 4)
        (TranslationLanguage.Estonian, "et", "LangEstonian", 4),
        (TranslationLanguage.Latvian, "lv", "LangLatvian", 4),
        (TranslationLanguage.Lithuanian, "lt", "LangLithuanian", 4),

        // Other European (group 5)
        (TranslationLanguage.Greek, "el", "LangGreek", 5),
        (TranslationLanguage.Hungarian, "hu", "LangHungarian", 5),
        (TranslationLanguage.Finnish, "fi", "LangFinnish", 5),
        (TranslationLanguage.Turkish, "tr", "LangTurkish", 5),

        // Middle Eastern (group 6)
        (TranslationLanguage.Arabic, "ar", "LangArabic", 6),
        (TranslationLanguage.Persian, "fa", "LangPersian", 6),
        (TranslationLanguage.Hebrew, "he", "LangHebrew", 6),

        // South Asian (group 7)
        (TranslationLanguage.Hindi, "hi", "LangHindi", 7),
        (TranslationLanguage.Bengali, "bn", "LangBengali", 7),
        (TranslationLanguage.Tamil, "ta", "LangTamil", 7),
        (TranslationLanguage.Telugu, "te", "LangTelugu", 7),
        (TranslationLanguage.Urdu, "ur", "LangUrdu", 7),

        // Southeast Asian (group 8)
        (TranslationLanguage.Vietnamese, "vi", "LangVietnamese", 8),
        (TranslationLanguage.Thai, "th", "LangThai", 8),
        (TranslationLanguage.Indonesian, "id", "LangIndonesian", 8),
        (TranslationLanguage.Malay, "ms", "LangMalay", 8),
        (TranslationLanguage.Filipino, "tl", "LangFilipino", 8),
    ];

    /// <summary>
    /// Returns the active languages based on the user's SelectedLanguages setting.
    /// The returned array maintains the same order as AllLanguages but filtered
    /// to only include languages the user has selected.
    /// </summary>
    public static (TranslationLanguage Language, string Tag, string LocalizationKey)[] GetActiveLanguages()
    {
        var selectedCodes = SettingsService.Instance.SelectedLanguages;
        var selectedSet = new HashSet<string>(selectedCodes, StringComparer.OrdinalIgnoreCase);

        return AllLanguages
            .Where(entry => selectedSet.Contains(entry.Tag))
            .Select(entry => (entry.Language, entry.Tag, entry.LocalizationKey))
            .ToArray();
    }

    /// <summary>
    /// Canonical list of selectable languages (excluding Auto).
    /// Now dynamically derived from user's SelectedLanguages setting.
    /// </summary>
    public static (TranslationLanguage Language, string Tag, string LocalizationKey)[] SelectableLanguages
        => GetActiveLanguages();

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

        foreach (var entry in AllLanguages)
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
        foreach (var entry in AllLanguages)
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
    /// Populate a source language combo box with Auto Detect + active languages.
    /// </summary>
    public static void PopulateSourceCombo(ComboBox combo, LocalizationService loc)
    {
        combo.Items.Clear();

        // Add "Auto Detect" as first item
        combo.Items.Add(new ComboBoxItem
        {
            Content = loc.GetString("LangAutoDetect"),
            Tag = "auto"
        });

        foreach (var entry in SelectableLanguages)
        {
            combo.Items.Add(new ComboBoxItem
            {
                Content = loc.GetString(entry.LocalizationKey),
                Tag = entry.Tag
            });
        }

        combo.SelectedIndex = 0; // Default to Auto Detect
    }

    /// <summary>
    /// Populate a target language combo box with active languages (no Auto).
    /// </summary>
    public static void PopulateTargetCombo(ComboBox combo, LocalizationService loc)
    {
        combo.Items.Clear();

        foreach (var entry in SelectableLanguages)
        {
            combo.Items.Add(new ComboBoxItem
            {
                Content = loc.GetString(entry.LocalizationKey),
                Tag = entry.Tag
            });
        }

        if (combo.Items.Count > 0)
            combo.SelectedIndex = 0;
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
            var firstLang = LanguageExtensions.FromCode(settings.FirstLanguage);
            var secondLang = LanguageExtensions.FromCode(settings.SecondLanguage);

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
