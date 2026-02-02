using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Web;
using Easydict.TranslationService.Models;

namespace Easydict.TranslationService.Services;

/// <summary>
/// Bing Translate service using the free web API (no API key required).
/// Fetches session credentials (IG, IID, token, key) from the Bing Translator page,
/// then calls the ttranslatev3 endpoint.
/// Supports cn.bing.com for China mainland access.
/// </summary>
public sealed class BingTranslateService : BaseTranslationService, IDisposable
{
    private const string GlobalHost = "www.bing.com";
    private const string ChinaHost = "cn.bing.com";
    private const string TranslatorPath = "/translator";
    private const string TranslateApiPath = "/ttranslatev3";

    /// <summary>
    /// Full Edge browser User-Agent string (required for EPT mode).
    /// </summary>
    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36 Edg/131.0.0.0";

    /// <summary>
    /// Maximum text length per request. EPT mode supports 3000 chars.
    /// </summary>
    private const int MaxTextLength = 3000;

    private static readonly IReadOnlyList<Language> _bingLanguages =
    [
        Language.SimplifiedChinese, Language.TraditionalChinese, Language.English,
        Language.Japanese, Language.Korean, Language.French, Language.Spanish,
        Language.Portuguese, Language.Italian, Language.German, Language.Russian,
        Language.Arabic, Language.Swedish, Language.Romanian, Language.Thai,
        Language.Dutch, Language.Hungarian, Language.Greek, Language.Danish,
        Language.Finnish, Language.Polish, Language.Czech, Language.Turkish,
        Language.Ukrainian, Language.Bulgarian, Language.Indonesian, Language.Malay,
        Language.Vietnamese, Language.Persian, Language.Hindi, Language.Telugu,
        Language.Tamil, Language.Urdu, Language.Filipino, Language.Bengali,
        Language.Norwegian, Language.Hebrew
    ];

    /// <summary>
    /// Bing uses some non-standard language codes compared to ISO 639-1.
    /// </summary>
    private static readonly Dictionary<Language, string> _bingLanguageCodes = new()
    {
        { Language.SimplifiedChinese, "zh-Hans" },
        { Language.TraditionalChinese, "zh-Hant" },
        { Language.Auto, "auto-detect" },
        { Language.Norwegian, "nb" },
        { Language.Filipino, "fil" },
    };

    private readonly SemaphoreSlim _credentialSemaphore = new(1, 1);
    private BingCredentials? _credentials;
    private bool _useChinaHost;
    private int _eptCounter;

    public BingTranslateService(HttpClient httpClient) : base(httpClient)
    {
    }

    public override string ServiceId => "bing";
    public override string DisplayName => "Bing Translate";
    public override bool RequiresApiKey => false;
    public override bool IsConfigured => true;
    public override IReadOnlyList<Language> SupportedLanguages => _bingLanguages;

    /// <summary>
    /// Configure whether to use China host (cn.bing.com) or global host (www.bing.com).
    /// </summary>
    public void Configure(bool useChinaHost)
    {
        _useChinaHost = useChinaHost;
    }

    protected override async Task<TranslationResult> TranslateInternalAsync(
        TranslationRequest request,
        CancellationToken cancellationToken)
    {
        var text = request.Text;
        if (text.Length > MaxTextLength)
        {
            text = text[..MaxTextLength];
        }

        const int maxAttempts = 2;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var credentials = await GetOrRefreshCredentialsAsync(cancellationToken);

            var sourceCode = GetBingLanguageCode(request.FromLanguage);
            var targetCode = GetBingLanguageCode(request.ToLanguage);

            var host = GetHost();
            var sfx = Interlocked.Increment(ref _eptCounter);
            var url = $"https://{host}{TranslateApiPath}?isVertical=1&IG={credentials.IG}&IID={credentials.IID}&ref=TThis&edgepdftranslator=1&SFX={sfx}";

            var postData = new Dictionary<string, string>
            {
                { "fromLang", sourceCode },
                { "to", targetCode },
                { "text", text },
                { "token", credentials.Token },
                { "key", credentials.Key.ToString() },
                { "tryFetchingGenderDebiasedTranslations", "true" }
            };

            using var content = new FormUrlEncodedContent(postData);
            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = content
            };
            httpRequest.Headers.Add("User-Agent", UserAgent);
            httpRequest.Headers.Add("Referer", $"https://{host}/translator");

