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
    private bool _useQualityOptimized; // Default: latency-optimized (DeepL API default)

    /// <summary>
    /// Baseline supported languages used for local validation.
    /// As of 2026 DeepL's next-generation model supports 100+ languages, covering every language in
    /// this app's <see cref="Language"/> enum except Classical/Literary Chinese (which DeepL has no
    /// target for). This list is only a local validation gate (it does not drive the UI), so it is
    /// derived from the enum to stay aligned automatically and already reflects DeepL's current
    /// support. When an API key is configured, <see cref="RefreshSupportedLanguagesAsync"/> can
    /// additively augment this baseline with DeepL's live <c>/v2/languages</c> list; it is invoked
    /// best-effort and on-demand from <see cref="SupportsLanguagePair"/> (mainly future-proofing,
    /// since the enum-derived baseline rarely misses).
    /// </summary>
    private static readonly IReadOnlyList<Language> DeepLLanguages =
        Enum.GetValues<Language>()
            .Where(l => l is not (Language.Auto or Language.ClassicalChinese))
            .ToArray();

    private static readonly TimeSpan LanguageCacheTtl = TimeSpan.FromHours(24);

    // Effective supported-language set (baseline ∪ dynamically fetched). Null until a successful
    // fetch; reads fall back to the baseline. Replaced atomically; arrays are immutable once built.
    private volatile Language[]? _effectiveLanguages;
    private long _lastLanguageFetchTicks; // UTC ticks of the last fetch attempt; 0 = never
    private int _languageFetchInFlight;   // 0/1 guard to avoid concurrent fetches

    public DeepLService(HttpClient httpClient) : base(httpClient)
    {
    }

    public override string ServiceId => "deepl";
    public override string DisplayName => "DeepL";
    public override bool RequiresApiKey => false; // Web mode doesn't require API key
    public override bool IsConfigured => true; // Web mode is always available
    public override IReadOnlyList<Language> SupportedLanguages => _effectiveLanguages ?? DeepLLanguages;

    /// <summary>
    /// Validate a language pair against the local/effective set (fast, offline path). When a
    /// requested language is not yet known locally but an API key is configured, trigger a
    /// background refresh of DeepL's official language list so a subsequent attempt succeeds.
    /// </summary>
    public override bool SupportsLanguagePair(Language from, Language to)
    {
        if (base.SupportsLanguagePair(from, to))
        {
            return true;
        }

        // "Fetch when something new shows up": warm the dynamic list for next time.
        if (!string.IsNullOrEmpty(_apiKey))
        {
            TriggerLanguageRefresh();
        }

        return false;
    }

    /// <summary>
    /// Configure the service with an API key and mode.
    /// </summary>
    /// <param name="apiKey">Optional API key for official API access.</param>
    /// <param name="useWebFirst">If true, try web translation first (default). If false, use API only.</param>
    /// <param name="useQualityOptimized">
    /// If true, use the official API path and request DeepL's quality-optimized model
    /// (next-generation, web-translator-equivalent) via <c>model_type=quality_optimized</c>.
    /// Default: false (DeepL's latency-optimized default).
    /// </param>
    public void Configure(string? apiKey, bool useWebFirst = true, bool useQualityOptimized = false)
    {
        _apiKey = apiKey;
        _useWebFirst = !useQualityOptimized && useWebFirst;
        _useQualityOptimized = useQualityOptimized;
    }

    /// <summary>
    /// Fire-and-forget, TTL-throttled background refresh of DeepL's supported-language list.
    /// Safe to call frequently: a single attempt runs per <see cref="LanguageCacheTtl"/> window and
    /// concurrent calls are coalesced. The comprehensive baseline list means a failed refresh has no
    /// functional impact.
    /// </summary>
    private void TriggerLanguageRefresh()
    {
        if (string.IsNullOrEmpty(_apiKey))
        {
            return;
        }

        var last = Interlocked.Read(ref _lastLanguageFetchTicks);
        if (last != 0 && DateTime.UtcNow - new DateTime(last, DateTimeKind.Utc) < LanguageCacheTtl)
        {
            return; // cached result is still fresh
        }

        if (Interlocked.CompareExchange(ref _languageFetchInFlight, 1, 0) != 0)
        {
            return; // a refresh is already running
        }

        // Throttle to one attempt per TTL window regardless of success/failure.
        Interlocked.Exchange(ref _lastLanguageFetchTicks, DateTime.UtcNow.Ticks);

        _ = Task.Run(async () =>
        {
            try
            {
                await RefreshSupportedLanguagesAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DeepL] Language refresh failed: {ex.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref _languageFetchInFlight, 0);
            }
        });
    }

    /// <summary>
    /// Fetch DeepL's official target-language list and union it onto the baseline. Additive only:
    /// a partial or empty response never shrinks supported languages below the baseline. Requires an
    /// API key (the free web JSON-RPC path has no languages endpoint).
    /// </summary>
    public async Task RefreshSupportedLanguagesAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(_apiKey))
        {
            return;
        }

        HashSet<Language> fetched;
        try
        {
            var url = $"{GetApiHost()}/v2/languages?type=target";
            using var httpRequest = new HttpRequestMessage(HttpMethod.Get, url);
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("DeepL-Auth-Key", _apiKey);

            using var response = await HttpClient.SendAsync(httpRequest, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return; // keep baseline; this is a best-effort enhancement
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            fetched = ParseLanguages(json);
        }
        catch (Exception ex) when (
            ex is HttpRequestException or JsonException or TaskCanceledException &&
            !cancellationToken.IsCancellationRequested)
        {
            // Best-effort: network/timeout/malformed-response failures keep the baseline.
            // Genuine caller cancellation (token signalled) is allowed to propagate.
            System.Diagnostics.Debug.WriteLine($"[DeepL] Language refresh failed: {ex.Message}");
            return;
        }

        if (fetched.Count == 0)
        {
            return;
        }

        var union = new HashSet<Language>(DeepLLanguages);
        union.UnionWith(fetched);
        _effectiveLanguages = union.ToArray();
    }

    /// <summary>
    /// Parse the DeepL <c>/v2/languages</c> JSON array (<c>[{"language":"EN-US","name":"..."}, ...]</c>)
    /// into a set of <see cref="Language"/> values, skipping any code not recognized by the app.
    /// </summary>
    internal static HashSet<Language> ParseLanguages(string json)
    {
        var result = new HashSet<Language>();
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var element in doc.RootElement.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object ||
                !element.TryGetProperty("language", out var codeElement) ||
                codeElement.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var mapped = MapDeepLCode(codeElement.GetString());
            if (mapped.HasValue)
            {
                result.Add(mapped.Value);
            }
        }

        return result;
    }

    /// <summary>
    /// Strict DeepL code → <see cref="Language"/> mapper (inverse of <see cref="GetDeepLLanguageCode"/>,
    /// plus regional target variants). Returns null for unrecognized codes — deliberately NOT using
    /// <c>LanguageCodes.FromIso639</c>, whose fallback would coerce unknown codes to English.
    /// </summary>
    internal static Language? MapDeepLCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        return code.Trim().ToUpperInvariant() switch
        {
            "ZH" or "ZH-HANS" => Language.SimplifiedChinese,
            "ZH-HANT" => Language.TraditionalChinese,
            "EN" or "EN-GB" or "EN-US" => Language.English,
            "PT" or "PT-PT" or "PT-BR" => Language.Portuguese,
            "JA" => Language.Japanese,
            "KO" => Language.Korean,
            "FR" => Language.French,
            "ES" => Language.Spanish,
            "IT" => Language.Italian,
            "DE" => Language.German,
            "RU" => Language.Russian,
            "NL" => Language.Dutch,
            "PL" => Language.Polish,
            "BG" => Language.Bulgarian,
            "CS" => Language.Czech,
            "DA" => Language.Danish,
            "ET" => Language.Estonian,
            "FI" => Language.Finnish,
            "EL" => Language.Greek,
            "HU" => Language.Hungarian,
            "ID" => Language.Indonesian,
            "LV" => Language.Latvian,
            "LT" => Language.Lithuanian,
            "NB" or "NO" => Language.Norwegian,
            "RO" => Language.Romanian,
            "SK" => Language.Slovak,
            "SL" => Language.Slovenian,
            "SV" => Language.Swedish,
            "TR" => Language.Turkish,
            "UK" => Language.Ukrainian,
            "VI" => Language.Vietnamese,
            "AR" => Language.Arabic,
            "TH" => Language.Thai,
            "HE" => Language.Hebrew,
            "TA" => Language.Tamil,
            "TE" => Language.Telugu,
            "HI" => Language.Hindi,
            "BN" => Language.Bengali,
            "UR" => Language.Urdu,
            "MS" => Language.Malay,
            "FA" => Language.Persian,
            "FIL" or "TL" => Language.Filipino,
            _ => null
        };
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

        if (_useQualityOptimized)
        {
            formData.Add(new("model_type", "quality_optimized"));
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
        var targetCode = GetDeepLLanguageCode(request.ToLanguage, isWeb: true);
        var sourceCode = request.FromLanguage == Language.Auto
            ? "auto"
            : GetDeepLLanguageCode(request.FromLanguage, isWeb: true);

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
    /// Get language code for DeepL API or web JSON-RPC.
    /// The only difference is Portuguese: API uses "PT", web uses "PT-PT".
    /// </summary>
    private static string GetDeepLLanguageCode(Language language, bool isWeb = false) => language switch
    {
        Language.SimplifiedChinese => "ZH",
        Language.TraditionalChinese => "ZH-HANT",
        Language.English => "EN",
        Language.Japanese => "JA",
        Language.Korean => "KO",
        Language.French => "FR",
        Language.Spanish => "ES",
        Language.Portuguese => isWeb ? "PT-PT" : "PT",
        Language.Italian => "IT",
        Language.German => "DE",
        Language.Russian => "RU",
        Language.Dutch => "NL",
        Language.Polish => "PL",
        Language.Bulgarian => "BG",
        Language.Czech => "CS",
        Language.Danish => "DA",
        Language.Estonian => "ET",
        Language.Finnish => "FI",
        Language.Greek => "EL",
        Language.Hungarian => "HU",
        Language.Indonesian => "ID",
        Language.Latvian => "LV",
        Language.Lithuanian => "LT",
        Language.Norwegian => "NB",
        Language.Romanian => "RO",
        Language.Slovak => "SK",
        Language.Slovenian => "SL",
        Language.Swedish => "SV",
        Language.Turkish => "TR",
        Language.Ukrainian => "UK",
        Language.Vietnamese => "VI",
        Language.Arabic => "AR",
        Language.Thai => "TH",
        Language.Hebrew => "HE",
        Language.Tamil => "TA",
        Language.Telugu => "TE",
        Language.Hindi => "HI",
        Language.Bengali => "BN",
        Language.Urdu => "UR",
        Language.Malay => "MS",
        Language.Persian => "FA",
        // NOTE: DeepL's exact Tagalog/Filipino code ("TL" vs "FIL") should be confirmed against a
        // live /v2/languages response; MapDeepLCode accepts both inbound. "TL" matches ToIso639.
        Language.Filipino => "TL",
        // ToUpperInvariant: DeepL codes are ASCII; avoid locale-sensitive casing (e.g. Turkish 'i').
        _ => language.ToIso639().ToUpperInvariant()
    };
}

