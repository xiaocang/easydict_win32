using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Easydict.TranslationService.Models;

namespace Easydict.TranslationService.Services;

/// <summary>
/// NiuTrans (小牛翻译) neural machine translation service.
/// Supports 450+ languages with HMAC-SHA256 authentication.
/// </summary>
public sealed class NiuTransService : BaseTranslationService
{
    private const string Endpoint = "https://ntrans.xfyun.cn/v1/trans";
    private const string Host = "ntrans.xfyun.cn";

    private static readonly IReadOnlyList<Language> NiuTransLanguages = new[]
    {
        Language.Auto,
        Language.SimplifiedChinese,
        Language.TraditionalChinese,
        Language.English,
        Language.Japanese,
        Language.Korean,
        Language.French,
        Language.Spanish,
        Language.German,
        Language.Russian,
        Language.Arabic,
        Language.Italian,
        Language.Portuguese,
        Language.Dutch,
        Language.Polish,
        Language.Turkish,
        Language.Vietnamese,
        Language.Thai,
        Language.Indonesian,
        Language.Malay,
        Language.Hindi,
        Language.Greek,
        Language.Czech,
        Language.Danish,
        Language.Finnish,
        Language.Hungarian,
        Language.Norwegian,
        Language.Romanian,
        Language.Slovak,
        Language.Swedish,
        Language.Bulgarian,
        Language.Estonian,
        Language.Latvian,
        Language.Lithuanian,
        Language.Slovenian,
        Language.Ukrainian,
        Language.Persian,
        Language.Hebrew,
        Language.Bengali,
        Language.Tamil,
        Language.Telugu,
        Language.Urdu,
        Language.Filipino
    };

    private string _apiKey = "";

    public NiuTransService(HttpClient httpClient) : base(httpClient) { }

    public override string ServiceId => "niutrans";
    public override string DisplayName => "NiuTrans";
    public override bool RequiresApiKey => true;
    public override bool IsConfigured => !string.IsNullOrEmpty(_apiKey);
    public override IReadOnlyList<Language> SupportedLanguages => NiuTransLanguages;

    /// <summary>
    /// Configure the NiuTrans service with API key.
    /// </summary>
    /// <param name="apiKey">NiuTrans API key.</param>
    public void Configure(string apiKey)
    {
        _apiKey = apiKey ?? "";
    }

    protected override async Task<TranslationResult> TranslateInternalAsync(
        TranslationRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(_apiKey))
        {
            throw new TranslationException("NiuTrans API key not configured")
            {
                ErrorCode = TranslationErrorCode.InvalidApiKey,
                ServiceId = ServiceId
            };
        }

        var fromCode = GetLanguageCode(request.FromLanguage);
        var toCode = GetLanguageCode(request.ToLanguage);

        // Build request body
        var requestBody = new
        {
            from = fromCode,
            to = toCode,
            src_text = request.Text,
            source = "text"
        };

        var json = JsonSerializer.Serialize(requestBody);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        // Calculate HMAC signature
        var date = DateTime.UtcNow.ToString("R", CultureInfo.InvariantCulture); // RFC1123 format
        var requestLine = "POST /v1/trans HTTP/1.1";
        var digest = CalculateSHA256Digest(json);
        var signature = GenerateHMACSignature(Host, date, requestLine, digest, _apiKey);

