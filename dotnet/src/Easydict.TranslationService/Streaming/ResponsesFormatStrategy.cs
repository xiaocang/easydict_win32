using Easydict.TranslationService.Models;
using Easydict.TranslationService.Services;

namespace Easydict.TranslationService.Streaming;

/// <summary>
/// OpenAI Responses wire format
/// (POST /v1/responses with { model, instructions, input, temperature, reasoning?, stream, store }).
/// The system message is folded into <c>instructions</c>; remaining messages are
/// concatenated into <c>input</c>.
/// </summary>
internal sealed class ResponsesFormatStrategy : IOpenAIFormatStrategy
{
    public static IOpenAIFormatStrategy Instance { get; } = new ResponsesFormatStrategy();

    private ResponsesFormatStrategy() { }

    public OpenAIApiFormat Format => OpenAIApiFormat.Responses;

    public object BuildRequestBody(
        IReadOnlyList<ChatMessage> messages,
        string model,
        double temperature,
        string? reasoningEffort)
    {
        var instructions = messages.FirstOrDefault(m => m.Role == ChatRole.System)?.Content;
        var input = string.Join(
            "\n\n",
            messages.Where(m => m.Role != ChatRole.System).Select(m => m.Content));

        var body = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["instructions"] = instructions,
            ["input"] = input,
            ["temperature"] = temperature,
            ["stream"] = true,
            ["store"] = false,
        };

        if (!string.IsNullOrWhiteSpace(reasoningEffort))
        {
            body["reasoning"] = new { effort = reasoningEffort };
        }

        return body;
    }

    public IAsyncEnumerable<string> ParseStreamAsync(Stream stream, CancellationToken cancellationToken)
        => ResponsesSseParser.ParseStreamAsync(stream, cancellationToken);
}
