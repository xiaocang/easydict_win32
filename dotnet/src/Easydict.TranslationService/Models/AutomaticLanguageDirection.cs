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
}
