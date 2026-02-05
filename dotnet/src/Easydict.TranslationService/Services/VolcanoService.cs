using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Easydict.TranslationService.Models;

namespace Easydict.TranslationService.Services;

/// <summary>
/// Volcano Engine (火山翻译) translation service.
/// Uses HMAC-SHA256 signing (AWS SigV4 style) for authentication.
/// API docs: https://www.volcengine.com/docs/4640/65067
/// </summary>
public sealed class VolcanoService : BaseTranslationService
{
    private const string Host = "translate.volcengineapi.com";
    private const string Endpoint = $"https://{Host}/";
    private const string QueryString = "Action=TranslateText&Version=2020-06-01";
    private const string Region = "cn-north-1";
    private const string ServiceName = "translate";
    private const string Algorithm = "HMAC-SHA256";
    private const int MaxTextLength = 5000;

    private static readonly IReadOnlyList<Language> _volcanoLanguages = new[]
    {
        Language.Auto,
        Language.SimplifiedChinese,
        Language.TraditionalChinese,
        Language.ClassicalChinese,
        Language.English,
        Language.Japanese,
        Language.Korean,
        Language.French,
        Language.German,
        Language.Spanish,
        Language.Portuguese,
        Language.Italian,
        Language.Russian,
        Language.Arabic,
        Language.Thai,
        Language.Vietnamese,
        Language.Indonesian,
        Language.Hindi,
        Language.Hebrew,
        Language.Ukrainian,
        Language.Urdu,
        Language.Turkish,
        Language.Tamil,
        Language.Telugu,
        Language.Slovenian,
        Language.Slovak,
        Language.Swedish,
        Language.Norwegian,
        Language.Bengali,
        Language.Malay,
        Language.Romanian,
        Language.Lithuanian,
        Language.Latvian,
        Language.Czech,
        Language.Dutch,
        Language.Finnish,
        Language.Danish,
        Language.Persian,
        Language.Polish,
        Language.Bulgarian,
        Language.Estonian,
        Language.Hungarian,
    };

    private string _accessKeyId = "";
    private string _secretAccessKey = "";

    public VolcanoService(HttpClient httpClient) : base(httpClient) { }

    public override string ServiceId => "volcano";
    public override string DisplayName => "Volcano";
    public override bool RequiresApiKey => true;
    public override bool IsConfigured => !string.IsNullOrEmpty(_accessKeyId) && !string.IsNullOrEmpty(_secretAccessKey);
    public override IReadOnlyList<Language> SupportedLanguages => _volcanoLanguages;

    /// <summary>
    /// Configure the Volcano service with AccessKeyID and SecretAccessKey.
    /// Obtain these from the Volcano Engine console at https://www.volcengine.com.
    /// </summary>
    /// <param name="accessKeyId">Volcano Engine AccessKeyID.</param>
    /// <param name="secretAccessKey">Volcano Engine SecretAccessKey.</param>
    public void Configure(string accessKeyId, string secretAccessKey)
    {
        _accessKeyId = accessKeyId ?? "";
        _secretAccessKey = secretAccessKey ?? "";
    }

    protected override async Task<TranslationResult> TranslateInternalAsync(
        TranslationRequest request,
        CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            throw new TranslationException("Volcano API keys not configured")
            {
                ErrorCode = TranslationErrorCode.InvalidApiKey,
                ServiceId = ServiceId
            };
        }

        if (request.Text.Length > MaxTextLength)
        {
            throw new TranslationException($"Text exceeds maximum length of {MaxTextLength} characters")
            {
                ErrorCode = TranslationErrorCode.TextTooLong,
                ServiceId = ServiceId
            };
        }

        var fromCode = request.FromLanguage == Language.Auto ? "" : GetLanguageCode(request.FromLanguage);
        var toCode = GetLanguageCode(request.ToLanguage);

        // Build request body
        var requestBody = new Dictionary<string, object>
        {
            ["TargetLanguage"] = toCode,
            ["TextList"] = new[] { request.Text }
        };

        if (!string.IsNullOrEmpty(fromCode))
        {
            requestBody["SourceLanguage"] = fromCode;
        }

        var bodyJson = JsonSerializer.Serialize(requestBody);
        var bodyBytes = Encoding.UTF8.GetBytes(bodyJson);

