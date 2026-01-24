using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Easydict.TranslationService.Models;

namespace Easydict.TranslationService.Services;

/// <summary>
/// DeepL translation service supporting both free web translation and official API.
/// Web translation uses JSON-RPC (no API key required).
/// Official API requires API key (free or pro).
/// </summary>
public sealed class DeepLService : BaseTranslationService
{
    // Official API endpoints
    private const string FreeApiHost = "https://api-free.deepl.com";
    private const string ProApiHost = "https://api.deepl.com";

    // Web translation endpoint (JSON-RPC, no API key required)
    private const string WebEndpoint = "https://www2.deepl.com/jsonrpc";

    private string? _apiKey;
    private bool _useWebFirst = true; // Default to web translation (no API key needed)

    private static readonly IReadOnlyList<Language> DeepLLanguages =
    [
        Language.SimplifiedChinese, Language.TraditionalChinese, Language.English, Language.Japanese,
        Language.Korean, Language.French, Language.Spanish, Language.Portuguese,
        Language.Italian, Language.German, Language.Russian, Language.Dutch,
        Language.Polish, Language.Bulgarian, Language.Czech, Language.Danish,
        Language.Estonian, Language.Finnish, Language.Greek, Language.Hungarian,
        Language.Indonesian, Language.Latvian, Language.Lithuanian, Language.Norwegian,
        Language.Romanian, Language.Slovak, Language.Slovenian, Language.Swedish,
        Language.Turkish, Language.Ukrainian
    ];

    public DeepLService(HttpClient httpClient) : base(httpClient)
    {
    }

    public override string ServiceId => "deepl";
    public override string DisplayName => "DeepL";
    public override bool RequiresApiKey => false; // Web mode doesn't require API key
    public override bool IsConfigured => true; // Web mode is always available
    public override IReadOnlyList<Language> SupportedLanguages => DeepLLanguages;

    /// <summary>
    /// Configure the service with an API key and mode.
    /// </summary>
    /// <param name="apiKey">Optional API key for official API access.</param>
    /// <param name="useWebFirst">If true, try web translation first (default). If false, use API only.</param>
    public void Configure(string? apiKey, bool useWebFirst = true)
    {
        _apiKey = apiKey;
        _useWebFirst = useWebFirst;
    }

    protected override async Task<TranslationResult> TranslateInternalAsync(
        TranslationRequest request,
        CancellationToken cancellationToken)
    {
        // Try web translation first if enabled
        if (_useWebFirst)
        {
            try
            {
                return await TranslateWebAsync(request, cancellationToken);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DeepL] Web translation failed: {ex.Message}");

                // Fall back to official API if key is provided
                if (!string.IsNullOrEmpty(_apiKey))
                {
                    System.Diagnostics.Debug.WriteLine("[DeepL] Falling back to official API");
                    return await TranslateApiAsync(request, cancellationToken);
                }
                throw;
            }
        }

