namespace Easydict.TranslationService.Models;

/// <summary>The same first/second-language routing rule used by the host's automatic target selector.</summary>
public sealed record AutomaticLanguageDirection(Language FirstLanguage, Language SecondLanguage)
{
    public string BuildInstruction() =>
        $"Detect the source language of the text. The user's first language is {FirstLanguage.GetDisplayName()} "
        + $"and second language is {SecondLanguage.GetDisplayName()}. "
        + $"If the source language is {FirstLanguage.GetDisplayName()}, translate into {SecondLanguage.GetDisplayName()}; "
        + $"otherwise translate into {FirstLanguage.GetDisplayName()}. "
        + "Return only the translated text, without reporting the detected language or translation direction.";

    /// <summary>
    /// Resolves the language the model actually produced. A request routed by
    /// <see cref="AutomaticLanguageDirection"/> leaves <see cref="TranslationRequest.ToLanguage"/> as
    /// <see cref="Language.Auto"/>, and that sentinel must not reach language-dependent work such as
    /// text-to-speech, which maps Auto to a default voice.
    /// </summary>
    /// <param name="translatedText">Produced text, used to disambiguate when the source is unknown.</param>
    /// <param name="sourceLanguage">
    /// Source language the caller already knows. <see cref="Language.Auto"/> when unknown; the direction
    /// rule is then applied to the script of <paramref name="translatedText"/>.
    /// </param>
    public Language ResolveOutputLanguage(string? translatedText, Language sourceLanguage = Language.Auto)
    {
        if (sourceLanguage != Language.Auto)
            return sourceLanguage == FirstLanguage ? SecondLanguage : FirstLanguage;

        var first = 0;
        var second = 0;
        foreach (var c in translatedText ?? string.Empty)
        {
            if (UsesScript(FirstLanguage, c)) first++;
            if (UsesScript(SecondLanguage, c)) second++;
        }

        // Distinct scripts settle the direction. Languages sharing a script (English/French) tie
        // and fall back to the first language, the target the rule uses for unknown sources.
        return second > first ? SecondLanguage : FirstLanguage;
    }

    /// <summary>Whether <paramref name="c"/> is written in the script of <paramref name="language"/>.</summary>
    private static bool UsesScript(Language language, char c) => language switch
    {
        Language.Japanese => IsKana(c) || IsHan(c),
        Language.SimplifiedChinese or Language.TraditionalChinese or Language.ClassicalChinese => IsHan(c),
        Language.Korean => IsHangul(c),
        Language.Russian or Language.Ukrainian or Language.Bulgarian => c is >= '\u0400' and <= '\u04FF',
        Language.Arabic or Language.Persian or Language.Urdu => c is >= '\u0600' and <= '\u06FF',
        Language.Hindi => c is >= '\u0900' and <= '\u097F',
        Language.Bengali => c is >= '\u0980' and <= '\u09FF',
        Language.Tamil => c is >= '\u0B80' and <= '\u0BFF',
        Language.Telugu => c is >= '\u0C00' and <= '\u0C7F',
        Language.Thai => c is >= '\u0E00' and <= '\u0E7F',
        Language.Hebrew => c is >= '\u0590' and <= '\u05FF',
        Language.Greek => c is >= '\u0370' and <= '\u03FF',
        _ => c < '\u0250' && char.IsLetter(c) // Latin-script languages
    };

    private static bool IsKana(char c) =>
        c is (>= '\u3040' and <= '\u309F') or (>= '\u30A0' and <= '\u30FF') or (>= '\u31F0' and <= '\u31FF');

    private static bool IsHan(char c) =>
        c is (>= '\u4E00' and <= '\u9FFF') or (>= '\u3400' and <= '\u4DBF') or (>= '\uF900' and <= '\uFAFF');

    private static bool IsHangul(char c) =>
        c is (>= '\uAC00' and <= '\uD7AF') or (>= '\u1100' and <= '\u11FF') or (>= '\u3130' and <= '\u318F');
}
