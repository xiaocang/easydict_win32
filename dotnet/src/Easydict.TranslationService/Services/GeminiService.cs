using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Easydict.TranslationService.Models;

namespace Easydict.TranslationService.Services;

/// <summary>
/// Google Gemini translation service.
/// Uses the Gemini API with different protocol than OpenAI-compatible services.
/// </summary>
public sealed class GeminiService : BaseTranslationService, IStreamTranslationService
{
    private const string DefaultModel = "gemini-2.5-flash";
    private const string BaseUrl = "https://generativelanguage.googleapis.com/v1beta";

    /// <summary>
    /// Available Gemini models.
    /// </summary>
    public static readonly string[] AvailableModels = new[]
    {
        "gemini-2.5-flash",
        "gemini-2.5-flash-lite",
        "gemini-2.5-pro",
        "gemini-2.0-flash",
        "gemini-1.5-flash",
        "gemini-1.5-pro",
        "gemini-3-flash-preview",
        "gemini-3-pro-preview"
    };

    private string _apiKey = "";
    private string _model = DefaultModel;
    private double _temperature = 0.3;

    public GeminiService(HttpClient httpClient) : base(httpClient) { }

    public override string ServiceId => "gemini";
    public override string DisplayName => "Gemini";
    public override bool RequiresApiKey => true;
    public override bool IsConfigured => !string.IsNullOrEmpty(_apiKey);
    public override IReadOnlyList<Language> SupportedLanguages => BaseOpenAIService.OpenAILanguages;

    /// <summary>
    /// This is a streaming service.
    /// </summary>
    public bool IsStreaming => true;

    /// <summary>
    /// Configure the Gemini service with API credentials and options.
    /// </summary>
    /// <param name="apiKey">Google AI API key.</param>
    /// <param name="model">Optional model name.</param>
    /// <param name="temperature">Optional temperature (0.0-2.0).</param>
    public void Configure(string apiKey, string? model = null, double? temperature = null)
    {
        _apiKey = apiKey ?? "";
        if (!string.IsNullOrEmpty(model)) _model = model;
        if (temperature.HasValue) _temperature = Math.Clamp(temperature.Value, 0.0, 2.0);
    }

    /// <summary>
    /// Implement non-streaming translation by consuming the stream.
    /// </summary>
    protected override async Task<TranslationResult> TranslateInternalAsync(
        TranslationRequest request,
        CancellationToken cancellationToken = default)
    {
        var translatedText = CleanupResult(
            await ConsumeStreamAsync(TranslateStreamAsync(request, cancellationToken), cancellationToken));

        return new TranslationResult
        {
            TranslatedText = translatedText,
            OriginalText = request.Text,
            DetectedLanguage = request.FromLanguage,
            TargetLanguage = request.ToLanguage,
            ServiceName = DisplayName
        };
    }

    /// <summary>
    /// Stream translate text using Gemini API.
    /// </summary>
    public async IAsyncEnumerable<string> TranslateStreamAsync(
        TranslationRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ValidateConfiguration();

        var sourceLangName = request.FromLanguage == Language.Auto
            ? "the detected language"
            : request.FromLanguage.GetDisplayName();
        var targetLangName = request.ToLanguage.GetDisplayName();

        var userPrompt = $"Translate the following {sourceLangName} text into {targetLangName} text: \"\"\"{request.Text}\"\"\"";

        // Build Gemini-specific request body
        var requestBody = new
        {
            contents = new[]
            {
                new
                {
                    role = "user",
                    parts = new[] { new { text = userPrompt } }
                }
            },
            systemInstruction = new
            {
                parts = new[] { new { text = BaseOpenAIService.TranslationSystemPrompt } }
            },
            generationConfig = new
            {
                temperature = _temperature
            }
        };

        // Gemini uses API key as query parameter, not header
        // alt=sse enables Server-Sent Events format for proper streaming
        var endpoint = $"{BaseUrl}/models/{_model}:streamGenerateContent?alt=sse&key={_apiKey}";

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint);
        httpRequest.Content = new StringContent(
            JsonSerializer.Serialize(requestBody),
            Encoding.UTF8,
            "application/json");

        HttpResponseMessage response;
        try
        {
            response = await HttpClient.SendAsync(
                httpRequest,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new TranslationException($"Network error: {ex.Message}", ex)
            {
                ErrorCode = TranslationErrorCode.NetworkError,
                ServiceId = ServiceId
            };
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                throw CreateErrorFromResponse(response.StatusCode, errorBody);
            }

            var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            await foreach (var chunk in ParseGeminiStreamAsync(stream, cancellationToken))
            {
                yield return chunk;
            }
        }
    }

    /// <summary>
    /// Parse Gemini streaming response.
    /// Gemini uses a different SSE format than OpenAI.
    /// </summary>
    private static async IAsyncEnumerable<string> ParseGeminiStreamAsync(
        Stream stream,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream);


        while (!reader.EndOfStream)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line == null) break;

            // Gemini streams JSON objects, sometimes prefixed with "data: "
            if (line.StartsWith("data: "))
            {
                line = line[6..];
            }

            if (string.IsNullOrWhiteSpace(line)) continue;

            // Skip [DONE] marker if present
            if (line == "[DONE]") break;

            // Try to parse as JSON
            string? text = null;
            try
            {
                using var doc = JsonDocument.Parse(line);

                // Gemini response format: candidates[0].content.parts[0].text
                if (doc.RootElement.TryGetProperty("candidates", out var candidates) &&
                    candidates.ValueKind == JsonValueKind.Array &&
                    candidates.GetArrayLength() > 0)
                {
                    var firstCandidate = candidates[0];
                    if (firstCandidate.TryGetProperty("content", out var content) &&
                        content.TryGetProperty("parts", out var parts) &&
                        parts.ValueKind == JsonValueKind.Array &&
                        parts.GetArrayLength() > 0)
                    {
                        var firstPart = parts[0];
                        if (firstPart.TryGetProperty("text", out var textElement))
                        {
                            text = textElement.GetString();
                        }
                    }
                }
            }
            catch (JsonException)
            {
                // Not valid JSON, skip
                continue;
            }

            if (!string.IsNullOrEmpty(text))
            {
                yield return text;
            }
        }
    }

    private void ValidateConfiguration()
    {
        if (string.IsNullOrEmpty(_apiKey))
        {
            throw new TranslationException("API key is required but not configured")
            {
                ErrorCode = TranslationErrorCode.InvalidApiKey,
                ServiceId = ServiceId
            };
        }
    }

}
