using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Easydict.TranslationService.Models;

namespace Easydict.TranslationService.Services;

/// <summary>
/// DeepL translation service using the official API.
/// Requires API key (free or pro).
/// </summary>
public sealed class DeepLService : BaseTranslationService
{
    private const string FreeApiHost = "https://api-free.deepl.com";
    private const string ProApiHost = "https://api.deepl.com";

    private string? _apiKey;

    private static readonly IReadOnlyList<Language> DeepLLanguages =
    [
        Language.SimplifiedChinese, Language.English, Language.Japanese,
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
    public override bool RequiresApiKey => true;
    public override bool IsConfigured => !string.IsNullOrEmpty(_apiKey);
    public override IReadOnlyList<Language> SupportedLanguages => DeepLLanguages;

    /// <summary>
    /// Configure the service with an API key.
    /// </summary>
    public void Configure(string apiKey)
    {
        _apiKey = apiKey;
    }

    protected override async Task<TranslationResult> TranslateInternalAsync(
        TranslationRequest request,
        CancellationToken cancellationToken)
    {
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
        return ParseResponse(json, request);
    }

    private string GetApiHost()
    {
        // Free API keys end with ":fx"
        return _apiKey?.EndsWith(":fx") == true ? FreeApiHost : ProApiHost;
    }

    private TranslationResult ParseResponse(string json, TranslationRequest request)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!root.TryGetProperty("translations", out var translations) ||
            translations.GetArrayLength() == 0)
        {
            throw new TranslationException("Invalid response from DeepL")
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

    private static string GetDeepLLanguageCode(Language language) => language switch
    {
        Language.SimplifiedChinese => "ZH",
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
}

