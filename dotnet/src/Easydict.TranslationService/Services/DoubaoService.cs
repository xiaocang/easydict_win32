using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Easydict.TranslationService.Models;

namespace Easydict.TranslationService.Services;

/// <summary>
/// Doubao (豆包) translation service using Doubao's translation-specific API.
/// ByteDance's specialized translation model with streaming support.
/// </summary>
public sealed class DoubaoService : BaseTranslationService, IStreamTranslationService
{
    private const string DefaultEndpoint = "https://ark.cn-beijing.volces.com/api/v3/responses";
    private const string DefaultModel = "doubao-seed-translation-250915";

    /// <summary>
    /// Available Doubao translation models.
    /// </summary>
    public static readonly string[] AvailableModels = new[]
    {
        "doubao-seed-translation-250915"
    };

    /// <summary>
    /// Languages supported by Doubao translation.
    /// </summary>
    private static readonly IReadOnlyList<Language> DoubaoLanguages = new[]
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
        Language.Turkish,
        Language.Swedish,
        Language.Indonesian,
        Language.Vietnamese,
        Language.Thai,
        Language.Hindi
    };

    private string _endpoint = DefaultEndpoint;
    private string _apiKey = "";
    private string _model = DefaultModel;

    public DoubaoService(HttpClient httpClient) : base(httpClient)
    {
    }

    public override string ServiceId => "doubao";
    public override string DisplayName => "Doubao";
    public override bool RequiresApiKey => true;
    public override bool IsConfigured => !string.IsNullOrEmpty(_apiKey);
    public override IReadOnlyList<Language> SupportedLanguages => DoubaoLanguages;
    public bool IsStreaming => true;

    /// <summary>
    /// Configure the Doubao service with API credentials and options.
    /// </summary>
    /// <param name="apiKey">Doubao API key.</param>
    /// <param name="endpoint">Optional custom endpoint URL.</param>
    /// <param name="model">Optional model name.</param>
    public void Configure(string apiKey, string? endpoint = null, string? model = null)
    {
        _apiKey = apiKey ?? "";
        if (!string.IsNullOrEmpty(endpoint)) _endpoint = endpoint;
        if (!string.IsNullOrEmpty(model)) _model = model;
    }

    protected override async Task<TranslationResult> TranslateInternalAsync(
        TranslationRequest request,
        CancellationToken cancellationToken = default)
    {
        var chunks = new List<string>();
        await foreach (var chunk in TranslateStreamAsync(request, cancellationToken))
        {
            chunks.Add(chunk);
        }

        var translatedText = string.Join("", chunks).Trim();
        translatedText = RemoveSurroundingQuotes(translatedText);

        return new TranslationResult
        {
            TranslatedText = translatedText,
            OriginalText = request.Text,
            DetectedLanguage = request.FromLanguage,
            TargetLanguage = request.ToLanguage,
            ServiceName = DisplayName
        };
    }

    public async IAsyncEnumerable<string> TranslateStreamAsync(
        TranslationRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            throw new TranslationException("Service not configured. Please provide an API key.")
            {
                ErrorCode = TranslationErrorCode.InvalidApiKey,
                ServiceId = ServiceId
            };
        }

        var sourceLanguage = GetDoubaoLanguageCode(request.FromLanguage);
        var targetLanguage = GetDoubaoLanguageCode(request.ToLanguage);

        // Build Doubao translation API request
        var requestBody = new
        {
            model = _model,
            stream = true,
            input = new[]
            {
                new
                {
                    role = "user",
                    content = new[]
                    {
                        new
                        {
                            type = "input_text",
                            text = request.Text,
                            translation_options = new
                            {
                                source_language = sourceLanguage,
                                target_language = targetLanguage
                            }
                        }
                    }
                }
            }
        };

        var json = JsonSerializer.Serialize(requestBody);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, _endpoint);
        httpRequest.Content = content;
        httpRequest.Headers.Add("Authorization", $"Bearer {_apiKey}");

        HttpResponseMessage response;
        try
        {
            response = await HttpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (Exception ex)
        {
            throw new TranslationException($"Network error: {ex.Message}", ex)
            {
                ErrorCode = TranslationErrorCode.NetworkError,
                ServiceId = ServiceId
            };
        }

        using (response)
        {
            // Handle error status codes
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                throw response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized => new TranslationException("Invalid API key or authentication failed.")
                    {
                        ErrorCode = TranslationErrorCode.InvalidApiKey,
                        ServiceId = ServiceId
                    },
                    HttpStatusCode.TooManyRequests => new TranslationException("Rate limit exceeded. Please try again later.")
                    {
                        ErrorCode = TranslationErrorCode.RateLimited,
                        ServiceId = ServiceId
                    },
                    HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable => new TranslationException($"Server error: {response.StatusCode}")
                    {
                        ErrorCode = TranslationErrorCode.ServiceUnavailable,
                        ServiceId = ServiceId
                    },
                    _ => new TranslationException($"API request failed with status {response.StatusCode}: {errorBody}")
                    {
                        ErrorCode = TranslationErrorCode.InvalidResponse,
                        ServiceId = ServiceId
                    }
                };
            }

            // Parse SSE stream
            var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            await foreach (var chunk in ParseDoubaoStreamAsync(stream, cancellationToken))
            {
                yield return chunk;
            }
        }
    }

    /// <summary>
    /// Parse Doubao's SSE stream format.
    /// Doubao uses named events: "event: response.output_text.delta" followed by "data: {...}"
    /// </summary>
    private static async IAsyncEnumerable<string> ParseDoubaoStreamAsync(
        Stream stream,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var reader = new StreamReader(stream);
        string? currentEvent = null;

        while (!reader.EndOfStream && !cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (string.IsNullOrEmpty(line))
            {
                currentEvent = null;
                continue;
            }

            // Parse event line
            if (line.StartsWith("event: "))
            {
                currentEvent = line[7..].Trim();
                continue;
            }

            // Parse data line
            if (line.StartsWith("data: "))
            {
                var data = line[6..].Trim();

                // Check for stream end
                if (data == "[DONE]")
                {
                    break;
                }

                // Only process delta events
                if (currentEvent == "response.output_text.delta")
                {
                    // Try to parse the delta field, skip on error
                    var delta = TryParseDelta(data);
                    if (!string.IsNullOrEmpty(delta))
                    {
                        yield return delta;
                    }
                }

                currentEvent = null;
            }
        }
    }

    /// <summary>
    /// Try to parse delta field from JSON data.
    /// Returns null if parsing fails.
    /// </summary>
    private static string? TryParseDelta(string data)
    {
        try
        {
            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;

            // Extract delta field
            if (root.TryGetProperty("delta", out var deltaElement))
            {
                return deltaElement.GetString();
            }
        }
        catch (JsonException)
        {
            // Skip malformed JSON
        }

        return null;
    }

    /// <summary>
    /// Map Language enum to Doubao language code.
    /// </summary>
    private static string GetDoubaoLanguageCode(Language language) => language switch
    {
        Language.Auto => "auto",
        Language.SimplifiedChinese => "zh",
        Language.TraditionalChinese => "zh-Hant",
        Language.English => "en",
        Language.Japanese => "ja",
        Language.Korean => "ko",
        Language.French => "fr",
        Language.Spanish => "es",
        Language.Portuguese => "pt",
        Language.Italian => "it",
        Language.German => "de",
        Language.Russian => "ru",
        Language.Arabic => "ar",
        Language.Dutch => "nl",
        Language.Polish => "pl",
        Language.Turkish => "tr",
        Language.Swedish => "sv",
        Language.Indonesian => "id",
        Language.Vietnamese => "vi",
        Language.Thai => "th",
        Language.Hindi => "hi",
        _ => language.ToIso639()
    };

    /// <summary>
    /// Remove surrounding quotes from translated text if present.
    /// </summary>
    private static string RemoveSurroundingQuotes(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        var trimmed = text.Trim();
        if (trimmed.Length >= 2 &&
            ((trimmed[0] == '"' && trimmed[^1] == '"') ||
             (trimmed[0] == '\'' && trimmed[^1] == '\'')))
        {
            return trimmed[1..^1];
        }

        return text;
    }
}
