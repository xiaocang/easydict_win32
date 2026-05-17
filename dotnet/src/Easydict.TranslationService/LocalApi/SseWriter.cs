using System.Text;
using System.Text.Json;

namespace Easydict.TranslationService.LocalApi;

/// <summary>
/// SSE framing helpers matching the OpenAI <c>chat/completions</c> streaming wire format.
/// </summary>
internal static class SseWriter
{
    private static readonly byte[] DoneBytes = Encoding.UTF8.GetBytes("data: [DONE]\n\n");

    public static async Task WriteChunkAsync(
        Stream output,
        ChatCompletionChunk chunk,
        CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(chunk, LocalApiJsonContext.Default.ChatCompletionChunk);
        var prefix = "data: "u8.ToArray();
        var suffix = "\n\n"u8.ToArray();
        var body = Encoding.UTF8.GetBytes(json);

        await output.WriteAsync(prefix, ct).ConfigureAwait(false);
        await output.WriteAsync(body, ct).ConfigureAwait(false);
        await output.WriteAsync(suffix, ct).ConfigureAwait(false);
        await output.FlushAsync(ct).ConfigureAwait(false);
    }

    public static async Task WriteDoneAsync(Stream output, CancellationToken ct)
    {
        await output.WriteAsync(DoneBytes, ct).ConfigureAwait(false);
        await output.FlushAsync(ct).ConfigureAwait(false);
    }
}
