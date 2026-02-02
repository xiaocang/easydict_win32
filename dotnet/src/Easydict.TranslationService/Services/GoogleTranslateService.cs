using System.Net;
using System.Text;
using System.Text.Json;
using System.Web;
using Easydict.TranslationService.Models;

namespace Easydict.TranslationService.Services;

/// <summary>
/// Google Translate service using the free GTX API.
/// No API key required.
/// </summary>
public sealed class GoogleTranslateService : BaseTranslationService
{
    private const string BaseUrl = "https://translate.googleapis.com/translate_a/single";

    private static readonly IReadOnlyList<Language> _googleLanguages =
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

    public GoogleTranslateService(HttpClient httpClient) : base(httpClient)
    {
    }

    public override string ServiceId => "google";
    public override string DisplayName => "Google Translate";
    public override bool RequiresApiKey => false;
    public override bool IsConfigured => true;
    public override IReadOnlyList<Language> SupportedLanguages => _googleLanguages;

    protected override async Task<TranslationResult> TranslateInternalAsync(
        TranslationRequest request,
        CancellationToken cancellationToken)
    {
        var sourceCode = request.FromLanguage == Language.Auto
            ? "auto"
            : GetLanguageCode(request.FromLanguage);
        var targetCode = GetLanguageCode(request.ToLanguage);

        var url = BuildUrl(request.Text, sourceCode, targetCode);

        using var response = await HttpClient.GetAsync(url, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new TranslationException($"Google API error: {response.StatusCode}")
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

    public override async Task<Language> DetectLanguageAsync(
        string text,
        CancellationToken cancellationToken = default)
    {
        var url = BuildUrl(text, "auto", "en");

        using var response = await HttpClient.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(cancellationToken);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // With dj=1, response is an object with "src" property
        if (root.TryGetProperty("src", out var srcElement) &&
            srcElement.ValueKind == JsonValueKind.String)
        {
            var detectedCode = srcElement.GetString() ?? "en";
            return LanguageCodes.FromIso639(detectedCode);
        }

        return Language.Auto;
    }

    private static string BuildUrl(string text, string sourceCode, string targetCode)
    {
        var encodedText = HttpUtility.UrlEncode(text);

        return $"{BaseUrl}?client=gtx&sl={sourceCode}&tl={targetCode}" +
               $"&dt=t&dt=bd&dt=at&dt=ex&dt=ld&dt=md&dt=qca&dt=rw&dt=rm&dt=ss" +
               $"&dj=1&ie=UTF-8&oe=UTF-8&q={encodedText}";
    }

    private TranslationResult ParseResponse(string json, TranslationRequest request)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Extract translated text from sentences array
        var sb = new StringBuilder();
        if (root.TryGetProperty("sentences", out var sentences))
        {
            foreach (var sentence in sentences.EnumerateArray())
            {
                if (sentence.TryGetProperty("trans", out var trans))
                {
                    var part = trans.GetString();
                    if (!string.IsNullOrEmpty(part))
                    {
                        sb.Append(part);
                    }
                }
            }
        }
        var translatedText = sb.ToString();

        // Get detected source language
        var detectedLang = Language.Auto;
        if (root.TryGetProperty("src", out var srcElement))
        {
            var srcCode = srcElement.GetString() ?? "";
            detectedLang = LanguageCodes.FromIso639(srcCode);
        }

        // Get alternatives if available
        List<string>? alternatives = null;
        if (root.TryGetProperty("alternative_translations", out var altTrans))
        {
            alternatives = [];
            foreach (var alt in altTrans.EnumerateArray())
            {
                if (alt.TryGetProperty("alternative", out var altArray))
                {
                    foreach (var item in altArray.EnumerateArray())
                    {
                        if (item.TryGetProperty("word_postproc", out var word))
                        {
                            var altText = word.GetString();
                            if (!string.IsNullOrEmpty(altText) && altText != translatedText)
                                alternatives.Add(altText);
                        }
                    }
                }
            }
        }

        return new TranslationResult
        {
            TranslatedText = translatedText,
            OriginalText = request.Text,
            DetectedLanguage = detectedLang,
            TargetLanguage = request.ToLanguage,
            ServiceName = DisplayName,
            Alternatives = alternatives?.Count > 0 ? alternatives : null
        };
    }
}

