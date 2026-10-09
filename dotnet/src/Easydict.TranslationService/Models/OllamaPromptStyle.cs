namespace Easydict.TranslationService.Models;

/// <summary>
/// Which system prompt the Ollama service sends with a translation.
/// </summary>
public enum OllamaPromptStyle
{
    /// <summary>The detailed "translation expert" prompt every LLM service uses.</summary>
    Standard,

    /// <summary>
    /// A short imperative prompt. Small local models (≈7B) follow it more reliably than the
    /// long persona prompt, which they tend to answer with mixed-language output.
    /// </summary>
    Concise,

    /// <summary>The user's own system prompt.</summary>
    Custom,
}

public static class OllamaPromptStyleParser
{
    /// <summary>Parses a stored setting value; anything unrecognised is <see cref="OllamaPromptStyle.Standard"/>.</summary>
    public static OllamaPromptStyle Parse(string? value)
        => Enum.TryParse<OllamaPromptStyle>(value, ignoreCase: true, out var style) && Enum.IsDefined(style)
            ? style
            : OllamaPromptStyle.Standard;
}
