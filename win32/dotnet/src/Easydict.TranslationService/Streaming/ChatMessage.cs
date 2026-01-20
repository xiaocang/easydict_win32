using Easydict.TranslationService.Models;

namespace Easydict.TranslationService.Streaming;

/// <summary>
/// Role in a chat conversation for LLM services.
/// </summary>
public enum ChatRole
{
    System,
    User,
    Assistant
}

/// <summary>
/// A message in a chat conversation.
/// </summary>
public sealed record ChatMessage(ChatRole Role, string Content)
{
    /// <summary>
    /// Get the role as a string for API requests.
    /// </summary>
    public string RoleString => Role switch
    {
        ChatRole.System => "system",
        ChatRole.User => "user",
        ChatRole.Assistant => "assistant",
        _ => "user"
    };
}

/// <summary>
/// Parameters for building a chat query for translation.
/// </summary>
public sealed class ChatQueryParam
{
    /// <summary>
    /// Text to translate.
    /// </summary>
    public required string Text { get; init; }

    /// <summary>
    /// Source language for translation.
    /// </summary>
    public required Language SourceLanguage { get; init; }

    /// <summary>
    /// Target language for translation.
    /// </summary>
    public required Language TargetLanguage { get; init; }

    /// <summary>
    /// Temperature for LLM generation (0.0-1.0).
    /// Lower values produce more deterministic output.
    /// </summary>
    public double Temperature { get; init; } = 0.3;
}
