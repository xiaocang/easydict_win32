using Easydict.TranslationService.Models;
using Easydict.TranslationService.Services;

namespace Easydict.TranslationService.Streaming;

/// <summary>
/// Strategy that encapsulates a single OpenAI-compatible wire format:
/// how to build the request body and how to parse its SSE response stream.
/// </summary>
internal interface IOpenAIFormatStrategy
{
    /// <summary>The format this strategy implements.</summary>
    OpenAIApiFormat Format { get; }

    /// <summary>Serialize chat messages into the format-specific request body shape.</summary>
    object BuildRequestBody(
        IReadOnlyList<ChatMessage> messages,
        string model,
        double temperature,
        string? reasoningEffort);

    /// <summary>Parse the SSE response stream into text chunks.</summary>
    IAsyncEnumerable<string> ParseStreamAsync(Stream stream, CancellationToken cancellationToken);
}

/// <summary>
/// Looks up the strategy implementation for a given format.
/// </summary>
internal static class OpenAIFormatStrategies
{
    public static IOpenAIFormatStrategy For(OpenAIApiFormat format) => format switch
    {
        OpenAIApiFormat.ChatCompletions => ChatCompletionsFormatStrategy.Instance,
        OpenAIApiFormat.Responses => ResponsesFormatStrategy.Instance,
        _ => throw new ArgumentOutOfRangeException(
            nameof(format), format, "Auto must be resolved to a concrete format before strategy lookup."),
    };
}
