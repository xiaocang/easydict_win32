using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Text.Json;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Easydict.TranslationService.Services;
using Easydict.WinUI.Models;
using Easydict.WinUI.Services.Memory;

namespace Easydict.WinUI.Services;

/// <summary>
/// OCR service using OpenAI Chat Completions or Responses-compatible vision APIs.
/// </summary>
public sealed class CustomApiOcrService : IOcrService
{
    private readonly HttpClient _httpClient;
    private readonly OcrServiceOptions _options;

    public string ServiceId => "custom_api_ocr";

    public string DisplayName => "Custom API OCR";

    public bool IsAvailable => true;

    public CustomApiOcrService(HttpClient httpClient)
        : this(httpClient, OcrServiceOptions.FromSettings(SettingsService.Instance))
    {
    }

    public CustomApiOcrService(HttpClient httpClient, OcrServiceOptions options)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc />
    public async Task<OcrResult> RecognizeAsync(
        ReadOnlyMemory<byte> pixelData,
        int pixelWidth,
        int pixelHeight,
        string? preferredLanguageTag = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pixelWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pixelHeight);

        var endpoint = _options.Endpoint;
        var model = _options.Model;

        Debug.WriteLine($"[CustomApiOcr] Sending {pixelWidth}x{pixelHeight} image to {endpoint} (model: {model})");

        var base64Image = await ConvertBgraToBase64JpegAsync(pixelData, pixelWidth, pixelHeight);
        var usesResponses = UsesResponsesEndpoint(endpoint);

        // Start small — a screenshot rarely needs more — and grow only when the provider
        // reports the response was cut off. Each attempt is a fresh completion, so the
        // longest one wins rather than being concatenated.
        var maxTokens = _options.GetInitialMaxTokens();
        var recognizedText = string.Empty;

        while (true)
        {
            var attempt = await SendAttemptAsync(base64Image, usesResponses, maxTokens, cancellationToken);

            if (attempt.Text.Length > recognizedText.Length)
            {
                recognizedText = attempt.Text;
            }

            if (!attempt.Truncated || maxTokens >= OcrServiceOptions.MaxTokensCeiling)
            {
                break;
            }

            maxTokens = Math.Min(maxTokens * 2, OcrServiceOptions.MaxTokensCeiling);
            Debug.WriteLine($"[CustomApiOcr] Response truncated, retrying with max_tokens={maxTokens}");
        }

        Debug.WriteLine($"[CustomApiOcr] Recognized {recognizedText.Length} chars");

