using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Easydict.TranslationService.Models;

namespace Easydict.TranslationService.Services;

/// <summary>
/// DeepL translation service supporting its public one-shot web endpoint and official API.
/// The web endpoint is unofficial and does not require an API key; the official API supports free or pro keys.
/// </summary>
public sealed class DeepLService : BaseTranslationService
{
    // Official API endpoints
    private const string FreeApiHost = "https://api-free.deepl.com";
    private const string ProApiHost = "https://api.deepl.com";

    // DeepL's public one-shot endpoint used by its interactive clients.
    private const string WebEndpoint = "https://oneshot-free.www.deepl.com/v1/translate";
    private const string DeepLIosOsVersion = "26.0";
    private const string DeepLIosAppBuild = "5443737";
    private const string DeepLIosCfNetworkVersion = "3826.600.41";
    private const string DeepLIosDarwinVersion = "25.0.0";
    private const int MaxWebTextLength = 1500;
    private static readonly string InstanceId = Guid.NewGuid().ToString();
    private static readonly string SessionId = Guid.NewGuid().ToString();
    private static readonly JsonSerializerOptions WebJsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly DeepLAppVersionProvider _appVersionProvider;
    private string? _apiKey;
    private bool _useWebFirst = true; // Default to web translation (no API key needed)
    private bool _useQualityOptimized; // Default: latency-optimized (DeepL API default)

    /// <summary>
    /// Full supported-language set, used when an official API key is configured.
    /// As of 2026 DeepL's next-generation model (official API) supports 100+ languages, covering every
    /// language in this app's <see cref="Language"/> enum except Classical/Literary Chinese (no DeepL
    /// target). This is only a local validation gate (it does not drive the UI), so it is derived from
    /// the enum to stay aligned automatically. When an API key is configured,
    /// <see cref="RefreshSupportedLanguagesAsync"/> can additively augment it with DeepL's live
    /// <c>/v2/languages</c> list (best-effort, on-demand from <see cref="SupportsLanguagePair"/>).
    /// </summary>
    private static readonly IReadOnlyList<Language> DeepLLanguages =
        Array.AsReadOnly(
            Enum.GetValues<Language>()
                .Where(l => l is not (Language.Auto or Language.ClassicalChinese))
                .ToArray());

    /// <summary>
    /// Languages supported by DeepL's public one-shot endpoint when no API key is configured.
    /// This list mirrors the target codes supported by the current endpoint; the official API
    /// supports additional languages when a key is configured.
    /// </summary>
    private static readonly IReadOnlyList<Language> WebOneShotLanguages =
        Array.AsReadOnly(new[]
        {
            Language.SimplifiedChinese, Language.TraditionalChinese, Language.English, Language.Japanese,
            Language.Korean, Language.French, Language.Spanish, Language.Portuguese,
            Language.Italian, Language.German, Language.Russian, Language.Dutch,
            Language.Polish, Language.Bulgarian, Language.Czech, Language.Danish,
            Language.Estonian, Language.Finnish, Language.Greek, Language.Hungarian,
            Language.Indonesian, Language.Latvian, Language.Lithuanian, Language.Norwegian,
            Language.Romanian, Language.Slovak, Language.Slovenian, Language.Swedish,
            Language.Turkish, Language.Ukrainian, Language.Vietnamese, Language.Arabic,
            Language.Hebrew
        });

    private static readonly TimeSpan LanguageCacheTtl = TimeSpan.FromHours(24);

    // Effective supported-language set (baseline ∪ dynamically fetched). Null until a successful
    // fetch; reads fall back to the baseline. Replaced atomically; stored as a read-only view so
    // callers of SupportedLanguages cannot mutate the underlying array.
    private volatile IReadOnlyList<Language>? _effectiveLanguages;
    private long _lastLanguageFetchTicks; // UTC ticks of the last fetch attempt; 0 = never
    private int _languageFetchInFlight;   // 0/1 guard to avoid concurrent fetches

    public DeepLService(HttpClient httpClient) : this(httpClient, DeepLAppVersionProvider.Shared)
    {
    }

    internal DeepLService(HttpClient httpClient, DeepLAppVersionProvider appVersionProvider) : base(httpClient)
    {
        _appVersionProvider = appVersionProvider;
    }

    public override string ServiceId => "deepl";
    public override string DisplayName => "DeepL";
    public override bool RequiresApiKey => false; // Web mode doesn't require API key
    public override bool IsConfigured => true; // Web mode is always available
    // Mode-aware: keyless web mode is limited to the one-shot endpoint's supported set; the official API
    // supports the full set. Prevents offering API-only languages to keyless users.
    public override IReadOnlyList<Language> SupportedLanguages =>
        string.IsNullOrEmpty(_apiKey) ? WebOneShotLanguages : (_effectiveLanguages ?? DeepLLanguages);

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

        // Intentionally dormant future-proofing: the enum-derived API baseline already covers DeepL's
        // current support, so this "miss" path (and the additive refresh it warms) effectively only
        // fires if the enum ever gains a language DeepL adds later. It is a safety net, not a hot path.
        if (!string.IsNullOrEmpty(_apiKey))
        {
            TriggerLanguageRefresh();
        }

