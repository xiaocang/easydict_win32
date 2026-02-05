namespace Easydict.TranslationService.Models;

/// <summary>
/// Helper for formatting phonetic transcription data for UI display.
/// </summary>
public static class PhoneticDisplayHelper
{
    /// <summary>
    /// Maps phonetic accent codes to short display labels.
    /// US/UK use Chinese labels (美/英), src/dest use 原/译.
    /// </summary>
    public static string? GetAccentDisplayLabel(string? accent)
    {
        return accent switch
        {
            "US" => "美",
            "UK" => "英",
            "src" => "原",
            "dest" => "译",
            null or "" => null,
            _ => accent
        };
    }

    /// <summary>
    /// Formats phonetic text for display, wrapping in slashes if not already wrapped.
    /// </summary>
    public static string FormatPhoneticText(string text)
    {
        if (text.StartsWith('/') && text.EndsWith('/'))
            return text;

        return $"/{text}/";
    }

    /// <summary>
    /// Extracts displayable phonetics from a TranslationResult.
    /// Returns an empty list if no phonetics are available.
    /// </summary>
    public static IReadOnlyList<Phonetic> GetDisplayablePhonetics(TranslationResult? result)
    {
        var phonetics = result?.WordResult?.Phonetics;
        if (phonetics == null || phonetics.Count == 0)
            return [];

        return phonetics.Where(p => !string.IsNullOrEmpty(p.Text)).ToList();
    }

    /// <summary>
    /// Extracts only target-language phonetics from a TranslationResult.
    /// Filters for "dest", "US", or "UK" accents and excludes source language phonetics.
    /// Returns an empty list if no target phonetics are available.
    /// </summary>
    public static IReadOnlyList<Phonetic> GetTargetPhonetics(TranslationResult? result)
    {
        var phonetics = result?.WordResult?.Phonetics;
        if (phonetics == null || phonetics.Count == 0)
            return [];

        // Only include phonetics that are explicitly marked as target language
        // dest = target language accent, US/UK = English accents (typically target for Chinese input)
        return phonetics
            .Where(p => !string.IsNullOrEmpty(p.Text) && 
                       (p.Accent == "dest" || p.Accent == "US" || p.Accent == "UK"))
            .ToList();
    }
}
