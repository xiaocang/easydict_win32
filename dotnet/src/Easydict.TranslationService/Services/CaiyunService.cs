using System.Text;
using System.Text.Json;
using Easydict.TranslationService.Models;

namespace Easydict.TranslationService.Services;

/// <summary>
/// Caiyun (彩云小译) translation service.
/// Popular Chinese translation service with free tier.
/// </summary>
public sealed class CaiyunService : BaseTranslationService
{
    private const string Endpoint = "https://api.interpreter.caiyunai.com/v1/translator";

    private static readonly IReadOnlyList<Language> CaiyunLanguages = new[]
    {
        Language.Auto,
        Language.SimplifiedChinese,
        Language.TraditionalChinese,
        Language.English,
        Language.Japanese,
        Language.Korean,
        Language.Spanish,
        Language.French,
        Language.Russian,
        Language.German,
        Language.Italian,
        Language.Portuguese,
        Language.Arabic,
        Language.Hindi,
        Language.Indonesian,
        Language.Malay,
        Language.Thai,
        Language.Vietnamese
    };

    private string _apiKey = "";

    public CaiyunService(HttpClient httpClient) : base(httpClient) { }

    public override string ServiceId => "caiyun";
    public override string DisplayName => "Caiyun";
    public override bool RequiresApiKey => true;
    public override bool IsConfigured => !string.IsNullOrEmpty(_apiKey);
    public override IReadOnlyList<Language> SupportedLanguages => CaiyunLanguages;

    /// <summary>
    /// Configure the Caiyun service with API token.
    /// </summary>
    /// <param name="apiKey">Caiyun API token.</param>
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
            throw new TranslationException("Caiyun API key not configured")
            {
                ErrorCode = TranslationErrorCode.InvalidApiKey,
                ServiceId = ServiceId
            };
        }

        var fromCode = GetLanguageCode(request.FromLanguage);
        var toCode = GetLanguageCode(request.ToLanguage);
        var transType = $"{fromCode}2{toCode}";

        // Build request body in Caiyun's format
        var requestBody = new
        {
            source = new[] { request.Text },
            trans_type = transType,
            request_id = Guid.NewGuid().ToString(),
            media = "text"
        };

        var json = JsonSerializer.Serialize(requestBody);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        // Add X-Authorization header
        var httpRequest = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = content
        };
        httpRequest.Headers.Add("X-Authorization", $"token {_apiKey}");

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

            throw new TranslationException($"Caiyun API returned {response.StatusCode}")
            {
                ErrorCode = errorCode,
                ServiceId = ServiceId
            };
        }

        var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
        var result = ParseCaiyunResponse(responseJson, request.Text);

        return result;
    }

    private TranslationResult ParseCaiyunResponse(string json, string originalText)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Extract target text from response
        var translatedText = originalText;
        if (root.TryGetProperty("target", out var targetArray) && targetArray.GetArrayLength() > 0)
        {
            var translations = new List<string>();
            for (int i = 0; i < targetArray.GetArrayLength(); i++)
            {
                var line = targetArray[i].GetString();
                if (!string.IsNullOrEmpty(line))
                {
                    translations.Add(line);
                }
            }
            translatedText = string.Concat(translations);
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
            Language.TraditionalChinese => "zh-Hant",
            Language.English => "en",
            Language.Japanese => "ja",
            Language.Korean => "ko",
            Language.Spanish => "es",
            Language.French => "fr",
            Language.Russian => "ru",
            Language.German => "de",
            Language.Italian => "it",
            Language.Portuguese => "pt",
            Language.Arabic => "ar",
            Language.Hindi => "hi",
            Language.Indonesian => "id",
            Language.Malay => "ms",
            Language.Thai => "th",
            Language.Vietnamese => "vi",
            _ => throw new TranslationException($"Unsupported language: {language}")
            {
                ErrorCode = TranslationErrorCode.UnsupportedLanguage,
                ServiceId = ServiceId
            }
        };
    }
}
