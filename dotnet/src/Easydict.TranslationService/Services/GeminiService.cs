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
    /// System prompt for translation.
    /// </summary>
    private const string TranslationSystemPrompt = """
        You are a translation expert proficient in various languages, focusing solely on translating text without interpretation. You accurately understand the meanings of proper nouns, idioms, metaphors, allusions, and other obscure words in sentences, translating them appropriately based on the context and language environment. The translation should be natural and fluent. Only return the translated text, without including redundant quotes or additional notes.
        """;

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

    /// <summary>
    /// Languages supported by Gemini.
    /// </summary>
    private static readonly IReadOnlyList<Language> _geminiLanguages = new[]
    {
        Language.SimplifiedChinese,
        Language.TraditionalChinese,
        Language.English,
        Language.Japanese,
        Language.Korean,
        Language.French,
        Language.Spanish,
        Language.Portuguese,
        Language.Italian,
        Language.German,
        Language.Russian,
        Language.Arabic,
        Language.Dutch,
        Language.Polish,
        Language.Vietnamese,
        Language.Thai,
        Language.Indonesian,
        Language.Turkish,
        Language.Swedish,
        Language.Danish,
        Language.Norwegian,
        Language.Finnish,
        Language.Greek,
        Language.Czech,
        Language.Romanian,
        Language.Hungarian,
        Language.Ukrainian,
        Language.Hebrew,
        Language.Hindi,
        Language.Bengali,
        Language.Tamil,
        Language.Persian
    };

    private string _apiKey = "";
    private string _model = DefaultModel;
    private double _temperature = 0.3;

    public GeminiService(HttpClient httpClient) : base(httpClient) { }

    public override string ServiceId => "gemini";
    public override string DisplayName => "Gemini";
    public override bool RequiresApiKey => true;
    public override bool IsConfigured => !string.IsNullOrEmpty(_apiKey);
    public override IReadOnlyList<Language> SupportedLanguages => _geminiLanguages;

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
        var sb = new StringBuilder();

        await foreach (var chunk in TranslateStreamAsync(request, cancellationToken))
        {
            sb.Append(chunk);
        }

        var translatedText = CleanupResult(sb.ToString());

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
                parts = new[] { new { text = TranslationSystemPrompt } }
            },
            generationConfig = new
            {
                temperature = _temperature
            }
        };

        // Gemini uses API key as query parameter, not header
        var endpoint = $"{BaseUrl}/models/{_model}:streamGenerateContent?key={_apiKey}";

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
        var buffer = new StringBuilder();

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
                    candidates.GetArrayLength() > 0)
                {
                    var firstCandidate = candidates[0];
                    if (firstCandidate.TryGetProperty("content", out var content) &&
                        content.TryGetProperty("parts", out var parts) &&
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

    private TranslationException CreateErrorFromResponse(System.Net.HttpStatusCode statusCode, string errorBody)
    {
        var errorCode = statusCode switch
        {
            System.Net.HttpStatusCode.Unauthorized => TranslationErrorCode.InvalidApiKey,
            System.Net.HttpStatusCode.Forbidden => TranslationErrorCode.InvalidApiKey,
            System.Net.HttpStatusCode.TooManyRequests => TranslationErrorCode.RateLimited,
            System.Net.HttpStatusCode.BadRequest => TranslationErrorCode.InvalidResponse,
            System.Net.HttpStatusCode.InternalServerError => TranslationErrorCode.ServiceUnavailable,
            System.Net.HttpStatusCode.ServiceUnavailable => TranslationErrorCode.ServiceUnavailable,
            System.Net.HttpStatusCode.GatewayTimeout => TranslationErrorCode.Timeout,
            _ => TranslationErrorCode.Unknown
        };

        var message = $"API error ({(int)statusCode}): {statusCode}";
        try
        {
            using var doc = JsonDocument.Parse(errorBody);
            if (doc.RootElement.TryGetProperty("error", out var error))
            {
                if (error.TryGetProperty("message", out var msgElement))
                {
                    message = msgElement.GetString() ?? message;
                }
            }
        }
        catch (JsonException)
        {
            // Use default message
        }

        return new TranslationException(message)
        {
            ErrorCode = errorCode,
            ServiceId = ServiceId
        };
    }

    private static string CleanupResult(string text)
    {
        var result = text.Trim();

        // Remove surrounding quotes if present
        if (result.Length >= 2 &&
            result.StartsWith('"') && result.EndsWith('"'))
        {
            result = result[1..^1].Trim();
        }

        return result;
    }
}
