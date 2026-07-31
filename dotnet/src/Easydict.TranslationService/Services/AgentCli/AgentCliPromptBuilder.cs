using Easydict.TranslationService.Models;

namespace Easydict.TranslationService.Services.AgentCli;

/// <summary>
/// Builds translation prompts for agent CLI services. The user-supplied
/// CustomPrompt is folded into the stdin prompt (never into argv) so that
/// arbitrary user text can never reach the command line.
/// </summary>
internal static class AgentCliPromptBuilder
{
    /// <summary>
    /// The task prompt, mirroring BaseOpenAIService.BuildChatMessages.
    /// </summary>
    public static string BuildUserPrompt(TranslationRequest request)
    {
        var sourceLangName = request.FromLanguage == Language.Auto
            ? "the detected language"
            : request.FromLanguage.GetDisplayName();
        var targetLangName = request.ToLanguage.GetDisplayName();

        var prompt = $"Translate the following {sourceLangName} text into {targetLangName} text: \"\"\"{request.Text}\"\"\"";
        if (!string.IsNullOrWhiteSpace(request.CustomPrompt))
        {
            prompt = $"Additional instructions: {request.CustomPrompt}\n\n{prompt}";
        }

        return prompt;
    }

    public static string BuildSystemPromptArgument(string prompt)
    {
        // Flatten newlines and use ASCII backticks to avoid .cmd metacharacter and code-page corruption.
        return prompt.ReplaceLineEndings(" ")
            .Replace('\'', '`')
            .Replace('"', '`');
    }

    /// <summary>
    /// Sanitize a user-configured model name for use on a CLI command line.
    /// Returns null when empty or containing characters outside the whitelist.
    /// </summary>
    public static string? SanitizeModelName(string? model)
    {
        var trimmed = model?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        return trimmed.All(static ch =>
            ch is >= 'a' and <= 'z'
                or >= 'A' and <= 'Z'
                or >= '0' and <= '9'
                or '.' or '_' or ':' or '/' or '-')
            ? trimmed
            : null;
    }
}
