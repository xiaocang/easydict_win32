using Easydict.TranslationService.Models;
using Easydict.TranslationService.Services;

namespace Easydict.TranslationService.Streaming;

/// <summary>
/// OpenAI Responses wire format
/// (POST /v1/responses with { model, instructions, input, temperature, stream, store }).
/// The system message is folded into <c>instructions</c>; remaining messages are
/// concatenated into <c>input</c>.
/// </summary>
internal sealed class ResponsesFormatStrategy : IOpenAIFormatStrategy
{
    public static IOpenAIFormatStrategy Instance { get; } = new ResponsesFormatStrategy();

    private ResponsesFormatStrategy() { }

    public OpenAIApiFormat Format => OpenAIApiFormat.Responses;

    public object BuildRequestBody(IReadOnlyList<ChatMessage> messages, string model, double temperature)
    {
        var instructions = messages.FirstOrDefault(m => m.Role == ChatRole.System)?.Content;
        var input = string.Join(
            "\n\n",
            messages.Where(m => m.Role != ChatRole.System).Select(m => m.Content));

        return new
        {
            model,
            instructions,
            input,
            temperature,
            stream = true,
            store = false,
        };
    }

    public IAsyncEnumerable<string> ParseStreamAsync(Stream stream, CancellationToken cancellationToken)
        => ResponsesSseParser.ParseStreamAsync(stream, cancellationToken);
}
