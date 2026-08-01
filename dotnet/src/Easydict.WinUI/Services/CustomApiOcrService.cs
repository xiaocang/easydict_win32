using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Easydict.TranslationService.Services;
using Easydict.WinUI.Models;

namespace Easydict.WinUI.Services;

/// <summary>
/// OCR service using OpenAI Chat Completions or Responses-compatible vision APIs.
/// </summary>
public sealed class CustomApiOcrService : IOcrService
{
    private const string ThinkingControlField = "thinking";
    private const string ReasoningControlField = "reasoning";
    private const string ReasoningEffortControlField = "reasoning_effort";

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

        var base64Image = await OcrImageEncoder.ToBase64PngAsync(pixelData, pixelWidth, pixelHeight);
        var usesResponses = UsesResponsesEndpoint(endpoint);

        // Start small — a screenshot rarely needs more — and grow only when the provider
        // reports the response was cut off. Each attempt is a fresh completion, so a
        // completed response wins; the longest partial is retained only at the ceiling.
        var maxTokens = _options.GetInitialMaxTokens();
        var recognizedText = string.Empty;

        while (true)
        {
            var attempt = await SendAttemptAsync(base64Image, usesResponses, maxTokens, cancellationToken);

            if (attempt.IsValid && !attempt.Truncated)
            {
                recognizedText = attempt.Text;
                break;
            }

            if (attempt.Text.Length > recognizedText.Length)
            {
                recognizedText = attempt.Text;
            }

            if (!attempt.Truncated)
            {
                break;
            }

            if (maxTokens >= OcrServiceOptions.MaxTokensCeiling)
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
        var disableThinkingControlField = GetDisableThinkingControlField(usesResponses);

        while (true)
        {
            var (statusCode, body) = await PostRequestAsync(
                base64Image, usesResponses, maxTokens, disableThinkingControlField, cancellationToken);

            if ((int)statusCode is >= 200 and < 300)
            {
                var emptyResponseRetryCeiling = _options.EnableThinking
                    ? OcrServiceOptions.MaxTokensCeiling
                    : OcrServiceOptions.ThinkingMaxTokens;
                return usesResponses
                    ? ParseResponsesTextResponse(body, maxTokens, emptyResponseRetryCeiling)
                    : ParseOpenAIVisionResponse(body, maxTokens, emptyResponseRetryCeiling);
            }

            if (disableThinkingControlField is not null &&
                OcrThinkingSupport.IsThinkingFieldRejection(
                    statusCode,
                    body,
                    disableThinkingControlField))
            {
                OcrThinkingSupport.MarkRejectedBy(_options.Endpoint, _options.Model);
                Debug.WriteLine(
                    $"[CustomApiOcr] {_options.Model} rejected {disableThinkingControlField}, retrying without it");
                disableThinkingControlField = null;
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
        string? disableThinkingControlField,
        CancellationToken cancellationToken)
    {
        var requestBody = usesResponses
            ? BuildResponsesRequestBody(_options.Model, _options.SystemPrompt, base64Image, maxTokens, disableThinkingControlField)
            : BuildChatCompletionsRequestBody(_options.Model, _options.SystemPrompt, base64Image, maxTokens, disableThinkingControlField);

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
            // Read before checking status: a rejected thinking control is only identifiable
            // from the error body.
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            return (response.StatusCode, body);
        }
    }

    /// <summary>
    /// The exact request field used to ask the model not to think, or null when no compatible
    /// control should be sent.
    /// </summary>
    private string? GetDisableThinkingControlField(bool usesResponses)
    {
        if (_options.EnableThinking ||
            OcrThinkingSupport.IsRejectedBy(_options.Endpoint, _options.Model))
        {
            return null;
        }

        var hasReasoningEffort =
            OpenAIService.GetResponsesReasoningEffort(_options.Model) is not null;

        if (usesResponses)
        {
            return hasReasoningEffort ? ReasoningControlField : null;
        }

        return hasReasoningEffort ? ReasoningEffortControlField : ThinkingControlField;
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
        string? disableThinkingControlField)
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
                            image_url = new { url = OcrImageEncoder.ToDataUrl(base64Image) }
                        }
                    }
                }
            }
        };

        if (disableThinkingControlField == ReasoningEffortControlField &&
            OpenAIService.GetResponsesReasoningEffort(model) is { } reasoningEffort)
        {
            body[ReasoningEffortControlField] = reasoningEffort;
        }
        else if (disableThinkingControlField == ThinkingControlField)
        {
            body[ThinkingControlField] = new { type = "disabled" };
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
        string? disableThinkingControlField)
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
                        new { type = "input_image", image_url = OcrImageEncoder.ToDataUrl(base64Image) }
                    }
                }
            }
        };

        if (disableThinkingControlField == ReasoningControlField &&
            OpenAIService.GetResponsesReasoningEffort(model) is { } reasoningEffort)
        {
            body[ReasoningControlField] = new { effort = reasoningEffort };
        }

        return body;
    }

    /// <summary>
    /// Extracts text from an OpenAI Vision-compatible JSON response.
    /// Response format: { "choices": [{ "message": { "content": "..." }, "finish_reason": "stop" }] }
    /// </summary>
    private static VisionApiResult ParseOpenAIVisionResponse(
        string json,
        int maxTokens,
        int emptyResponseRetryCeiling)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("choices", out var choices) ||
                choices.ValueKind != JsonValueKind.Array ||
                choices.GetArrayLength() == 0)
            {
                return new VisionApiResult(string.Empty, Truncated: false, IsValid: false);
            }

            // A thinking model that spent the whole budget reasoning returns no content at
            // all, so a missing field must still report truncation and trigger a retry.
            var choice = choices[0];
            var text = choice.TryGetProperty("message", out var message) &&
                       message.TryGetProperty("content", out var content) &&
                       content.ValueKind == JsonValueKind.String
                ? content.GetString()?.Trim() ?? string.Empty
                : string.Empty;
            var hasFinishReason =
                choice.TryGetProperty("finish_reason", out var finishReason) &&
                finishReason.ValueKind == JsonValueKind.String;
            var isCompleted = hasFinishReason &&
                string.Equals(finishReason.GetString(), "stop", StringComparison.OrdinalIgnoreCase);

            return new VisionApiResult(
                text,
                IsChatCompletionTruncated(
                    root, choice, maxTokens, text.Length > 0, emptyResponseRetryCeiling),
                isCompleted || (!hasFinishReason && text.Length > 0));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CustomApiOcr] Failed to parse response: {ex.Message}");
            return new VisionApiResult(string.Empty, Truncated: false, IsValid: false);
        }
    }

    /// <summary>
    /// Whether the model stopped because it ran out of output tokens. Metadata-less
    /// responses with content are retried to the former 2048-token budget; when thinking is
    /// enabled, empty responses are retried to the ceiling because reasoning may consume it.
    /// </summary>
    private static bool IsChatCompletionTruncated(
        JsonElement root,
        JsonElement choice,
        int maxTokens,
        bool hasText,
        int emptyResponseRetryCeiling)
    {
        if (choice.TryGetProperty("finish_reason", out var finishReason) &&
            finishReason.ValueKind == JsonValueKind.String)
        {
            var reason = finishReason.GetString();

            return string.Equals(reason, "length", StringComparison.OrdinalIgnoreCase)
                || string.Equals(reason, "max_tokens", StringComparison.OrdinalIgnoreCase);
        }

        if (root.TryGetProperty("usage", out var usage) &&
            usage.TryGetProperty("completion_tokens", out var completionTokens) &&
            completionTokens.ValueKind == JsonValueKind.Number &&
            completionTokens.TryGetInt32(out var used))
        {
            return used >= maxTokens;
        }

        return maxTokens < (hasText
            ? OcrServiceOptions.ThinkingMaxTokens
            : emptyResponseRetryCeiling);
    }

    private static VisionApiResult ParseResponsesTextResponse(
        string json,
        int maxTokens,
        int emptyResponseRetryCeiling)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var hasStatus =
                root.TryGetProperty("status", out var status) &&
                status.ValueKind == JsonValueKind.String;
            var isCompleted = hasStatus &&
                string.Equals(status.GetString(), "completed", StringComparison.OrdinalIgnoreCase);

            if (root.TryGetProperty("output_text", out var outputText))
            {
                var recognizedText = outputText.GetString()?.Trim() ?? string.Empty;
                return new VisionApiResult(
                    recognizedText,
                    IsResponsesTruncated(
                        root, maxTokens, recognizedText.Length > 0, emptyResponseRetryCeiling),
                    isCompleted || (!hasStatus && recognizedText.Length > 0));
            }

            if (!root.TryGetProperty("output", out var output) ||
                output.ValueKind != JsonValueKind.Array)
            {
                return new VisionApiResult(
                    string.Empty,
                    IsResponsesTruncated(
                        root, maxTokens, hasText: false, emptyResponseRetryCeiling),
                    isCompleted);
            }

            var textBuilder = new StringBuilder();
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
                        textBuilder.Append(textElement.GetString());
                    }
                }
            }

            var text = textBuilder.ToString().Trim();
            return new VisionApiResult(
                text,
                IsResponsesTruncated(
                    root, maxTokens, text.Length > 0, emptyResponseRetryCeiling),
                isCompleted || (!hasStatus && text.Length > 0));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CustomApiOcr] Failed to parse Responses response: {ex.Message}");
            return new VisionApiResult(string.Empty, Truncated: false, IsValid: false);
        }
    }

    /// <summary>
    /// The Responses API reports a cut-off generation as
    /// { "status": "incomplete", "incomplete_details": { "reason": "max_output_tokens" } }.
    /// </summary>
    private static bool IsResponsesTruncated(
        JsonElement root,
        int maxTokens,
        bool hasText,
        int emptyResponseRetryCeiling)
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

        if (root.TryGetProperty("usage", out var usage) &&
            usage.TryGetProperty("output_tokens", out var outputTokens) &&
            outputTokens.ValueKind == JsonValueKind.Number &&
            outputTokens.TryGetInt32(out var used))
        {
            return used >= maxTokens;
        }

        return maxTokens < (hasText
            ? OcrServiceOptions.ThinkingMaxTokens
            : emptyResponseRetryCeiling);
    }

    /// <summary>
    /// One completion attempt: its text, whether the token budget cut it short, and whether
    /// the response contained enough completion data to supersede an earlier partial.
    /// </summary>
    private readonly record struct VisionApiResult(string Text, bool Truncated, bool IsValid);

    private static bool UsesResponsesEndpoint(string endpoint)
    {
        return Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) &&
               uri.AbsolutePath.TrimEnd('/').EndsWith("/responses", StringComparison.OrdinalIgnoreCase);
    }
}
