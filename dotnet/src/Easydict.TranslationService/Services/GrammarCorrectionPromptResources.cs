using Easydict.TranslationService.Models;

namespace Easydict.TranslationService.Services;

/// <summary>
/// Shared prompt resources for grammar correction services.
/// </summary>
public static class GrammarCorrectionPromptResources
{
    /// <summary>
    /// System prompt for grammar correction mode without explanations.
    /// </summary>
    public const string SystemPrompt = """
        You are a grammar correction expert. Your task is to correct grammar, spelling, and punctuation errors in the text provided by the user.

        Rules:
        1. NEVER translate the text. The output must be in the exact same language as the input.
        2. Keep the original meaning unchanged.
        3. Only fix actual errors; do not rephrase, paraphrase, or "polish" correct text.
        4. Output ONLY the corrected text with no additional commentary, labels, or formatting.
        5. If the text has no errors, output it unchanged.
        """;

    /// <summary>
    /// System prompt for grammar correction mode with explanations.
    /// </summary>
    public const string SystemPromptWithExplanation = """
        You are a grammar correction expert. Your task is to correct grammar, spelling, and punctuation errors in the text provided by the user.

        Rules:
        1. NEVER translate the text. The output must be in the exact same language as the input.
        2. Keep the original meaning unchanged.
        3. Only fix actual errors; do not rephrase, paraphrase, or "polish" correct text.
        4. First output the fully corrected text, then on a new line output "---", then briefly list the key corrections you made.
        5. The "---" separator MUST be on its own line after the corrected text. NEVER put "---" before the corrected text.
        6. If the text has no errors, output it unchanged followed by "---" and "No errors found."
        """;

    public static string GetSystemPrompt(bool includeExplanations)
    {
        return includeExplanations
            ? SystemPromptWithExplanation
            : SystemPrompt;
    }

    public static string BuildUserPrompt(GrammarCorrectionRequest request)
    {
        return BuildUserPrompt(request.Language, request.Text);
    }

    public static string BuildUserPrompt(Language language, string text)
    {
        return language == Language.Auto
            ? $"Correct the grammar in the following text:\n\n{text}"
            : $"Correct the grammar in the following {language.GetDisplayName()} text. The result MUST remain in {language.GetDisplayName()}:\n\n{text}";
    }

    public static string BuildPlainTextPrompt(GrammarCorrectionRequest request)
    {
        return $"""
        {GetSystemPrompt(request.IncludeExplanations)}

        {BuildUserPrompt(request)}
        """;
    }
}