        // Build signed request
        var now = DateTime.UtcNow;
        var xDate = now.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture);
        var shortDate = now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

        var authorization = ComputeAuthorization(bodyBytes, xDate, shortDate);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{Endpoint}?{QueryString}")
        {
            Content = new ByteArrayContent(bodyBytes)
        };
        httpRequest.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        httpRequest.Headers.Add("Host", Host);
        httpRequest.Headers.Add("X-Date", xDate);
        httpRequest.Headers.TryAddWithoutValidation("Authorization", authorization);

        using var response = await HttpClient.SendAsync(httpRequest, cancellationToken);
        var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var errorCode = response.StatusCode switch
            {
                System.Net.HttpStatusCode.Unauthorized => TranslationErrorCode.InvalidApiKey,
                System.Net.HttpStatusCode.Forbidden => TranslationErrorCode.InvalidApiKey,
                System.Net.HttpStatusCode.TooManyRequests => TranslationErrorCode.RateLimited,
                _ => TranslationErrorCode.ServiceUnavailable
            };

            throw new TranslationException($"Volcano API returned {response.StatusCode}")
            {
                ErrorCode = errorCode,
                ServiceId = ServiceId
            };
        }

        return ParseVolcanoResponse(responseJson, request.Text, request.ToLanguage);
    }

    private TranslationResult ParseVolcanoResponse(string json, string originalText, Language targetLanguage)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Check for API-level error in ResponseMetadata
        if (root.TryGetProperty("ResponseMetadata", out var metadata) &&
            metadata.TryGetProperty("Error", out var error))
        {
            var code = error.TryGetProperty("Code", out var codeProp)
                ? codeProp.GetString() ?? "Unknown"
                : "Unknown";
            var message = error.TryGetProperty("Message", out var msgProp)
                ? msgProp.GetString() ?? "Unknown error"
                : "Unknown error";

            throw new TranslationException($"Volcano API error: {message} (code: {code})")
            {
                ErrorCode = TranslationErrorCode.ServiceUnavailable,
                ServiceId = ServiceId
            };
        }

        // Extract translated text from TranslationList
        var translatedText = originalText;
        var detectedLanguage = Language.Auto;

        if (root.TryGetProperty("TranslationList", out var translationList) &&
            translationList.ValueKind == JsonValueKind.Array &&
            translationList.GetArrayLength() > 0)
        {
            var firstItem = translationList[0];

            if (firstItem.TryGetProperty("Translation", out var translationProp))
            {
                translatedText = translationProp.GetString() ?? originalText;
            }

            if (firstItem.TryGetProperty("DetectedSourceLanguage", out var detectedLangProp))
            {
                var detectedCode = detectedLangProp.GetString();
                if (!string.IsNullOrEmpty(detectedCode))
                {
                    detectedLanguage = LanguageCodes.FromIso639(detectedCode);
                }
            }
        }

        return new TranslationResult
        {
            TranslatedText = translatedText,
            OriginalText = originalText,
            DetectedLanguage = detectedLanguage,
            TargetLanguage = targetLanguage,
            ServiceName = DisplayName,
            TimingMs = 0,
            FromCache = false
        };
    }

    /// <summary>
    /// Compute the HMAC-SHA256 Authorization header per Volcano Engine signing spec.
    /// https://www.volcengine.com/docs/6369/67269
    /// </summary>
    internal string ComputeAuthorization(byte[] body, string xDate, string shortDate)
    {
        var credentialScope = $"{shortDate}/{Region}/{ServiceName}/request";

        // 1. Canonical headers (sorted by lowercase key)
        var canonicalHeaders = $"content-type:application/json\nhost:{Host}\nx-date:{xDate}\n";
        var signedHeaders = "content-type;host;x-date";

        // 2. Hash the body
        var bodyHash = HexEncode(SHA256.HashData(body));

        // 3. Canonical request
        var canonicalRequest = string.Join("\n",
            "POST",
            "/",
            QueryString,
            canonicalHeaders,
            signedHeaders,
            bodyHash);

        // 4. String to sign
        var canonicalRequestHash = HexEncode(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest)));
        var stringToSign = string.Join("\n",
            Algorithm,
            xDate,
            credentialScope,
            canonicalRequestHash);

        // 5. Signing key derivation
        var kDate = HmacSha256(Encoding.UTF8.GetBytes(_secretAccessKey), shortDate);
        var kRegion = HmacSha256(kDate, Region);
        var kService = HmacSha256(kRegion, ServiceName);
        var kSigning = HmacSha256(kService, "request");

        // 6. Signature
        var signature = HexEncode(HmacSha256(kSigning, stringToSign));

        // 7. Authorization header
        return $"{Algorithm} Credential={_accessKeyId}/{credentialScope}, SignedHeaders={signedHeaders}, Signature={signature}";
    }

    private static byte[] HmacSha256(byte[] key, string data)
    {
        using var hmac = new HMACSHA256(key);
        return hmac.ComputeHash(Encoding.UTF8.GetBytes(data));
    }

    private static string HexEncode(byte[] data)
    {
        return Convert.ToHexString(data).ToLowerInvariant();
    }

    protected override string GetLanguageCode(Language language)
    {
        return language switch
        {
            Language.Auto => "",
            Language.SimplifiedChinese => "zh",
            Language.TraditionalChinese => "zh-Hant",
            Language.ClassicalChinese => "lzh",
            Language.English => "en",
            Language.Japanese => "ja",
            Language.Korean => "ko",
            Language.French => "fr",
            Language.German => "de",
            Language.Spanish => "es",
            Language.Portuguese => "pt",
            Language.Italian => "it",
            Language.Russian => "ru",
            Language.Arabic => "ar",
            Language.Thai => "th",
            Language.Vietnamese => "vi",
            Language.Indonesian => "id",
            Language.Hindi => "hi",
            Language.Hebrew => "he",
            Language.Ukrainian => "uk",
            Language.Urdu => "ur",
            Language.Turkish => "tr",
            Language.Tamil => "ta",
            Language.Telugu => "te",
            Language.Slovenian => "sl",
            Language.Slovak => "sk",
            Language.Swedish => "sv",
            Language.Norwegian => "no",
            Language.Bengali => "bn",
            Language.Malay => "ms",
            Language.Romanian => "ro",
            Language.Lithuanian => "lt",
            Language.Latvian => "lv",
            Language.Czech => "cs",
            Language.Dutch => "nl",
            Language.Finnish => "fi",
            Language.Danish => "da",
            Language.Persian => "fa",
            Language.Polish => "pl",
            Language.Bulgarian => "bg",
            Language.Estonian => "et",
            Language.Hungarian => "hu",
            _ => throw new TranslationException($"Unsupported language: {language}")
            {
                ErrorCode = TranslationErrorCode.UnsupportedLanguage,
                ServiceId = ServiceId
            }
        };
    }
}