            using var response = await HttpClient.SendAsync(httpRequest, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var isRetryable = response.StatusCode == HttpStatusCode.TooManyRequests ||
                                  response.StatusCode == HttpStatusCode.Unauthorized;

                // Clear credentials so next attempt fetches fresh ones
                if (isRetryable)
                {
                    _credentials = null;
                }

                // Retry once with fresh credentials on 429/401
                if (isRetryable && attempt < maxAttempts)
                {
                    Debug.WriteLine($"[BingTranslate] Got {response.StatusCode}, retrying with fresh credentials (attempt {attempt}/{maxAttempts})");
                    continue;
                }

                throw new TranslationException($"Bing API error: {response.StatusCode}")
                {
                    ErrorCode = response.StatusCode == HttpStatusCode.TooManyRequests
                        ? TranslationErrorCode.RateLimited
                        : TranslationErrorCode.ServiceUnavailable,
                    ServiceId = ServiceId
                };
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            return ParseResponse(json, request);
        }

        // Unreachable, but satisfies the compiler
        throw new TranslationException("Bing translation failed after retries")
        {
            ErrorCode = TranslationErrorCode.ServiceUnavailable,
            ServiceId = ServiceId
        };
    }

    public override async Task<Language> DetectLanguageAsync(
        string text,
        CancellationToken cancellationToken = default)
    {
        var request = new TranslationRequest
        {
            Text = text.Length > 100 ? text[..100] : text,
            FromLanguage = Language.Auto,
            ToLanguage = Language.English
        };

        var result = await TranslateAsync(request, cancellationToken);
        return result.DetectedLanguage;
    }

    protected override string GetLanguageCode(Language language)
    {
        return GetBingLanguageCode(language);
    }

    /// <summary>
    /// Check if the Bing Translate endpoint is reachable (for network detection).
    /// </summary>
    public async Task<bool> IsReachableAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var host = GetHost();
            using var request = new HttpRequestMessage(HttpMethod.Head, $"https://{host}/translator");
            request.Headers.Add("User-Agent", UserAgent);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(5));

            using var response = await HttpClient.SendAsync(request, cts.Token);
            return response.IsSuccessStatusCode ||
                   response.StatusCode == HttpStatusCode.Found ||
                   response.StatusCode == HttpStatusCode.MovedPermanently;
        }
        catch
        {
            return false;
        }
    }

    private string GetHost() => _useChinaHost ? ChinaHost : GlobalHost;

    private static string GetBingLanguageCode(Language language)
    {
        if (_bingLanguageCodes.TryGetValue(language, out var code))
            return code;
        return language.ToIso639();
    }

    private async Task<BingCredentials> GetOrRefreshCredentialsAsync(CancellationToken cancellationToken)
    {
        var creds = _credentials;
        if (creds != null && !creds.IsExpired)
            return creds;

        await _credentialSemaphore.WaitAsync(cancellationToken);
        try
        {
            // Double-check after acquiring the semaphore (another thread may have refreshed)
            creds = _credentials;
            if (creds != null && !creds.IsExpired)
                return creds;

            creds = await FetchCredentialsAsync(cancellationToken);
            _credentials = creds;
            return creds;
        }
        finally
        {
            _credentialSemaphore.Release();
        }
    }

    /// <summary>
    /// Fetch IG, IID, token, and key from the Bing Translator page HTML.
    /// </summary>
    private async Task<BingCredentials> FetchCredentialsAsync(CancellationToken cancellationToken)
    {
        var host = GetHost();
        var url = $"https://{host}{TranslatorPath}";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("User-Agent", UserAgent);

        using var response = await HttpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var html = await response.Content.ReadAsStringAsync(cancellationToken);

        // Extract IG
        var igMatch = Regex.Match(html, @"IG:""([^""]+)""");
        var ig = igMatch.Success ? igMatch.Groups[1].Value : GenerateIG();

        // Extract IID
        var iidMatch = Regex.Match(html, @"data-iid=""([^""]+)""");
        var iid = iidMatch.Success ? iidMatch.Groups[1].Value : "translator.5023.1";

        // Extract token, key, and expiry from params_AbusePreventionHelper
        var paramsMatch = Regex.Match(html, @"params_AbusePreventionHelper\s*=\s*\[(\d+),""([^""]+)"",(\d+)\]");
        long key = 0;
        var token = "";
        var expiryInterval = 3600000L; // Default 1 hour

        if (paramsMatch.Success)
        {
            key = long.Parse(paramsMatch.Groups[1].Value);
            token = paramsMatch.Groups[2].Value;
            expiryInterval = long.Parse(paramsMatch.Groups[3].Value);
        }

        if (!paramsMatch.Success || string.IsNullOrEmpty(token))
        {
            // Log a snippet of the HTML around where the params are expected for debugging
            var paramsIndex = html.IndexOf("params_AbusePreventionHelper", StringComparison.Ordinal);
            if (paramsIndex >= 0)
            {
                var start = Math.Max(0, paramsIndex - 20);
                var end = Math.Min(html.Length, paramsIndex + 120);
                Debug.WriteLine($"[BingTranslate] HTML near params_AbusePreventionHelper: ...{html[start..end]}...");
            }
            else
            {
                Debug.WriteLine($"[BingTranslate] params_AbusePreventionHelper not found in HTML (length={html.Length})");
            }

            throw new TranslationException(
                "Failed to extract Bing session credentials. The page format may have changed.")
            {
                ErrorCode = TranslationErrorCode.ServiceUnavailable,
                ServiceId = "bing"
            };
        }

        Debug.WriteLine($"[BingTranslate] Credentials fetched: IG={ig[..Math.Min(8, ig.Length)]}..., IID={iid}");

        return new BingCredentials(ig, iid, token, key, expiryInterval);
    }

    /// <summary>
    /// Generate a random IG value as fallback.
    /// </summary>
    private static string GenerateIG()
    {
        var bytes = new byte[16];
        using var rng = System.Security.Cryptography.RandomNumberGenerator.Create();
        rng.GetBytes(bytes);
        return Convert.ToHexString(bytes);
    }

    private TranslationResult ParseResponse(string json, TranslationRequest request)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Response format: [{"detectedLanguage":{"language":"en","score":1.0},"translations":[{"text":"...","to":"zh-Hans"}]}]
        if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0)
        {
            var firstResult = root[0];

            // Extract translated text
            var translatedText = "";
            if (firstResult.TryGetProperty("translations", out var translations) &&
                translations.ValueKind == JsonValueKind.Array &&
                translations.GetArrayLength() > 0)
            {
                var firstTranslation = translations[0];
                if (firstTranslation.TryGetProperty("text", out var textElement))
                {
                    translatedText = textElement.GetString() ?? "";
                }
            }

            // Extract detected language
            var detectedLang = Language.Auto;
            if (firstResult.TryGetProperty("detectedLanguage", out var detectedObj) &&
                detectedObj.TryGetProperty("language", out var langElement))
            {
                var langCode = langElement.GetString() ?? "";
                detectedLang = FromBingLanguageCode(langCode);
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

        // Handle error response: {"statusCode":400,"errorMessage":"..."}
        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("statusCode", out var statusCode))
        {
            var errorMsg = root.TryGetProperty("errorMessage", out var errElement)
                ? errElement.GetString() ?? "Unknown error"
                : "Unknown error";

            throw new TranslationException($"Bing API error {statusCode}: {errorMsg}")
            {
                ErrorCode = TranslationErrorCode.ServiceUnavailable,
                ServiceId = ServiceId
            };
        }

        throw new TranslationException("Unexpected response format from Bing Translate")
        {
            ErrorCode = TranslationErrorCode.Unknown,
            ServiceId = ServiceId
        };
    }

    /// <summary>
    /// Convert Bing language code back to Language enum.
    /// </summary>
    private static Language FromBingLanguageCode(string code)
    {
        return code.ToLowerInvariant() switch
        {
            "zh-hans" => Language.SimplifiedChinese,
            "zh-hant" => Language.TraditionalChinese,
            "fil" => Language.Filipino,
            "nb" => Language.Norwegian,
            _ => LanguageCodes.FromIso639(code)
        };
    }

    public void Dispose()
    {
        _credentialSemaphore.Dispose();
    }

    /// <summary>
    /// Cached Bing session credentials with expiry tracking.
    /// </summary>
    private sealed class BingCredentials
    {
        public string IG { get; }
        public string IID { get; }
        public string Token { get; }
        public long Key { get; }
        private readonly long _expiryInterval;
        private readonly long _createdAt;

        public BingCredentials(string ig, string iid, string token, long key, long expiryInterval)
        {
            IG = ig;
            IID = iid;
            Token = token;
            Key = key;
            _expiryInterval = expiryInterval;
            _createdAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }

        public bool IsExpired =>
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - _createdAt > _expiryInterval;
    }
}