        return false;
    }

    /// <summary>
    /// Adds a clearer message when a keyless user requests a language that the current one-shot
    /// endpoint does not support, guiding them to add an API key.
    /// </summary>
    protected override void ValidateRequest(TranslationRequest request)
    {
        if (string.IsNullOrEmpty(_apiKey) &&
            !SupportsLanguagePair(request.FromLanguage, request.ToLanguage))
        {
            // The offending language may be the source or the target (e.g. Vietnamese -> English).
            Language? apiOnly =
                IsApiOnlyLanguage(request.ToLanguage) ? request.ToLanguage :
                IsApiOnlyLanguage(request.FromLanguage) ? request.FromLanguage :
                null;

            if (apiOnly is { } language)
            {
                throw new TranslationException(
                    $"DeepL free (web) mode does not support {language}. " +
                    "Add a DeepL API key in Settings to translate this language.")
                {
                    ErrorCode = TranslationErrorCode.UnsupportedLanguage,
                    ServiceId = ServiceId
                };
            }
        }

        base.ValidateRequest(request);
    }

    /// <summary>A language DeepL supports via the official API but not the public one-shot endpoint.</summary>
    private static bool IsApiOnlyLanguage(Language language) =>
        DeepLLanguages.Contains(language) && !WebOneShotLanguages.Contains(language);

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
    /// API key (the public one-shot path has no languages endpoint).
    /// <para>
    /// NOTE: This is intentionally dormant future-proofing. The API-mode baseline is already the full
    /// <see cref="Language"/> enum (minus Classical Chinese), so a fetched list (a subset of the enum)
    /// cannot currently change <see cref="SupportedLanguages"/>. It is retained so that, if the enum
    /// gains a language DeepL also supports, the live set can confirm/augment it without a code change.
    /// It stays public/tested so the parsing + mapping logic remains correct and ready.
    /// </para>
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
        _effectiveLanguages = Array.AsReadOnly(union.ToArray());
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
        // Try web translation first if enabled — but only when the languages are supported by the
        // one-shot endpoint. Other languages route directly to the official API (a key is guaranteed here,
        // since keyless next-gen requests are rejected by ValidateRequest before reaching this point).
        if (_useWebFirst && WebSupportsRequest(request))
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
    /// Whether the request's languages are translatable via DeepL's public one-shot endpoint.
    /// </summary>
    private static bool WebSupportsRequest(TranslationRequest request) =>
        WebOneShotLanguages.Contains(request.ToLanguage) &&
        (request.FromLanguage == Language.Auto || WebOneShotLanguages.Contains(request.FromLanguage));

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

        var formData = new List<KeyValuePair<string, string>>
        {
            new("text", request.Text),
            new("target_lang", GetDeepLApiTargetLanguageCode(request.ToLanguage))
        };

        if (request.FromLanguage != Language.Auto)
        {
            formData.Add(new("source_lang", GetDeepLApiSourceLanguageCode(request.FromLanguage)));
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
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            var errorMessage = TryGetDeepLErrorMessage(errorBody);
            var errorCode = response.StatusCode switch
            {
                HttpStatusCode.Forbidden => TranslationErrorCode.InvalidApiKey,
                HttpStatusCode.TooManyRequests => TranslationErrorCode.RateLimited,
                (HttpStatusCode)456 => TranslationErrorCode.RateLimited, // DeepL quota exceeded
                _ => TranslationErrorCode.ServiceUnavailable
            };

            throw new TranslationException(FormatApiError(response.StatusCode, errorMessage))
            {
                ErrorCode = errorCode,
                ServiceId = ServiceId
            };
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return ParseApiResponse(json, request);
    }

    /// <summary>
    /// Translate using DeepL's public one-shot endpoint with the current interactive-client request shape.
    /// </summary>
    private async Task<TranslationResult> TranslateWebAsync(
        TranslationRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Text.Length > MaxWebTextLength)
        {
            throw new TranslationException($"DeepL web translation is limited to {MaxWebTextLength} UTF-16 code units per request")
            {
                ErrorCode = TranslationErrorCode.TextTooLong,
                ServiceId = ServiceId
            };
        }

        var appVersion = await _appVersionProvider.GetCurrentVersionAsync(cancellationToken);
        var payload = new
        {
            text = new[] { request.Text },
            target_lang = GetDeepLOneShotTargetLanguageCode(request.ToLanguage),
            source_lang = request.FromLanguage == Language.Auto
                ? null
                : GetDeepLOneShotSourceLanguageCode(request.FromLanguage),
            usage_type = "translate",
            app_information = new
            {
                os = "iOS",
                os_version = DeepLIosOsVersion,
                app_version = appVersion,
                app_build = DeepLIosAppBuild,
                instance_id = InstanceId
            }
        };

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, WebEndpoint)
        {
            Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(payload, WebJsonOptions)),
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower
        };
        httpRequest.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        httpRequest.Headers.TryAddWithoutValidation("Authorization", "None");
        httpRequest.Headers.TryAddWithoutValidation("Accept", "*/*");
        httpRequest.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
        httpRequest.Headers.UserAgent.ParseAdd(
            $"DeepL/{appVersion} CFNetwork/{DeepLIosCfNetworkVersion} Darwin/{DeepLIosDarwinVersion}");
        httpRequest.Headers.TryAddWithoutValidation("x-app-os-version", DeepLIosOsVersion);
        httpRequest.Headers.TryAddWithoutValidation("x-app-instance-id", InstanceId);
        httpRequest.Headers.TryAddWithoutValidation("x-app-session-id", SessionId);

        using var response = await HttpClient.SendAsync(httpRequest, cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var errorDetail = TryGetOneShotErrorMessage(json) ?? response.ReasonPhrase ?? "Unknown error";
            var isOutdatedClient = response.StatusCode == HttpStatusCode.Forbidden &&
                (errorDetail.Contains("outdated", StringComparison.OrdinalIgnoreCase) ||
                 errorDetail.Contains("client version", StringComparison.OrdinalIgnoreCase));
            throw new TranslationException(isOutdatedClient
                ? $"DeepL web client version is outdated: {errorDetail}"
                : $"DeepL web translation failed: {(int)response.StatusCode} ({errorDetail})")
            {
                ErrorCode = response.StatusCode == HttpStatusCode.TooManyRequests
                    ? TranslationErrorCode.RateLimited
                    : isOutdatedClient || (int)response.StatusCode >= 500
                        ? TranslationErrorCode.ServiceUnavailable
                        : TranslationErrorCode.InvalidResponse,
                ServiceId = ServiceId
            };
        }

        var result = ParseApiResponse(json, request);
        if (IsWebClientVersionRejection(result.TranslatedText))
        {
            throw new TranslationException("DeepL web client version is outdated")
            {
                ErrorCode = TranslationErrorCode.ServiceUnavailable,
                ServiceId = ServiceId
            };
        }

        return result;
    }

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
    /// Read the title and message fields returned for a rejected one-shot request.
    /// </summary>
    private static string? TryGetOneShotErrorMessage(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var title = root.TryGetProperty("title", out var titleElement) ? titleElement.GetString() : null;
            var message = root.TryGetProperty("message", out var messageElement) ? messageElement.GetString() : null;
            return string.Join(": ", new[] { title, message }.Where(value => !string.IsNullOrWhiteSpace(value)));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsWebClientVersionRejection(string text) =>
        text.Contains("older version of DeepL", StringComparison.OrdinalIgnoreCase) &&
        text.Contains("update to the latest version", StringComparison.OrdinalIgnoreCase);

    private static string GetDeepLOneShotTargetLanguageCode(Language language) => language switch
    {
        Language.SimplifiedChinese => "zh-Hans",
        Language.TraditionalChinese => "zh-Hant",
        Language.English => "en-US",
        Language.Portuguese => "pt-PT",
        Language.Norwegian => "nb",
        _ => GetDeepLLanguageCode(language).ToLowerInvariant()
    };

    private static string GetDeepLOneShotSourceLanguageCode(Language language) => language switch
    {
        Language.SimplifiedChinese or Language.TraditionalChinese => "zh",
        Language.English => "en",
        Language.Portuguese => "pt",
        Language.Norwegian => "nb",
        _ => GetDeepLLanguageCode(language).ToLowerInvariant()
    };

    /// <summary>
    /// Get the common DeepL language code used by the official API.
    /// API target codes use <see cref="GetDeepLApiTargetLanguageCode"/> because some targets require variants.
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

    /// <summary>
    /// Source codes are broad language identifiers. DeepL's API accepts variants such as EN-US as
    /// targets, but source_lang uses EN; likewise Chinese source is ZH while target can be ZH-HANT.
    /// </summary>
    private static string GetDeepLApiSourceLanguageCode(Language language) => language switch
    {
        Language.SimplifiedChinese or Language.TraditionalChinese => "ZH",
        Language.English => "EN",
        Language.Portuguese => "PT",
        _ => GetDeepLLanguageCode(language)
    };

    /// <summary>
    /// Target codes must use DeepL's target-capable variants for languages where the generic code is
    /// source-only or deprecated as a target.
    /// </summary>
    private static string GetDeepLApiTargetLanguageCode(Language language) => language switch
    {
        Language.English => "EN-US",
        Language.Portuguese => "PT-PT",
        _ => GetDeepLLanguageCode(language)
    };

    private static string? TryGetDeepLErrorMessage(string? errorBody)
    {
        if (string.IsNullOrWhiteSpace(errorBody))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(errorBody);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("message", out var messageElement) &&
                messageElement.ValueKind == JsonValueKind.String)
            {
                return messageElement.GetString();
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }

    private static string FormatApiError(HttpStatusCode statusCode, string? errorMessage) =>
        string.IsNullOrWhiteSpace(errorMessage)
            ? $"DeepL API error: {statusCode}"
            : $"DeepL API error: {statusCode} ({errorMessage})";
}

