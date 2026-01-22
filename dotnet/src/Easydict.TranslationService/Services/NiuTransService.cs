using System.Text;
using System.Text.Json;
using Easydict.TranslationService.Models;

namespace Easydict.TranslationService.Services;

/// <summary>
/// NiuTrans (小牛翻译) neural machine translation service.
/// Supports 450+ languages with simple API key authentication.
/// </summary>
public sealed class NiuTransService : BaseTranslationService
{
    private const string Endpoint = "https://api.niutrans.com/NiuTransServer/translation";
    private const int MaxTextLength = 5000;

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

        if (request.Text.Length > MaxTextLength)
        {
            throw new TranslationException($"Text exceeds maximum length of {MaxTextLength} characters")
            {
                ErrorCode = TranslationErrorCode.TextTooLong,
                ServiceId = ServiceId
            };
        }

        var fromCode = GetLanguageCode(request.FromLanguage);
        var toCode = GetLanguageCode(request.ToLanguage);

        // Build request body
        var requestBody = new
        {
            apikey = _apiKey,
            src_text = request.Text,
            from = fromCode,
            to = toCode,
            source = "Easydict"
        };

        var json = JsonSerializer.Serialize(requestBody);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await HttpClient.PostAsync(Endpoint, content, cancellationToken);
        var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
        var result = ParseNiuTransResponse(responseJson, request.Text);

        return result;
    }

    private TranslationResult ParseNiuTransResponse(string json, string originalText)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Check for error first
        if (root.TryGetProperty("error_code", out var errorCodeProp))
        {
            var errorCode = errorCodeProp.GetString() ?? "";
            var errorMsg = root.TryGetProperty("error_msg", out var errorMsgProp)
                ? errorMsgProp.GetString() ?? "Unknown error"
                : "Unknown error";

            var translationErrorCode = errorCode switch
            {
                "13002" => TranslationErrorCode.InvalidApiKey, // apikey is empty
                "13003" => TranslationErrorCode.InvalidApiKey, // apikey is invalid
                "13004" => TranslationErrorCode.RateLimited,   // balance insufficient
                "13005" => TranslationErrorCode.TextTooLong,   // text too long
                _ => TranslationErrorCode.ServiceUnavailable
            };

            throw new TranslationException($"NiuTrans API error: {errorMsg} (code: {errorCode})")
            {
                ErrorCode = translationErrorCode,
                ServiceId = ServiceId
            };
        }

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
            Language.TraditionalChinese => "cht",
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