        return new OcrResult
        {
            Text = recognizedText,
            Lines = [],
            TextAngle = null,
            DetectedLanguage = null
        };
    }

    /// <inheritdoc />
    public IReadOnlyList<OcrLanguage> GetAvailableLanguages() => [];

    /// <summary>
    /// Posts one request at the given token budget, retrying once without the thinking
    /// field if the provider does not recognize it.
    /// </summary>
    private async Task<VisionApiResult> SendAttemptAsync(
        string base64Image,
        bool usesResponses,
        int maxTokens,
        CancellationToken cancellationToken)
    {
        var includeThinkingField = ShouldSendThinkingField(usesResponses);

        while (true)
        {
            var (statusCode, body) = await PostRequestAsync(
                base64Image, usesResponses, maxTokens, includeThinkingField, cancellationToken);

            if ((int)statusCode is >= 200 and < 300)
            {
                return usesResponses
                    ? ParseResponsesTextResponse(body, maxTokens)
                    : ParseOpenAIVisionResponse(body, maxTokens);
            }

            if (includeThinkingField && OcrThinkingSupport.IsThinkingFieldRejection(statusCode, body))
            {
                OcrThinkingSupport.MarkRejectedBy(_options.Endpoint, _options.Model);
                includeThinkingField = false;
                Debug.WriteLine(
                    $"[CustomApiOcr] {_options.Model} rejected the thinking field, retrying without it");
                continue;
            }

            throw new HttpRequestException(
                $"Custom API OCR request failed with status {(int)statusCode} ({statusCode}). {Summarize(body)}",
                null,
                statusCode);
        }
    }

    private async Task<(HttpStatusCode StatusCode, string Body)> PostRequestAsync(
        string base64Image,
        bool usesResponses,
        int maxTokens,
        bool includeThinkingField,
        CancellationToken cancellationToken)
    {
        var requestBody = usesResponses
            ? BuildResponsesRequestBody(_options.Model, _options.SystemPrompt, base64Image, maxTokens, includeThinkingField)
            : BuildChatCompletionsRequestBody(_options.Model, _options.SystemPrompt, base64Image, maxTokens, includeThinkingField);

        using var request = new HttpRequestMessage(HttpMethod.Post, _options.Endpoint);
        request.Content = new StringContent(
            JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");

        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        }

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, cancellationToken);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            Debug.WriteLine(
                $"[CustomApiOcr] Request timed out. timeout={FormatTimeout(_httpClient.Timeout)}");
            throw new TimeoutException(
                $"Custom API OCR request timed out after {FormatTimeout(_httpClient.Timeout)}.",
                ex);
        }

        using (response)
        {
            // Read before checking status: a rejected thinking field is only identifiable
            // from the error body.
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            return (response.StatusCode, body);
        }
    }

    /// <summary>
    /// Whether to ask the model not to think. Nothing is sent when thinking is enabled —
    /// the model's own default applies, which is what makes the setting a no-op for models
    /// without thinking support.
    /// </summary>
    private bool ShouldSendThinkingField(bool usesResponses)
    {
        if (_options.EnableThinking ||
            OcrThinkingSupport.IsRejectedBy(_options.Endpoint, _options.Model))
        {
            return false;
        }

        // The Responses API has no "thinking" field; the equivalent knob is reasoning
        // effort, and only the GPT-5 family accepts a value that means "barely think".
        return !usesResponses || OpenAIService.GetResponsesReasoningEffort(_options.Model) is not null;
    }

    private static string Summarize(string body)
    {
        const int maxLength = 300;
        var collapsed = body.Trim();

        return collapsed.Length <= maxLength ? collapsed : collapsed[..maxLength] + "...";
    }

    private static Dictionary<string, object?> BuildChatCompletionsRequestBody(
        string model,
        string systemPrompt,
        string base64Image,
        int maxTokens,
        bool disableThinking)
    {
        var body = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["max_tokens"] = maxTokens,
            ["messages"] = new object[]
            {
                new { role = "system", content = systemPrompt },
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new
                        {
                            type = "image_url",
                            image_url = new { url = $"data:image/jpeg;base64,{base64Image}" }
                        }
                    }
                }
            }
        };

        if (disableThinking)
        {
            body["thinking"] = new { type = "disabled" };
        }

        return body;
    }

    private static string FormatTimeout(TimeSpan timeout)
    {
        return timeout == Timeout.InfiniteTimeSpan
            ? "infinite"
            : $"{timeout.TotalSeconds:0.#}s";
    }

    private static Dictionary<string, object?> BuildResponsesRequestBody(
        string model,
        string systemPrompt,
        string base64Image,
        int maxTokens,
        bool disableThinking)
    {
        var body = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["max_output_tokens"] = maxTokens,
            ["store"] = false,
            ["input"] = new object[]
            {
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "input_text", text = systemPrompt },
                        new { type = "input_image", image_url = $"data:image/jpeg;base64,{base64Image}" }
                    }
                }
            }
        };

        if (disableThinking &&
            OpenAIService.GetResponsesReasoningEffort(model) is { } reasoningEffort)
        {
            body["reasoning"] = new { effort = reasoningEffort };
        }

        return body;
    }

    /// <summary>
    /// Extracts text from an OpenAI Vision-compatible JSON response.
    /// Response format: { "choices": [{ "message": { "content": "..." }, "finish_reason": "stop" }] }
    /// </summary>
    private static VisionApiResult ParseOpenAIVisionResponse(string json, int maxTokens)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("choices", out var choices) ||
                choices.ValueKind != JsonValueKind.Array ||
                choices.GetArrayLength() == 0)
            {
                return new VisionApiResult(string.Empty, Truncated: false);
            }

            // A thinking model that spent the whole budget reasoning returns no content at
            // all, so a missing field must still report truncation and trigger a retry.
            var choice = choices[0];
            var text = choice.TryGetProperty("message", out var message) &&
                       message.TryGetProperty("content", out var content) &&
                       content.ValueKind == JsonValueKind.String
                ? content.GetString()
                : null;

            return new VisionApiResult(
                OcrTextSanitizer.StripThinkingMarkup(text),
                IsChatCompletionTruncated(root, choice, maxTokens));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CustomApiOcr] Failed to parse response: {ex.Message}");
            return new VisionApiResult(string.Empty, Truncated: false);
        }
    }

    /// <summary>
    /// Whether the model stopped because it ran out of output tokens. Providers that omit
    /// <c>finish_reason</c> are covered by comparing reported usage against the budget.
    /// </summary>
    private static bool IsChatCompletionTruncated(
        JsonElement root,
        JsonElement choice,
        int maxTokens)
    {
        if (choice.TryGetProperty("finish_reason", out var finishReason) &&
            finishReason.ValueKind == JsonValueKind.String)
        {
            var reason = finishReason.GetString();

            return string.Equals(reason, "length", StringComparison.OrdinalIgnoreCase)
                || string.Equals(reason, "max_tokens", StringComparison.OrdinalIgnoreCase);
        }

        return root.TryGetProperty("usage", out var usage) &&
               usage.TryGetProperty("completion_tokens", out var completionTokens) &&
               completionTokens.ValueKind == JsonValueKind.Number &&
               completionTokens.TryGetInt32(out var used) &&
               used >= maxTokens;
    }

    private static VisionApiResult ParseResponsesTextResponse(string json, int maxTokens)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var truncated = IsResponsesTruncated(root, maxTokens);

            if (root.TryGetProperty("output_text", out var outputText))
            {
                return new VisionApiResult(
                    OcrTextSanitizer.StripThinkingMarkup(outputText.GetString()),
                    truncated);
            }

            if (!root.TryGetProperty("output", out var output) ||
                output.ValueKind != JsonValueKind.Array)
            {
                return new VisionApiResult(string.Empty, truncated);
            }

            var text = new StringBuilder();
            foreach (var outputItem in output.EnumerateArray())
            {
                if (!outputItem.TryGetProperty("content", out var content) ||
                    content.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var contentItem in content.EnumerateArray())
                {
                    if (contentItem.TryGetProperty("text", out var textElement))
                    {
                        text.Append(textElement.GetString());
                    }
                }
            }

            return new VisionApiResult(
                OcrTextSanitizer.StripThinkingMarkup(text.ToString()),
                truncated);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CustomApiOcr] Failed to parse Responses response: {ex.Message}");
            return new VisionApiResult(string.Empty, Truncated: false);
        }
    }

    /// <summary>
    /// The Responses API reports a cut-off generation as
    /// { "status": "incomplete", "incomplete_details": { "reason": "max_output_tokens" } }.
    /// </summary>
    private static bool IsResponsesTruncated(JsonElement root, int maxTokens)
    {
        // When the provider reports a status, trust it rather than second-guessing from usage.
        if (root.TryGetProperty("status", out var status) &&
            status.ValueKind == JsonValueKind.String)
        {
            if (!string.Equals(status.GetString(), "incomplete", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return !root.TryGetProperty("incomplete_details", out var details) ||
                   !details.TryGetProperty("reason", out var reason) ||
                   reason.ValueKind != JsonValueKind.String ||
                   string.Equals(reason.GetString(), "max_output_tokens", StringComparison.OrdinalIgnoreCase);
        }

        return root.TryGetProperty("usage", out var usage) &&
               usage.TryGetProperty("output_tokens", out var outputTokens) &&
               outputTokens.ValueKind == JsonValueKind.Number &&
               outputTokens.TryGetInt32(out var used) &&
               used >= maxTokens;
    }

    /// <summary>
    /// One completion attempt: the recognized text plus whether it was cut short by the
    /// token budget.
    /// </summary>
    private readonly record struct VisionApiResult(string Text, bool Truncated);

    private static bool UsesResponsesEndpoint(string endpoint)
    {
        return Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) &&
               uri.AbsolutePath.TrimEnd('/').EndsWith("/responses", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Convert BGRA8 pixel data to a base64-encoded JPEG string.
    /// Uses Windows.Graphics.Imaging for high-quality encoding.
    /// </summary>
    private static async Task<string> ConvertBgraToBase64JpegAsync(ReadOnlyMemory<byte> pixelData, int width, int height)
    {
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, stream);

        byte[]? temporaryPixels = null;
        try
        {
            var pixels = PixelMemory.ToArrayForInterop(pixelData, out var offset, out var length);
            if (offset != 0 || length != pixels.Length)
            {
                temporaryPixels = pixelData.ToArray();
                pixels = temporaryPixels;
            }

            encoder.SetPixelData(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                (uint)width,
                (uint)height,
                96,
                96,
                pixels);
        }
        finally
        {
            if (temporaryPixels is not null)
            {
                Array.Clear(temporaryPixels);
            }
        }

        await encoder.FlushAsync();

        // Convert WinRT stream to Base64
        var streamSize = stream.Size;
        if (streamSize > int.MaxValue)
        {
            throw new InvalidOperationException("Encoded image is too large to convert to Base64.");
        }

        var size = (int)streamSize;
        stream.Seek(0);

        var bytes = ArrayPool<byte>.Shared.Rent(size);
        try
        {
            await stream.ReadAsync(bytes.AsBuffer(0, size), (uint)size, InputStreamOptions.None);
            return Convert.ToBase64String(bytes, 0, size);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bytes, clearArray: true);
        }
    }
}