        // Build request with HMAC headers
        var httpRequest = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = content
        };
        httpRequest.Headers.TryAddWithoutValidation("Date", date);
        httpRequest.Headers.TryAddWithoutValidation("Digest", $"SHA-256={digest}");
        httpRequest.Headers.TryAddWithoutValidation("Authorization", signature);

        var response = await HttpClient.SendAsync(httpRequest, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var errorCode = response.StatusCode switch
            {
                System.Net.HttpStatusCode.Unauthorized => TranslationErrorCode.InvalidApiKey,
                System.Net.HttpStatusCode.Forbidden => TranslationErrorCode.InvalidApiKey,
                System.Net.HttpStatusCode.TooManyRequests => TranslationErrorCode.RateLimited,
                _ => TranslationErrorCode.ServiceUnavailable
            };

            throw new TranslationException($"NiuTrans API returned {response.StatusCode}")
            {
                ErrorCode = errorCode,
                ServiceId = ServiceId
            };
        }

        var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
        var result = ParseNiuTransResponse(responseJson, request.Text);

        return result;
    }

    /// <summary>
    /// Calculate SHA-256 digest of the request body.
    /// </summary>
    private static string CalculateSHA256Digest(string body)
    {
        using var sha256 = SHA256.Create();
        var bytes = Encoding.UTF8.GetBytes(body);
        var hash = sha256.ComputeHash(bytes);
        return Convert.ToBase64String(hash);
    }

    /// <summary>
    /// Generate HMAC-SHA256 signature for NiuTrans authentication.
    /// </summary>
    private static string GenerateHMACSignature(string host, string date, string requestLine, string digest, string apiKey)
    {
        // Construct canonical string
        var canonicalString = $"host: {host}\ndate: {date}\n{requestLine}\ndigest: SHA-256={digest}";

        // Calculate HMAC-SHA256
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(apiKey));
        var signatureBytes = hmac.ComputeHash(Encoding.UTF8.GetBytes(canonicalString));
        var signatureBase64 = Convert.ToBase64String(signatureBytes);

        // Build Authorization header
        var authorization = $"algorithm=\"hmac-sha256\", headers=\"host date request-line digest\", signature=\"{signatureBase64}\"";

        return authorization;
    }

    private TranslationResult ParseNiuTransResponse(string json, string originalText)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Extract translated text
        var translatedText = originalText;
        if (root.TryGetProperty("tgt_text", out var tgtTextProp))
        {
            translatedText = tgtTextProp.GetString() ?? originalText;
        }

        return new TranslationResult
        {
            TranslatedText = translatedText,
            OriginalText = originalText,
            DetectedLanguage = Language.Auto,
            TargetLanguage = Language.Auto,
            ServiceName = DisplayName,
            TimingMs = 0,
            FromCache = false
        };
    }

    protected override string GetLanguageCode(Language language)
    {
        return language switch
        {
            Language.Auto => "auto",
            Language.SimplifiedChinese => "zh",
            Language.TraditionalChinese => "zh",
            Language.English => "en",
            Language.Japanese => "ja",
            Language.Korean => "ko",
            Language.French => "fr",
            Language.Spanish => "es",
            Language.German => "de",
            Language.Russian => "ru",
            Language.Arabic => "ar",
            Language.Italian => "it",
            Language.Portuguese => "pt",
            Language.Dutch => "nl",
            Language.Polish => "pl",
            Language.Turkish => "tr",
            Language.Vietnamese => "vi",
            Language.Thai => "th",
            Language.Indonesian => "id",
            Language.Malay => "ms",
            Language.Hindi => "hi",
            Language.Greek => "el",
            Language.Czech => "cs",
            Language.Danish => "da",
            Language.Finnish => "fi",
            Language.Hungarian => "hu",
            Language.Norwegian => "no",
            Language.Romanian => "ro",
            Language.Slovak => "sk",
            Language.Swedish => "sv",
            Language.Bulgarian => "bg",
            Language.Estonian => "et",
            Language.Latvian => "lv",
            Language.Lithuanian => "lt",
            Language.Slovenian => "sl",
            Language.Ukrainian => "uk",
            Language.Persian => "fa",
            Language.Hebrew => "he",
            Language.Bengali => "bn",
            Language.Tamil => "ta",
            Language.Telugu => "te",
            Language.Urdu => "ur",
            Language.Filipino => "fil",
            _ => throw new TranslationException($"Unsupported language: {language}")
            {
                ErrorCode = TranslationErrorCode.UnsupportedLanguage,
                ServiceId = ServiceId
            }
        };
    }
}
