using Easydict.Llm.Streaming;
using Easydict.TranslationService.Models;
using Easydict.TranslationService.Services;

namespace Easydict.TranslationService.Streaming;

/// <summary>
/// Classic OpenAI Chat Completions wire format
/// (POST /v1/chat/completions with { model, messages[], temperature, stream }).
/// </summary>
internal sealed class ChatCompletionsFormatStrategy : IOpenAIFormatStrategy
{
    public static IOpenAIFormatStrategy Instance { get; } = new ChatCompletionsFormatStrategy();

    private ChatCompletionsFormatStrategy() { }

    public OpenAIApiFormat Format => OpenAIApiFormat.ChatCompletions;

    public object BuildRequestBody(
        IReadOnlyList<ChatMessage> messages,
        string model,
        double temperature,
        string? reasoningEffort)
    {
        var body = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["messages"] = messages.Select(m => new { role = m.RoleString, content = m.Content }),
            ["temperature"] = temperature,
            ["stream"] = true,
        };

        if (!string.IsNullOrWhiteSpace(reasoningEffort))
        {
            body["reasoning_effort"] = reasoningEffort;
        }

        return body;
    }

    public IAsyncEnumerable<string> ParseStreamAsync(Stream stream, CancellationToken cancellationToken)
        => SseParser.ParseStreamAsync(stream, cancellationToken);
}