        // Use official API directly
        return await TranslateApiAsync(request, cancellationToken);
    }

    /// <summary>
    /// Translate using DeepL's official API (requires API key).
    /// </summary>
    private async Task<TranslationResult> TranslateApiAsync(
        TranslationRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(_apiKey))
        {
            throw new TranslationException("DeepL API key is required for API mode")
            {
                ErrorCode = TranslationErrorCode.InvalidApiKey,
                ServiceId = ServiceId
            };
        }

        var host = GetApiHost();
        var url = $"{host}/v2/translate";

        var targetCode = GetDeepLLanguageCode(request.ToLanguage);
        var sourceCode = request.FromLanguage == Language.Auto
            ? null
            : GetDeepLLanguageCode(request.FromLanguage);

        var formData = new List<KeyValuePair<string, string>>
        {
            new("text", request.Text),
            new("target_lang", targetCode)
        };

        if (sourceCode != null)
        {
            formData.Add(new("source_lang", sourceCode));
        }

        using var content = new FormUrlEncodedContent(formData);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url);
        httpRequest.Content = content;
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("DeepL-Auth-Key", _apiKey);

        using var response = await HttpClient.SendAsync(httpRequest, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var errorCode = response.StatusCode switch
            {
                HttpStatusCode.Forbidden => TranslationErrorCode.InvalidApiKey,
                HttpStatusCode.TooManyRequests => TranslationErrorCode.RateLimited,
                (HttpStatusCode)456 => TranslationErrorCode.RateLimited, // DeepL quota exceeded
                _ => TranslationErrorCode.ServiceUnavailable
            };

            throw new TranslationException($"DeepL API error: {response.StatusCode}")
            {
                ErrorCode = errorCode,
                ServiceId = ServiceId
            };
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return ParseApiResponse(json, request);
    }

    /// <summary>
    /// Translate using DeepL's free web interface via JSON-RPC (no API key required).
    /// Implements anti-detection measures similar to the macOS implementation.
    /// </summary>
    private async Task<TranslationResult> TranslateWebAsync(
        TranslationRequest request,
        CancellationToken cancellationToken)
    {
        var targetCode = GetDeepLWebLanguageCode(request.ToLanguage);
        var sourceCode = request.FromLanguage == Language.Auto
            ? "auto"
            : GetDeepLWebLanguageCode(request.FromLanguage);

        // Generate anti-detection values (matching macOS implementation)
        var requestId = GetRandomRequestId();
        var iCount = GetICount(request.Text);
        var timestamp = GetAlignedTimestamp(iCount);

        // Build JSON-RPC payload
        var payload = new
        {
            jsonrpc = "2.0",
            method = "LMT_handle_texts",
            id = requestId,
            @params = new
            {
                texts = new[] { new { text = request.Text, requestAlternatives = 3 } },
                splitting = "newlines",
                lang = new
                {
                    source_lang_user_selected = sourceCode.ToUpper(),
                    target_lang = targetCode.ToUpper()
                },
                timestamp = timestamp,
                commonJobParams = new
                {
                    wasSpoken = false,
                    transcribe_as = ""
                }
            }
        };

        // Serialize JSON with dynamic spacing for anti-detection
        var jsonPayload = JsonSerializer.Serialize(payload);
        jsonPayload = ApplyDynamicSpacing(jsonPayload, requestId);

        using var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, WebEndpoint);
        httpRequest.Content = content;

        // Set headers to mimic browser
        httpRequest.Headers.Add("Accept", "*/*");
        httpRequest.Headers.Add("Accept-Language", "en-US,en;q=0.9");
        httpRequest.Headers.Add("Origin", "https://www.deepl.com");
        httpRequest.Headers.Add("Referer", "https://www.deepl.com/");
        httpRequest.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");

        using var response = await HttpClient.SendAsync(httpRequest, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new TranslationException($"DeepL web translation failed: {response.StatusCode}")
            {
                ErrorCode = TranslationErrorCode.ServiceUnavailable,
                ServiceId = ServiceId
            };
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return ParseWebResponse(json, request);
    }

    #region Anti-Detection Helpers

    /// <summary>
    /// Generate random request ID (matching macOS pattern).
    /// </summary>
    private static long GetRandomRequestId()
    {
        return Random.Shared.Next(100000, 189999) * 1000L;
    }

    /// <summary>
    /// Count 'i' characters for timestamp alignment (DeepL's checksum mechanism).
    /// </summary>
    private static int GetICount(string text)
    {
        return text.Count(c => c == 'i');
    }

    /// <summary>
    /// Generate timestamp aligned to i-count (anti-detection measure).
    /// </summary>
    private static long GetAlignedTimestamp(int iCount)
    {
        var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (iCount > 0)
        {
            var count = iCount + 1;
            return ts - (ts % count) + count;
        }
        return ts;
    }

    /// <summary>
    /// Apply dynamic spacing in JSON "method" field (anti-detection).
    /// </summary>
    private static string ApplyDynamicSpacing(string json, long requestId)
    {
        // Add variable spacing around "method" based on request ID
        if ((requestId + 5) % 29 == 0 || (requestId + 3) % 13 == 0)
        {
            return json.Replace("\"method\":\"", "\"method\" : \"");
        }
        return json.Replace("\"method\":\"", "\"method\": \"");
    }

    #endregion

    private string GetApiHost()
    {
        // Free API keys end with ":fx"
        return _apiKey?.EndsWith(":fx") == true ? FreeApiHost : ProApiHost;
    }

    /// <summary>
    /// Parse official API response.
    /// </summary>
    private TranslationResult ParseApiResponse(string json, TranslationRequest request)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!root.TryGetProperty("translations", out var translations) ||
            translations.GetArrayLength() == 0)
        {
            throw new TranslationException("Invalid response from DeepL API")
            {
                ErrorCode = TranslationErrorCode.InvalidResponse,
                ServiceId = ServiceId
            };
        }

        var first = translations[0];
        var translatedText = first.GetProperty("text").GetString() ?? "";

        var detectedLang = Language.Auto;
        if (first.TryGetProperty("detected_source_language", out var detectedElement))
        {
            var code = detectedElement.GetString()?.ToLower() ?? "";
            detectedLang = LanguageCodes.FromIso639(code);
        }

        return new TranslationResult
        {
            TranslatedText = translatedText,
            OriginalText = request.Text,
            DetectedLanguage = detectedLang,
            TargetLanguage = request.ToLanguage,
            ServiceName = DisplayName
        };
    }

    /// <summary>
    /// Parse JSON-RPC web response.
    /// Response format: {"jsonrpc":"2.0","id":123,"result":{"texts":[{"text":"..."}],"lang":"EN"}}
    /// </summary>
    private TranslationResult ParseWebResponse(string json, TranslationRequest request)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Check for JSON-RPC error
            if (root.TryGetProperty("error", out var error))
            {
                var errorMsg = error.TryGetProperty("message", out var msg)
                    ? msg.GetString() ?? "Unknown error"
                    : "Unknown error";
                throw new TranslationException($"DeepL web error: {errorMsg}")
                {
                    ErrorCode = TranslationErrorCode.ServiceUnavailable,
                    ServiceId = ServiceId
                };
            }

            if (!root.TryGetProperty("result", out var result))
            {
                throw new TranslationException("Invalid response from DeepL web")
                {
                    ErrorCode = TranslationErrorCode.InvalidResponse,
                    ServiceId = ServiceId
                };
            }

            // Extract translated text from result.texts[0].text
            if (!result.TryGetProperty("texts", out var texts) || texts.GetArrayLength() == 0)
            {
                throw new TranslationException("No translation result from DeepL web")
                {
                    ErrorCode = TranslationErrorCode.InvalidResponse,
                    ServiceId = ServiceId
                };
            }

            var firstText = texts[0];
            var translatedText = firstText.GetProperty("text").GetString() ?? "";

            // Try to get detected language from result.lang
            var detectedLang = Language.Auto;
            if (result.TryGetProperty("lang", out var langElement))
            {
                var code = langElement.GetString()?.ToLower() ?? "";
                detectedLang = LanguageCodes.FromIso639(code);
            }

            return new TranslationResult
            {
                TranslatedText = translatedText,
                OriginalText = request.Text,
                DetectedLanguage = detectedLang,
                TargetLanguage = request.ToLanguage,
                ServiceName = DisplayName
            };
        }
        catch (JsonException ex)
        {
            throw new TranslationException($"Failed to parse DeepL web response: {ex.Message}")
            {
                ErrorCode = TranslationErrorCode.InvalidResponse,
                ServiceId = ServiceId
            };
        }
    }

    /// <summary>
    /// Get language code for official DeepL API.
    /// </summary>
    private static string GetDeepLLanguageCode(Language language) => language switch
    {
        Language.SimplifiedChinese => "ZH",
        Language.TraditionalChinese => "ZH-HANT",
        Language.English => "EN",
        Language.Japanese => "JA",
        Language.Korean => "KO",
        Language.French => "FR",
        Language.Spanish => "ES",
        Language.Portuguese => "PT",
        Language.Italian => "IT",
        Language.German => "DE",
        Language.Russian => "RU",
        Language.Dutch => "NL",
        Language.Polish => "PL",
        Language.Bulgarian => "BG",
        Language.Czech => "CS",
        Language.Danish => "DA",
        Language.Finnish => "FI",
        Language.Greek => "EL",
        Language.Hungarian => "HU",
        Language.Indonesian => "ID",
        Language.Norwegian => "NB",
        Language.Romanian => "RO",
        Language.Swedish => "SV",
        Language.Turkish => "TR",
        Language.Ukrainian => "UK",
        _ => language.ToIso639().ToUpper()
    };

    /// <summary>
    /// Get language code for DeepL web JSON-RPC (slightly different format).
    /// </summary>
    private static string GetDeepLWebLanguageCode(Language language) => language switch
    {
        Language.SimplifiedChinese => "ZH",
        Language.TraditionalChinese => "ZH-HANT",
        Language.English => "EN",
        Language.Japanese => "JA",
        Language.Korean => "KO",
        Language.French => "FR",
        Language.Spanish => "ES",
        Language.Portuguese => "PT-PT",
        Language.Italian => "IT",
        Language.German => "DE",
        Language.Russian => "RU",
        Language.Dutch => "NL",
        Language.Polish => "PL",
        Language.Bulgarian => "BG",
        Language.Czech => "CS",
        Language.Danish => "DA",
        Language.Finnish => "FI",
        Language.Greek => "EL",
        Language.Hungarian => "HU",
        Language.Indonesian => "ID",
        Language.Norwegian => "NB",
        Language.Romanian => "RO",
        Language.Swedish => "SV",
        Language.Turkish => "TR",
        Language.Ukrainian => "UK",
        _ => language.ToIso639().ToUpper()
    };
}

