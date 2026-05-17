using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Easydict.TranslationService.Streaming;

/// <summary>
/// Parses Server-Sent Events from the OpenAI Responses API streaming format.
/// Recognizes the "response.output_text.delta" event and yields its delta text.
/// </summary>
public static class ResponsesSseParser
{
    private const string EventPrefix = "event: ";
    private const string DataPrefix = "data: ";
    private const string DeltaEvent = "response.output_text.delta";
    private const string DoneMarker = "[DONE]";

    public static async IAsyncEnumerable<string> ParseStreamAsync(
        Stream responseStream,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var reader = new StreamReader(responseStream);
        string? currentEvent = null;

        while (!reader.EndOfStream)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);

            if (string.IsNullOrEmpty(line))
            {
                currentEvent = null;
                continue;
            }

            if (line.StartsWith(EventPrefix, StringComparison.Ordinal))
            {
                currentEvent = line[EventPrefix.Length..].Trim();
                continue;
            }

            if (!line.StartsWith(DataPrefix, StringComparison.Ordinal))
                continue;

            var data = line[DataPrefix.Length..].Trim();

            if (data == DoneMarker)
                yield break;

            var delta = ExtractDelta(data, currentEvent);
            if (!string.IsNullOrEmpty(delta))
                yield return delta;
        }
    }

    private static string? ExtractDelta(string json, string? currentEvent)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!IsDeltaEvent(root, currentEvent))
                return null;

            return root.TryGetProperty("delta", out var delta)
                ? delta.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsDeltaEvent(JsonElement root, string? currentEvent)
    {
        if (string.Equals(currentEvent, DeltaEvent, StringComparison.Ordinal))
            return true;

        return root.TryGetProperty("type", out var type) &&
               string.Equals(type.GetString(), DeltaEvent, StringComparison.Ordinal);
    }
}
