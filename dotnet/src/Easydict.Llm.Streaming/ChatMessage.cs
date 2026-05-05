namespace Easydict.Llm.Streaming;

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
