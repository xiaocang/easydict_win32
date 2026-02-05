using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Easydict.TranslationService.Streaming;

/// <summary>
/// Parses Server-Sent Events (SSE) for OpenAI chat completions streaming format.
/// Handles: data: {"choices":[{"delta":{"content":"..."}}]} and [DONE]
/// </summary>
public static class SseParser
{
    private const string DataPrefix = "data: ";
    private const string DoneMarker = "[DONE]";

    /// <summary>
    /// Parse SSE stream and yield content chunks.
    /// </summary>
    /// <param name="responseStream">HTTP response stream containing SSE data.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Async enumerable of content strings extracted from SSE events.</returns>
    public static async IAsyncEnumerable<string> ParseStreamAsync(
        Stream responseStream,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var reader = new StreamReader(responseStream);

        while (!reader.EndOfStream)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var line = await reader.ReadLineAsync(cancellationToken);

            if (string.IsNullOrEmpty(line))
                continue;

            if (!line.StartsWith(DataPrefix))
                continue;

            var data = line[DataPrefix.Length..];

            if (data == DoneMarker)
                yield break;

            var content = ExtractContent(data);
            if (content != null)
                yield return content;
        }
    }

    /// <summary>
    /// Extract content from OpenAI chat completion delta JSON.
    /// Expected format: {"choices":[{"delta":{"content":"text"}}]}
    /// </summary>
    private static string? ExtractContent(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("choices", out var choices) &&
                choices.ValueKind == JsonValueKind.Array &&
                choices.GetArrayLength() > 0)
            {
                var firstChoice = choices[0];
                if (firstChoice.TryGetProperty("delta", out var delta) &&
                    delta.TryGetProperty("content", out var content))
                {
                    return content.GetString();
                }
            }
        }
        catch (JsonException)
        {
            // Malformed JSON, skip this chunk
        }

        return null;
    }
}
