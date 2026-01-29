using System.Text.Json;
using Easydict.TranslationService.Models;

namespace Easydict.TranslationService.Services;

/// <summary>
/// Linguee dictionary service providing translations with context examples.
/// Uses public proxy API at linguee-api.fly.dev.
/// </summary>
public sealed class LingueeService : BaseTranslationService
{
    private const string BaseUrl = "https://linguee-api.fly.dev/api/v2/translations";

    private static readonly IReadOnlyList<Language> LingueeLanguages = new[]
    {
        Language.English,
        Language.German,
        Language.French,
        Language.Spanish,
        Language.Italian,
        Language.Portuguese,
        Language.Dutch,
        Language.Polish,
        Language.Russian,
        Language.Bulgarian,
        Language.Czech,
        Language.Danish,
        Language.Greek,
        Language.Estonian,
        Language.Finnish,
        Language.Hungarian,
        Language.Lithuanian,
        Language.Latvian,
        Language.Romanian,
        Language.Slovak,
        Language.Slovenian,
        Language.Swedish,
        Language.SimplifiedChinese,
        Language.Japanese
    };

    public LingueeService(HttpClient httpClient) : base(httpClient) { }

    public override string ServiceId => "linguee";
    public override string DisplayName => "Linguee Dictionary";
    public override bool RequiresApiKey => false; // Public proxy, no API key needed
    public override bool IsConfigured => true;
    public override IReadOnlyList<Language> SupportedLanguages => LingueeLanguages;

    protected override async Task<TranslationResult> TranslateInternalAsync(
        TranslationRequest request,
        CancellationToken cancellationToken)
    {
        var fromCode = GetLanguageCode(request.FromLanguage);
        var toCode = GetLanguageCode(request.ToLanguage);

        // Build URL with query parameters
        var url = $"{BaseUrl}?query={Uri.EscapeDataString(request.Text)}&src={fromCode}&dst={toCode}";

        using var response = await HttpClient.GetAsync(url, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new TranslationException($"Linguee API returned {response.StatusCode}")
            {
                ErrorCode = response.StatusCode == System.Net.HttpStatusCode.TooManyRequests
                    ? TranslationErrorCode.RateLimited
                    : TranslationErrorCode.ServiceUnavailable,
                ServiceId = ServiceId
            };
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        var result = ParseLingueeResponse(json, request.Text, request.ToLanguage);

        return result;
    }

    private TranslationResult ParseLingueeResponse(string json, string originalText, Language targetLanguage)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        string translatedText = originalText;
        var alternatives = new List<string>();

        // Root is an array of dictionary entries
        if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0)
        {
            var firstEntry = root[0];
            if (firstEntry.TryGetProperty("translations", out var translations) && translations.GetArrayLength() > 0)
            {
                var firstTranslation = translations[0];
                if (firstTranslation.TryGetProperty("text", out var textProp))
                {
                    translatedText = textProp.GetString() ?? originalText;
                }

                for (int i = 1; i < translations.GetArrayLength(); i++)
                {
                    var trans = translations[i];
                    if (trans.TryGetProperty("text", out var altText))
                    {
                        var alt = altText.GetString();
                        if (!string.IsNullOrEmpty(alt))
                        {
                            alternatives.Add(alt);
                        }
                    }
                }
            }
        }

        return new TranslationResult
        {
            TranslatedText = translatedText,
            OriginalText = originalText,
            DetectedLanguage = Language.Auto,
            TargetLanguage = targetLanguage,
            ServiceName = DisplayName,
            TimingMs = 0,
            FromCache = false,
            Alternatives = alternatives.Count > 0 ? alternatives : null
        };
    }

    protected override string GetLanguageCode(Language language)
    {
        return language switch
        {
            Language.Auto => "auto",
            Language.English => "en",
            Language.German => "de",
            Language.French => "fr",
            Language.Spanish => "es",
            Language.Italian => "it",
            Language.Portuguese => "pt",
            Language.Dutch => "nl",
            Language.Polish => "pl",
            Language.Russian => "ru",
            Language.Bulgarian => "bg",
            Language.Czech => "cs",
            Language.Danish => "da",
            Language.Greek => "el",
            Language.Estonian => "et",
            Language.Finnish => "fi",
            Language.Hungarian => "hu",
            Language.Lithuanian => "lt",
            Language.Latvian => "lv",
            Language.Romanian => "ro",
            Language.Slovak => "sk",
            Language.Slovenian => "sl",
            Language.Swedish => "sv",
            Language.SimplifiedChinese => "zh",
            Language.Japanese => "ja",
            _ => throw new TranslationException($"Unsupported language: {language}")
            {
                ErrorCode = TranslationErrorCode.UnsupportedLanguage,
                ServiceId = ServiceId
            }
        };
    }
}
