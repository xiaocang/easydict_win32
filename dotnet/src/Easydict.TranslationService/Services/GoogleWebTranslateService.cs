using System.Net;
using System.Text;
using System.Text.Json;
using System.Web;
using Easydict.TranslationService.Models;

namespace Easydict.TranslationService.Services;

/// <summary>
/// Google Translate service using the WebApp API with rich dictionary results.
/// Returns phonetics, definitions (parts of speech + meanings), and examples
/// in addition to plain translation text.
/// </summary>
public sealed class GoogleWebTranslateService : BaseTranslationService
{
    private const string BaseUrl = "https://translate.google.com/translate_a/single";

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

    public GoogleWebTranslateService(HttpClient httpClient) : base(httpClient)
    {
    }

    public override string ServiceId => "google_web";
    public override string DisplayName => "Google Dict";
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
            throw new TranslationException($"Google WebApp API error: {response.StatusCode}")
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

        // WebApp API returns a nested array; detected language is at index [2]
        if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 2)
        {
            var langElement = root[2];
            if (langElement.ValueKind == JsonValueKind.String)
            {
                var detectedCode = langElement.GetString() ?? "en";
                return LanguageCodes.FromIso639(detectedCode);
            }
        }

        return Language.Auto;
    }

    private static string BuildUrl(string text, string sourceCode, string targetCode)
    {
        var encodedText = HttpUtility.UrlEncode(text);

        // dt parameters: t=translation, bd=dictionary, at=alternatives, ex=examples,
        // rm=romanization, md=definitions, ss=synonyms
        return $"{BaseUrl}?client=gtx&sl={sourceCode}&tl={targetCode}" +
               $"&dt=at&dt=bd&dt=ex&dt=ld&dt=md&dt=qca&dt=rw&dt=rm&dt=ss&dt=t" +
               $"&ie=UTF-8&oe=UTF-8&q={encodedText}";
    }

    private TranslationResult ParseResponse(string json, TranslationRequest request)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.ValueKind != JsonValueKind.Array)
        {
            throw new TranslationException("Unexpected response format from Google WebApp API")
            {
                ErrorCode = TranslationErrorCode.Unknown,
                ServiceId = ServiceId
            };
        }

        // Extract translated text from root[0] (array of sentence arrays)
        var translatedText = ExtractTranslatedText(root);

        // Extract detected language from root[2]
        var detectedLang = ExtractDetectedLanguage(root);

        // Extract phonetic from root[0]
        var phonetic = ExtractPhonetic(root);

        // Extract dictionary definitions from root[1]
        var definitions = ExtractDefinitions(root);

        // Extract examples from root[13] (if available)
        var examples = ExtractExamples(root);

        // Build WordResult if we have any rich data
        WordResult? wordResult = null;
        if (phonetic != null || definitions != null || examples != null)
        {
            wordResult = new WordResult
            {
                Phonetics = phonetic != null ? [phonetic] : null,
                Definitions = definitions,
                Examples = examples
            };
        }

        return new TranslationResult
        {
            TranslatedText = translatedText,
            OriginalText = request.Text,
            DetectedLanguage = detectedLang,
            TargetLanguage = request.ToLanguage,
            ServiceName = DisplayName,
            WordResult = wordResult
        };
    }

    private static string ExtractTranslatedText(JsonElement root)
    {
        var sb = new StringBuilder();

        if (root.GetArrayLength() > 0 && root[0].ValueKind == JsonValueKind.Array)
        {
            foreach (var sentenceArray in root[0].EnumerateArray())
            {
                if (sentenceArray.ValueKind == JsonValueKind.Array &&
                    sentenceArray.GetArrayLength() > 0 &&
                    sentenceArray[0].ValueKind == JsonValueKind.String)
                {
                    var part = sentenceArray[0].GetString();
                    if (!string.IsNullOrEmpty(part))
                    {
                        sb.Append(part);
                    }
                }
            }
        }

        return sb.ToString();
    }

    private static Language ExtractDetectedLanguage(JsonElement root)
    {
        // Try root[8] for more accurate detection first (handles zh-TW vs zh-CN)
        if (root.GetArrayLength() > 8 && root[8].ValueKind == JsonValueKind.Array)
        {
            var langDetect = root[8];
            var lastIdx = langDetect.GetArrayLength() - 1;
            if (lastIdx >= 0 && langDetect[lastIdx].ValueKind == JsonValueKind.Array &&
                langDetect[lastIdx].GetArrayLength() > 0 &&
                langDetect[lastIdx][0].ValueKind == JsonValueKind.String)
            {
                var code = langDetect[lastIdx][0].GetString();
                if (!string.IsNullOrEmpty(code))
                    return LanguageCodes.FromIso639(code);
            }
        }

        // Fallback to root[2]
        if (root.GetArrayLength() > 2 && root[2].ValueKind == JsonValueKind.String)
        {
            var code = root[2].GetString() ?? "";
            return LanguageCodes.FromIso639(code);
        }

        return Language.Auto;
    }

    private static Phonetic? ExtractPhonetic(JsonElement root)
    {
        // Phonetic is at root[0][1][3] (romanization of source text)
        if (root.GetArrayLength() > 0 && root[0].ValueKind == JsonValueKind.Array)
        {
            var sentences = root[0];
            var lastIdx = sentences.GetArrayLength() - 1;
            if (lastIdx >= 0 && sentences[lastIdx].ValueKind == JsonValueKind.Array)
            {
                var lastSentence = sentences[lastIdx];
                // The last entry in root[0] often contains phonetics at index 3
                if (lastSentence.GetArrayLength() > 3 &&
                    lastSentence[3].ValueKind == JsonValueKind.String)
                {
                    var phoneticText = lastSentence[3].GetString();
                    if (!string.IsNullOrEmpty(phoneticText))
                    {
                        return new Phonetic { Text = phoneticText };
                    }
                }
            }
        }

        return null;
    }

    private static List<Definition>? ExtractDefinitions(JsonElement root)
    {
        // Dictionary results are at root[1]
        if (root.GetArrayLength() <= 1 || root[1].ValueKind != JsonValueKind.Array)
            return null;

        var definitions = new List<Definition>();

        foreach (var entry in root[1].EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Array || entry.GetArrayLength() < 2)
                continue;

            // entry[0] = part of speech, entry[1] = array of meanings
            var partOfSpeech = entry[0].ValueKind == JsonValueKind.String
                ? entry[0].GetString()
                : null;

            var meanings = new List<string>();

            if (entry[1].ValueKind == JsonValueKind.Array)
            {
                foreach (var meaning in entry[1].EnumerateArray())
                {
                    if (meaning.ValueKind == JsonValueKind.String)
                    {
                        var text = meaning.GetString();
                        if (!string.IsNullOrEmpty(text))
                            meanings.Add(text);
                    }
                }
            }

            // Also check entry[2] for "simple words" (used in zh->en direction)
            if (meanings.Count == 0 && entry.GetArrayLength() > 2 &&
                entry[2].ValueKind == JsonValueKind.Array)
            {
                foreach (var simpleWord in entry[2].EnumerateArray())
                {
                    if (simpleWord.ValueKind == JsonValueKind.Array &&
                        simpleWord.GetArrayLength() > 0 &&
                        simpleWord[0].ValueKind == JsonValueKind.String)
                    {
                        var text = simpleWord[0].GetString();
                        if (!string.IsNullOrEmpty(text))
                            meanings.Add(text);
                    }
                }
            }

            if (meanings.Count > 0)
            {
                definitions.Add(new Definition
                {
                    PartOfSpeech = partOfSpeech,
                    Meanings = meanings
                });
            }
        }

        return definitions.Count > 0 ? definitions : null;
    }

    private static List<string>? ExtractExamples(JsonElement root)
    {
        // Examples are at root[13]
        if (root.GetArrayLength() <= 13 || root[13].ValueKind != JsonValueKind.Array)
            return null;

        var examples = new List<string>();
        var examplesArray = root[13];

        if (examplesArray.GetArrayLength() > 0 && examplesArray[0].ValueKind == JsonValueKind.Array)
        {
            foreach (var example in examplesArray[0].EnumerateArray())
            {
                if (example.ValueKind == JsonValueKind.Array &&
                    example.GetArrayLength() > 0 &&
                    example[0].ValueKind == JsonValueKind.String)
                {
                    var text = example[0].GetString();
                    if (!string.IsNullOrEmpty(text))
                    {
                        // Strip HTML tags (Google returns <b>word</b> in examples)
                        text = System.Text.RegularExpressions.Regex.Replace(text, "<[^>]+>", "");
                        examples.Add(text);
                    }
                }
            }
        }

        return examples.Count > 0 ? examples : null;
    }
}
